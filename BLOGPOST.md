# ArcGIS Pro, Claude Code and a Loopback Bridge

Continuing the GenAI-with-a-GeoSpatial-twist thread I started a while back. Back
then, I had an LLM *reason* about geospatial logic. This time, I wanted it to
actually *do* the work — on my open project, in my running
[ArcGIS Pro](https://www.esri.com/en-us/arcgis/products/arcgis-pro/overview)
session, while I watch.

The result is [ProCowork](https://github.com/mraad/ProCowork), a native Pro
add-in that embeds the [Claude Code](https://www.claude.com/product/claude-code)
engine as a dockable chat panel. You type a prompt, Claude writes Python/ArcPy,
and the code runs against your live map. Add a field, calculate it, run a
geoprocessing tool, select and zoom — the generated code and its output stream
back into the panel, and the changes show up in the project right away.

A few things I asked it, verbatim:

- "List the layers in the current map."
- "Add a DOUBLE field POP_DEN to Parcels and set it to POP / AREASQMI."
- "Select parcels where POP_DEN > 5000 and zoom to them."
- "Buffer Roads by 100 meters and add the result to the map."

## The interesting part is not the chat panel

It is the plumbing. `arcpy.mp.ArcGISProject("CURRENT")` only resolves inside
Pro's own Python, on the foreground geoprocessing thread. An external process
cannot reach the live project, and the Claude engine is very much an external
process — a headless `claude` child.

My first pass kept a long-lived Python daemon parked on that thread. It worked,
and it also went stale in all the ways a long-lived daemon does. So I flipped it
around. A persistent **C# bridge** now lives in-process inside Pro for the whole
session, and per request it either answers instantly from the .NET SDK or stands
up **one fresh ArcPy geoprocessing tool** that resolves `CURRENT`, runs Claude's
code, and goes away. No daemon to outlive its host.

```
Chat panel (WPF, in-process)
   -> Claude Code engine (child process, your login)
        -> MCP over HTTP POST 127.0.0.1:<ephemeral port>/mcp
             -> BridgeService (C#, in-process)
                  reads  -> .NET SDK on Pro's CIM thread   (no arcpy, no "CURRENT")
                  writes -> one fresh GP tool per call     (RunScript.pyt)
                       -> LIVE project and map
```

The bridge speaks [MCP](https://modelcontextprotocol.io/) over loopback HTTP, on
an ephemeral port picked at startup and handed to the engine in a generated
`.mcp.json`. Reads (`list_layers`, `describe_layer`, `feature_count`, …) never
touch ArcPy at all — they come straight off the CIM thread, so they answer even
with no project open. Everything that writes goes through the geoprocessing
path, one call at a time behind a semaphore, because geoprocessing does not
appreciate re-entrancy.

It uses *your* Claude Code login — subscription, OAuth token, or an API key you
paste into the options page. There is no key baked into the add-in.

## A word of caution

By default the thing runs in YOLO mode: generated code executes on your open
project with no approval prompt. That is the whole point — it is also exactly as
dangerous as it sounds. Keep backups. The bundled instructions tell Claude to
use edit sessions and to state row counts before it deletes anything, but I
would not bet a day of unsaved work on good intentions. The architecture
supports an approval gate — flip `PermissionMode` from `bypassPermissions` to
`acceptEdits` — and I will probably wire a proper approval card into the UI next.

Windows and ArcGIS Pro only, and very much experimental. Not an Esri product,
not supported or endorsed by Esri, use at your own risk.

As usual, you can check out the source code
[here](https://github.com/mraad/ProCowork).

More to come :-)
