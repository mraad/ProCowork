# -*- coding: utf-8 -*-
"""
pro_bridge.py  --  in-process ArcPy bridge for the "Claude in ArcGIS Pro" add-in.

Run this INSIDE ArcGIS Pro's own Python. The add-in runs it via the foreground
ClaudeBridge.pyt tool (canRunInBackground = False), whose execute() calls start()
on Pro's foreground/main thread -- the only place arcpy.mp.ArcGISProject("CURRENT")
resolves.

KEY DESIGN (this is what makes it work): CURRENT is resolved ONCE in start() and
the ArcGISProject object is cached. A daemon thread then serves commands reusing
the cached object. Re-resolving CURRENT from the daemon thread raises
"OSError: CURRENT", so we never do that -- generated code is handed the cached
`aprx` and `m` (active map) instead. Path-based arcpy (operating on a feature
class's data-source path) needs no CURRENT and is thread-safe, so data edits run
fine on the daemon and still show up in the open map.

File IPC (one file per request, correlation id in name + body):
  <ipc>/cmd_<id>.json   {"id","op","args"}
  <ipc>/res_<id>.json   {"id","ok","error","data"}
  <ipc>/bridge.alive    heartbeat (proves the daemon is up)
  <ipc>/bridge.stop     stop signal (daemon honours it, then exits)
  <ipc>/bridge.status   {"connected","project","error"}  (read by the Start button UI)
  <ipc>/bridge.log      timestamped daemon lifecycle/error log (for diagnosis)
"""

import os
import io
import json
import time
import threading
import traceback
import contextlib

import arcpy

# Fixed IPC folder under the user profile (NOT %TEMP%): Pro sets a per-session TMP,
# and .NET vs Python resolve it differently, so a temp path would desync the daemon
# and the MCP server. All three sides compute ~/.arcgis_claude identically.
IPC_DIR = os.environ.get("ARCGIS_CLAUDE_IPC") or os.path.join(
    os.path.expanduser("~"), ".arcgis_claude")
ALIVE = os.path.join(IPC_DIR, "bridge.alive")
STOP = os.path.join(IPC_DIR, "bridge.stop")
STATUS = os.path.join(IPC_DIR, "bridge.status")
LOG = os.path.join(IPC_DIR, "bridge.log")

_POLL_SECONDS = 0.1
_HEARTBEAT_SECONDS = 1.0

# Resolved ONCE on the foreground/main thread in start(); reused by the daemon.
_APRX = None
_PROJECT_PATH = None
_CURRENT_ERROR = None


def _log(msg):
    """Append a timestamped line to bridge.log (for diagnosing daemon lifetime)."""
    try:
        with io.open(LOG, "a", encoding="utf-8") as f:
            f.write("%s [%s] %s\n" % (
                time.strftime("%Y-%m-%d %H:%M:%S"),
                threading.current_thread().name, msg))
    except Exception:
        pass


# --------------------------------------------------------------------------- #
#  CURRENT project (cached) + helpers
# --------------------------------------------------------------------------- #
def _proj():
    """The cached CURRENT project. Never re-resolves CURRENT (that fails off the
    main thread)."""
    if _APRX is None:
        raise RuntimeError(
            "Bridge is not connected to a project. Open a project and (re)start the "
            "bridge. Detail: %s" % (_CURRENT_ERROR or "CURRENT was not resolved"))
    return _APRX


