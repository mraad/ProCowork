using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISClaude.Engine;
using ArcGISClaude.UI;
using RelayCommand = ArcGISClaude.UI.RelayCommand;

namespace ArcGISClaude
{
    /// <summary>
    /// The chat dock pane. Owns the Claude Code engine for the session, sends the
    /// user's turns, and renders the engine's stream-json events natively into the
    /// transcript (assistant prose, the generated code that ran, and tool results).
    /// </summary>
    internal class ChatDockPaneViewModel : DockPane
    {
        private const string DockPaneId = "ArcGISClaude_ChatDockPane";

        private readonly Dispatcher _ui;
        private readonly Dictionary<string, ToolCallVm> _toolsById = new Dictionary<string, ToolCallVm>();
        private readonly Queue<string> _stderrTail = new Queue<string>();

        // Pro-shutdown hook target: DAML instantiates exactly one VM per session,
        // and the Pro SDK offers no DockPane dispose hook, so Module1.Uninitialize
        // reaches the engine through this field. (DockPaneManager.Find would
        // *instantiate* a never-opened pane during shutdown just to no-op.)
        private static ChatDockPaneViewModel _instance;

        private ClaudeCodeProcess _engine;
        private Action<JObject> _onEvent;
        private Action<string> _onStdErr;
        private Action<int> _onExited;

        // Claude Code emits system/init at the start of every turn, so the connect
        // notice must be gated to once per engine session. _hasConnectedBefore then
        // distinguishes the first connect from a genuine respawn (true reconnect).
        private bool _sessionAnnounced;
        private bool _hasConnectedBefore;

        // Bumped on every ShutdownEngine so BeginInvoke'd callbacks and in-flight
        // SendUserMessageAsync catches from a replaced engine cannot mutate the
        // new session's transcript or turn state.
        private int _engineEpoch;

        public ObservableCollection<ChatItemVm> Items { get; } = new ObservableCollection<ChatItemVm>();

        private string _input = "";
        public string Input
        {
            get => _input;
            set { SetProperty(ref _input, value); SendCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isTurnActive;
        public bool IsTurnActive
        {
            get => _isTurnActive;
            set
            {
                SetProperty(ref _isTurnActive, value);
                SendCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }

        private string _statusText = "Not connected — type a message to start Claude.";
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        // When off, the transcript hides the tool-call cards (generated code + results).
        private bool _showToolOutputs = false;
        public bool ShowToolOutputs
        {
            get => _showToolOutputs;
            set => SetProperty(ref _showToolOutputs, value);
        }

        public RelayCommand SendCommand { get; }
        public RelayCommand StopCommand { get; }

        protected ChatDockPaneViewModel()
        {
            _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            SendCommand = new RelayCommand(() => _ = OnSendAsync(),
                                           () => !IsTurnActive && !string.IsNullOrWhiteSpace(Input));
            StopCommand = new RelayCommand(OnStop, () => IsTurnActive);
            _instance = this;
        }

        /// <summary>
        /// Pro-shutdown hook, called by <see cref="Module1.Uninitialize"/>: without
        /// it the claude child process tree outlives Pro — Windows does not kill
        /// children when the parent exits.
        /// </summary>
        internal static void ShutdownInstance() => _instance?.ShutdownEngine();

        private void ShutdownEngine()
        {
            var e = _engine;
            if (e == null) return;
            _engine = null;
            _engineEpoch++;
            // Detach the exact subscribe-time delegates (not method groups) so a
            // handler already on the stack still carries the old epoch.
            if (_onEvent != null) { e.EventReceived -= _onEvent; _onEvent = null; }
            if (_onStdErr != null) { e.StdErrReceived -= _onStdErr; _onStdErr = null; }
            if (_onExited != null) { e.Exited -= _onExited; _onExited = null; }
            try { e.Dispose(); } catch { }
        }

        internal static void Show()
            => FrameworkApplication.DockPaneManager.Find(DockPaneId)?.Activate();

        // ---- engine lifecycle -------------------------------------------------

        private void EnsureEngine()
        {
            if (_engine != null && _engine.IsRunning) return;

            // A previous engine that exited or was stopped must be detached and
            // disposed before we replace it, or its process handle and event
            // subscriptions leak. Stale tool ids from that session go with it.
            ShutdownEngine();

            _toolsById.Clear();
            lock (_stderrTail) _stderrTail.Clear();

            var paths = Module1.Current.Paths;
            _engine = new ClaudeCodeProcess(paths.WorkspaceDir, paths.McpConfigPath);
            var epoch = _engineEpoch;
            _onEvent = ev => OnEngineEvent(epoch, ev);
            _onStdErr = line => OnEngineStdErr(epoch, line);
            _onExited = code => OnEngineExited(epoch, code);
            _engine.EventReceived += _onEvent;
            _engine.StdErrReceived += _onStdErr;
            _engine.Exited += _onExited;
            _engine.Start(EngineSettings.Current);
            _sessionAnnounced = false;  // new session -> announce once on its first init
        }

        private async Task OnSendAsync()
        {
            var text = Input?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            Input = "";
            Items.Add(new UserMessageVm(text));
            IsTurnActive = true;
            int? epoch = null;

            try
            {
                EnsureEngine();
                epoch = _engineEpoch;
                await _engine.SendUserMessageAsync(text);
            }
            catch (Exception ex)
            {
                // Stop/respawn during the await bumps the epoch. Don't show a
                // send-failure or clear IsTurnActive for a newer session.
                // EnsureEngine itself throwing leaves epoch null — always surface that.
                if (epoch is int e && e != _engineEpoch) return;
                Items.Add(new SystemNoticeVm("Failed to send: " + ex.Message));
                IsTurnActive = false;
            }
        }

        private void OnStop()
        {
            IsTurnActive = false;
            StatusText = "Engine stopped.";
            // Kill immediately (same path as Pro shutdown). A grace-period Stop
            // left _engine.IsRunning true, so the next Send wrote to a dying stdin.
            ShutdownEngine();
        }

        // ---- event rendering (marshalled to the UI thread) --------------------

        private void PostUi(int epoch, Action action)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                if (epoch != _engineEpoch) return;
                action();
            }));
        }

