using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Vector3 = UnityEngine.Vector3;
using Vector3Int = UnityEngine.Vector3Int;

// Runs the terrain route map rebuild and the plant water check against the installed game's real classes.
//
// Terrain: a terrain graph is built with TerrainNavMeshGraph, maps are filled one by one with the game's
// TerrainFlowFieldGenerator and again through TerrainMaps.FillParallel, and the two sets must match exactly, node
// order included. Then the navigation tick hook is driven against a real TerrainFlowFieldCache.
//
// Water: two identical sets of real WaterObjects over a stand-in water map; one goes through the game's own
// WaterObjectService.Tick, the other through the mod's replacement, and the levels stored and the order and
// content of the events raised must be the same.
internal static class TerrainAndWaterTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const float Range = 20f;

    public static void Run(string managed, Action<bool, string> check)
    {
        RunOrderedParallel(check);
        RunTerrain(check);
        RunWater(managed, check);
    }

    private static void RunOrderedParallel(Action<bool, string> check)
    {
        Random random = new Random(7);
        bool same = true;
        foreach (int count in new[] { 0, 1, 2, 7, 8, 1000, 8191 })
        {
            List<int> items = new List<int>();
            for (int i = 0; i < count; i++) items.Add(random.Next());
            foreach (int workers in new[] { 1, 3, 7, 64 })
            {
                int[] results = new int[count + 3];
                for (int i = 0; i < results.Length; i++) results[i] = -1;
                Exception failure = OrderedParallel.Read(items, count, results, item => item / 3, workers);
                same &= failure == null;
                for (int i = 0; i < count; i++) same &= results[i] == items[i] / 3;
                for (int i = count; i < results.Length; i++) same &= results[i] == -1;
            }
        }
        check(same, "ordered parallel: every item is read exactly once into its own slot, whatever the worker count");
        List<int> poisoned = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 };
        Exception expected = OrderedParallel.Read(poisoned, 8, new int[8],
            item => item == 5 ? throw new InvalidOperationException("five") : item, 4);
        check(expected is InvalidOperationException, "ordered parallel: a failing read is returned, not thrown");
    }

    private static void RunTerrain(Action<bool, string> check)
    {
        TerrainMaps.CreateFeature(new Config());
        TerrainMaps.BindAccessors();

        const int width = 120, height = 120, maps = 90, workers = 7;
        object nodeIdService = RouteMapsTests.CreateNodeIdService(width * height);
        object groupService = RouteMapsTests.Create("NavMeshGroupService");
        object graph = RouteMapsTests.Create("TerrainNavMeshGraph", nodeIdService, groupService);
        RouteMapsTests.Call(graph, "Load");
        Random random = new Random(4242);
        List<int> nodes = BuildTerrain(graph, width, height, random);
        check(nodes.Count > 9000, $"test terrain graph built ({nodes.Count} walkable tiles)");

        object heapFactory = RouteMapsTests.Create("BinaryHeapFactory", nodeIdService);
        int[] starts = new int[maps];
        for (int i = 0; i < maps; i++) starts[i] = nodes[random.Next(nodes.Count)];

        object[] single = TerrainMaps.CreateWorkerGenerators(heapFactory, groupService, 1);
        List<TerrainMaps.Work> sequential = CreateWork(starts);
        Stopwatch sequentialTimer = Stopwatch.StartNew();
        check(TerrainMaps.FillParallel(graph, sequential, single, Range) == null, "terrain: one-by-one fill ran without error");
        sequentialTimer.Stop();

        object[] pool = TerrainMaps.CreateWorkerGenerators(heapFactory, groupService, workers);
        List<TerrainMaps.Work> parallel = CreateWork(starts);
        Stopwatch parallelTimer = Stopwatch.StartNew();
        check(TerrainMaps.FillParallel(graph, parallel, pool, Range) == null, "terrain: parallel fill ran without error");
        parallelTimer.Stop();

        int mismatches = 0, unfilled = 0, tiles = 0;
        long compared = 0;
        for (int i = 0; i < maps; i++)
        {
            if (!(bool)RouteMapsTests.Get(parallel[i].Field, "IsFilled")) unfilled++;
            if (!RouteMapsTests.SameMap(sequential[i].Field, parallel[i].Field, ref compared)) mismatches++;
            tiles = Math.Max(tiles, (int)RouteMapsTests.Get(parallel[i].Field, "NumberOfNodes"));
        }
        check(unfilled == 0, $"terrain: all {maps} parallel maps are marked filled");
        check(mismatches == 0, $"terrain: parallel maps identical to one-by-one ones, node order included ({compared} nodes compared)");
        check(tiles > 300 && tiles < width * height / 2, $"terrain: maps stop at the game's range (largest {tiles} tiles)");

        List<TerrainMaps.Work> second = CreateWork(starts);
        TerrainMaps.FillParallel(graph, second, pool, Range);
        int secondMismatches = 0;
        for (int i = 0; i < maps; i++)
        {
            if (!RouteMapsTests.SameMap(sequential[i].Field, second[i].Field, ref compared)) secondMismatches++;
        }
        check(secondMismatches == 0, "terrain: a second batch on the same workers is identical too");

        // The navigation tick hook against a real cache. PathfindingService is a bare object holding the three
        // fields the mod reads.
        object cache = RouteMapsTests.Create("TerrainFlowFieldCache");
        object gameGenerator = TerrainMaps.CreateWorkerGenerators(heapFactory, groupService, 1)[0];
        Type serviceType = graph.GetType().Assembly.GetType("Timberborn.Navigation.PathfindingService", true);
        object service = RuntimeHelpers.GetUninitializedObject(serviceType);
        RouteMapsTests.SetField(service, "_terrainFlowFieldCache", cache);
        RouteMapsTests.SetField(service, "_terrainFlowFieldGenerator", gameGenerator);
        RouteMapsTests.SetField(service, "_terrainNavMeshGraph", graph);

        int offMesh = FirstOffMesh(graph, width * height);
        HashSet<int> distinct = new HashSet<int>(starts);
        foreach (int start in distinct) RouteMapsTests.Call(cache, "StartCachingAtNode", start);
        RouteMapsTests.Call(cache, "StartCachingAtNode", offMesh);
        // Some already built, as they would be from a building's own search.
        int prebuilt = 0;
        foreach (int start in distinct)
        {
            if (prebuilt++ % 3 == 0)
            {
                RouteMapsTests.Call(gameGenerator, "FillFlowFieldUpToDistance", graph,
                    RouteMapsTests.Call(cache, "GetFlowFieldAtNode", start), Range, start);
            }
        }
        TerrainMaps.Activate();
        TerrainMaps.BuildUnbuiltMaps(service, Range);
        int wrong = 0, notBuilt = 0;
        for (int i = 0; i < maps; i++)
        {
            object field = RouteMapsTests.Call(cache, "GetFlowFieldAtNode", starts[i]);
            if (!(bool)RouteMapsTests.Get(field, "IsFilled")) notBuilt++;
            if (!RouteMapsTests.SameMap(sequential[i].Field, field, ref compared)) wrong++;
        }
        check(notBuilt == 0 && wrong == 0, $"terrain hook: every cached map is built and identical to the game's ({distinct.Count} maps)");
        check(!(bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", offMesh), "IsFilled"),
            "terrain hook: a map whose start is off the terrain graph is left alone");
        string stats = TerrainMaps.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats.Contains("1 rebuilds of ") && TerrainMaps.IsActive, "terrain hook: one batch, feature still active");
        TerrainMaps.BuildUnbuiltMaps(service, Range);
        check(TerrainMaps.TakeStatsLine().Contains("0 rebuilds of 0 "), "terrain hook: a second tick builds nothing");
        // A ground change clears the maps that contain the changed tile; the next tick builds exactly those again.
        object changed = ReadOnlyListOf(graph.GetType().Assembly, new List<int> { starts[0] });
        RouteMapsTests.Call(RouteMapsTests.GetField(cache, "_flowFields"), "OnNodesChanged", changed);
        int cleared = 0;
        foreach (int start in distinct)
        {
            if (!(bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", start), "IsFilled")) cleared++;
        }
        TerrainMaps.BuildUnbuiltMaps(service, Range);
        int stillWrong = 0;
        for (int i = 0; i < maps; i++)
        {
            if (!RouteMapsTests.SameMap(sequential[i].Field, RouteMapsTests.Call(cache, "GetFlowFieldAtNode", starts[i]), ref compared)) stillWrong++;
        }
        check(cleared > 0 && stillWrong == 0, $"terrain hook: after a ground change the {cleared} cleared maps are rebuilt, identical again");
        TerrainMaps.TakeStatsLine();

        // Scanning only after a change (MapChanges, 0.4.25), through the navigation tick hook: flagged, a tick with
        // nothing marked leaves the cache alone; marked, the next tick builds; the periodic check every 200 ticks
        // finds what the hooks missed.
        List<int> cachedStarts = new List<int>(distinct);
        TerrainMaps.PathfindingServiceCreatedPostfix(service);
        TerrainMaps.SetRangeForTests(Range);
        TerrainMaps.ScanOnlyWhenChanged = true;
        TerrainMaps.NavigationTickedPostfix();                        // marked by the service's creation: scans, nothing to build
        for (int i = 1; i <= 5; i++) RouteMapsTests.Call(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "Clear");
        TerrainMaps.NavigationTickedPostfix();
        int leftAlone = 0;
        for (int i = 1; i <= 5; i++) if (!(bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "IsFilled")) leftAlone++;
        TerrainMaps.MarkChanged();
        TerrainMaps.NavigationTickedPostfix();
        int builtAfterMark = 0;
        for (int i = 1; i <= 5; i++) if ((bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "IsFilled")) builtAfterMark++;
        check(leftAlone == 5 && builtAfterMark == 5, "terrain scan: with nothing marked a tick leaves the cache alone; marked, the next tick builds");
        for (int i = 6; i <= 8; i++) RouteMapsTests.Call(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "Clear");
        for (int tick = 0; tick < 199; tick++) TerrainMaps.NavigationTickedPostfix();
        int stillUnbuilt = 0;
        for (int i = 6; i <= 8; i++) if (!(bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "IsFilled")) stillUnbuilt++;
        TerrainMaps.NavigationTickedPostfix();
        int builtByCheck = 0;
        for (int i = 6; i <= 8; i++) if ((bool)RouteMapsTests.Get(RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[i]), "IsFilled")) builtByCheck++;
        string scanStats = TerrainMaps.TakeStatsLine();
        Console.WriteLine("     " + scanStats);
        check(stillUnbuilt == 3 && builtByCheck == 3 && scanStats.Contains("3 unbuilt maps were found by the periodic check alone") &&
              scanStats.Contains("left alone 200 times") && scanStats.Contains($"{distinct.Count + 1} maps cached"),
            "terrain scan: the periodic check builds what the hooks missed, and the line counts the cache");
        TerrainMaps.ScanOnlyWhenChanged = false;

        // The reach pre-filter (TerrainReach, 0.4.25): each built map's box, measured with the game's own node ids,
        // contains every tile of the map, a tile just outside it is certainly not in the map, and a map cleared
        // since its box was made answers maybe.
        TerrainReach.Bind();
        TerrainReach.Activate();
        Vector3Int[] table = new Vector3Int[width * height];
        for (int id = 0; id < table.Length; id++) table[id] = new Vector3Int(id / height, id % height, 0);
        nodeIdService.GetType().GetField("_idToCoordinatesTable", Any).SetValue(nodeIdService, table);
        MethodInfo gridToWorld = graph.GetType().Assembly.GetType("Timberborn.Navigation.NavigationCoordinateSystem", true)
            .GetMethod("GridToWorld", BindingFlags.Static | BindingFlags.Public);
        Func<Vector3Int, Vector3> world = grid => (Vector3)gridToWorld.Invoke(null, new object[] { grid });
        int measured = 0, insideWrong = 0, outsideWrong = 0, nodesChecked = 0;
        foreach (int start in cachedStarts)
        {
            object field = RouteMapsTests.Call(cache, "GetFlowFieldAtNode", start);
            TerrainReach.Measure(field, nodeIdService);
            TerrainReach.Box box = TerrainReach.BoxOf(field);
            if (box == null)
            {
                insideWrong++;
                continue;
            }
            measured++;
            foreach (int id in (IEnumerable<int>)RouteMapsTests.Call(field, "GetAllNodeIds"))
            {
                nodesChecked++;
                if (!TerrainReach.MayReach(box, world(table[id]))) insideWrong++;
            }
            if (TerrainReach.MayReach(box, world(new Vector3Int(box.MinX - 1, box.MinY, 0)))) outsideWrong++;
            if (TerrainReach.MayReach(box, world(new Vector3Int(box.MaxX + 1, box.MaxY, 0)))) outsideWrong++;
            if (TerrainReach.MayReach(box, world(new Vector3Int(box.MinX, box.MaxY + 1, 0)))) outsideWrong++;
            if (TerrainReach.MayReach(box, world(new Vector3Int(box.MinX, box.MinY, 1)))) outsideWrong++;
        }
        check(measured == cachedStarts.Count && insideWrong == 0 && nodesChecked > 10000,
            $"terrain reach: every tile of every built map is inside its box ({nodesChecked} tiles of {measured} maps)");
        check(outsideWrong == 0, "terrain reach: a tile just outside a box is certainly not in the map");
        object clearedField = RouteMapsTests.Call(cache, "GetFlowFieldAtNode", cachedStarts[0]);
        TerrainReach.Box clearedBox = TerrainReach.BoxOf(clearedField);
        RouteMapsTests.Call(clearedField, "Clear");
        check(TerrainReach.MayReach(clearedBox, world(new Vector3Int(clearedBox.MaxX + 5, clearedBox.MaxY + 5, 0))),
            "terrain reach: a map cleared since its box was made answers maybe for everything");
        string reachStats = TerrainReach.TakeStatsLine();
        Console.WriteLine("     " + reachStats);
        check(reachStats.StartsWith($"TerrainReach: {measured} terrain route maps measured after a fill, 0 of them empty"),
            "terrain reach: the stats line counts the boxes made");

        Console.WriteLine($"     timing: {maps} terrain maps one by one {sequentialTimer.Elapsed.TotalMilliseconds:0.0} ms " +
                          $"({sequentialTimer.Elapsed.TotalMilliseconds / maps:0.00} ms each), {workers} workers " +
                          $"{parallelTimer.Elapsed.TotalMilliseconds:0.0} ms");
    }

    private static object ReadOnlyListOf(Assembly navigation, List<int> values)
    {
        // Timberborn.Common.ReadOnlyList<int>, whatever assembly it lives in: take it from the method's signature.
        Type cacheType = navigation.GetType("Timberborn.Navigation.FlowFieldCache", true);
        Type readOnlyList = cacheType.GetMethod("OnNodesChanged", Any).GetParameters()[0].ParameterType;
        foreach (ConstructorInfo constructor in readOnlyList.GetConstructors(Any))
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(List<int>)))
            {
                return constructor.Invoke(new object[] { values });
            }
        }
        throw new MissingMethodException("ReadOnlyList<int>(List<int>)");
    }

    private static List<int> BuildTerrain(object graph, int width, int height, Random random)
    {
        bool[] walkable = new bool[width * height];
        for (int i = 0; i < walkable.Length; i++) walkable[i] = random.Next(100) >= 15;
        MethodInfo connect = graph.GetType().GetMethod("ConnectNodes", Any);
        bool[] connected = new bool[walkable.Length];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                int id = x * height + y;
                if (!walkable[id]) continue;
                // Mostly unit costs so many routes tie; a few other groups and costs, as paths and stairs have.
                if (x + 1 < width && walkable[id + height])
                {
                    connect.Invoke(graph, new object[] { id, id + height, random.Next(12) == 0 ? 1 : 0, random.Next(10) == 0 ? 0.4f : 1f });
                    connected[id] = connected[id + height] = true;
                }
                if (y + 1 < height && walkable[id + 1])
                {
                    connect.Invoke(graph, new object[] { id, id + 1, 0, random.Next(10) == 0 ? 3f : 1f });
                    connected[id] = connected[id + 1] = true;
                }
            }
        }
        List<int> nodes = new List<int>();
        for (int i = 0; i < connected.Length; i++)
        {
            if (connected[i]) nodes.Add(i);
        }
        return nodes;
    }

    private static int FirstOffMesh(object graph, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!(bool)RouteMapsTests.Call(graph, "IsOnNavMesh", i)) return i;
        }
        throw new InvalidOperationException("no tile off the terrain graph");
    }

    private static List<TerrainMaps.Work> CreateWork(int[] starts)
    {
        List<TerrainMaps.Work> work = new List<TerrainMaps.Work>();
        foreach (int start in starts)
        {
            work.Add(new TerrainMaps.Work { Field = RouteMapsTests.Create("AccessFlowField"), StartNodeId = start });
        }
        return work;
    }

    // ---------------------------------------------------------------------------------------------- water

    // Stands in for the game's thread-safe water map: the water height at a tile is a function of the tile.
    public class WaterMapProxy : DispatchProxy
    {
        public Func<int, int, int> Height;
        public static PropertyInfo X, Y;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "CeiledWaterHeight")
            {
                return Height((int)X.GetValue(args[0]), (int)Y.GetValue(args[0]));
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }

    private static void RunWater(string managed, Action<bool, string> check)
    {
        Assembly waterObjects = Assembly.LoadFrom(Path.Combine(managed, "Timberborn.WaterObjects.dll"));
        Type serviceType = waterObjects.GetType("Timberborn.WaterObjects.WaterObjectService", true);
        Type objectType = waterObjects.GetType("Timberborn.WaterObjects.WaterObject", true);
        FieldInfo mapField = objectType.GetField("_threadSafeWaterMap", Any);
        FieldInfo tileField = objectType.GetField("_baseCoordinates", Any);
        Type vector = tileField.FieldType;
        WaterMapProxy.X = vector.GetProperty("x");
        WaterMapProxy.Y = vector.GetProperty("y");
        PropertyInfo level = objectType.GetProperty("WaterAboveBase");
        EventInfo changed = objectType.GetEvent("WaterAboveBaseChanged");

        PlantWater.CreateFeature(new Config()).Patches[0].Target();
        PlantWater.Activate();

        const int count = 6000;
        int round = 0;
        // Water that moves between rounds, and stands still for most tiles.
        Func<int, int, int> height = (x, y) => x == 199 && y == 199
            ? 50 + round
            : (x * 7 + y * 13 + round * (x % 5 == 0 ? 1 : 0)) % 9 == 0 ? 3 + round % 2 : 0;

        object[] services = new object[2];
        List<object>[] objects = { new List<object>(), new List<object>() };
        List<string>[] events = { new List<string>(), new List<string>() };
        for (int world = 0; world < 2; world++)
        {
            object map = typeof(DispatchProxy).GetMethod("Create", 2, Type.EmptyTypes)
                .MakeGenericMethod(mapField.FieldType, typeof(WaterMapProxy)).Invoke(null, null);
            ((WaterMapProxy)map).Height = (x, y) => height(x, y);
            services[world] = Activator.CreateInstance(serviceType);
            Random random = new Random(99);
            for (int i = 0; i < count; i++)
            {
                object waterObject = RuntimeHelpers.GetUninitializedObject(objectType);
                mapField.SetValue(waterObject, map);
                tileField.SetValue(waterObject, Activator.CreateInstance(vector, random.Next(200), random.Next(200), random.Next(3)));
                int index = i, which = world;
                changed.AddEventHandler(waterObject, new EventHandler((sender, _) =>
                    events[which].Add(index + "=" + level.GetValue(sender))));
                RouteMapsTests.Call(services[world], "RegisterWaterObject", waterObject);
                objects[world].Add(waterObject);
            }
        }

        bool sameEvents = true, sameLevels = true;
        int totalEvents = 0;
        for (round = 0; round < 6; round++)
        {
            events[0].Clear();
            events[1].Clear();
            RouteMapsTests.Call(services[0], "Tick");                       // the game's own loop
            bool runOriginal = PlantWater.TickPrefix(services[1]);           // the mod's replacement
            sameEvents &= !runOriginal && events[0].Count == events[1].Count;
            for (int i = 0; sameEvents && i < events[0].Count; i++) sameEvents &= events[0][i] == events[1][i];
            for (int i = 0; i < count; i++) sameLevels &= (int)level.GetValue(objects[0][i]) == (int)level.GetValue(objects[1][i]);
            totalEvents += events[0].Count;
        }
        check(totalEvents > 500, $"plant water: the test water actually moves ({totalEvents} level changes over 6 ticks)");
        check(sameEvents, "plant water: the same events, with the same levels, in the same order as the game's own loop");
        check(sameLevels, $"plant water: all {count} stored levels equal the game's after every tick");
        string stats = PlantWater.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats.Contains("6 passes over 6000 objects") && PlantWater.IsActive, "plant water: every pass went through the mod, which is still active");

        // A handler that throws: the game's loop would throw into the game. The mod hands over to the game's loop,
        // which meets the same handler; nothing is applied twice on the way.
        round = 7;
        int calls = 0;
        changed.AddEventHandler(objects[1][0], new EventHandler((_, _) => { calls++; throw new InvalidOperationException("handler"); }));
        tileField.SetValue(objects[1][0], Activator.CreateInstance(vector, 199, 199, 0));   // a tile whose water rises every round
        bool handedOver = PlantWater.TickPrefix(services[1]);
        check(handedOver && !PlantWater.IsActive && calls == 1, "plant water: a throwing handler switches the feature off and hands the tick to the game");
    }
}
