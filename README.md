# Claude in ArcGIS Pro

A native ArcGIS Pro add-in that embeds **Claude as a dockable chat panel**. You type a
prompt; Claude **writes Python/ArcPy and runs it on your live, open project** — adding
fields, editing data, running geoprocessing, opening cursors, manipulating the map — and
streams the code and results back into the panel.

It runs the **Claude Code engine** headless behind a custom WPF panel, so it uses **your
Claude Code login (Pro/Max subscription, or an API key)** — no hard-coded key.

> ⚠️ **Auto-run.** By design, generated code executes immediately on your open project
> (no approval prompt). The code and its results are always shown. Keep unsaved work
> backed up; see [Safety](#safety).

---

## How it works

Everything runs on your machine. The parts that touch the live project are **in-process**
inside ArcGIS Pro (the add-in's .NET code); the Claude engine and the MCP server are local
**child processes** that reach into Pro over a loopback socket.

```
You type a prompt in the Claude dock pane   (WPF, in-process)
        │
        ▼
Claude Code engine   ── headless `claude`, your subscription / API key   (child process)
        │
        ├─ built-in tools (Bash / Read / Write / Edit) ───────────►  files on disk
        │
        └─ MCP tool call   (run_python_current, list_layers, …)
               │   stdio JSON-RPC
               ▼
        arcgis_bridge_mcp.py   ── zero-dependency stdlib MCP server   (child process)
               │   newline-delimited JSON over 127.0.0.1:<port>   (loopback TCP)
               ▼
        BridgeService (C#)   ── persistent, owned by Module1, lives the whole Pro session
               │                 (in-process — same process as ArcGIS Pro)
               │
               ├─ read ops ───────►  AppStateOps   (.NET SDK on Pro's CIM thread; no arcpy,
               │                      no "CURRENT")  ·  list_layers, get_field_list,
               │                      describe_layer, feature_count, select_by_attribute,
               │                      zoom_to_layer, ping
               │
               └─ run_python_* / data writes ──►  ScriptRunner ──►  RunScript.pyt
                                      one fresh foreground geoprocessing tool per call;
                                      resolves arcpy.mp.ArcGISProject("CURRENT") best-effort
               │
               ▼
        LIVE project & map   ──  results stream back up the same path to the panel
```

The key constraint: `arcpy.mp.ArcGISProject("CURRENT")` only resolves **inside Pro's own
Python on the foreground thread**. The old design kept a long-lived Python daemon there,
which could go stale. This design flips it: a persistent **C# bridge** owns the session and,
per request, either answers instantly from the .NET SDK (reads) or stands up **one fresh
ArcPy geoprocessing tool** (`RunScript.pyt`) that resolves `CURRENT` and runs Claude's
code — so there's no daemon to outlive its host. The centerpiece tool is
**`run_python_current(code)`** (arbitrary ArcPy on the live map); curated tools
(`list_layers`, `add_field`, `search_cursor`, …) ride the same bridge.

The bridge's loopback port is ephemeral (chosen at startup) and handed to the MCP server via
`ARCGIS_CLAUDE_PORT` in the generated `.mcp.json`. It starts **automatically** with the
add-in — there's no button to press and nothing to keep alive.

---

## Prerequisites

- **Windows** + **ArcGIS Pro 3.7** (this project targets `net10.0-windows`, which Pro 3.7
  runs on). For **Pro 3.6** use `net8.0-windows` + `Esri.ArcGISPro.Extensions30` `3.6.*`;
  for 3.5 and earlier, `net6.0-windows` — edit `src/ArcGISClaude/ArcGISClaude.csproj`.
- **ArcGIS Pro SDK for .NET** + **Visual Studio 2022 (or newer)** + the **.NET 10 SDK**.
- **Claude Code** installed and **logged in with your Pro/Max subscription**
  (`claude` then `/login`). No Anthropic API key required.
- A project with some layers for testing.

> Verified build: Pro **3.7.0**, .NET **10.0.301** SDK, VS 18 MSBuild.

---

## Build & install

1. Open `ArcGISClaude.sln` in Visual Studio (with the ArcGIS Pro SDK installed).
2. **Build** (Release, x64). The Esri build targets produce
   `src\ArcGISClaude\bin\x64\Release\ArcGISClaude.esriAddinX` and **auto-register it with
   Pro** (via `RegisterAddIn.exe`), so launching/debugging Pro picks it up. You can also
   double-click the `.esriAddinX` to install it.

Command-line build (note: must use **full-framework MSBuild**, not `dotnet build`):

```powershell
# dotnet build COMPILES but cannot run the Esri packaging task (it uses CodeTaskFactory,
# unsupported by the .NET-Core MSBuild), so it won't produce the .esriAddinX.
# Note: full MSBuild rejects dotnet-style -c; use -p:Configuration=.
$msb = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"   # SDK resolver needs dotnet on PATH
& $msb .\src\ArcGISClaude\ArcGISClaude.csproj -restore -t:Rebuild -p:Configuration=Release -p:Platform=x64
```

---

## First run

1. Make sure you've logged into Claude Code with your subscription once (`claude`, then
   `/login`).
2. In ArcGIS Pro, open a project, then go to the **Claude** ribbon tab.
3. Click **Chat** to open the panel. Type a request and press **Enter**. The live-project
   bridge is already running — it starts automatically with the add-in, so there's no
   "Start Bridge" step.

The status bar shows the model and that it's using your subscription. By default the engine
uses model `claude-opus-4-8`; pick a different one from the **Model** dropdown on
**Options ▸ Claude** (or leave it blank to use Claude Code's own default).

### Try it
- "List the layers in the current map."
- "Add a DOUBLE field POP_DEN to Parcels and set it to POP / AREASQMI."
- "Select parcels where POP_DEN > 5000 and zoom to them."
- "Give me the 5 highest POP_DEN parcels."
- "Buffer Roads by 100 meters and add the result to the map."

You'll see Claude's generated ArcPy and its output inline, and the changes appear live in
the project.

---

## Authentication

Configure under **Options ▸ Claude**:

- **Claude subscription (Pro/Max login)** — *default, recommended.* Uses your stored
  Claude Code credentials. The add-in deliberately removes any `ANTHROPIC_API_KEY` from the
  engine's environment so it doesn't override your subscription.
- **OAuth token** — paste a token from `claude setup-token` (stored DPAPI-encrypted).
- **API key** — paste an `ANTHROPIC_API_KEY` (per-token billing; DPAPI-encrypted).

> Subscription-funded headless usage draws on the monthly **Agent SDK credit** (separate
> from interactive limits). Distributing this add-in for *other* users to run on *their*
> subscriptions isn't permitted by Anthropic's terms — use API keys / Console / Team auth
> for that.

---

## Safety

Generated code runs automatically and some edits are irreversible. The bundled `CLAUDE.md`
instructs Claude to use **edit sessions** (so changes are undoable), **back up** layers
before destructive ops, and **state row counts** before deletes. To reintroduce an
approval step, change `PermissionMode` in `EngineSettings` from `bypassPermissions` to
`acceptEdits` (and add an allow-list) — the architecture supports a future approval card
without rework.

---

## Troubleshooting

- **"claude not found"** — install Claude Code and log in; or set the path on
  **Options ▸ Claude**.
- **Live tools say the bridge isn't responding** — the bridge starts automatically with the
  add-in, so this usually means Pro is still loading or the add-in didn't load. Confirm the
  **Claude** ribbon tab is present, then retry; disk/Bash analysis works regardless. Check
  `%USERPROFILE%\.arcgis_claude\bridge.log` if it persists.
- **It's billing my API account, not my subscription** — an `ANTHROPIC_API_KEY` is set
  somewhere with higher precedence. Use subscription mode (which strips it) and confirm via
  the status bar / **Options ▸ Claude ▸ Check**.
- **Edits don't appear** — reads and map/selection writes run on Pro's CIM thread and ArcPy
  runs on the foreground GP thread, so edits generally show up live; if a data write doesn't
  refresh, re-run the request or refresh the layer.

---

## Project layout

```
ArcGISClaude.sln
src/ArcGISClaude/
  Config.daml                 ribbon tab, dock pane, Chat button, options page
  Module1.cs                  paths, owns the BridgeService, workspace + .mcp.json seeding, settings load
  Engine/                     ClaudeCodeProcess, StreamJsonReader, AuthResolver, ClaudeLocator, EngineSettings
  Bridge/                     BridgeService (loopback server), AppStateOps (.NET read path), ScriptRunner (per-call ArcPy tool)
  UI/                         ChatDockPane (View/VM), item view models, templates
  Options/                    AuthOptionsPage (View/VM), AuthSettingsStore (DPAPI)
  Python/                     arcgis_bridge_mcp.py (stdlib MCP server), RunScript.pyt (per-call ArcPy executor)
  Workspace/CLAUDE.md         "Claude's own file" — the embedded assistant's instructions
                              (authoritative; re-seeded to the user workspace when it changes)
  Images/                     ribbon icons (placeholders — replace with real art)
```

Runtime workspace (engine cwd): `%USERPROFILE%\Documents\ArcGIS\ClaudeWorkspace\` — holds
`CLAUDE.md` (re-seeded from the shipped template whenever the template changes; edit the
repo copy, not this one) and the generated `.mcp.json`. The bridge's request/result handoff
files live under `%USERPROFILE%\.arcgis_claude\` (alongside `bridge.log`).

---

## Status

MVP. Implemented: add-in shell, headless engine + native Markdown rendering (Markdig →
themed WPF: headings, lists, links, code blocks, and real tables — matching Pro's dark and
light themes), subscription auth, the persistent **C# execution bridge** (loopback MCP
transport, .NET fast-path reads + a per-call ArcPy tool for writes — no long-lived Python
daemon), the `run_python_current` centerpiece plus curated tools, and the options page
(auth + model dropdown). Not yet: token-level streaming and the optional approval gate.