        private void OnEngineEvent(int epoch, JObject ev) => PostUi(epoch, () => HandleEvent(ev));

        private void HandleEvent(JObject ev)
        {
            switch (StreamJsonReader.Type(ev))
            {
                case "system":
                    if (StreamJsonReader.Subtype(ev) == "init") HandleInit(ev);
                    break;
                case "assistant":
                    HandleAssistant(ev);
                    break;
                case "user":
                    HandleToolResults(ev);
                    break;
                case "result":
                    IsTurnActive = false;
                    var sub = StreamJsonReader.Subtype(ev);
                    if (!string.IsNullOrEmpty(sub) && sub != "success")
                        Items.Add(new SystemNoticeVm("Turn ended: " + sub));
                    break;
            }
        }

        private void HandleInit(JObject ev)
        {
            var model = (string)ev["model"];
            var auth = AuthResolver.Describe(EngineSettings.Current);
            StatusText = $"Connected · {model} · {auth}";

            // Announce once per engine session; per-turn inits are silent. A fresh
            // session after a prior one is a genuine reconnect (the process died and
            // EnsureEngine respawned it), so say so.
            if (!_sessionAnnounced)
            {
                var verb = _hasConnectedBefore ? "Reconnected to" : "Connected to";
                Items.Add(new SystemNoticeVm($"{verb} Claude ({model}) using {auth}."));
                _sessionAnnounced = true;
                _hasConnectedBefore = true;
            }
        }

        private void HandleAssistant(JObject ev)
        {
            var text = StreamJsonReader.JoinedText(ev);
            if (!string.IsNullOrEmpty(text))
                Items.Add(new AssistantMessageVm(text));

            foreach (var tu in StreamJsonReader.ToolUseBlocks(ev))
            {
                var id = (string)tu["id"];
                var name = (string)tu["name"];
                var code = StreamJsonReader.DescribeToolInput(tu["input"]);
                var vm = new ToolCallVm(name, code);
                Items.Add(vm);
                if (!string.IsNullOrEmpty(id)) _toolsById[id] = vm;
            }
        }

        private void HandleToolResults(JObject ev)
        {
            foreach (var tr in StreamJsonReader.ToolResultBlocks(ev))
            {
                var id = (string)tr["tool_use_id"];
                bool isErr = (bool?)tr["is_error"] ?? false;
                var text = StreamJsonReader.ToolResultText(tr);
                if (id != null && _toolsById.TryGetValue(id, out var vm))
                {
                    vm.Result = text;
                    vm.Status = isErr ? "error" : "done";
                }
            }
        }

        private void OnEngineStdErr(int epoch, string line)
        {
            // stderr carries diagnostics, not the error channel — real errors arrive
            // as structured result/tool_result events. Keep only a short tail to show
            // if the engine exits abnormally.
            if (string.IsNullOrEmpty(line)) return;
            System.Diagnostics.Debug.WriteLine("[claude stderr] " + line);
            lock (_stderrTail)
            {
                if (epoch != _engineEpoch) return;
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > 10) _stderrTail.Dequeue();
            }
        }

        private void OnEngineExited(int epoch, int code)
        {
            PostUi(epoch, () =>
            {
                IsTurnActive = false;
                StatusText = "Engine stopped.";
                if (code == 0) return;

                string tail;
                lock (_stderrTail) tail = string.Join("\n", _stderrTail);
                var msg = $"Claude engine exited (code {code}).";
                if (!string.IsNullOrWhiteSpace(tail)) msg += "\n" + tail;
                Items.Add(new SystemNoticeVm(msg));
            });
        }
    }
}
