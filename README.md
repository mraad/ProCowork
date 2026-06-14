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

```
WPF chat panel  ──stdio stream-json──►  claude (headless engine, your subscription)
  (dock pane)                              │  built-in tools (Bash/Read/Write/Edit…) for disk work
                                           │  MCP tool calls for the LIVE project ▼
   arcgis_bridge (stdio MCP, stdlib Python) ──file IPC──►  pro_bridge.py
                                                            (runs INSIDE ArcGIS Pro's
                                                             in-process Python, where
                                                             arcpy "CURRENT" is live)
```

The key trick: `arcpy.mp.ArcGISProject("CURRENT")` only works **inside Pro's own Python**.
So a tiny daemon (`pro_bridge.py`) runs there and executes Claude's generated code against
the open project; the add-in and the MCP server talk to it through a temp-folder IPC
channel. The centerpiece tool is **`run_python_current(code)`** — arbitrary ArcPy on the
live map. Curated tools (`list_layers`, `add_field`, `search_cursor`, …) ride the same
bridge.

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
$msb = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"   # SDK resolver needs dotnet on PATH
& $msb .\src\ArcGISClaude\ArcGISClaude.csproj -t:Rebuild -c Release -p:Platform=x64
```

---

## First run

1. Make sure you've logged into Claude Code with your subscription once (`claude`, then
   `/login`).
2. In ArcGIS Pro, open a project, then go to the **Claude** ribbon tab.
3. Click **Start Bridge** (one time per Pro session). It tries to auto-start the
   in-process ArcPy bridge; if it can't, it copies a one-line bootstrap to your clipboard —
   paste it once into **Analysis ▸ Python**.
4. Click **Chat** to open the panel. Type a request and press **Enter**.

The status bar shows the model and that it's using your subscription. By default the
engine uses model `claude-opus-4-8`; change it on **Options ▸ Claude**.

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
- **Live tools say the bridge isn't running** — click **Start Bridge**, or paste the
  bootstrap into Analysis ▸ Python. Disk/Bash analysis still works without it.
- **It's billing my API account, not my subscription** — an `ANTHROPIC_API_KEY` is set
  somewhere with higher precedence. Use subscription mode (which strips it) and confirm via
  the status bar / **Options ▸ Claude ▸ Check**.
- **Edits don't appear** — some live-map writes from the bridge's background thread may not
  refresh immediately; re-running or a manual refresh helps. (A future C# `arcgis_live` MCP
  moves map/UI writes onto Pro's main thread.)

---

## Project layout

```
ArcGISClaude.sln
src/ArcGISClaude/
  Config.daml                 ribbon tab, dock pane, buttons, options page
  Module1.cs                  paths, bridge client, workspace seeding, settings load
  Engine/                     ClaudeCodeProcess, StreamJsonReader, AuthResolver, ClaudeLocator, EngineSettings
  Bridge/                     ProBridgeClient, BridgeBootstrap
  UI/                         ChatDockPane (View/VM), item view models, templates
  Options/                    AuthOptionsPage (View/VM), AuthSettingsStore (DPAPI)
  Python/                     pro_bridge.py, arcgis_bridge_mcp.py, ClaudeBridge.pyt
  Workspace/CLAUDE.md         "Claude's own file" (seeded to the user workspace)
  Images/                     ribbon icons (placeholders — replace with real art)
```

Runtime workspace (engine cwd, seeded on first run):
`%USERPROFILE%\Documents\ArcGIS\ClaudeWorkspace\` — holds `CLAUDE.md` and the generated
`.mcp.json`.

---

## Status

MVP. Implemented: add-in shell, headless engine + native rendering, subscription auth,
in-process ArcPy bridge + zero-dependency MCP server, the `run_python_current` centerpiece
plus curated tools, auto-start + paste fallback, and the options page. Not yet:
token-level streaming, the `arcgis_live` QueuedTask MCP for thread-safe map/UI writes, and
the optional approval gate.
