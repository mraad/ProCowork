using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The MCP tool catalog served by <see cref="BridgeService"/> via tools/list.
    ///
    /// This is a verbatim port of the TOOLS array from the deleted
    /// arcgis_bridge_mcp.py (the former stdio MCP child process). It is kept as
    /// one JSON literal — not hand-built JObjects — so it stays eyeball-diffable
    /// against the tool-name references in Workspace/CLAUDE.md and the git
    /// history of the Python original. Names, descriptions, and inputSchemas
    /// must not drift: the embedded assistant's instructions depend on them.
    /// </summary>
    internal static class McpTools
    {
        public static readonly JArray Tools = JArray.Parse(Json);

        public static readonly HashSet<string> Names = BuildNames();

        private static HashSet<string> BuildNames()
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var t in Tools) names.Add((string)t["name"]);
            return names;
        }

        private const string Json = """
[
  {
    "name": "run_python_current",
    "description": "PRIMARY TOOL. Execute Python/ArcPy in-process against the open project. Injected: `arcpy`; `aprx` (project or None); `m` (active map or None) — guard both before use; `proj()` / `active_map()` raise if no project. Do not call ArcGISProject('CURRENT'). Assign JSON-serializable data to `result`; print() is captured. Use for anything the curated tools don't cover. Inspect schema with list_layers/get_field_list first.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "code": { "type": "string", "description": "Python source to exec." }
      },
      "required": ["code"],
      "additionalProperties": false
    }
  },
  {
    "name": "run_python_file",
    "description": "Run a .py file (e.g. one you wrote into the workspace) the same way as run_python_current (injected arcpy/aprx/m; aprx and m may be None).",
    "inputSchema": {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Absolute path to the .py file." }
      },
      "required": ["path"],
      "additionalProperties": false
    }
  },
  {
    "name": "list_layers",
    "description": "List layers and standalone tables in the active (or named) map of the CURRENT project, with type/visibility/geometry and each feature layer/table's on-disk source path. Feature counts are omitted by default (they can be slow on large sources); pass include_counts=true to include them, or use feature_count for one layer/table.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "include_counts": { "type": "boolean", "description": "Include each feature layer/table row count (slower). Default false." }
      },
      "additionalProperties": false
    }
  },
  {
    "name": "get_field_list",
    "description": "List fields (name/type/alias/length) of a layer or table in the CURRENT project.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "describe_layer",
    "description": "Describe a layer: data type, geometry/shape type, spatial reference, catalog path, feature count.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string" }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "feature_count",
    "description": "Return the feature/row count of a layer or table.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "search_cursor",
    "description": "Read rows from a layer/table with an arcpy.da.SearchCursor. Returns the requested fields for matching rows.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "fields": { "type": "array", "items": { "type": "string" }, "description": "Field names, e.g. ['OID@','POP']. Tokens like SHAPE@XY allowed." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map when layer is a name." },
        "where": { "type": "string", "description": "Optional SQL where-clause." },
        "limit": { "type": "integer", "description": "Optional max rows; defaults to the app's configured search row limit (Options page, 10000 unless changed)." }
      },
      "required": ["layer", "fields"],
      "additionalProperties": false
    }
  },
  {
    "name": "select_by_attribute",
    "description": "Select features on a LIVE map layer by SQL where-clause (highlights them in the open map).",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "where": { "type": "string" },
        "map": { "type": "string" },
        "selection_type": { "type": "string", "enum": ["NEW_SELECTION", "ADD_TO_SELECTION", "REMOVE_FROM_SELECTION", "SUBSET_SELECTION"] }
      },
      "required": ["layer", "where"],
      "additionalProperties": false
    }
  },
  {
    "name": "zoom_to_layer",
    "description": "Zoom the active map view to a layer's extent.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string" }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "add_field",
    "description": "Add a field to a layer/table in the CURRENT project (modifies schema).",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map when layer is a name." },
        "field_name": { "type": "string" },
        "field_type": { "type": "string", "enum": ["TEXT", "FLOAT", "DOUBLE", "SHORT", "LONG", "DATE", "BLOB", "GUID"] },
        "field_length": { "type": "integer" },
        "field_alias": { "type": "string" }
      },
      "required": ["layer", "field_name", "field_type"],
      "additionalProperties": false
    }
  },
  {
    "name": "calc_field",
    "description": "Calculate a field with an arcpy expression (modifies values).",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map when layer is a name." },
        "field": { "type": "string" },
        "expression": { "type": "string" },
        "expression_type": { "type": "string", "enum": ["PYTHON3", "ARCADE", "SQL"] }
      },
      "required": ["layer", "field", "expression"],
      "additionalProperties": false
    }
  },
  {
    "name": "update_field",
    "description": "Set a single field to a constant value for matching rows via an UpdateCursor (modifies values).",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string" },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map when layer is a name." },
        "field": { "type": "string" },
        "value": {},
        "where": { "type": "string" }
      },
      "required": ["layer", "field", "value"],
      "additionalProperties": false
    }
  },
  {
    "name": "run_geoprocessing",
    "description": "Run any geoprocessing tool by name (e.g. 'analysis.Buffer') against the live data. Returns GP messages + outputs.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "tool": { "type": "string", "description": "e.g. 'analysis.Buffer' or 'management.AddField'." },
        "params": { "description": "Positional list OR keyword object of parameters." }
      },
      "required": ["tool"],
      "additionalProperties": false
    }
  },
  {
    "name": "ping",
    "description": "Health check: returns CURRENT project path, default gdb, and map names. Confirms the live bridge is up.",
    "inputSchema": {
      "type": "object",
      "properties": {},
      "additionalProperties": false
    }
  }
]
""";
    }
}
