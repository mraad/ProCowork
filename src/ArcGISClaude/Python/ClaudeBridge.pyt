# -*- coding: utf-8 -*-
"""
ClaudeBridge.pyt -- a tiny Python toolbox used by the add-in to auto-start the
in-process ArcPy bridge.

The 'Start Claude Bridge' tool runs in the FOREGROUND (canRunInBackground = False),
which means it executes inside ArcGIS Pro's own in-process Python -- the only place
arcpy.mp.ArcGISProject("CURRENT") resolves. It exec()s pro_bridge.py, which spawns
a daemon thread that keeps serving after this tool returns.
"""

import os
import arcpy


class Toolbox(object):
    def __init__(self):
        self.label = "Claude Bridge"
        self.alias = "claudebridge"
        self.tools = [StartBridge]


class StartBridge(object):
    def __init__(self):
        self.label = "Start Claude Bridge"
        self.description = ("Start the in-process ArcPy bridge so the Claude panel "
                            "can run code against the CURRENT project.")
        self.canRunInBackground = False  # foreground => in-process => CURRENT works

    def getParameterInfo(self):
        return []

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        here = os.path.dirname(__file__)
        path = os.path.join(here, "pro_bridge.py")
        with open(path, "r", encoding="utf-8") as f:
            code = f.read()
        g = {"__file__": path, "__name__": "__claude_bridge__"}
        exec(compile(code, path, "exec"), g)
        messages.addMessage("Claude ArcPy bridge started (watching the IPC folder).")
