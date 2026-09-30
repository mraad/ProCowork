# -*- coding: utf-8 -*-
"""
RunScript.pyt  --  per-call ArcPy executor for the "Claude in ArcGIS Pro" add-in.

This REPLACES the old long-lived in-process daemon (pro_bridge.py). The persistent
C# BridgeService owns the session and, for each Claude request, runs this foreground
geoprocessing tool ONCE. There is no daemon thread to outlive its host, so the
"bridge went stale / reconnects / breaks" failure mode cannot happen.

Contract (driven by ScriptRunner.cs):
  param[0] request_json : {"op": <str>, "args": {...}}
  param[1] result_json  : derived GPString {"ok": <bool>, "error": <str|null>, "data": <any>}

execute() resolves arcpy.mp.ArcGISProject("CURRENT") **best-effort** on the
foreground/main thread (the only place it can resolve) and injects `arcpy`, `aprx`
(may be None), and `m` (active map, may be None) for run_python_*. Data ops resolve
the layer's on-disk data-source PATH and use path-based arcpy, which needs no CURRENT
at all -- so they run regardless of whether CURRENT resolved.

Path vs layer is a deliberate semantic split, not just a robustness trick: a PATH is
the whole dataset, while a live LAYER object is the user's view of it (arcpy cursors
and GP tools honour its selection and definition query). Data ops default to the
path and switch to the layer only when the caller asks for `selected_only`.
"""

import arcpy

import contextlib
import datetime
import io
import json
import linecache
import os
import sys
import traceback


# Resolved best-effort per call in _run(); injected into generated code.
_APRX = None
_M = None


DEFAULT_SEARCH_LIMIT = 10000

# Pseudo-filename generated code is compiled under, so its frames can be told
# apart from this file's in a traceback.
_CODE_FILENAME = "<claude_code>"

# arcpy's own package folder: its wrapper frames are noise in a traceback.
_ARCPY_DIR = os.path.normcase(os.path.dirname(os.path.abspath(arcpy.__file__)))


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
    """The map's layer OR standalone table called `name` -- list_layers reports both,
    so both must resolve here."""
    m = _active_map(map_name)
    if m is None:
        raise ValueError("no map is open; pass the data-source path instead of a layer name")
    found = m.listLayers(name) or m.listTables(name)
    if not found:
        raise ValueError("layer or table '%s' not found in map '%s'" % (name, m.name))
    return found[0]


def _is_path(s):
    return ("\\" in s) or ("/" in s)


def _source(name_or_path, map_name=None):
    """Resolve a layer NAME to its on-disk data-source path. Path-based arcpy needs
    no CURRENT, so data ops should use this. A value that already looks like a path
    is returned unchanged (so callers can pass paths from list_layers directly)."""
    s = str(name_or_path)
    if _is_path(s):
        return s
    lyr = _find_layer(s, map_name)
    try:
        if lyr.supports("DATASOURCE"):
            return lyr.dataSource
    except Exception:
        pass
    return lyr


def _target(args):
    """What a data op runs against. Default: the data-source PATH (every row of the
    dataset). With selected_only: the live layer object, which arcpy restricts to the
    layer's selection -- refused when nothing is selected, because arcpy would then
    silently process every row."""
    name, map_name = str(args["layer"]), args.get("map")
    if not args.get("selected_only"):
        return _source(name, map_name)
    if _is_path(name):
        raise ValueError("selected_only needs a layer NAME from the map, not a path")
    lyr = _find_layer(name, map_name)
    if not lyr.getSelectionSet():
        raise ValueError(
            "'%s' has no selected rows; nothing was changed. Select first "
            "(select_by_attribute) or drop selected_only to use every row." % name)
    return lyr


