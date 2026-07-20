# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

An ArcGIS Pro **add-in** ("GeoCowork in ArcGIS Pro") that embeds the Claude Code engine as a dockable WPF chat panel. Claude writes Python/ArcPy and runs it on the user's live, open project. Windows + ArcGIS Pro only. See `README.md` for the user-facing overview and the full architecture diagram; this file is the developer orientation.

## Build

There is no test suite. This is a Windows/ArcGIS Pro SDK project — it does not build on macOS/Linux, and it does not run outside ArcGIS Pro.

- **Must use full-framework MSBuild, not `dotnet build`.** `dotnet build` compiles but cannot run the Esri `.esriAddinX` packaging task (it uses `CodeTaskFactory`, unsupported by .NET-Core MSBuild), so no add-in is produced. Always **Release, x64**.
- Requires: ArcGIS Pro SDK for .NET, Visual Studio 2022+, .NET 10 SDK, with `dotnet` on PATH (the SDK resolver needs it).
- Build in VS (Release|x64) — Esri targets produce `src\ArcGISClaude\bin\x64\Release\ArcGISClaude.esriAddinX`, auto-register it with Pro, and the `DeployAddinToProFolder` target copies it to `Documents\ArcGIS\AddIns\ArcGISPro\` (handles OneDrive/Parallels Documents redirection).
- Command line (note: full-framework MSBuild rejects `dotnet`-style `-c` — use
  `-p:Configuration=`; `-restore` picks up new PackageReferences):
  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    .\src\ArcGISClaude\ArcGISClaude.csproj -restore -t:Rebuild -p:Configuration=Release -p:Platform=x64
  ```
- Quick sanity check for the Python toolbox (no ArcPy needed):
  ```bash
  python -c "import ast; ast.parse(open('RunScript.pyt').read()); print('parses OK')"
  ```

