using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Framework.Threading.Tasks;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The fast read path: answers the live-state tools straight from the .NET SDK on
    /// the main CIM thread (<see cref="QueuedTask"/>). It never touches arcpy and never
    /// resolves <c>"CURRENT"</c>, so these calls are instant and can't hit the CURRENT
    /// failure that made the old daemon fragile. Crucially, <c>list_layers</c> reports
    /// each layer's <b>data-source path</b>, which Claude then uses to write robust,
    /// path-based ArcPy — and the layer's selection count and definition query, which
    /// that path-based ArcPy cannot see (a path is the whole dataset; only the live
    /// layer is the user's filtered view of it).
    ///
    /// Results are shaped for a language model: optional keys are omitted rather than
    /// emitted as null/false, because every character stays in the engine's context.
    /// </summary>
    internal static class AppStateOps
    {
        // One table is both the "is this a read op?" answer and the router, so a
        // tool can't be routed here without a handler (or vice versa).
        private static readonly Dictionary<string, Func<JObject, object>> Handlers =
            new Dictionary<string, Func<JObject, object>>(StringComparer.Ordinal)
        {
            ["ping"] = _ => Ping(),
            ["list_layers"] = ListLayers,
            ["get_field_list"] = GetFieldList,
            ["describe_layer"] = DescribeLayer,
            ["feature_count"] = FeatureCount,
            ["select_by_attribute"] = SelectByAttribute,
            ["zoom_to_layer"] = ZoomToLayer,
        };

        public static bool Handles(string op) => Handlers.ContainsKey(op);

        /// <summary>Runs the op on the CIM thread and returns a JSON-serializable result.</summary>
        public static Task<object> DispatchAsync(string op, JObject args)
            => QueuedTask.Run(() => Handlers[op](args));

        private static object Ping()
        {
            var proj = Project.Current;
            var info = new Dictionary<string, object>
            {
                ["connected"] = proj != null,
                ["project"] = proj?.URI,
            };
            if (proj != null)
            {
                info["default_gdb"] = proj.DefaultGeodatabasePath;
                info["home_folder"] = proj.HomeFolderPath;
                // Every map, not just the active one: when a layout or the catalog
                // view is active there is no active map, and the engine needs the
                // names to pass an explicit `map`.
                info["maps"] = proj.GetItems<MapProjectItem>().Select(i => i.Name).ToList();
                // Pending edits in Pro's own edit session can collide with ArcPy
                // edits to the same workspace — the engine should say so first.
                if (proj.HasEdits) info["unsaved_edits"] = true;
            }
            var map = MapView.Active?.Map;
            if (map != null)
            {
                info["active_map"] = map.Name;
                info["layers"] = map.GetLayersAsFlattenedList().Count;
            }
            // Decides which GP tools exist at all (Basic / Standard / Advanced).
            try { info["license"] = ArcGIS.Core.Licensing.LicenseInformation.Level.ToString(); }
            catch { }
            return info;
        }

        private static object ListLayers(JObject args)
        {
            var map = ResolveMap((string)args["map"]);
            if (map == null) return new List<object>(); // no map open is not an error

            // Counting rows means a GetCount() per feature layer, which can force a
            // full scan on large/enterprise sources. Skip it by default (feature_count
            // gives it on demand); include only when the caller explicitly asks.
            bool includeCounts = (bool?)args["include_counts"] ?? false;

            var layers = new List<object>();
            foreach (var layer in map.GetLayersAsFlattenedList())
            {
                var info = new Dictionary<string, object>
                {
                    ["name"] = layer.Name,
                    ["type"] = layer.GetType().Name, // FeatureLayer, RasterLayer, GroupLayer, …
                    ["visible"] = layer.IsVisible,
                };
                if (layer.Parent is GroupLayer group) info["group"] = group.Name;
                try
                {
                    if (layer is FeatureLayer fl) AddRowSource(info, fl, includeCounts);
                    else if (layer is RasterLayer rl)
                    {
                        using (var raster = rl.GetRaster())
                        using (var dataset = raster?.GetRasterDataset())
                            if (dataset != null) info["source"] = DataSourcePath(dataset, rl);
                    }
                }
                catch (Exception ex) { info["source_error"] = ex.Message; }
                layers.Add(info);
            }
            foreach (var tableView in map.GetStandaloneTablesAsFlattenedList())
            {
                var info = new Dictionary<string, object>
                {
                    ["name"] = tableView.Name,
                    ["type"] = "StandaloneTable",
                };
                try { AddRowSource(info, tableView, includeCounts); }
                catch (Exception ex) { info["source_error"] = ex.Message; }
                layers.Add(info);
            }
            return layers;
        }

        /// <summary>
        /// The parts of a feature layer / standalone table ArcPy needs: where the data
        /// lives, and how the live layer narrows it (selection, definition query).
        /// </summary>
        private static void AddRowSource(Dictionary<string, object> info, IDisplayTable rows, bool includeCounts)
        {
            using (var table = rows.GetTable())
            {
                if (table == null) { info["source_error"] = "broken data source"; return; }
                info["source"] = DataSourcePath(table, (MapMember)rows);
                if (includeCounts) info["count"] = table.GetCount();
                if (table is FeatureClass fc)
                    using (var def = fc.GetDefinition())
                        info["geometry"] = def.GetShapeType().ToString();
            }
            AddLiveFilters(info, rows);
        }

        private static void AddLiveFilters(Dictionary<string, object> info, IDisplayTable rows)
        {
            long selected = SelectionCount(rows);
            if (selected > 0) info["selected"] = selected;
            var query = rows is BasicFeatureLayer bfl ? bfl.DefinitionQuery
                      : (rows as StandaloneTable)?.DefinitionQuery;
            if (!string.IsNullOrWhiteSpace(query)) info["definition_query"] = query;
        }

        // Neither member lives on IDisplayTable itself, only on its two implementers.
        private static long SelectionCount(IDisplayTable rows)
            => rows is BasicFeatureLayer bfl ? bfl.SelectionCount
             : (rows as StandaloneTable)?.SelectionCount ?? 0;

        private static object GetFieldList(JObject args)
        {
            using (var table = OpenTable(FindRows(args)))
            using (var def = table.GetDefinition())
            {
                return def.GetFields().Select(f =>
                {
                    var info = new Dictionary<string, object>
                    {
                        ["name"] = f.Name,
                        ["type"] = f.FieldType.ToString(),
                    };
                    if (!string.IsNullOrEmpty(f.AliasName) && f.AliasName != f.Name) info["alias"] = f.AliasName;
                    if (f.FieldType == FieldType.String) info["length"] = f.Length;
                    // What a write needs to know: system-maintained fields (OID,
                    // Shape_Area, editor tracking) reject it, and a coded-value
                    // domain means the stored value is the code, not the label.
                    if (!f.IsEditable) info["editable"] = false;
                    if (!f.IsNullable) info["nullable"] = false;
                    try
                    {
                        using (var domain = f.GetDomain())
                            if (domain != null) info["domain"] = domain.GetName();
                    }
                    catch { }
                    return (object)info;
                }).ToList();
            }
        }

        private static object DescribeLayer(JObject args)
        {
            var rows = FindRows(args);
            using (var table = OpenTable(rows))
            {
                // Key names follow arcpy.Describe where one exists, so the engine
                // reads them the way it would read a Describe object.
                var info = new Dictionary<string, object>
                {
                    ["name"] = ((MapMember)rows).Name,
                    ["dataType"] = table is FeatureClass ? "FeatureClass" : "Table",
                    ["catalogPath"] = DataSourcePath(table, (MapMember)rows),
                    ["count"] = table.GetCount(),
                };
                try
                {
                    // SQL dialect, edit-session and schema rules all hang off this.
                    using (var ds = table.GetDatastore())
                        info["workspaceType"] = ds is Geodatabase gdb
                            ? gdb.GetGeodatabaseType().ToString()
                            : ds is FileSystemDatastore ? "FileSystem" : ds?.GetType().Name;
                }
                catch { }
                try
                {
                    if (table.GetRegistrationType() == RegistrationType.Versioned) info["isVersioned"] = true;
                }
                catch { }
                using (var def = table.GetDefinition())
                {
                    if (def.HasObjectID()) info["OIDFieldName"] = def.GetObjectIDField();
                    if (def is FeatureClassDefinition fdef)
                    {
                        info["shapeType"] = fdef.GetShapeType().ToString();
                        info["shapeFieldName"] = fdef.GetShapeField();
                        if (fdef.HasZ()) info["hasZ"] = true;
                        if (fdef.HasM()) info["hasM"] = true;
                        var sr = fdef.GetSpatialReference();
                        if (sr != null)
                            info["spatialReference"] = new Dictionary<string, object>
                            {
                                ["name"] = sr.Name,
                                ["factoryCode"] = sr.Wkid,
                                // Geographic = degrees: planar distance/area math on it is
                                // wrong, so the engine must go geodesic or project first.
                                ["type"] = sr.IsGeographic ? "Geographic" : sr.IsProjected ? "Projected" : "Unknown",
                                ["unit"] = sr.Unit?.Name,
                            };
                    }
                }
                if (table is FeatureClass fc)
                {
                    try
                    {
                        var ext = fc.GetExtent();
                        if (ext != null && !ext.IsEmpty)
                            info["extent"] = new[] { ext.XMin, ext.YMin, ext.XMax, ext.YMax };
                    }
                    catch { }
                }
                AddLiveFilters(info, rows);
                return info;
            }
        }

        private static object FeatureCount(JObject args)
        {
            var rows = FindRows(args);
            using (var table = OpenTable(rows))
            {
                var info = new Dictionary<string, object> { ["count"] = table.GetCount() };
                long selected = SelectionCount(rows);
                if (selected > 0) info["selected"] = selected;
                return info;
            }
        }

        private static object SelectByAttribute(JObject args)
        {
            var rows = FindRows(args);
            var type = (string)args["selection_type"];
            if (type == "CLEAR_SELECTION")
            {
                rows.ClearSelection();
                return new Dictionary<string, object> { ["selected"] = 0 };
            }

            var where = (string)args["where"];
            if (where == null)
                throw new ArgumentException("'where' is required unless selection_type is CLEAR_SELECTION.");
            var method = type switch
            {
                "ADD_TO_SELECTION" => SelectionCombinationMethod.Add,
                "REMOVE_FROM_SELECTION" => SelectionCombinationMethod.Subtract,
                "SUBSET_SELECTION" => SelectionCombinationMethod.And,
                _ => SelectionCombinationMethod.New,
            };
            using (var sel = rows.Select(new QueryFilter { WhereClause = where }, method))
                return new Dictionary<string, object> { ["selected"] = sel.GetCount() };
        }

        private static object ZoomToLayer(JObject args)
        {
            var name = (string)args["layer"];
            var map = RequireMap(args);
            var layer = map.GetLayersAsFlattenedList().FirstOrDefault(l => NameIs(l, name))
                ?? throw new InvalidOperationException("Layer '" + name + "' not found in map '" + map.Name + "'.");

            var view = MapView.Active;
            if (view == null) return NotZoomed("no active map view");
            if (view.Map?.URI != map.URI) return NotZoomed("map '" + map.Name + "' is not the active view");

            bool selectedOnly = (bool?)args["selected_only"] ?? false;
            if (selectedOnly && !(layer is BasicFeatureLayer bfl && bfl.SelectionCount > 0))
                return NotZoomed("layer has no selection");
            return new Dictionary<string, object> { ["zoomed"] = view.ZoomTo(layer, selectedOnly) };
        }

        private static object NotZoomed(string reason)
            => new Dictionary<string, object> { ["zoomed"] = false, ["reason"] = reason };

        // --- helpers ----------------------------------------------------------- //

        private static bool NameIs(MapMember member, string name)
            => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase);

        private static Map ResolveMap(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return MapView.Active?.Map;

            var project = Project.Current;
            if (project == null)
                throw new InvalidOperationException("No project is open; cannot find map '" + name + "'.");

            var item = project.GetItems<MapProjectItem>()
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                throw new InvalidOperationException("Map '" + name + "' was not found in the current project.");
            return item.GetMap();
        }

        private static Map RequireMap(JObject args)
            => ResolveMap((string)args["map"])
               ?? throw new InvalidOperationException(
                   "No active map view. Pass `map` (names are in ping's `maps`) or ask the user to click a map.");

        /// <summary>
        /// The named feature layer or standalone table — the two map members that have
        /// rows, and the two <c>list_layers</c> reports a <c>source</c> for.
        /// </summary>
        private static IDisplayTable FindRows(JObject args)
        {
            var name = (string)args["layer"];
            var map = RequireMap(args);
            IDisplayTable match = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => NameIs(l, name));
            match = match ?? map.GetStandaloneTablesAsFlattenedList().FirstOrDefault(t => NameIs(t, name));
            return match ?? throw new InvalidOperationException(
                "Layer or table '" + name + "' not found in map '" + map.Name + "'.");
        }

        private static Table OpenTable(IDisplayTable rows)
            => rows.GetTable() ?? throw new InvalidOperationException(
                "'" + ((MapMember)rows).Name + "' has a broken data source.");

        /// <summary>
        /// The path ArcPy opens this dataset by: <c>C:\data\city.gdb\roads</c> for a
        /// geodatabase, <c>C:\data\roads.shp</c> for a shapefile (the SDK reports the
        /// name without the extension ArcPy needs), <c>memory\roads</c> for the memory
        /// workspace, and <c>https://…/FeatureServer/0</c> for a feature service.
        /// </summary>
        private static string DataSourcePath(Dataset dataset, MapMember member)
        {
            var name = dataset.GetName();
            try
            {
                using (var ds = dataset.GetDatastore())
                {
                    if (ds is Geodatabase gdb && gdb.GetGeodatabaseType() == GeodatabaseType.Memory)
                        return "memory\\" + name;

                    var uri = ds?.GetPath();
                    if (uri == null) return name;
                    if (!uri.IsFile)
                    {
                        // A service sublayer is addressed by its numeric id, which only
                        // the layer's data connection carries (the dataset name is not it).
                        var connection = (member as Layer)?.GetDataConnection()
                            ?? (member as StandaloneTable)?.GetDataConnection();
                        var id = (connection as ArcGIS.Core.CIM.CIMStandardDataConnection)?.Dataset;
                        return uri.AbsoluteUri.TrimEnd('/') + "/" + (string.IsNullOrEmpty(id) ? name : id);
                    }

                    var path = Path.Combine(uri.LocalPath, name);
                    if (ds is FileSystemDatastore && !Path.HasExtension(name))
                    {
                        if (File.Exists(path + ".shp")) return path + ".shp";
                        if (File.Exists(path + ".dbf")) return path + ".dbf";
                    }
                    return path;
                }
            }
            catch { return name; }
        }
    }
}
