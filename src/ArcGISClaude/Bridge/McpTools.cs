using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The MCP tool catalog served by <see cref="BridgeService"/> via tools/list.
    ///
    /// It is kept as one JSON literal — not hand-built JObjects — so it stays
    /// eyeball-diffable against the three things it must agree with: the handler
    /// tables (<see cref="AppStateOps"/> for reads, RunScript.pyt's OPS for the
    /// rest), the arguments those handlers actually read, and the tool-name
    /// references in Workspace/CLAUDE.md. Nothing checks that at compile time, so
    /// change them together. The descriptions are prompt text: the engine reads
    /// them on every session, so they state semantics the engine cannot guess
    /// (path = whole dataset vs. layer = the user's selection) and nothing else.
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
    "description": "PRIMARY TOOL. Run Python/ArcPy inside ArcGIS Pro against the open project. Each call is a fresh namespace. Injected: `arcpy`; `aprx` (project or None); `m` (active map or None) — guard both before use; `proj()` / `active_map()` raise if no project. Do not call ArcGISProject('CURRENT'). Assign JSON-serializable data to `result`; print() is captured; an exception returns the traceback of your code. Geoprocessing outputs are NOT added to the map automatically (m.addDataFromPath). Inspect schema with list_layers/get_field_list first.",
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
    "description": "Run a .py file (e.g. one you wrote into the workspace) the same way as run_python_current (injected arcpy/aprx/m; aprx and m may be None; `__file__` is set).",
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
    "description": "List layers and standalone tables of the active (or named) map: name, type, visibility, parent group, and for feature layers, tables and rasters the `source` path ArcPy opens the data by. Also reports what the live layer narrows that data to: `selected` (rows currently selected, when any) and `definition_query` (when set) — path-based ArcPy sees neither. Row counts are omitted by default (slow on large sources); pass include_counts=true, or use feature_count for one layer.",
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
    "description": "Fields of a layer or table: name and type, plus alias, length (text), domain, editable=false and nullable=false where they apply.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer or standalone-table name from list_layers." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "describe_layer",
    "description": "Describe a layer or table: dataType, catalogPath, row count, workspaceType (LocalDatabase = file/mobile gdb, RemoteDatabase = enterprise, FileSystem = shapefile, Service, Memory), OID and shape field names, shapeType, spatial reference (name, WKID, Geographic|Projected, unit), extent, plus selection and definition query. Check the spatial reference before any distance or area math.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer or standalone-table name from list_layers." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "feature_count",
    "description": "Row count of a layer or table's whole dataset, plus `selected` when the layer has a selection.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer or standalone-table name from list_layers." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "search_cursor",
    "description": "Read rows with an arcpy.da.SearchCursor. Reads the whole dataset — the layer's selection and definition query are ignored unless selected_only. Returns {fields, rows, truncated}.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer/table name from list_layers, or a data-source path." },
        "fields": { "type": "array", "items": {"type": "string"}, "description": "Field names, e.g. ['OID@','POP']. For geometry use tokens SHAPE@XY, SHAPE@WKT, SHAPE@AREA, SHAPE@LENGTH (plain SHAPE@ is an opaque object)." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "where": { "type": "string", "description": "Optional SQL where-clause." },
        "order_by": { "type": "string", "description": "Optional SQL ORDER BY list, e.g. 'POP DESC, NAME' — with limit this is a top-N query. Geodatabases only (ignored by shapefiles/dBASE)." },
        "limit": { "type": "integer", "description": "Optional max rows; defaults to the app's configured search row limit (Options page, 10000 unless changed)." },
        "selected_only": { "type": "boolean", "description": "Only the layer's currently selected rows (needs a layer name; errors if nothing is selected). Default false = every row of the dataset." }
      },
      "required": ["layer", "fields"],
      "additionalProperties": false
    }
  },
  {
    "name": "select_by_attribute",
    "description": "Select rows of a LIVE map layer or standalone table by SQL where-clause (highlights them in the open map). Returns the resulting selection count.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer or standalone-table name from list_layers." },
        "where": { "type": "string", "description": "SQL where-clause. Not needed for CLEAR_SELECTION." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "selection_type": { "type": "string", "enum": ["NEW_SELECTION", "ADD_TO_SELECTION", "REMOVE_FROM_SELECTION", "SUBSET_SELECTION", "CLEAR_SELECTION"], "description": "Default NEW_SELECTION." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "zoom_to_layer",
    "description": "Zoom the active map view to a layer's extent, or to its selected features.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer or standalone-table name from list_layers." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "selected_only": { "type": "boolean", "description": "Zoom to the layer's selected features instead of its full extent. Default false." }
      },
      "required": ["layer"],
      "additionalProperties": false
    }
  },
  {
    "name": "add_field",
    "description": "Add a field to a layer/table's dataset (modifies schema). An already-existing field comes back as a warning, not an error.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer/table name from list_layers, or a data-source path." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "field_name": { "type": "string" },
        "field_type": { "type": "string", "enum": ["TEXT", "SHORT", "LONG", "BIGINTEGER", "FLOAT", "DOUBLE", "DATE", "DATEHIGHPRECISION", "DATEONLY", "TIMEONLY", "TIMESTAMPOFFSET", "GUID", "BLOB"] },
        "field_length": { "type": "integer", "description": "TEXT fields only." },
        "field_alias": { "type": "string" }
      },
      "required": ["layer", "field_name", "field_type"],
      "additionalProperties": false
    }
  },
  {
    "name": "calc_field",
    "description": "Calculate a field with Calculate Field (modifies values). Runs on every row of the dataset unless selected_only. If the field does not exist it is created as field_type.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer/table name from list_layers, or a data-source path." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "field": { "type": "string" },
        "expression": { "type": "string", "description": "e.g. '!POP! / !AREASQMI!' (PYTHON3) or '$feature.POP / $feature.AREASQMI' (ARCADE)." },
        "expression_type": { "type": "string", "enum": ["PYTHON3", "ARCADE", "SQL"], "description": "Default PYTHON3." },
        "code_block": { "type": "string", "description": "Optional PYTHON3 code block defining functions the expression calls." },
        "field_type": { "type": "string", "enum": ["TEXT", "SHORT", "LONG", "BIGINTEGER", "FLOAT", "DOUBLE", "DATE", "DATEONLY", "TIMEONLY", "GUID"], "description": "Type to create the field as when it does not exist yet." },
        "selected_only": { "type": "boolean", "description": "Only the layer's currently selected rows (needs a layer name; errors if nothing is selected). Default false = every row of the dataset." }
      },
      "required": ["layer", "field", "expression"],
      "additionalProperties": false
    }
  },
  {
    "name": "update_field",
    "description": "Set a single field to a constant value for matching rows via an UpdateCursor (modifies values). Runs on every matching row of the dataset unless selected_only. Returns the number of rows updated.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "layer": { "type": "string", "description": "Layer/table name from list_layers, or a data-source path." },
        "map": { "type": "string", "description": "Optional map name; defaults to the active map." },
        "field": { "type": "string" },
        "value": {},
        "where": { "type": "string" },
        "selected_only": { "type": "boolean", "description": "Only the layer's currently selected rows (needs a layer name; errors if nothing is selected). Default false = every row of the dataset." }
      },
      "required": ["layer", "field", "value"],
      "additionalProperties": false
    }
  },
  {
    "name": "run_geoprocessing",
    "description": "Run one geoprocessing tool by name (e.g. 'analysis.Buffer'). Returns GP messages + outputs. An input given as a layer NAME honors that layer's selection and definition query; a data-source PATH is the whole dataset. Outputs are not added to the map unless add_to_map.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "tool": { "type": "string", "description": "'<module>.<Tool>', e.g. 'analysis.Buffer' or 'management.AddField'." },
        "params": { "description": "Positional list OR keyword object of parameters." },
        "env": { "type": "object", "description": "Optional geoprocessing environments for this run only, e.g. {'overwriteOutput': true, 'outputCoordinateSystem': 'PROJCS[...]'}." },
        "add_to_map": { "type": "boolean", "description": "Add the output dataset(s) to the active map. Default false." }
      },
      "required": ["tool"],
      "additionalProperties": false
    }
  },
  {
    "name": "ping",
    "description": "Session state: project path, default gdb, home folder, every map name (`maps`), the active map (`active_map` is absent when no map view is active), license level, and `unsaved_edits` when the user has pending edits. Confirms the live bridge is up.",
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
