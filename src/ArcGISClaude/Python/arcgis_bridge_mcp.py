# -*- coding: utf-8 -*-
"""
arcgis_bridge_mcp.py  --  a zero-dependency MCP server (stdio transport) that
exposes the ArcGIS Pro live-session tools to the Claude Code engine.

It does NOT import arcpy. It forwards each tool call to the C# bridge running
inside ArcGIS Pro over a loopback TCP socket, so it runs on any Python 3
(stdlib only) -- including Pro's `arcgispro-py3`.

Transport (engine side): newline-delimited JSON-RPC 2.0 over stdin/stdout (the
MCP stdio contract). Transport (bridge side): newline-delimited JSON over a
127.0.0.1 socket. Anything that is not a protocol message goes to stderr.
"""

import os
import sys
import json
import uuid
import socket

# The add-in binds an ephemeral loopback port and passes it in via .mcp.json env.
BRIDGE_PORT = int(os.environ.get("ARCGIS_CLAUDE_PORT") or 0)

SERVER_NAME = "arcgis_bridge"
SERVER_VERSION = "0.1.0"
DEFAULT_PROTOCOL = "2025-06-18"
CALL_TIMEOUT = 300.0   # seconds; covers long geoprocessing / code runs

_NOT_RUNNING_MSG = (
    "The ArcGIS Pro live-project bridge isn't responding. Make sure ArcGIS Pro is "
    "running with the Claude add-in loaded — the bridge starts automatically with Pro "
    "and needs no manual step. (If Pro is open, it may still be loading.)"
)


def _log(msg):
    sys.stderr.write("[arcgis_bridge_mcp] %s\n" % msg)
    sys.stderr.flush()


# One cached connection reused across calls: the C# bridge serves a single client
# serially, and MCP requests here are already serial (one stdin reader), so no lock
# is needed. Reconnect transparently if it drops.
_sock = None
_sock_file = None


def _connect():
    """Return a live socket to the bridge, or None if it isn't reachable."""
    global _sock, _sock_file
    if _sock is not None:
        return _sock
    if not BRIDGE_PORT:
        return None
    try:
        s = socket.create_connection(("127.0.0.1", BRIDGE_PORT), timeout=5.0)
    except Exception:
        return None
    _sock = s
    _sock_file = s.makefile("r", encoding="utf-8", newline="\n")
    return _sock


def _reset():
    global _sock, _sock_file
    for c in (_sock_file, _sock):
        try:
            if c is not None:
                c.close()
        except Exception:
            pass
    _sock = None
    _sock_file = None


def _call_bridge(op, args, timeout=CALL_TIMEOUT):
    """Send one command over the loopback socket, wait for the reply. Returns dict."""
    payload = (json.dumps({"id": uuid.uuid4().hex, "op": op, "args": args or {}}) + "\n").encode("utf-8")

    # Send with one reconnect — a cached socket may have gone stale between calls.
    # Only the SEND is retried; once bytes are on the wire we must NOT resend, or a
    # mutating op would run twice.
    for attempt in (1, 2):
        s = _connect()
        if s is None:
            return {"ok": False, "error": _NOT_RUNNING_MSG, "data": None}
        try:
            s.settimeout(timeout)
            s.sendall(payload)
            break
        except Exception:
            _reset()
            if attempt == 2:
                return {"ok": False, "error": _NOT_RUNNING_MSG, "data": None}

    # Wait for the reply. A timeout or drop here is terminal — do NOT resend.
    reader = _sock_file
    if reader is None:
        return {"ok": False, "error": _NOT_RUNNING_MSG, "data": None}
    try:
        reply = reader.readline()
    except Exception as ex:
        _reset()
        return {"ok": False, "error": "bridge read failed: %s" % ex, "data": None}
    if reply == "":
        _reset()  # connection closed before a reply -> bridge went down
        return {"ok": False, "error": _NOT_RUNNING_MSG, "data": None}
    try:
        return json.loads(reply)
    except Exception as ex:
        _reset()
        return {"ok": False, "error": "bad reply from bridge: %s" % ex, "data": None}


# --------------------------------------------------------------------------- #
#  tool catalog  (names match pro_bridge.py ops)
# --------------------------------------------------------------------------- #
_S = lambda **p: {"type": "object", "properties": p, "additionalProperties": False}
_str = {"type": "string"}