def _active_map(name=None):
    aprx = _proj()
    if name:
        maps = aprx.listMaps(name)
        if not maps:
            raise ValueError("map '%s' not found" % name)
        return maps[0]
    m = getattr(aprx, "activeMap", None)
    if m is not None:
        return m
    maps = aprx.listMaps()
    return maps[0] if maps else None


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
    no CURRENT and is thread-safe, so data ops should use this. A value that already
    looks like a path is returned unchanged."""
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
#  operations
# --------------------------------------------------------------------------- #
def op_ping(_args):
    info = {"connected": _APRX is not None, "project": _PROJECT_PATH,
            "current_error": _CURRENT_ERROR}
    if _APRX is not None:
        try:
            info["active_map"] = getattr(_active_map(), "name", None)
            info["maps"] = [m.name for m in _proj().listMaps()]
            info["default_gdb"] = _proj().defaultGeodatabase
        except Exception:
            pass
    return info


def op_run_python_current(args):
    """Execute Claude-generated code with the cached project injected. `arcpy`,
    `aprx` (current project), `m` (active map), and helpers proj()/active_map() are
    available. Captures stdout, an optional JSON-serializable `result`, and the
    traceback (so the engine can fix-and-retry)."""
    code = args["code"]
    g = {"arcpy": arcpy, "aprx": _APRX, "proj": _proj,
         "active_map": _active_map, "__name__": "__claude__"}
    try:
        g["m"] = _active_map() if _APRX is not None else None
    except Exception:
        g["m"] = None

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


def op_list_layers(args):
    m = _active_map(args.get("map"))
    if m is None:
        return []
    out = []
    for lyr in m.listLayers():
        info = {"name": lyr.name, "visible": bool(lyr.visible)}
        try:
            info["is_feature"] = bool(lyr.isFeatureLayer)
            info["is_raster"] = bool(lyr.isRasterLayer)
            info["is_group"] = bool(lyr.isGroupLayer)
        except Exception:
            pass
        try:
            if lyr.supports("DATASOURCE"):
                info["source"] = lyr.dataSource
        except Exception:
            pass
        if getattr(lyr, "isFeatureLayer", False):
            try:
                info["geometry"] = arcpy.Describe(lyr).shapeType
                info["count"] = int(arcpy.management.GetCount(lyr)[0])
            except Exception:
                pass
        out.append(info)
    return out


def op_get_field_list(args):
    return [
        {"name": f.name, "type": f.type, "alias": f.aliasName, "length": f.length}
        for f in arcpy.ListFields(_source(args["layer"], args.get("map")))
    ]


def op_describe_layer(args):
    lyr = _find_layer(args["layer"], args.get("map"))
    d = arcpy.Describe(lyr)
    out = {
        "name": lyr.name,
        "dataType": getattr(d, "dataType", None),
        "shapeType": getattr(d, "shapeType", None),
        "catalogPath": getattr(d, "catalogPath", None),
    }
    sr = getattr(d, "spatialReference", None)
    if sr is not None:
        out["spatialReference"] = {"name": sr.name, "factoryCode": sr.factoryCode}
    try:
        out["count"] = int(arcpy.management.GetCount(lyr)[0])
    except Exception:
        pass
    return out


def op_feature_count(args):
    return {"count": int(arcpy.management.GetCount(_source(args["layer"], args.get("map")))[0])}


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


def op_select_by_attribute(args):
    lyr = _find_layer(args["layer"], args.get("map"))
    sel_type = args.get("selection_type", "NEW_SELECTION")
    arcpy.management.SelectLayerByAttribute(lyr, sel_type, args.get("where"))
    return {"selected": int(arcpy.management.GetCount(lyr)[0])}


def op_zoom_to_layer(args):
    lyr = _find_layer(args["layer"], args.get("map"))
    view = _proj().activeView
    extent = arcpy.Describe(lyr).extent
    try:
        view.camera.setExtent(extent)
        return {"zoomed": True}
    except Exception as ex:
        return {"zoomed": False, "reason": str(ex)}


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
    "ping": op_ping,
    "run_python_current": op_run_python_current,
    "run_python_file": op_run_python_file,
    "list_layers": op_list_layers,
    "get_field_list": op_get_field_list,
    "describe_layer": op_describe_layer,
    "feature_count": op_feature_count,
    "search_cursor": op_search_cursor,
    "select_by_attribute": op_select_by_attribute,
    "zoom_to_layer": op_zoom_to_layer,
    "add_field": op_add_field,
    "calc_field": op_calc_field,
    "update_field": op_update_field,
    "run_geoprocessing": op_run_geoprocessing,
}


# --------------------------------------------------------------------------- #
#  daemon
# --------------------------------------------------------------------------- #
def _handle(cmd):
    cid = cmd.get("id")
    op = cmd.get("op")
    fn = OPS.get(op)
    if fn is None:
        return {"id": cid, "ok": False, "error": "unknown op: %s" % op, "data": None}
    try:
        return {"id": cid, "ok": True, "error": None, "data": fn(cmd.get("args") or {})}
    except Exception:
        return {"id": cid, "ok": False, "error": traceback.format_exc(), "data": None}


def _write_result(res):
    final = os.path.join(IPC_DIR, "res_%s.json" % res["id"])
    tmp = final + ".tmp"
    with io.open(tmp, "w", encoding="utf-8") as f:
        json.dump(res, f)
    os.replace(tmp, final)


def _heartbeat():
    try:
        with io.open(ALIVE, "w", encoding="utf-8") as f:
            f.write(str(time.time()))
    except Exception:
        pass


def _write_status():
    try:
        with io.open(STATUS, "w", encoding="utf-8") as f:
            json.dump({"connected": _APRX is not None,
                       "project": _PROJECT_PATH, "error": _CURRENT_ERROR}, f)
    except Exception:
        pass


def _shutdown():
    """Stop was requested: drop the heartbeat + stop + status, then end the thread."""
    _log("shutdown: stop signal seen, ending daemon")
    for p in (ALIVE, STOP, STATUS):
        try:
            if os.path.exists(p):
                os.remove(p)
        except OSError:
            pass
    print("[arcgis_claude] bridge stopped")


def _process_one(name):
    path = os.path.join(IPC_DIR, name)
    try:
        with io.open(path, "r", encoding="utf-8") as f:
            cmd = json.load(f)
    except Exception:
        return  # half-written; pick it up next pass
    try:
        os.remove(path)
    except OSError:
        pass
    _log("cmd id=%s op=%s" % (cmd.get("id"), cmd.get("op")))
    try:
        _write_result(_handle(cmd))
    except Exception:
        _log("write_result failed: " + traceback.format_exc())


def _poll_loop():
    last_beat = 0.0
    ticks = 0
    while True:
        try:
            if os.path.exists(STOP):
                _shutdown()
                return
            now = time.time()
            if now - last_beat >= _HEARTBEAT_SECONDS:
                _heartbeat()
                last_beat = now
                ticks += 1
                if ticks % 15 == 1:
                    _log("alive (tick %d)" % ticks)
            try:
                names = [n for n in os.listdir(IPC_DIR)
                         if n.startswith("cmd_") and n.endswith(".json")]
            except Exception:
                names = []
            for name in sorted(names):
                _process_one(name)
        except Exception:
            # The daemon must NEVER die from a stray error.
            _log("poll loop error: " + traceback.format_exc())
        time.sleep(_POLL_SECONDS)


def _already_running():
    try:
        if os.path.exists(ALIVE):
            return (time.time() - os.path.getmtime(ALIVE)) < 5.0
    except Exception:
        pass
    return False


def _resolve_current():
    """Resolve + cache CURRENT. MUST be called on the foreground/main thread."""
    global _APRX, _PROJECT_PATH, _CURRENT_ERROR
    try:
        _APRX = arcpy.mp.ArcGISProject("CURRENT")
        _PROJECT_PATH = _APRX.filePath
        _CURRENT_ERROR = None
    except Exception as ex:
        _APRX = None
        _PROJECT_PATH = None
        _CURRENT_ERROR = "".join(traceback.format_exception_only(type(ex), ex)).strip()
    _write_status()


def start():
    try:
        os.makedirs(IPC_DIR, exist_ok=True)
    except Exception:
        pass
    try:
        if os.path.exists(STOP):
            os.remove(STOP)  # clear a stale stop signal from a prior session
    except OSError:
        pass

    if _already_running():
        _log("start(): already running; skipping (start_thread=%s)" % threading.current_thread().name)
        print("[arcgis_claude] bridge already running -> %s "
              "(stop then start to reconnect to a different project)" % IPC_DIR)
        return

    # Resolve + cache CURRENT HERE, on the foreground/main thread (this runs inside
    # the ClaudeBridge.pyt foreground execute(), where CURRENT is available).
    _resolve_current()
    _log("start(): connected=%s project=%r current_error=%r start_thread=%s"
         % (_APRX is not None, _PROJECT_PATH, _CURRENT_ERROR, threading.current_thread().name))

    _heartbeat()
    t = threading.Thread(target=_poll_loop, name="arcgis_claude_bridge", daemon=True)
    t.start()
    _log("daemon thread started: %s (alive=%s)" % (t.name, t.is_alive()))

    if _APRX is not None:
        print("[arcgis_claude] bridge started; connected to %s"
              % (_PROJECT_PATH or "(unsaved project)"))
    else:
        print("[arcgis_claude] bridge started, but CURRENT could not be resolved: %s"
              % _CURRENT_ERROR)


# Auto-start when exec'd by ClaudeBridge.pyt (foreground) or pasted in the Python window.
start()
