# arcpy.mp on the live map (Pro 3.x, verified in Pro 3.7)

Everything here runs in `run_python_current` with the pre-bound `aprx` and `m`
(guard both). `arcpy.mapping` (ArcMap) does not exist in Pro — never use it.
Changes to layers, symbology and views apply to the open map immediately;
the project file changes only when the user saves.

## Layers and maps

```python
lyr = m.listLayers("Parcels")[0]           # wildcard match, case-insensitive
tbl = m.listTables("Owners")[0]            # standalone tables are not layers
other = aprx.listMaps("Second")[0]         # any map, active or not
other.openView()                           # make it the active view (CURRENT only)

lyr.visible = False
lyr.transparency = 30                      # percent
m.moveLayer(lyr, m.listLayers("Roads")[0], "BEFORE")   # draw order
grp = m.createGroupLayer("Results")        # then m.addLayerToGroup(grp, lyr)
m.removeLayer(lyr); m.removeTable(tbl)

# add data (paths from list_layers / ping.default_gdb)
new = m.addDataFromPath(r"C:\proj\data.gdb\Buffers")      # gdb, shapefile, raster, service URL
# the memory workspace cannot be added by path — go through a GP layer:
new = m.addLayer(arcpy.management.MakeFeatureLayer(r"memory\buf", "Buffers").getOutput(0))[0]
m.addTable(arcpy.management.MakeTableView(r"memory\stats", "Stats").getOutput(0))
```

`lyr.dataSource` is the path (memory layers report
`INSTANCE_ID=GPProMemoryWorkspace\name`). `lyr.isBroken` says the source is
missing; `lyr.supports("DEFINITIONQUERY")` etc. before touching a property a
layer type may not have (group, basemap, service layers).

## Definition query and selection

```python
lyr.definitionQuery = "POP > 1000"         # the user now sees only these rows
lyr.definitionQuery = ""                   # clear (the query stays listed but inactive)
lyr.listDefinitionQueries()                # [{'name', 'sql', 'isActive'}]

lyr.setSelectionSet([12, 15], "NEW")       # OIDs; NEW | UNION | INTERSECT | SUBTRACT
oids = lyr.getSelectionSet()               # set of OIDs, or None when nothing is selected
m.clearSelection()
```

Cursors and GP tools given `lyr` (the object) respect both the definition
query and the selection. Given `lyr.dataSource` (the path) they see every row.

## Symbology

Always: get `sym = lyr.symbology`, change it, assign it back
(`lyr.symbology = sym`) — the property returns a copy.

```python
sym = lyr.symbology
sym.updateRenderer("GraduatedColorsRenderer")
sym.renderer.classificationField = "POP"
sym.renderer.breakCount = 5                # or classBreaks[i].upperBound / .label
ramps = aprx.listColorRamps("Yellow-Orange-Red (5 Classes)")
if ramps: sym.renderer.colorRamp = ramps[0]
lyr.symbology = sym

sym = lyr.symbology
sym.updateRenderer("UniqueValueRenderer")
sym.renderer.fields = ["ZONE"]             # groups[0].items → .values / .label / .symbol
lyr.symbology = sym

sym = lyr.symbology
sym.updateRenderer("SimpleRenderer")
sym.renderer.symbol.color = {"RGB": [0, 112, 255, 60]}        # RGBA, alpha 0–100
sym.renderer.symbol.outlineColor = {"RGB": [0, 0, 0, 100]}
sym.renderer.symbol.size = 1.0             # point size / line width / outline width
# or a gallery symbol: sym.renderer.symbol.applySymbolFromGallery("Circle 1")
lyr.symbology = sym
```

`sym.renderer.type` tells you what is there now. Rasters: `sym.updateColorizer(
"RasterStretchColorizer")`, then `sym.colorizer.stretchType`, `.colorRamp`.
Anything the symbology API doesn't expose lives in the CIM (below).

## Labels

```python
lyr.showLabels = True
lc = lyr.listLabelClasses()[0]
lc.expression = "$feature.NAME"            # Arcade (default engine)
lc.expressionEngine = "Arcade"             # or "Python": "[NAME]"-style → "!NAME!"
lc.visible = True
# lc.SQLQuery = "POP > 1000"  limits which features get labels
```

Font, size, halo and placement are CIM-only: `d = lyr.getDefinition("V3")`,
`d.labelClasses[0].textSymbol.symbol.height = 10`, `lyr.setDefinition(d)`.

## The view: extent, zoom, bookmarks

```python
mv = aprx.activeView                       # MapView when a map is active (else a Layout / None)
ext = mv.getLayerExtent(lyr, selection_only=True)    # arcpy.Extent
mv.camera.setExtent(ext)                   # zoom the live view
mv.zoomToAllLayers()
mv.panToExtent(ext)
mv.camera.scale = 24000                    # 1:24,000
mv.camera.X, mv.camera.Y                   # centre, in the map's spatial reference

bm = mv.createBookmark("Site A", "description")
m.listBookmarks(); mv.zoomToBookmark(bm)
mv.exportToPNG(r"C:\out\map.png", width=1600, height=1000, resolution=96)
```

`mv` is `None` when no view is active (`m` is then also `None`): tell the
user to click a map, or `some_map.openView()`.

## Layouts and export

```python
lyt = aprx.listLayouts("Print*")[0]        # existing
lyt = aprx.createLayout(11, 8.5, "INCH", "Site map")     # new
mf = lyt.createMapFrame(arcpy.Extent(0.5, 0.5, 10.5, 8.0), m, "Main Map")   # page units
mf.camera.setExtent(aprx.activeView.getLayerExtent(lyr))
mf = lyt.listElements("MAPFRAME_ELEMENT", "Main Map")[0]                    # existing frame
lyt.listElements()                         # TEXT_ELEMENT, LEGEND_ELEMENT, …; each has .name/.text
lyt.exportToPDF(r"C:\out\site.pdf", resolution=200)
lyt.openView()
```

Map series: `lyt.mapSeries` (`.enabled`, `.pageCount`, `.exportToPDF(...,
"ALL")`). Element creation (`createTextElement`, `createLegend…`) hangs off
`aprx`: `aprx.createTextElement(lyt, arcpy.Point(1, 1), "POINT", "Title")`.

## CIM: everything else

```python
d = lyr.getDefinition("V3")                # CIMFeatureLayer (V3 = Pro 3.x schema)
d.minScale = 500000                        # visibility range
d.featureTable.displayField = "NAME"
d.renderer.symbol.symbol.symbolLayers[0].color.values = [255, 0, 0, 100]   # deep symbol edits
lyr.setDefinition(d)

md = m.getDefinition("V3")                 # CIMMap: m.setDefinition(md)
```

Inspect with `dir(d)` / `vars(d)` before guessing a path; CIM class and
property names match the JSON in a `.lyrx` (`lyr.saveACopy(r"C:\out\x.lyrx")`
shows the full structure). Never edit `d.uRI` / `featureTable.dataConnection`
by hand — use `lyr.updateConnectionProperties(old, new)` to repoint data.

## Project-level

```python
aprx.filePath, aprx.homeFolder, aprx.defaultGeodatabase
aprx.listMaps(); aprx.listLayouts(); aprx.listBrokenDataSources()
aprx.createMap("Analysis")                 # no view until .openView()
aprx.importDocument(r"C:\old\project.mxd") # ArcMap import
# aprx.save() only when the user asks; aprx.saveACopy(path) is always safe
```
