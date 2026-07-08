using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The persistent execution bridge. It is owned by <see cref="Module1"/> and lives
    /// the entire ArcGIS Pro session, so — unlike the old in-process Python daemon —
    /// there is nothing to be torn down and go stale.
    ///
    /// It is an MCP streamable-HTTP server the Claude Code engine talks to directly
    /// (no Python child process): the generated .mcp.json points the engine at
    /// <c>http://127.0.0.1:<see cref="Port"/>/mcp</c> with a Bearer <see cref="Token"/>.
    /// Each request is one stateless <c>POST /mcp</c> carrying a JSON-RPC 2.0 message,
    /// answered with a single application/json body (no SSE stream, no session id).
    /// Tool calls are dispatched to the .NET read path (<see cref="AppStateOps"/>) or a
    /// fresh ArcPy tool (<see cref="ScriptRunner"/>), serialized by <see cref="_gate"/>.
    ///
    /// Raw TcpListener + hand-rolled HTTP/1.1 rather than HttpListener: HTTP.sys URL
    /// ACLs only allow a non-admin process to reserve <c>localhost</c> hostname
    /// prefixes (which listen on ALL interfaces via Host-header routing), never the
    /// 127.0.0.1 literal — and HttpListener cannot bind port 0. TcpListener gives true
    /// loopback-only binding on an OS-chosen ephemeral port with no ACLs, and the
    /// parsing surface is one method, one path, one known client (the engine's fetch).
    /// </summary>
    internal sealed class BridgeService : IDisposable
    {
        private readonly string _ipcDir;
        private readonly ScriptRunner _runner;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// The serial-dispatch invariant. The old TCP protocol was serial by
        /// construction (one client, one read loop); HTTP allows concurrent POSTs,
        /// so this gate is now what guarantees one tool call — and therefore one
        /// geoprocessing run — at a time. No GP re-entrancy. Do not remove it.
        /// Only tools/call takes the gate: initialize/tools/list/ping touch no Pro
        /// state and must keep answering while a long GP call is in flight.
        /// </summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private TcpListener _listener;
        private Task _loop;

        /// <summary>Loopback port the engine connects to; 0 until <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>
        /// Per-session shared secret. Any local process can reach a loopback port,
        /// and this bridge executes arbitrary Python — so every request must carry
        /// <c>Authorization: Bearer &lt;token&gt;</c>. Handed to the engine via the
        /// headers block in the generated .mcp.json (same channel as the port).
        /// </summary>
        public string Token { get; private set; }

        /// <summary>
        /// MCP server identity. The name is also the mcpServers key in the generated
        /// .mcp.json (see <see cref="AppPaths.WriteMcpConfig"/>), which namespaces the
        /// engine-visible tool ids (mcp__arcgis_bridge__*) that the workspace CLAUDE.md
        /// references — one constant so they cannot drift apart. The version reports
        /// the add-in version (csproj &lt;Version&gt;) rather than a second literal.
        /// </summary>
        public const string ServerName = "arcgis_bridge";
        private static readonly string ServerVersion =
            typeof(BridgeService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private static readonly string[] ProtocolsSupported =
            { "2025-06-18", "2025-03-26", "2024-11-05" }; // newest first
        private static readonly string ProtocolDefault = ProtocolsSupported[0];

        public BridgeService(AppPaths paths)
        {
            _ipcDir = paths.IpcDir;
            _runner = new ScriptRunner(paths);
        }

        /// <summary>Bind the loopback listener and begin serving. Sets <see cref="Port"/>.</summary>
        public void Start()
        {
            try { Directory.CreateDirectory(_ipcDir); } catch { }
            _runner.ClearStale();

            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            _listener = new TcpListener(IPAddress.Loopback, 0); // OS picks a free port
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break; // Stop() unblocked the accept — shutting down
                    Log("accept loop error: " + ex);
                    continue;
                }
                // Fire-and-forget: each connection is one stateless HTTP exchange.
                // Requests may overlap; the _gate serializes the part that matters.
                _ = Task.Run(() => ServeConnectionAsync(client));
            }
        }

        /// <summary>
        /// Serve exactly one HTTP request/response on the connection, then close it.
        /// Every response carries Connection: close — the engine's client reconnects
        /// per request, which costs microseconds on loopback.
        /// ponytail: no keep-alive; loop the request parser here if it ever matters.
        /// </summary>
        private async Task ServeConnectionAsync(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    // ponytail: synchronous socket I/O on this threadpool task — the
                    // client count is ~1, this is not a web server. Sync reads are
                    // also what make ReadTimeout effective (async reads ignore it),
                    // and that timeout is what reaps half-open sockets.
                    stream.ReadTimeout = 15000;
                    stream.WriteTimeout = 15000;

                    // --- header block: read until \r\n\r\n (16 KB cap) ---
                    var buffered = new MemoryStream();
                    var chunk = new byte[8192];
                    int headerEnd;
                    while ((headerEnd = FindHeaderEnd(buffered)) < 0)
                    {
                        if (buffered.Length > 16 * 1024) { WriteResponse(stream, 400, null, null); return; }
                        int n = stream.Read(chunk, 0, chunk.Length);
                        if (n <= 0) return; // client closed mid-header
                        buffered.Write(chunk, 0, n);
                    }

                    var raw = buffered.GetBuffer();
                    var headerText = Encoding.UTF8.GetString(raw, 0, headerEnd);
                    var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

                    // --- request line: METHOD SP TARGET SP HTTP/1.1 ---
                    var reqLine = lines[0].Split(' ');
                    if (reqLine.Length < 3) { WriteResponse(stream, 400, null, null); return; }
                    var method = reqLine[0];
                    var path = reqLine[1];
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    // Only Content-Length and Authorization are consumed.
                    // ponytail: no chunked-transfer support — the engine's fetch always
                    // sends Content-Length for a string JSON body; chunked gets a 400.
                    string authHeader = null;
                    long contentLength = -1;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int c = lines[i].IndexOf(':');
                        if (c <= 0) continue;
                        var name = lines[i].Substring(0, c).Trim();
                        var value = lines[i].Substring(c + 1).Trim();
                        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authHeader = value;
                        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) long.TryParse(value, out contentLength);
                    }

                    // 405 is also the spec-legal answer to a GET probe for the optional
                    // SSE stream — this server is stateless and has none.
                    if (method != "POST") { WriteResponse(stream, 405, "Allow: POST", null); return; }
                    if (path != "/mcp") { WriteResponse(stream, 404, null, null); return; }

                    if (!IsAuthorized(authHeader)) { WriteResponse(stream, 401, null, null); return; }

                    if (contentLength < 0 || contentLength > 4 * 1024 * 1024) { WriteResponse(stream, 400, null, null); return; }

                    // --- body: bytes already buffered past the header, then the rest ---
                    var body = new byte[contentLength];
                    int have = (int)Math.Min(buffered.Length - headerEnd, contentLength);
                    Buffer.BlockCopy(raw, headerEnd, body, 0, have);
                    while (have < contentLength)
                    {
                        int n = stream.Read(body, have, (int)(contentLength - have));
                        if (n <= 0) return; // client closed mid-body
                        have += n;
                    }

                    // --- JSON-RPC ---
                    JObject msg;
                    try
                    {
                        var tok = JToken.Parse(Encoding.UTF8.GetString(body));
                        msg = tok as JObject;
                        if (msg == null)
                        {
                            // A JSON array would be a batch — removed from the MCP spec
                            // as of 2025-06-18; this server never accepted one.
                            WriteResponse(stream, 400, null, RpcError(null, -32600, "invalid request: expected a single JSON-RPC object"));
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteResponse(stream, 400, null, RpcError(null, -32700, "parse error: " + ex.Message));
                        return;
                    }

                    var reply = await HandleRpcAsync(msg).ConfigureAwait(false);
                    if (reply == null) WriteResponse(stream, 202, null, null); // notification — no body
                    else WriteResponse(stream, 200, null, Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None)));
                }
            }
            catch (Exception ex)
            {
                // Includes ReadTimeout expiry on a half-open socket: log and drop.
                Log("serve error: " + ex.Message);
            }
        }

        /// <summary>
        /// Handle one JSON-RPC message. Returns the response object, or null for a
        /// notification (no id — nothing to answer). Direct port of the deleted
        /// arcgis_bridge_mcp.py handlers, minus its socket plumbing.
        /// </summary>
        private async Task<JObject> HandleRpcAsync(JObject msg)
        {
            var id = msg["id"];
            bool isRequest = id != null && id.Type != JTokenType.Null;
            var method = (string)msg["method"];
            var p = msg["params"] as JObject ?? new JObject();

            // Notifications (notifications/initialized, notifications/cancelled, …)
            // are acknowledged and ignored. ponytail: a cancelled GP call runs to
            // completion — same behavior as the old single-threaded python loop.
            if (!isRequest) return null;

            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        // Echo the client's version only if we actually support it;
                        // otherwise answer with our latest supported version.
                        var proto = (string)p["protocolVersion"];
                        if (Array.IndexOf(ProtocolsSupported, proto) < 0) proto = ProtocolDefault;
                        return RpcResult(id, new JObject
                        {
                            ["protocolVersion"] = proto,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
                        });
                    }
                    case "tools/list":
                        return RpcResult(id, new JObject { ["tools"] = McpTools.Tools });
                    case "ping":
                        return RpcResult(id, new JObject());
                    case "tools/call":
                        return await HandleToolsCallAsync(id, p).ConfigureAwait(false);
                    default:
                        return JObjectRpcError(id, -32601, "method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return JObjectRpcError(id, -32603, "internal error: " + ex.Message);
            }
        }

        private async Task<JObject> HandleToolsCallAsync(JToken id, JObject p)
        {
            var name = (string)p["name"];
            var args = p["arguments"] as JObject ?? new JObject();
            if (name == null || !McpTools.Names.Contains(name))
                return JObjectRpcError(id, -32602, "unknown tool: " + name);

            JObject res;
            await _gate.WaitAsync().ConfigureAwait(false); // the serial-dispatch invariant
            try { res = await DispatchAsync(name, args, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { res = Err(ex.GetType().Name + ": " + ex.Message); }
            finally { _gate.Release(); }

            // Shape the {ok,error,data} envelope into an MCP tool result. run_python_*
            // return {stdout, result, error}: if the code raised, surface stdout + the
            // traceback with isError so the engine can fix its code and retry.
            string text;
            bool isError;
            bool ok = (bool?)res["ok"] ?? false;
            var data = res["data"];
            var traceback = data is JObject dataObj ? (string)dataObj["error"] : null;
            if (ok && !string.IsNullOrEmpty(traceback))
            {
                var stdout = (string)((JObject)data)["stdout"];
                text = (string.IsNullOrEmpty(stdout) ? "" : "stdout:\n" + stdout + "\n\n")
                     + "error:\n" + traceback;
                isError = true;
            }
            else if (ok)
            {
                text = data == null || data.Type == JTokenType.Null
                    ? "ok"
                    : data.ToString(Newtonsoft.Json.Formatting.Indented);
                isError = false;
            }
            else
            {
                text = (string)res["error"] ?? "error";
                isError = true;
            }

            return RpcResult(id, new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError,
            });
        }

        /// <summary>
        /// Read ops → fast .NET path; everything else (run_python_*, data mutators) →
        /// a fresh ArcPy tool. Returns the <c>{ok, error, data}</c> envelope.
        /// </summary>
        private async Task<JObject> DispatchAsync(string op, JObject args, CancellationToken ct)
        {
            if (AppStateOps.Handles(op))
            {
                var data = await AppStateOps.DispatchAsync(op, args).ConfigureAwait(false);
                return new JObject
                {
                    ["ok"] = true,
                    ["error"] = null,
                    ["data"] = data == null ? null : JToken.FromObject(data),
                };
            }
            return await _runner.RunAsync(op, args, ct).ConfigureAwait(false);
        }

        private bool IsAuthorized(string authHeader)
        {
            if (string.IsNullOrEmpty(authHeader) || Token == null) return false;
            var expected = Encoding.UTF8.GetBytes("Bearer " + Token);
            var got = Encoding.UTF8.GetBytes(authHeader);
            // FixedTimeEquals returns false on length mismatch without leaking timing.
            return CryptographicOperations.FixedTimeEquals(got, expected);
        }

        /// <summary>Index just past the \r\n\r\n header terminator, or -1 if not buffered yet.</summary>
        private static int FindHeaderEnd(MemoryStream buffered)
        {
            var b = buffered.GetBuffer();
            int len = (int)buffered.Length;
            for (int i = 3; i < len; i++)
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r')
                    return i + 1;
            return -1;
        }

        private static void WriteResponse(NetworkStream stream, int status, string extraHeader, byte[] body)
        {
            string reason;
            switch (status)
            {
                case 200: reason = "OK"; break;
                case 202: reason = "Accepted"; break;
                case 400: reason = "Bad Request"; break;
                case 401: reason = "Unauthorized"; break;
                case 404: reason = "Not Found"; break;
                case 405: reason = "Method Not Allowed"; break;
                default: reason = "Error"; break;
            }
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            if (extraHeader != null) sb.Append(extraHeader).Append("\r\n");
            if (body != null) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (body != null) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static JObject RpcResult(JToken id, JObject result)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JObject JObjectRpcError(JToken id, int code, string message)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };

        private static byte[] RpcError(JToken id, int code, string message)
            => Encoding.UTF8.GetBytes(JObjectRpcError(id, code, message).ToString(Newtonsoft.Json.Formatting.None));

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }   // unblocks a pending AcceptTcpClientAsync
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _cts.Dispose(); } catch { }
            // Per-connection tasks are not awaited: an in-flight ScriptRunner call
            // holds the cancelled token and exits; its socket write then fails. No
            // Pro-shutdown hang.
        }

        private static JObject Err(string message)
            => new JObject { ["ok"] = false, ["error"] = message, ["data"] = null };

        private void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(_ipcDir, "bridge.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [C#] " + msg + Environment.NewLine); }
            catch { }
        }
    }
}
