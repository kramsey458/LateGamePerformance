using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace LateGamePerformance
{
    // Where a building's terrain route map reaches. A terrain route map (AccessFlowField) holds the tiles within
    // the game's resource range of the building's access; asking whether a tile is in it (what
    // Accessible.FindTerrainPath does for every marked tree a lumberjack considers) costs a world-to-node
    // conversion and a few dictionary lookups, about a microsecond. The map's bounding box, taken once when the
    // map is filled, answers "not in it" for any tile outside the box with a conversion and six compares. A tile
    // inside the box is asked the game's way.
    //
    // Exact: a tile outside the box of the map's tiles is not one of the map's tiles, so the game's lookup would
    // say "unreachable" and the search would drop the candidate. The box is computed right after every fill (a
    // postfix on the generator's fill method, on whichever thread fills: the game's on-demand fill on the main
    // thread, or this mod's terrain map builders) and is only trusted while the map is still filled; a map that
    // was cleared answers "maybe" until it is filled again, and a map never filled has no box.
    internal static class TerrainReach
    {
        private const string Namespace = "Timberborn.Navigation.";

        internal sealed class Box
        {
            public object Field;
            public volatile bool Valid;
            public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }

        private static readonly ConditionalWeakTable<object, Box> Boxes = new ConditionalWeakTable<object, Box>();
        private static Func<object, bool> _isFilled;
        private static Func<object, IEnumerable<int>> _nodeIds;
        private static Func<object, int, Vector3Int> _idToGrid;
        private static Func<object, object> _nodeIdServiceOf;
        private static Func<Vector3, Vector3Int> _worldToGrid;
        private static bool _active;
        private static long _boxesMade;
        private static long _boxesEmpty;

        public static Feature CreateFeature()
        {
            Type self = typeof(TerrainReach);
            Feature feature = new Feature { Name = "TerrainReach" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TerrainFlowFieldGenerator.FillFlowFieldUpToDistance",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Method(Namespace + "TerrainFlowFieldGenerator", "FillFlowFieldUpToDistance");
                },
                Prefix = Reflect.Own(self, nameof(FillPrefix)),
                Postfix = Reflect.Own(self, nameof(FillPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        internal static void Bind()
        {
            Type field = Reflect.GameType(Namespace + "AccessFlowField");
            Type nodeIds = Reflect.GameType(Namespace + "NodeIdService");
            Type pathfinding = Reflect.GameType(Namespace + "PathfindingService");
            Type coordinates = Reflect.GameType(Namespace + "NavigationCoordinateSystem");
            if (field == null || nodeIds == null || pathfinding == null || coordinates == null)
            {
                throw new TypeLoadException("navigation types not found");
            }
            _isFilled = Reflect.PropertyGetter<bool>(field, "IsFilled");
            _nodeIds = Reflect.InstanceCall<Func<object, IEnumerable<int>>>(AccessTools.Method(field, "GetAllNodeIds"));
            _idToGrid = Reflect.InstanceCall<Func<object, int, Vector3Int>>(AccessTools.Method(nodeIds, "IdToGrid"));
            _nodeIdServiceOf = Reflect.FieldGetter<object>(pathfinding, "_nodeIdService");
            _worldToGrid = (Func<Vector3, Vector3Int>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector3Int>),
                AccessTools.Method(coordinates, "WorldToGridInt", new[] { typeof(Vector3) }));
        }

        public static string TakeStatsLine()
        {
            if (!_active || _boxesMade == 0)
            {
                return null;
            }
            string line = $"TerrainReach: {Interlocked.Read(ref _boxesMade)} terrain route maps measured after a fill, {Interlocked.Read(ref _boxesEmpty)} of them empty";
            Interlocked.Exchange(ref _boxesMade, 0);
            Interlocked.Exchange(ref _boxesEmpty, 0);
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static void FillPrefix(object flowField, out bool __state)
        {
            // The game calls the fill on every lookup and it returns at once when the map is filled; only a fill
            // that actually happened is measured.
            __state = !_active || _isFilled(flowField);
        }

        internal static void FillPostfix(object flowField, bool __state)
        {
            if (__state || !_active)
            {
                return;
            }
            try
            {
                object pathfinding = TerrainMaps.PathfindingServiceInstance;
                if (pathfinding == null || !_isFilled(flowField))
                {
                    return;
                }
                Measure(flowField, _nodeIdServiceOf(pathfinding));
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("TerrainReach failed and is off for this session; every candidate is looked up as before: " + exception);
            }
        }
        // ReSharper restore InconsistentNaming

        // The box of a filled map, from its own node ids.
        internal static void Measure(object flowField, object nodeIdService)
        {
            Box box = Boxes.GetValue(flowField, _ => new Box());
            box.Valid = false;
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            bool any = false;
            foreach (int id in _nodeIds(flowField))
            {
                Vector3Int grid = _idToGrid(nodeIdService, id);
                if (grid.x < minX) minX = grid.x;
                if (grid.x > maxX) maxX = grid.x;
                if (grid.y < minY) minY = grid.y;
                if (grid.y > maxY) maxY = grid.y;
                if (grid.z < minZ) minZ = grid.z;
                if (grid.z > maxZ) maxZ = grid.z;
                any = true;
            }
            if (!any)
            {
                // An empty box: nothing is inside it, which is right for a map that reaches nothing.
                minX = minY = minZ = 1;
                maxX = maxY = maxZ = 0;
                Interlocked.Increment(ref _boxesEmpty);
            }
            box.Field = flowField;
            box.MinX = minX;
            box.MinY = minY;
            box.MinZ = minZ;
            box.MaxX = maxX;
            box.MaxY = maxY;
            box.MaxZ = maxZ;
            box.Valid = true;
            Interlocked.Increment(ref _boxesMade);
        }

        // The box of a map, or null when the map has not been measured since it was created.
        internal static Box BoxOf(object flowField)
        {
            return _active && Boxes.TryGetValue(flowField, out Box box) ? box : null;
        }

        // False only when the tile is certainly not in the map. No box, or a map cleared since its box was made,
        // counts as "maybe", and the game's lookup decides.
        internal static bool MayReach(Box box, Vector3 worldPosition)
        {
            if (box == null || !box.Valid || !_isFilled(box.Field))
            {
                return true;
            }
            Vector3Int grid = _worldToGrid(worldPosition);
            return grid.x >= box.MinX && grid.x <= box.MaxX && grid.y >= box.MinY && grid.y <= box.MaxY &&
                   grid.z >= box.MinZ && grid.z <= box.MaxZ;
        }

        // Whether MayReach answers from the box. False (no box, or a map cleared since its box was made) means it
        // answers "maybe" for every tile.
        internal static bool Trusted(Box box)
        {
            return box != null && box.Valid && _isFilled(box.Field);
        }

        // The tile of a world position, as MayReach reads it (GrownTrees files each tree under it).
        internal static Vector3Int GridOf(Vector3 worldPosition)
        {
            return _worldToGrid(worldPosition);
        }

        // For the tests: a box from explicit bounds and a stand-in map.
        internal static Box BoxForTests(object field, Vector3Int min, Vector3Int max)
        {
            Box box = Boxes.GetValue(field, _ => new Box());
            box.Field = field;
            box.MinX = min.x;
            box.MinY = min.y;
            box.MinZ = min.z;
            box.MaxX = max.x;
            box.MaxY = max.y;
            box.MaxZ = max.z;
            box.Valid = true;
            return box;
        }

        internal static void SetFilledForTests(Func<object, bool> isFilled, Func<Vector3, Vector3Int> worldToGrid)
        {
            _isFilled = isFilled;
            _worldToGrid = worldToGrid;
            _active = true;
        }
    }
}