TOOLS = [
    {
        "name": "run_python_current",
        "description": (
            "PRIMARY TOOL. Execute Python/ArcPy code IN-PROCESS against the user's "
            "open ArcGIS Pro project (arcpy.mp.ArcGISProject('CURRENT')). `arcpy` is "
            "imported; helpers `proj()` and `active_map()` are available. Assign a "
            "JSON-serializable value to a variable named `result` to return data; "
            "anything you print() is captured. Use this for anything the curated tools "
            "don't cover. Inspect schema with list_layers/get_field_list first."),
        "inputSchema": {"type": "object",
                        "properties": {"code": {"type": "string",
                                                "description": "Python source to exec."}},
                        "required": ["code"], "additionalProperties": False},
    },
    {
        "name": "run_python_file",
        "description": "Run a .py file (e.g. one you wrote into the workspace) in-process against the CURRENT project.",
        "inputSchema": {"type": "object",
                        "properties": {"path": {"type": "string", "description": "Absolute path to the .py file."}},
                        "required": ["path"], "additionalProperties": False},
    },
    {
        "name": "list_layers",
        "description": ("List layers in the active (or named) map of the CURRENT project, with "
                        "type/visibility/geometry and each feature layer's on-disk source path. "
                        "Feature counts are omitted by default (they can be slow on large sources); "
                        "pass include_counts=true to include them, or use feature_count for one layer."),
        "inputSchema": _S(
            map={"type": "string", "description": "Optional map name; defaults to the active map."},
            include_counts={"type": "boolean",
                            "description": "Include each feature layer's row count (slower). Default false."}),
    },
    {
        "name": "get_field_list",
        "description": "List fields (name/type/alias/length) of a layer or table in the CURRENT project.",
        "inputSchema": {"type": "object", "properties": {"layer": _str},
                        "required": ["layer"], "additionalProperties": False},
    },
    {
        "name": "describe_layer",
        "description": "Describe a layer: data type, geometry/shape type, spatial reference, catalog path, feature count.",
        "inputSchema": {"type": "object", "properties": {"layer": _str, "map": _str},
                        "required": ["layer"], "additionalProperties": False},
    },
    {
        "name": "feature_count",
        "description": "Return the feature/row count of a layer or table.",
        "inputSchema": {"type": "object", "properties": {"layer": _str},
                        "required": ["layer"], "additionalProperties": False},
    },
    {
        "name": "search_cursor",
        "description": "Read rows from a layer/table with an arcpy.da.SearchCursor. Returns the requested fields for matching rows.",
        "inputSchema": {"type": "object", "properties": {
            "layer": _str,
            "fields": {"type": "array", "items": {"type": "string"},
                       "description": "Field names, e.g. ['OID@','POP']. Tokens like SHAPE@XY allowed."},
            "where": {"type": "string", "description": "Optional SQL where-clause."},
            "limit": {"type": "integer", "description": "Optional max rows."}},
            "required": ["layer", "fields"], "additionalProperties": False},
    },
    {
        "name": "select_by_attribute",
        "description": "Select features on a LIVE map layer by SQL where-clause (highlights them in the open map).",
        "inputSchema": {"type": "object", "properties": {
            "layer": _str, "where": _str, "map": _str,
            "selection_type": {"type": "string",
                               "enum": ["NEW_SELECTION", "ADD_TO_SELECTION",
                                        "REMOVE_FROM_SELECTION", "SUBSET_SELECTION"]}},
            "required": ["layer", "where"], "additionalProperties": False},
    },
    {
        "name": "zoom_to_layer",
        "description": "Zoom the active map view to a layer's extent.",
        "inputSchema": {"type": "object", "properties": {"layer": _str, "map": _str},
                        "required": ["layer"], "additionalProperties": False},
    },
    {
        "name": "add_field",
        "description": "Add a field to a layer/table in the CURRENT project (modifies schema).",
        "inputSchema": {"type": "object", "properties": {
            "layer": _str, "field_name": _str,
            "field_type": {"type": "string",
                           "enum": ["TEXT", "FLOAT", "DOUBLE", "SHORT", "LONG", "DATE", "BLOB", "GUID"]},
            "field_length": {"type": "integer"}, "field_alias": _str},
            "required": ["layer", "field_name", "field_type"], "additionalProperties": False},
    },
    {
        "name": "calc_field",
        "description": "Calculate a field with an arcpy expression (modifies values).",
        "inputSchema": {"type": "object", "properties": {
            "layer": _str, "field": _str, "expression": _str,
            "expression_type": {"type": "string", "enum": ["PYTHON3", "ARCADE", "SQL"]}},
            "required": ["layer", "field", "expression"], "additionalProperties": False},
    },
    {
        "name": "update_field",
        "description": "Set a single field to a constant value for matching rows via an UpdateCursor (modifies values).",
        "inputSchema": {"type": "object", "properties": {
            "layer": _str, "field": _str, "value": {}, "where": _str},
            "required": ["layer", "field", "value"], "additionalProperties": False},
    },
    {
        "name": "run_geoprocessing",
        "description": "Run any geoprocessing tool by name (e.g. 'analysis.Buffer') against the live data. Returns GP messages + outputs.",
        "inputSchema": {"type": "object", "properties": {
            "tool": {"type": "string", "description": "e.g. 'analysis.Buffer' or 'management.AddField'."},
            "params": {"description": "Positional list OR keyword object of parameters."}},
            "required": ["tool"], "additionalProperties": False},
    },
    {
        "name": "ping",
        "description": "Health check: returns CURRENT project path, default gdb, and map names. Confirms the live bridge is up.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
]

_TOOL_NAMES = {t["name"] for t in TOOLS}


# --------------------------------------------------------------------------- #
#  JSON-RPC plumbing
# --------------------------------------------------------------------------- #
def _send(msg):
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()


def _result(req_id, result):
    _send({"jsonrpc": "2.0", "id": req_id, "result": result})


def _error(req_id, code, message):
    _send({"jsonrpc": "2.0", "id": req_id, "error": {"code": code, "message": message}})


def _handle_tools_call(req_id, params):
    name = (params or {}).get("name")
    args = (params or {}).get("arguments") or {}
    if name not in _TOOL_NAMES:
        _error(req_id, -32602, "unknown tool: %s" % name)
        return
    res = _call_bridge(name, args)
    if res.get("ok"):
        payload = res.get("data")
        # run_python_* return {stdout, result, error}: if the code raised, surface
        # stdout + traceback as an error so the engine can fix it and retry.
        if isinstance(payload, dict) and payload.get("error"):
            parts = []
            if payload.get("stdout"):
                parts.append("stdout:\n" + payload["stdout"])
            parts.append("error:\n" + payload["error"])
            _result(req_id, {"content": [{"type": "text", "text": "\n\n".join(parts)}],
                             "isError": True})
        else:
            text = json.dumps(payload, indent=2, ensure_ascii=False) if payload is not None else "ok"
            _result(req_id, {"content": [{"type": "text", "text": text}], "isError": False})
    else:
        _result(req_id, {"content": [{"type": "text", "text": str(res.get("error"))}],
                         "isError": True})


def _handle(message):
    method = message.get("method")
    req_id = message.get("id")
    params = message.get("params") or {}
    is_request = req_id is not None

    if method == "initialize":
        proto = params.get("protocolVersion", DEFAULT_PROTOCOL)
        _result(req_id, {
            "protocolVersion": proto,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        })
    elif method == "notifications/initialized":
        pass  # notification, no response
    elif method == "tools/list":
        _result(req_id, {"tools": TOOLS})
    elif method == "tools/call":
        _handle_tools_call(req_id, params)
    elif method == "ping":
        _result(req_id, {})
    elif is_request:
        _error(req_id, -32601, "method not found: %s" % method)
    # else: unknown notification -> ignore


def main():
    _log("starting; bridge port=%s" % BRIDGE_PORT)
    while True:
        line = sys.stdin.readline()
        if line == "":
            break  # stdin closed -> shut down
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except Exception as ex:
            _log("bad JSON: %s" % ex)
            continue
        try:
            _handle(message)
        except Exception as ex:
            _log("handler error: %s" % ex)
            if message.get("id") is not None:
                _error(message.get("id"), -32603, "internal error: %s" % ex)


if __name__ == "__main__":
    main()
