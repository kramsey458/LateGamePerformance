using System;

namespace LateGamePerformance
{
    // The events after which a cached route map can be unbuilt or newly buildable, so RouteMaps and TerrainMaps
    // scan their caches only after one of them instead of on every tick (0.27 ms per tick of walking every cached
    // map in the logged colony). A new cache entry (a building finished), a nav-mesh update (which clears the maps
    // it touches and can put a start tile on or off the mesh), and a district added, removed or blocked (which
    // decides whether a road map has a district map to be limited by). Each hook only sets a flag; the scan itself
    // is unchanged. If any of these hooks cannot be installed, both features scan every tick as before, and either
    // way a full scan every 200 ticks reports anything the flags missed, so a missed event costs at most 200
    // ticks of delay before a map is built ahead of its first use, never a wrong map.
    internal static class MapChanges
    {
        private const string Namespace = "Timberborn.Navigation.";

        private static bool _active;

        public static Feature CreateFeature()
        {
            Type self = typeof(MapChanges);
            Feature feature = new Feature { Name = "MapChanges" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "FlowFieldCache.StartCachingAtNode",
                Required = true,
                Target = () => Reflect.Method(Namespace + "FlowFieldCache", "StartCachingAtNode"),
                Postfix = Reflect.Own(self, nameof(ChangedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "RoadFlowFieldCache.OnNavMeshUpdated",
                Required = true,
                Target = () => Reflect.Method(Namespace + "RoadFlowFieldCache", "OnNavMeshUpdated"),
                Postfix = Reflect.Own(self, nameof(ChangedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TerrainFlowFieldCache.OnNavMeshUpdated",
                Required = true,
                Target = () => Reflect.Method(Namespace + "TerrainFlowFieldCache", "OnNavMeshUpdated"),
                Postfix = Reflect.Own(self, nameof(ChangedPostfix))
            });
            foreach (string method in new[] { "AddDistrictCenter", "RemoveDistrictCenter", "OnObstacleChanged", "OnNavMeshUpdated" })
            {
                string name = method;
                feature.Patches.Add(new PatchSpec
                {
                    Name = "DistrictMap." + name,
                    Required = true,
                    Target = () => Reflect.Method(Namespace + "DistrictMap", name),
                    Postfix = Reflect.Own(self, nameof(ChangedPostfix))
                });
            }
            return feature;
        }

        public static void Activate()
        {
            _active = true;
            RouteMaps.ScanOnlyWhenChanged = true;
            TerrainMaps.ScanOnlyWhenChanged = true;
        }

        public static bool IsActive => _active;

        internal static void ChangedPostfix()
        {
            RouteMaps.MarkChanged();
            TerrainMaps.MarkChanged();
        }
    }
}
