# ArcPy data recipes (Pro 3.x)

Paths come from `list_layers` `source` / `describe_layer` `catalogPath`; a
feature class inside a feature dataset also resolves as `gdb\name`. `ws` below
is `arcpy.Describe(path).workspace.catalogPath` (the gdb / folder / .sde).

## Reading

```python
# only the fields you need; tokens for geometry
with arcpy.da.SearchCursor(src, ["OID@", "NAME", "SHAPE@AREA"], "POP > 1000",
                           sql_clause=(None, "ORDER BY POP DESC")) as cur:
    top = [row for _, row in zip(range(10), cur)]          # top-10, stop early

# spatial filter (Pro 3.x): rows whose geometry intersects a geometry
with arcpy.da.SearchCursor(src, ["OID@"], spatial_filter=poly,
                           spatial_relationship="INTERSECTS") as cur: ...

# whole table into numpy / pandas (fast, memory-bound)
import pandas as pd
arr = arcpy.da.TableToNumPyArray(src, ["ZONE", "POP"], null_value=-1)
df = pd.DataFrame(arr); result = df.groupby("ZONE")["POP"].sum().to_dict()
# or a GP tool that writes a table
arcpy.analysis.Statistics(src, r"memory\stats", [["POP", "SUM"], ["POP", "MEAN"]], "ZONE")

arcpy.management.GetCount(src)[0]          # count as a string → int()
d = arcpy.Describe(src)                    # d.shapeType, d.spatialReference.factoryCode, d.extent, d.OIDFieldName
arcpy.ListFields(src)                      # f.name / f.type / f.editable / f.domain
```

