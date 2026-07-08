# Architecture Diagrams

Companion to `CLAUDE.md`. Two views: static components/processes, and the
runtime sequence of one live-project tool call.

## Components & processes

```mermaid
flowchart TB
    subgraph ProProcess["ArcGIS Pro process (in-process add-in)"]
        Module1["Module1<br/>(autoLoad, owns BridgeService)"]
        DockPane["ChatDockPaneViewModel<br/>(owns ClaudeCodeProcess;<br/>IsTurnActive → marquee progress bar)"]
        BridgeService["BridgeService<br/>MCP streamable-HTTP server<br/>loopback, ephemeral port, Bearer auth"]
        McpTools["McpTools<br/>(tool catalog, JSON literal)"]
        AppStateOps["AppStateOps<br/>.NET SDK, CIM thread"]
        ScriptRunner["ScriptRunner<br/>req_*.json / out_*.json handoff"]
        RunScriptPyt["RunScript.pyt\RunScript<br/>fresh GP tool per call, GPThread"]
        HtmlPresenter["MarkdownToHtml + HtmlPresenter<br/>themed WPF rendering"]
        LiveMap["Live ArcGIS Pro project / map"]
    end

    subgraph EngineProcess["Claude Code engine (child process)"]
        ClaudeCodeProcess["claude -p --output-format stream-json<br/>--input-format stream-json"]
        StreamJsonReader["StreamJsonReader"]
    end

    User(["User in chat panel"])

    Module1 -- "starts first, learns port" --> BridgeService
    Module1 -- "seeds workspace + .mcp.json<br/>(type: http, url, Bearer token)" --> EngineProcess
    DockPane -- "spawns / respawns (EnsureEngine)" --> ClaudeCodeProcess
    User -- "turn (stream-json on stdin)" --> DockPane --> ClaudeCodeProcess
    ClaudeCodeProcess --> StreamJsonReader --> DockPane
    DockPane -- "HandleEvent" --> HtmlPresenter --> User

    ClaudeCodeProcess -- "POST http://127.0.0.1:port/mcp<br/>JSON-RPC 2.0, stateless" --> BridgeService
    BridgeService -- "tools/list" --> McpTools

    BridgeService -- "reads: list_layers, describe_layer,<br/>feature_count, select_by_attribute,<br/>zoom_to_layer, ping" --> AppStateOps
    BridgeService -- "writes (serialized by gate):<br/>run_python_*, add_field, calc_field,<br/>update_field, search_cursor,<br/>run_geoprocessing" --> ScriptRunner
    ScriptRunner --> RunScriptPyt
    AppStateOps <--> LiveMap
    RunScriptPyt <--> LiveMap
    RunScriptPyt -- "out_*.json (290s timeout)" --> ScriptRunner --> BridgeService --> ClaudeCodeProcess
```

## One live-project tool call (sequence)

```mermaid
sequenceDiagram
    actor User
    participant DockPane as ChatDockPaneViewModel
    participant Engine as ClaudeCodeProcess (child)
    participant Bridge as BridgeService (MCP over loopback HTTP)
    participant State as AppStateOps (CIM thread)
    participant Runner as ScriptRunner
    participant GP as RunScript.pyt (fresh GP tool)
    participant Map as Live ArcGIS Pro project

    User->>DockPane: chat message
    Note over DockPane: IsTurnActive = true —\n marquee progress bar shows
    DockPane->>Engine: stream-json turn (stdin)
    Engine->>Bridge: POST /mcp tools/call\n (JSON-RPC, Bearer token)
    Note over Bridge: SemaphoreSlim gate —\n one tool call at a time

    alt read op (list_layers, describe_layer, feature_count, ...)
        Bridge->>State: dispatch on CIM thread
        State->>Map: query via .NET SDK
        Map-->>State: result
        State-->>Bridge: result
    else write op (run_python_*, add_field, search_cursor, ...)
        Bridge->>Runner: dispatch
        Runner->>Runner: write req_*.json
        Runner->>GP: ExecuteToolAsync (GPThread, kept out of GP history)
        GP->>Map: ArcPy on data-source path
        Map-->>GP: edits applied (live)
        GP->>Runner: write out_*.json
        Runner-->>Bridge: result (<=290s)
    end

    Bridge-->>Engine: 200 application/json\n {content, isError}
    Engine-->>DockPane: assistant/tool-result events (stdout, stream-json)
    DockPane->>DockPane: HandleEvent -> MarkdownToHtml -> HtmlPresenter
    DockPane-->>User: rendered chat + tool-call card
    Note over DockPane: result event — IsTurnActive = false,\n progress bar hides
```

## Notes

- Serial dispatch: a `SemaphoreSlim(1,1)` gate around `tools/call` guarantees one tool call — and therefore one GP run — at a time. `initialize`/`tools/list`/`ping` answer outside the gate.
- The bridge is stateless: each request is one `POST /mcp` + one JSON response, no SSE stream, no session id. The engine's HTTP MCP client reconnects with backoff on its own.
- The port is ephemeral (OS-chosen) and written with the Bearer token into `.mcp.json` every session.
- `RunScript.pyt` runs as a **fresh** foreground GP tool per write call — no long-lived ArcPy daemon, avoiding the staleness of `arcpy.mp.ArcGISProject("CURRENT")`.
- Work-in-progress: a thin indeterminate `ProgressBar` (marquee) under the status bar, bound to `IsTurnActive` — set on send, cleared on the `result` event, Stop, or engine exit.