def _jsonable(value, _depth=0):
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        # NaN / Infinity are not JSON; json.dumps would emit them bare.
        return value if value == value and value not in (float("inf"), float("-inf")) else str(value)
    if _depth > 6:
        return str(value)
    if isinstance(value, (datetime.datetime, datetime.date, datetime.time)):
        return value.isoformat()
    if isinstance(value, dict):
        return {str(k): _jsonable(v, _depth + 1) for k, v in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [_jsonable(v, _depth + 1) for v in value]
    tolist = getattr(value, "tolist", None)  # numpy scalars and arrays
    if callable(tolist):
        try:
            return _jsonable(tolist(), _depth + 1)
        except Exception:
            pass
    return str(value)


def _warnings():
    """Warning-severity messages of the GP tool that just ran ('' if none). Tools
    report things like "field already exists" or "empty output" as warnings while
    still succeeding, so they must reach the engine."""
    try:
        return (arcpy.GetMessages(1) or "").strip()
    except Exception:
        return ""


def _with_warnings(data):
    w = _warnings()
    if w:
        data["warnings"] = w
    return data


def _resolve_tool(name):
    """'analysis.Buffer' -- also tolerates 'arcpy.analysis.Buffer' and the legacy
    'Buffer_analysis' spelling, since the engine writes all three."""
    name = str(name).strip()
    if name.startswith("arcpy."):
        name = name[len("arcpy."):]
    obj = arcpy
    try:
        for part in name.split("."):
            obj = getattr(obj, part)
    except AttributeError:
        obj = None
    if not callable(obj):
        raise ValueError(
            "unknown geoprocessing tool '%s'; use '<module>.<Tool>', e.g. 'analysis.Buffer'" % name)
    return obj


def _source_key(path):
    """Comparable form of a data-source path. Layers on the memory workspace report
    it as INSTANCE_ID=GPProMemoryWorkspace\\x while everything else calls it memory\\x."""
    return os.path.normcase(str(path)).replace("instance_id=gppromemoryworkspace", "memory")


def _add_dataset(m, path):
    """Add one dataset to the map as Pro would from the Catalog pane. The memory
    workspace can't be added by path (Pro 3.x: "Failed to add data"), but a GP
    layer / table view of it can."""
    try:
        return m.addDataFromPath(path)
    except Exception:
        dtype = arcpy.Describe(path).dataType
        name = os.path.basename(path)
        if dtype == "FeatureClass":
            return m.addLayer(arcpy.management.MakeFeatureLayer(path, name).getOutput(0))[0]
        if dtype == "Table":
            return m.addTable(arcpy.management.MakeTableView(path, name).getOutput(0))
        raise


def _add_to_map(outputs):
    """Add a GP tool's output datasets to the map (nested tools never auto-add).
    Skips anything already in the map -- many tools return their *input* as the
    derived output (AddField, CalculateField), which must not become a duplicate layer."""
    m = _active_map()
    if m is None:
        return {"added": [], "skipped": "no active map"}
    present = set()
    for member in list(m.listLayers()) + list(m.listTables()):
        try:
            present.add(_source_key(member.dataSource))
        except Exception:
            pass  # group / basemap layers have no data source
    added, failed = [], {}
    for out in outputs:
        if not isinstance(out, str) or not _is_path(out) or _source_key(out) in present:
            continue
        try:
            if not arcpy.Exists(out):
                continue  # a number, a message, a layer name: not a dataset
            added.append(_add_dataset(m, out).name)
            present.add(_source_key(out))
        except Exception as ex:
            failed[out] = (str(ex).strip().splitlines() or [type(ex).__name__])[-1]
    data = {"added": added}
    if failed:
        data["failed"] = failed
    return data


def _trim_stack(te, filename):
    """Reduce one TracebackException (and its __cause__/__context__ chain) to the
    frames the engine can act on: from the generated code's first frame onward,
    minus arcpy's own wrapper frames, and without the 3.11+ caret lines."""
    frames = list(te.stack)
    start = next((i for i, f in enumerate(frames) if f.filename == filename), len(frames))
    te.stack = traceback.StackSummary.from_list([
        traceback.FrameSummary(f.filename, f.lineno, f.name, line=f.line)
        for f in frames[start:]
        if not os.path.normcase(f.filename).startswith(_ARCPY_DIR)])
    for linked in (te.__cause__, te.__context__):
        if linked is not None:
            _trim_stack(linked, filename)


def _format_error(filename):
    """The active exception as a compact traceback -- the engine fixes its code from
    this text alone, and it stays in context, so only the generated code's frames
    and the exception itself are kept (an ExecuteError already carries the GP
    messages; a SyntaxError its file/line/caret)."""
    te = traceback.TracebackException(*sys.exc_info())
    _trim_stack(te, filename)
    return "".join(te.format())


def _exec(code, filename):
    """Execute generated code. `arcpy`, best-effort `aprx`/`m`, and helpers
    proj()/active_map() are injected. Captures stdout+stderr, an optional
    JSON-serializable `result`, and the traceback (so the engine can fix-and-retry).
    Empty parts are omitted -- every character returned stays in the engine's context."""
    g = {"arcpy": arcpy, "aprx": _APRX, "m": _M, "proj": _proj,
         "active_map": _active_map, "__name__": "__claude__"}
    in_memory = filename == _CODE_FILENAME
    if in_memory:
        # Let tracebacks quote the failing source line of code that exists on no disk.
        linecache.cache[filename] = (len(code), None, code.splitlines(True), filename)
    else:
        g["__file__"] = filename
        linecache.checkcache(filename)  # the file may have been rewritten since last run
    buf = io.StringIO()
    err = None
    try:
        compiled = compile(code, filename, "exec")  # validate syntax first
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            exec(compiled, g)
    except SystemExit as ex:
        # sys.exit()/exit() in generated code must end the snippet, not the GP tool.
        if ex.code not in (None, 0):
            err = "SystemExit: %s" % (ex.code,)
    except Exception:
        err = _format_error(filename)
    finally:
        if in_memory:
            linecache.cache.pop(filename, None)

    data = {}
    if buf.getvalue():
        data["stdout"] = buf.getvalue()
    if g.get("result") is not None:
        data["result"] = _jsonable(g["result"])
    if err:
        data["error"] = err
    return data


# --------------------------------------------------------------------------- #
#  operations  (names match the MCP tool catalog)
#
#  Only the write / code-exec ops live here. The live-read ops (ping,
#  list_layers, get_field_list, describe_layer, feature_count,
#  select_by_attribute, zoom_to_layer) are answered by the C# fast path
#  (AppStateOps) and never reach this tool, so they are intentionally absent.
# --------------------------------------------------------------------------- #
def op_run_python_current(args):
    return _exec(args["code"], _CODE_FILENAME)


def op_run_python_file(args):
    path = os.path.abspath(args["path"])
    with io.open(path, "r", encoding="utf-8-sig") as f:  # -sig: tolerate a BOM
        code = f.read()
    return _exec(code, path)


def op_search_cursor(args):
    src = _target(args)
    fields = args["fields"]
    limit = args.get("limit", DEFAULT_SEARCH_LIMIT)
    n = None if limit is None else int(limit)
    if n is not None and n < 0:
        raise ValueError("limit must be >= 0")
    order_by = (args.get("order_by") or "").strip()
    if order_by.upper().startswith("ORDER BY"):
        order_by = order_by[len("ORDER BY"):].strip()
    sql_clause = (None, "ORDER BY " + order_by) if order_by else (None, None)
    rows = []
    truncated = False
    with arcpy.da.SearchCursor(src, fields, args.get("where"), sql_clause=sql_clause) as cur:
        names = list(cur.fields)  # the real names -- expands "*"
        for i, r in enumerate(cur):
            if n is not None and i >= n:
                truncated = True  # this row is beyond the limit -> more data exists
                break
            rows.append([_jsonable(v) for v in r])
    return {"fields": names, "rows": rows, "truncated": truncated}


def op_add_field(args):
    arcpy.management.AddField(
        _source(args["layer"], args.get("map")), args["field_name"], args["field_type"],
        field_length=args.get("field_length"), field_alias=args.get("field_alias"))
    return _with_warnings({"added": args["field_name"]})


def op_calc_field(args):
    src = _target(args)
    arcpy.management.CalculateField(
        src, args["field"], args["expression"], args.get("expression_type", "PYTHON3"),
        args.get("code_block"), args.get("field_type"))
    data = _with_warnings({"calculated": args["field"]})
    data["count"] = int(arcpy.management.GetCount(src)[0])
    return data


def op_update_field(args):
    src = _target(args)
    n = 0
    with arcpy.da.UpdateCursor(src, [args["field"]], args.get("where")) as cur:
        for row in cur:
            row[0] = args["value"]
            cur.updateRow(row)
            n += 1
    return {"updated": n}


def op_run_geoprocessing(args):
    tool = _resolve_tool(args["tool"])
    params = args.get("params") or []
    # Scoped to this one tool run, so nothing leaks into the user's session.
    with arcpy.EnvManager(**(args.get("env") or {})):
        res = tool(**params) if isinstance(params, dict) else tool(*params)
    try:
        outputs = [res[i] for i in range(res.outputCount)]
    except Exception:
        outputs = [str(res)]
    outputs = _jsonable(outputs)
    data = {"outputs": outputs, "messages": arcpy.GetMessages()}
    if args.get("add_to_map"):
        data["map"] = _add_to_map(outputs)
    return data


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
    except Exception as ex:
        # The exception alone: an ExecuteError already carries the GP messages, and
        # this file's frames are nothing the engine can act on.
        return {"ok": False, "error": "".join(traceback.format_exception_only(type(ex), ex)).strip(),
                "data": None}


def _run(request_json):
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
        try:
            cmd = json.loads(request_json)
        except Exception:
            result = {"ok": False, "error": "could not parse request: " + traceback.format_exc(),
                      "data": None}
        else:
            result = _handle(cmd)
    finally:
        # Pro caches this module between runs; don't pin a project/map the user
        # may close or switch away from before the next call.
        _APRX = None
        _M = None

    try:
        return json.dumps(result, ensure_ascii=False, separators=(",", ":"))
    except Exception:
        # Last-ditch: even a serialization failure must populate the derived output
        # so the caller gets a useful response when ExecuteToolAsync completes.
        return json.dumps(
            {"ok": False, "error": "result serialization failed: " + traceback.format_exc(),
             "data": None}, separators=(",", ":"))


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
                            "Accepts request JSON and returns result JSON.")
        self.canRunInBackground = False  # foreground => in-process, CURRENT can resolve

    def getParameterInfo(self):
        request = arcpy.Parameter(
            displayName="Request JSON", name="request_json",
            datatype="GPString", parameterType="Required", direction="Input")
        result = arcpy.Parameter(
            displayName="Result JSON", name="result_json",
            datatype="GPString", parameterType="Derived", direction="Output")
        return [request, result]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        parameters[1].value = _run(parameters[0].valueAsText)
