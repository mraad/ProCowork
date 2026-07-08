# Session Recap — 2026-07-08

Work done on branch `diagram` (uncommitted unless noted).

## 1. GeoCowork rename (merged)
- PR #4: rebranded user-facing add-in text from "Claude" to "GeoCowork" in
  `Config.daml` and `ArcGISClaude.csproj`. Squash-merged to `main`, branch deleted.

## 2. .gitignore (merged)
- Added `.idea/` (pushed directly to `main`). Directory was never tracked.

## 3. DIAGRAM.md (new)
- Mermaid architecture diagrams: component/process flowchart + one-tool-call
  sequence diagram. Updated in step 4 to the two-process architecture.

## 4. Simplification: dropped the Python MCP child process (3 processes → 2)

Review findings: **zero polling existed** (all waits event-driven); the one real
process reduction was the Python MCP server, which was pure transport glue
(MCP stdio ⇄ custom TCP protocol).

`BridgeService` now serves **MCP streamable-HTTP directly** — the engine
connects straight to it via `.mcp.json`
`{"type":"http", "url":"http://127.0.0.1:<port>/mcp", headers: Bearer <token>, timeout: 320000}`.

Changes:
- **Deleted** `src/ArcGISClaude/Python/arcgis_bridge_mcp.py` (~370 lines) plus
  the custom newline-JSON TCP protocol, token-first-line handshake, python
  reconnect/resend logic, new-connection-wins half-open-socket machinery, and
  `ResolveProPython`/`ProPythonExe`/`BridgeMcpScript` in `Module1`.
- **`Bridge/BridgeService.cs`** rewritten: raw `TcpListener` + minimal
  hand-rolled HTTP/1.1 (not `HttpListener` — HTTP.sys URL ACLs block non-admin
  binds of the `127.0.0.1` literal, and it can't bind port 0). Stateless
  `POST /mcp`, single JSON response, no SSE/session id. Bearer auth with
  constant-time compare. `SemaphoreSlim(1,1)` gate around `tools/call` carries
  the serial-dispatch / no-GP-re-entrancy invariant; `initialize`/`tools/list`/
  `ping` answer outside the gate. `DispatchAsync` read/write split unchanged.
- **New `Bridge/McpTools.cs`**: 14-tool catalog as one JSON literal — verified
  programmatically identical to the deleted python `TOOLS`.
- **`Engine/ClaudeCodeProcess.cs`**: sets `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT=600000`
  on the engine child (default 5-min idle abort left 10 s margin over the 290 s
  ScriptRunner bound; a queued second call can wait another ~290 s).
- `ScriptRunner` 290 s timeout unchanged (now just under the 320 s per-request
  timeout in `.mcp.json`).
- Docs updated: root `CLAUDE.md` (two processes, new bridge protocol section,
  python sanity check now `RunScript.pyt` only), `README.md`, `DIAGRAM.md`.
  `Workspace/CLAUDE.md` untouched (tool names/behavior identical).

Accepted behavior deltas:
- Pro closed → engine sees connection-refused instead of the python
  "bridge isn't responding" prose; engine's HTTP MCP client auto-reconnects
  with backoff.
- Python read-only-op resend logic gone — moot under HTTP request/response.
- Per-request TCP connect (`Connection: close`) replaces the cached socket —
  microseconds on loopback.

## 5. Work-in-progress indicator (UI)
- `UI/ChatDockPaneView.xaml`: thin 3 px strip under the status bar, pulses
  opacity 0.2↔1.0 every 1.2 s while `IsTurnActive` (existing VM property, no
  C# changes). Opacity animation, not color — Esri theme brushes are shared
  `DynamicResource` objects whose colors can't be animated, and opacity follows
  dark/light themes for free. Uses `Esri_BackgroundSelectedBrush` (already
  proven in this codebase). `StopStoryboard` snaps it hidden when the turn ends.

## Pending verification (Windows, VS + ArcGIS Pro, non-admin)
1. Build Release|x64; `.esriAddinX` has `Install/Python/RunScript.pyt`, no
   `arcgis_bridge_mcp.py`.
2. `.mcp.json` has the new http shape after Pro start.
3. curl the live port: GET → 405; `initialize` + Bearer → 200 echoed version;
   wrong token → 401; `notifications/initialized` → 202; `tools/list` → 14
   tools; `tools/call ping` → ok; `run_python_current` with a raise →
   `isError:true` with stdout + traceback; unknown method → -32601; garbage → 400.
4. Chat panel: `mcp__arcgis_bridge__*` tools listed; layer list works; data
   edit shows live; >60 s GP call completes; two tool calls in one turn
   serialize (check `bridge.log` timing).
5. Task Manager: no `python.exe` under Pro/claude.
6. Pro close: no hang, port dead. Kill claude mid-session: respawn works.
7. Pulse strip: shows while a turn runs, stops on end/Stop, follows theme.
