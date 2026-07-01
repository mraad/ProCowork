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

        private ClaudeCodeProcess _engine;

        // Claude Code emits system/init at the start of every turn, so the connect
        // notice must be gated to once per engine session. _hasConnectedBefore then
        // distinguishes the first connect from a genuine respawn (true reconnect).
        private bool _sessionAnnounced;
        private bool _hasConnectedBefore;

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
        private bool _showToolOutputs = true;
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
            StopCommand = new RelayCommand(OnStop, () => _engine != null && _engine.IsRunning);
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
            if (_engine != null)
            {
                _engine.EventReceived -= OnEngineEvent;
                _engine.StdErrReceived -= OnEngineStdErr;
                _engine.Exited -= OnEngineExited;
                _engine.Dispose();
                _toolsById.Clear();
            }

            var paths = Module1.Current.Paths;
            _engine = new ClaudeCodeProcess(paths.WorkspaceDir, paths.McpConfigPath);
            _engine.EventReceived += OnEngineEvent;
            _engine.StdErrReceived += OnEngineStdErr;
            _engine.Exited += OnEngineExited;
            _engine.Start(EngineSettings.Current);
            _sessionAnnounced = false;  // new session -> announce once on its first init
            // The ArcPy bridge is owned by Module1 and runs automatically for the
            // whole Pro session — nothing to start here.
        }

        private async Task OnSendAsync()
        {
            var text = Input?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            Input = "";
            Items.Add(new UserMessageVm(text));
            IsTurnActive = true;

            try
            {
                EnsureEngine();
                await _engine.SendUserMessageAsync(text);
            }
            catch (Exception ex)
            {
                Items.Add(new SystemNoticeVm("Failed to send: " + ex.Message, true));
                IsTurnActive = false;
            }
        }

        private void OnStop()
        {
            _engine?.Stop();
            IsTurnActive = false;
        }

        // ---- event rendering (marshalled to the UI thread) --------------------

        private void OnEngineEvent(JObject ev) => _ui.BeginInvoke(new Action(() => HandleEvent(ev)));

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
                        Items.Add(new SystemNoticeVm("Turn ended: " + sub, true));
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
                Items.Add(new AssistantMessageVm { Text = text });

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
                    vm.IsError = isErr;
                    vm.Status = isErr ? "error" : "done";
                }
            }
        }

        private void OnEngineStdErr(string line)
        {
            // stderr carries diagnostics, not the error channel — real errors arrive
            // as structured result/tool_result events. Keep only a short tail to show
            // if the engine exits abnormally.
            if (string.IsNullOrEmpty(line)) return;
            System.Diagnostics.Debug.WriteLine("[claude stderr] " + line);
            lock (_stderrTail)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > 10) _stderrTail.Dequeue();
            }
        }

        private void OnEngineExited(int code) => _ui.BeginInvoke(new Action(() =>
        {
            IsTurnActive = false;
            StatusText = "Engine stopped.";
            if (code == 0) return;

            string tail;
            lock (_stderrTail) tail = string.Join("\n", _stderrTail);
            var msg = $"Claude engine exited (code {code}).";
            if (!string.IsNullOrWhiteSpace(tail)) msg += "\n" + tail;
            Items.Add(new SystemNoticeVm(msg, true));
        }));
    }
}
