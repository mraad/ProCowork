using System;
using System.Collections.Generic;
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
    /// each feature layer's on-disk <b>data-source path</b>, which Claude then uses to
    /// write robust, path-based ArcPy.
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
            var map = MapView.Active?.Map;
            if (map != null)
            {
                info["active_map"] = map.Name;
                info["layers"] = map.GetLayersAsFlattenedList().Count;
            }
            if (proj != null) info["default_gdb"] = proj.DefaultGeodatabasePath;
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
                    ["visible"] = layer.IsVisible,
                };
                if (layer is FeatureLayer fl)
                {
                    info["is_feature"] = true;
                    try
                    {
                        using (var table = fl.GetTable())
                        {
                            info["source"] = DataSourcePath(table);
                            if (includeCounts) info["count"] = table.GetCount();
                            if (table is FeatureClass fc)
                                using (var def = fc.GetDefinition())
                                    info["geometry"] = def.GetShapeType().ToString();
                        }
                    }
                    catch (Exception ex) { info["source_error"] = ex.Message; }
                }
                else if (layer is GroupLayer) info["is_group"] = true;
                else info["is_raster"] = layer is RasterLayer;
                layers.Add(info);
            }
            return layers;
        }

        private static object GetFieldList(JObject args)
        {
            using (var table = FindTable(args))
            using (var def = table.GetDefinition())
            {
                return def.GetFields().Select(f => (object)new Dictionary<string, object>
                {
                    ["name"] = f.Name,
                    ["type"] = f.FieldType.ToString(),
                    ["alias"] = f.AliasName,
                    ["length"] = f.Length,
                }).ToList();
            }
        }

        private static object DescribeLayer(JObject args)
        {
            using (var table = FindTable(args))
            {
                var info = new Dictionary<string, object>
                {
                    ["name"] = (string)args["layer"],
                    ["dataType"] = table is FeatureClass ? "FeatureClass" : "Table",
                    ["catalogPath"] = DataSourcePath(table),
                    ["count"] = table.GetCount(),
                };
                if (table is FeatureClass fc)
                    using (var def = fc.GetDefinition())
                    {
                        info["shapeType"] = def.GetShapeType().ToString();
                        var sr = def.GetSpatialReference();
                        if (sr != null)
                            info["spatialReference"] = new Dictionary<string, object>
                            {
                                ["name"] = sr.Name,
                                ["factoryCode"] = sr.Wkid,
                            };
                    }
                return info;
            }
        }

        private static object FeatureCount(JObject args)
        {
            using (var table = FindTable(args))
                return new Dictionary<string, object> { ["count"] = table.GetCount() };
        }

        private static object SelectByAttribute(JObject args)
        {
            var fl = FindFeatureLayer(args);
            var method = ((string)args["selection_type"]) switch
            {
                "ADD_TO_SELECTION" => SelectionCombinationMethod.Add,
                "REMOVE_FROM_SELECTION" => SelectionCombinationMethod.Subtract,
                "SUBSET_SELECTION" => SelectionCombinationMethod.And,
                _ => SelectionCombinationMethod.New,
            };
            using (var sel = fl.Select(new QueryFilter { WhereClause = (string)args["where"] }, method))
                return new Dictionary<string, object> { ["selected"] = sel.GetCount() };
        }

        private static object ZoomToLayer(JObject args)
        {
            var fl = FindFeatureLayer(args);
            var view = MapView.Active;
            if (view == null)
                return new Dictionary<string, object> { ["zoomed"] = false, ["reason"] = "no active map view" };
            bool ok = view.ZoomTo(fl);
            return new Dictionary<string, object> { ["zoomed"] = ok };
        }

        // --- helpers ----------------------------------------------------------- //

        private static Map ResolveMap(string name)
        {
            if (string.IsNullOrEmpty(name)) return MapView.Active?.Map;
            return Project.Current?.GetItems<MapProjectItem>()
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.GetMap() ?? MapView.Active?.Map;
        }

        private static FeatureLayer FindFeatureLayer(JObject args)
        {
            var name = (string)args["layer"];
            var map = ResolveMap((string)args["map"])
                ?? throw new InvalidOperationException("No active map is open.");
            var match = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new InvalidOperationException(
                "Feature layer '" + name + "' not found in the active map.");
        }

        /// <summary>The underlying Table for the named feature layer or standalone table.</summary>
        private static Table FindTable(JObject args)
        {
            var name = (string)args["layer"];
            var map = ResolveMap((string)args["map"])
                ?? throw new InvalidOperationException("No active map is open.");

            var fl = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (fl != null) return fl.GetTable();

            var st = map.GetStandaloneTablesAsFlattenedList()
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (st != null) return st.GetTable();

            throw new InvalidOperationException(
                "Layer or table '" + name + "' not found in the active map.");
        }

        /// <summary>
        /// On-disk data-source path, e.g. <c>C:\data\city.gdb\roads</c>. Correct for
        /// file/enterprise geodatabases; for shapefiles the file extension may be absent
        /// (Claude can fall back to run_python_current for those).
        /// </summary>
        private static string DataSourcePath(Table table)
        {
            try
            {
                using (var ds = table.GetDatastore())
                {
                    var container = ds?.GetPath()?.LocalPath;
                    var name = table.GetName();
                    return string.IsNullOrEmpty(container) ? name : System.IO.Path.Combine(container, name);
                }
            }
            catch { return table.GetName(); }
        }
    }
}