### Targeting a different Pro version
`ArcGISClaude.csproj` targets **Pro 3.7** via `net10.0-windows` + `Esri.ArcGISPro.Extensions30` `3.7.*`. For Pro 3.6 use `net8.0-windows` + `3.6.*`; for 3.5 and earlier, `net6.0-windows`. (`Config.daml`'s `desktopVersion="3.6"` is the *minimum* and stays put.)

### Dependency rule
All ArcGIS Pro assemblies and Newtonsoft.Json (pinned to Pro's `13.0.3`) use `<ExcludeAssets>runtime</ExcludeAssets>` — Pro provides them at runtime; shipping our own copies causes assembly-load/binding conflicts. Keep this on any new PackageReference that Pro already supplies.

The flip side, for packages Pro does **not** ship (e.g. Markdig): the csproj sets `CopyLocalLockFileAssemblies=true` because .NET-SDK class libraries don't copy NuGet runtime assemblies to the output by default (they're resolved from the NuGet cache via `deps.json`) — but Pro loads the add-in from its deployed folder, where no such resolution exists. Without it the build "succeeds" and the add-in crashes at runtime. When adding such a package, verify its DLL lands inside the `.esriAddinX` (`Install/<name>.dll`).

## Architecture: two processes, one loopback bridge

The system spans two processes, both local:

1. **The add-in (in-process, inside ArcGIS Pro)** — WPF panel + the C# that touches the live project, including `BridgeService`, an MCP streamable-HTTP server the engine talks to directly.
2. **The Claude Code engine** — a headless `claude` child process, one per chat session.

Data flow of one live-project tool call:
`chat panel → engine (child) → HTTP POST /mcp → BridgeService (in-process) → AppStateOps (reads) or ScriptRunner→RunScript.pyt (writes) → live map → results stream back up`.

**Why the bridge exists (the core constraint):** `arcpy.mp.ArcGISProject("CURRENT")` only resolves inside Pro's own Python on the foreground GP thread. Rather than keep a long-lived Python daemon there (which goes stale), a persistent **C# bridge** owns the session and, per request, either answers instantly from the .NET SDK (reads) or stands up **one fresh ArcPy geoprocessing tool** (writes) — no daemon to outlive its host.

### Ownership & lifecycle (who owns what)
- `Module1` (the add-in Module, `autoLoad="true"`) owns **`BridgeService`** for the whole Pro session. It starts the bridge *first* (to learn the loopback port), then seeds the workspace + `.mcp.json`. The bridge is fully automatic — no UI to start/stop it.
- `ChatDockPaneViewModel` owns the **Claude engine** (`ClaudeCodeProcess`), one per chat session, and respawns it (`EnsureEngine`) if it dies. Detaches events + disposes the old one to avoid handle/subscription leaks.

### The bridge protocol (`Bridge/BridgeService.cs`)
- MCP **streamable HTTP** served in-process: stateless `POST /mcp` with a JSON-RPC 2.0 body, answered with a single `application/json` response (no SSE, no session id, no python child). Tool catalog lives in `Bridge/McpTools.cs` as one JSON literal.
- Loopback only, **ephemeral port** (OS-chosen at startup). Raw `TcpListener` + minimal hand-rolled HTTP/1.1, deliberately *not* `HttpListener` — HTTP.sys URL ACLs won't let a non-admin process bind the `127.0.0.1` literal, and `HttpListener` can't bind port 0.
- URL + Bearer token are written into the generated `.mcp.json` (`"type": "http"`), rewritten each session (because the port changes).
- **Serial dispatch** — a `SemaphoreSlim(1,1)` gate around `tools/call` dispatch guarantees one tool call at a time, no GP re-entrancy. `initialize`/`tools/list`/`ping` answer outside the gate so they never block behind a long GP run.

### Read path vs write path (`DispatchAsync`)
- **Reads** (`list_layers`, `get_field_list`, `describe_layer`, `feature_count`, `select_by_attribute`, `zoom_to_layer`, `ping`) → `AppStateOps` on Pro's CIM thread via the .NET SDK. **No ArcPy, no `"CURRENT"`** — fast and works even with no project open.
- **Everything else** (`run_python_*`, `add_field`, `calc_field`, `update_field`, `search_cursor`, `run_geoprocessing`) → `ScriptRunner` runs `RunScript.pyt\RunScript` as a **fresh foreground GP tool per call** (`GPExecuteToolFlags.GPThread`, kept out of the user's GP history). Request JSON is the tool's input `GPString`; result JSON is a derived `GPString` read from `IGPResult.ReturnValue`, so this path uses no file polling, watcher, or IPC spool. 290 s timeout (just under the 320 s per-request timeout in `.mcp.json`).
- Data edits should operate on the layer's **data-source path** (from `list_layers`' `source`), not layer objects — path-based ArcPy is robust when `aprx`/`m` are `None`, and edits still show live.

### Engine wiring (`Engine/`)
- `ClaudeCodeProcess` spawns `claude -p --output-format stream-json --input-format stream-json --verbose --mcp-config <path>` + `--permission-mode` + `--model`. stdin = user turns as stream-json; stdout = one JSON event per line. `StreamJsonReader` parses events; `ChatDockPaneViewModel.HandleEvent` maps `system/init`, `assistant`, `user` (tool results), `result` to view models.
- `AuthResolver.Apply` shapes the child env by `AuthMode`. **In Subscription mode it strips `ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN`** so the subscription login isn't silently overridden into API billing. `EngineSettings` defaults: model `claude-opus-4-8`, `PermissionMode = "bypassPermissions"` (auto-run, no approval prompt). To reintroduce an approval gate, change `PermissionMode` to `acceptEdits`/`default` — the design supports it without rework.
- Auth secrets are stored DPAPI-encrypted (`Options/AuthSettingsStore`).

### Rendering (`UI/`)
Engine emits Markdown → `MarkdownToHtml` (Markdig: pipe tables, autolinks, strikethrough — deliberately *not* `UseAdvancedExtensions()`, so it never emits tags the presenter can't render) → `HtmlPresenter` renders the HTML subset as themed native WPF (headings, lists incl. nested, tables with shaded header row, blockquote, hr, links, code). Two hard-won invariants:
- `MarkdownToHtml.Convert` first **unwraps ` ```markdown `/` ```md ` fences** — the engine sometimes wraps its answer (tables especially) in one, which would otherwise render as a raw-pipe code block. Real code fences pass through.
- `HtmlPresenter` must tolerate malformed/half-streamed HTML (it re-renders on every streaming update), and any tag Markdig can emit needs a parser case — if you add a Markdig extension, extend the presenter to match.

Tool-call cards (generated code + results) are toggled by `ShowToolOutputs`. A thin indeterminate `ProgressBar` under the status bar shows while `IsTurnActive` (pure XAML in `ChatDockPaneView.xaml`; both `IsIndeterminate` and `Visibility` are bound so the marquee storyboard doesn't run while hidden, and its `Foreground` is an Esri theme brush so it follows both themes).

## Filesystem locations (resolved in `Module1.AppPaths`)
- **Workspace** (engine cwd): `Documents\ArcGIS\ClaudeWorkspace\` — holds the seeded `CLAUDE.md` and the generated `.mcp.json`. Uses `MyDocuments` (not `%USERPROFILE%\Documents`) so redirected Documents (OneDrive/Parallels) resolves.
- **Bridge diagnostics**: `%USERPROFILE%\.arcgis_claude\bridge.log`.

## Two different CLAUDE.md files — don't confuse them
- **This file** (`/CLAUDE.md`) — guidance for developing this repo.
- **`src/ArcGISClaude/Workspace/CLAUDE.md`** — a **shipped runtime artifact**: the system-prompt-level instructions for the *embedded* Claude that drives the live map (how to use the `arcgis_bridge` tools, ArcPy recipes, safety rules, output formatting). Edit it to change the embedded assistant's behavior, not to document the build. The template is **authoritative**: `Module1.EnsureWorkspace` re-seeds the workspace copy whenever it differs, so edits made directly to `Documents\ArcGIS\ClaudeWorkspace\CLAUDE.md` are overwritten on the next Pro start — customize here, in the repo.

## Conventions
- C# in `src/ArcGISClaude/`, grouped by concern: `Engine/`, `Bridge/`, `UI/`, `Options/`, `Python/`, `Workspace/`. `Nullable`/`ImplicitUsings` are **disabled**; explicit `using`s, `internal sealed` classes, doc-comments explaining *why*.
- DAML (`Config.daml`) declares the ribbon tab, `Chat` button, dock pane, and options page; its `className` attributes bind to the C# classes by name.
- `ponytail:` comments mark deliberate simplifications and name the upgrade path — respect them; don't "fix" them without cause.