Cursor field types: Date → `datetime`; DateOnly/TimeOnly → `date`/`time`;
BigInteger → int; Geometry → `arcpy.Geometry` (`.area`, `.length`, `.centroid`,
`.WKT`, `.projectAs(sr)`, `.buffer(d)` in the *geometry's own units*).

## Writing

```python
ws = arcpy.Describe(src).workspace.catalogPath
with arcpy.da.Editor(ws):                              # rolls back on exception
    with arcpy.da.UpdateCursor(src, ["POP", "POP_DEN", "AREASQMI"], "AREASQMI > 0") as cur:
        for pop, _, area in cur:
            cur.updateRow([pop, pop / area, area])
    with arcpy.da.InsertCursor(src, ["SHAPE@", "NAME"]) as cur:
        cur.insertRow([arcpy.PointGeometry(arcpy.Point(x, y), sr), "new"])
    with arcpy.da.UpdateCursor(src, ["OID@"], "NAME IS NULL") as cur:
        for _ in cur: cur.deleteRow()
```

- `Editor(ws, multiuser_mode=True)` for versioned / archived enterprise data
  (`describe_layer` → `isVersioned`). Feature services: no `Editor`, and every
  cursor write posts immediately.
- Inside an edit session don't open two write cursors on the same table.
- Set-a-field jobs are simpler as `management.CalculateField(src, "F",
  "expr", "PYTHON3", code_block, field_type)` — it creates the field when
  missing (also `calc_field`). Expressions: `!POP! / !AREASQMI!`,
  `!NAME!.upper()`, `!shape.geodesicArea@SQUAREKILOMETERS!`,
  `!shape.centroid.X!`; Arcade: `$feature.POP`; SQL (gdb/db only, no code_block).
- Nulls: cursor rows carry `None`; SQL `IS NULL`; CalculateField expressions
  must handle `None` in a code block.
- Schema: `management.AddField(src, name, "DOUBLE"|"LONG"|"TEXT"|"DATE"|"BIGINTEGER"…, field_length=…, field_alias=…)`,
  `AlterField` (rename/alias), `DeleteField`, `AddIndex(src, ["KEY"], "KEY_IDX")`.
  Domains: `management.CreateDomain` + `AddCodedValueToDomain` + `AssignDomainToField`;
  `arcpy.da.ListDomains(ws)` → `.codedValues` dict.

## SQL where-clauses

| Workspace (`describe_layer` → `workspaceType`) | field | string | date |
|---|---|---|---|
| LocalDatabase (file / mobile gdb) | `POP` | `'x'` | `date '2020-01-01'` or `timestamp '2020-01-01 00:00:00'` |
| FileSystem (shapefile / dBASE) | `"POP"` | `'x'` | `date '2020-01-01'` |
| RemoteDatabase (enterprise) | per DBMS | `'x'` | `timestamp`/`date` per DBMS |
| Service | `POP` | `'x'` | `date '2020-01-01'` or `timestamp` |

`arcpy.AddFieldDelimiters(src, "POP")` returns the right delimiter for any of
them. Everywhere: `LIKE '%main%'`, `IN ('a','b')`, `IS NULL`, `NOT`, `AND`/`OR`
with parentheses. Shapefiles ignore `ORDER BY`; only databases support
`DISTINCT`. Case-sensitivity of LIKE depends on the DBMS.

## Joins, relates, lookups

```python
# permanent: copies OWNER into src (index the key first on big tables)
arcpy.management.JoinField(src, "NAME", owners, "PARCEL", ["OWNER"])

# fastest lookup for a one-off calculation: dict + cursor
lookup = {k: v for k, v in arcpy.da.SearchCursor(owners, ["PARCEL", "OWNER"])}
with arcpy.da.UpdateCursor(src, ["NAME", "OWNER"]) as cur:
    for name, _ in cur: cur.updateRow([name, lookup.get(name)])

# map-only join on a live layer (the user sees it, the dataset does not change)
arcpy.management.AddJoin(lyr, "NAME", owners, "PARCEL")      # field names become Parcels.NAME
arcpy.management.RemoveJoin(lyr)
```

## Geometry and coordinate systems

- `sr = arcpy.SpatialReference(26911)` (WKID) / `arcpy.SpatialReference("WGS 1984")`;
  `sr.type` is `"Geographic"` or `"Projected"`, `sr.linearUnitName`.
- Distances and areas: on projected data `SHAPE@AREA`/`.area` are in the SR's
  unit; on geographic data use `geom.getArea("GEODESIC", "SQUAREMETERS")`,
  `geom.getLength("GEODESIC", "METERS")`, or GP tools with linear units
  (`"100 Meters"`) and `method="GEODESIC"` (Buffer, Near), or `management.Project`
  first. `geom.buffer(100)` on WGS84 is 100 *degrees*.
- Reproject on read: `SearchCursor(src, ["SHAPE@"], spatial_reference=sr)`.
- Build geometry: `arcpy.PointGeometry(arcpy.Point(x, y), sr)`,
  `arcpy.Polyline(arcpy.Array([pt1, pt2]), sr)`, `arcpy.Polygon(arcpy.Array([...ring...]), sr)`,
  `arcpy.FromWKT(wkt, sr)`, `arcpy.AsShape(geojson_dict)` (`AsShape(esri_json, True)`).
- Relationships: `a.intersect(b, 4)` (4 = polygon output dim), `a.union(b)`,
  `a.contains(b)`, `a.distanceTo(b)`, `a.projectAs(sr)`.
- Extents: `arcpy.Describe(src).extent` (`.XMin` …); `env` `extent` clips GP.

## Geoprocessing

```python
out = arcpy.analysis.Buffer(src, r"memory\buf", "100 Meters", method="GEODESIC")[0]   # [0] = output path
res = arcpy.analysis.Buffer(...); res.getMessages(1)   # warnings only; res.status
with arcpy.EnvManager(overwriteOutput=True, outputCoordinateSystem=sr, extent="MAXOF"):
    arcpy.analysis.Clip(src, clip_fc, out_fc)
arcpy.Exists(path); arcpy.management.Delete(path)      # clean up memory\ outputs when done
arcpy.CreateUniqueName("buf", ws)                      # unique output name in ws
```

- Tool names: `arcpy.<module>.<Tool>` — `analysis`, `management`, `conversion`,
  `sa` (Spatial Analyst, licence), `na`, `stats`, `cartography`, `edit`, `ga`.
  `arcpy.Usage("Buffer_analysis")` prints a signature; the error text of a
  failed tool (`ExecuteError`) already lists the bad parameter.
- Selection-aware: pass a *layer* (object or map layer name) to process only
  its selected / definition-query rows; pass a path for the whole dataset.
- Outputs are not added to the map — see CLAUDE.md.
- Long tools: chunk by attribute or extent; the bridge cuts a call at ~5 min.

## Rasters (Spatial Analyst)

```python
if arcpy.CheckExtension("Spatial") == "Available":
    arcpy.CheckOutExtension("Spatial")
    from arcpy.sa import Raster, Slope, Con, ZonalStatisticsAsTable
    dem = Raster(dem_path)                    # dem.minimum / .maximum / .meanCellWidth / .spatialReference
    slope = Slope(dem, "DEGREE")              # in-memory until .save()
    steep = Con(slope > 30, 1)                # map algebra
    steep.save(os.path.join(default_gdb, "steep"))
    ZonalStatisticsAsTable(zones, "ZONE", dem, r"memory\zs", "DATA", "MEAN")
    arcpy.CheckInExtension("Spatial")
else:
    ...tell the user Spatial Analyst is not licensed...
```

`arcpy.RasterToNumPyArray(raster)` / `arcpy.NumPyArrayToRaster` for custom
math; `management.CopyRaster`, `Resample`, `ProjectRaster`, `Clip` (with
`"#"` rectangle + mask) for the common transforms. Rasters in a file gdb are
`gdb\name`; on disk `folder\dem.tif`.

## Files on disk and exports

```python
arcpy.conversion.ExportFeatures(src_or_layer, r"C:\out\parcels.shp")   # honours selection when given a layer
arcpy.conversion.ExportTable(src, r"C:\out\parcels.csv")
arcpy.conversion.FeaturesToJSON(src, r"C:\out\parcels.geojson", geoJSON="GEOJSON")
arcpy.conversion.TableToExcel(src, r"C:\out\parcels.xlsx")
arcpy.conversion.JSONToFeatures(...); arcpy.management.XYTableToPoint(csv, out, "lon", "lat", coordinate_system=sr)
```

Write derived files to the project home folder (`ping` → `home_folder`) or
the workspace and return the path; the user can open either from Pro.
