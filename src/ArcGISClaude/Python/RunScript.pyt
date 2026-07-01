# -*- coding: utf-8 -*-
"""
RunScript.pyt  --  per-call ArcPy executor for the "Claude in ArcGIS Pro" add-in.

This REPLACES the old long-lived in-process daemon (pro_bridge.py). The persistent
C# BridgeService owns the session and, for each Claude request, runs this foreground
geoprocessing tool ONCE. There is no daemon thread to outlive its host, so the
"bridge went stale / reconnects / breaks" failure mode cannot happen.

Contract (driven by ScriptRunner.cs):
  param[0] request_path : a JSON file  {"op": <str>, "args": {...}}
  param[1] result_path  : where to write {"ok": <bool>, "error": <str|null>, "data": <any>}

execute() resolves arcpy.mp.ArcGISProject("CURRENT") **best-effort** on the
foreground/main thread (the only place it can resolve) and injects `arcpy`, `aprx`
(may be None), and `m` (active map, may be None) for run_python_*. Data ops resolve
the layer's on-disk data-source PATH and use path-based arcpy, which needs no CURRENT
at all -- so they run regardless of whether CURRENT resolved.
"""

import arcpy

import os
import io
import json
import traceback
import contextlib


# Resolved best-effort per call in execute(); injected into generated code.
_APRX = None
_M = None


# --------------------------------------------------------------------------- #
#  CURRENT project (best-effort) + helpers
# --------------------------------------------------------------------------- #
def _proj():
    if _APRX is None:
        raise RuntimeError(
            "No current project is available (arcpy \"CURRENT\" did not resolve). "
            "Use a data-source path instead of a layer name.")
    return _APRX


def _active_map(name=None):
    if name:
        aprx = _proj()
        maps = aprx.listMaps(name)
        if not maps:
            raise ValueError("map '%s' not found" % name)
        return maps[0]
    if _M is not None:
        return _M
    if _APRX is not None:
        maps = _APRX.listMaps()
        return maps[0] if maps else None
    return None


def _find_layer(name, map_name=None):
    m = _active_map(map_name)
    if m is None:
        raise ValueError("no map is open")
    lyrs = m.listLayers(name)
    if not lyrs:
        raise ValueError("layer '%s' not found in map '%s'" % (name, m.name))
    return lyrs[0]


def _source(name_or_path, map_name=None):
    """Resolve a layer NAME to its on-disk data-source path. Path-based arcpy needs
    no CURRENT, so data ops should use this. A value that already looks like a path
    is returned unchanged (so callers can pass paths from list_layers directly)."""
    s = str(name_or_path)
    if ("\\" in s) or ("/" in s):
        return s
    lyr = _find_layer(s, map_name)
    try:
        if lyr.supports("DATASOURCE"):
            return lyr.dataSource
    except Exception:
        pass
    return lyr


def _jsonable(value, _depth=0):
    if _depth > 6:
        return str(value)
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, dict):
        return {str(k): _jsonable(v, _depth + 1) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [_jsonable(v, _depth + 1) for v in value]
    return str(value)


def _resolve_tool(name):
    obj = arcpy
    for part in name.split("."):
        obj = getattr(obj, part)
    return obj


# --------------------------------------------------------------------------- #
#  operations  (names match the MCP tool catalog)
#
#  Only the write / code-exec ops live here. The live-read ops (ping,
#  list_layers, get_field_list, describe_layer, feature_count,
#  select_by_attribute, zoom_to_layer) are answered by the C# fast path
#  (AppStateOps) and never reach this tool, so they are intentionally absent.
# --------------------------------------------------------------------------- #
def op_run_python_current(args):
    """Execute Claude-generated code. `arcpy`, best-effort `aprx`/`m`, and helpers
    proj()/active_map() are injected. Captures stdout, an optional JSON-serializable
    `result`, and the traceback (so the engine can fix-and-retry)."""
    code = args["code"]
    g = {"arcpy": arcpy, "aprx": _APRX, "m": _M, "proj": _proj,
         "active_map": _active_map, "__name__": "__claude__"}
    buf = io.StringIO()
    err = None
    try:
        compiled = compile(code, "<claude_code>", "exec")  # validate syntax first
        with contextlib.redirect_stdout(buf):
            exec(compiled, g)
    except Exception:
        err = traceback.format_exc()
    return {"stdout": buf.getvalue(), "result": _jsonable(g.get("result")), "error": err}


def op_run_python_file(args):
    with io.open(args["path"], "r", encoding="utf-8") as f:
        code = f.read()
    return op_run_python_current({"code": code})


def op_search_cursor(args):
    src = _source(args["layer"], args.get("map"))
    fields = args["fields"]
    limit = args.get("limit")
    rows = []
    with arcpy.da.SearchCursor(src, fields, args.get("where")) as cur:
        for i, r in enumerate(cur):
            if limit is not None and i >= int(limit):
                break
            rows.append([_jsonable(v) for v in r])
    return {"fields": fields, "rows": rows,
            "truncated": limit is not None and len(rows) >= int(limit)}


def op_add_field(args):
    arcpy.management.AddField(
        _source(args["layer"], args.get("map")), args["field_name"], args["field_type"],
        field_length=args.get("field_length"), field_alias=args.get("field_alias"))
    return {"added": args["field_name"]}


def op_calc_field(args):
    src = _source(args["layer"], args.get("map"))
    arcpy.management.CalculateField(
        src, args["field"], args["expression"], args.get("expression_type", "PYTHON3"))
    return {"calculated": args["field"], "count": int(arcpy.management.GetCount(src)[0])}


def op_update_field(args):
    src = _source(args["layer"], args.get("map"))
    n = 0
    with arcpy.da.UpdateCursor(src, [args["field"]], args.get("where")) as cur:
        for row in cur:
            row[0] = args["value"]
            cur.updateRow(row)
            n += 1
    return {"updated": n}


def op_run_geoprocessing(args):
    tool = _resolve_tool(args["tool"])
    params = args.get("params", [])
    res = tool(**params) if isinstance(params, dict) else tool(*params)
    try:
        outputs = [res[i] for i in range(res.outputCount)]
    except Exception:
        outputs = [str(res)]
    return {"messages": arcpy.GetMessages(), "outputs": _jsonable(outputs)}


OPS = {
    "run_python_current": op_run_python_current,
    "run_python_file": op_run_python_file,
    "search_cursor": op_search_cursor,
    "add_field": op_add_field,
    "calc_field": op_calc_field,
    "update_field": op_update_field,
    "run_geoprocessing": op_run_geoprocessing,
}


def _handle(cmd):
    op = cmd.get("op")
    fn = OPS.get(op)
    if fn is None:
        return {"ok": False, "error": "unknown op: %s" % op, "data": None}
    try:
        return {"ok": True, "error": None, "data": fn(cmd.get("args") or {})}
    except Exception:
        return {"ok": False, "error": traceback.format_exc(), "data": None}


def _write(path, obj):
    tmp = path + ".tmp"
    with io.open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f)
    os.replace(tmp, path)


def _run(request_path, result_path):
    # Resolve CURRENT best-effort on the foreground/main thread (this tool is
    # canRunInBackground=False, so execute() runs there). Path-based ops still work
    # if this fails; run_python_* simply receives aprx=None/m=None.
    global _APRX, _M
    try:
        _APRX = arcpy.mp.ArcGISProject("CURRENT")
    except Exception:
        _APRX = None
    try:
        _M = _APRX.activeMap if _APRX is not None else None
    except Exception:
        _M = None

    try:
        with io.open(request_path, "r", encoding="utf-8") as f:
            cmd = json.load(f)
    except Exception:
        _write(result_path,
               {"ok": False, "error": "could not read request: " + traceback.format_exc(),
                "data": None})
        return

    try:
        _write(result_path, _handle(cmd))
    except Exception:
        # Last-ditch: even a serialization failure must produce a result file so the
        # caller never blocks waiting for output.
        _write(result_path,
               {"ok": False, "error": "result write failed: " + traceback.format_exc(),
                "data": None})


# --------------------------------------------------------------------------- #
#  Python toolbox wrapper
# --------------------------------------------------------------------------- #
class Toolbox(object):
    def __init__(self):
        self.label = "Claude RunScript"
        self.alias = "claude_runscript"
        self.tools = [RunScript]


class RunScript(object):
    def __init__(self):
        self.label = "RunScript"
        self.description = ("Per-call ArcPy executor for the Claude add-in bridge. "
                            "Reads a request JSON, runs the op, writes a result JSON.")
        self.canRunInBackground = False  # foreground => in-process, CURRENT can resolve

    def getParameterInfo(self):
        request = arcpy.Parameter(
            displayName="Request path", name="request_path",
            datatype="GPString", parameterType="Required", direction="Input")
        result = arcpy.Parameter(
            displayName="Result path", name="result_path",
            datatype="GPString", parameterType="Required", direction="Input")
        return [request, result]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        _run(parameters[0].valueAsText, parameters[1].valueAsText)
