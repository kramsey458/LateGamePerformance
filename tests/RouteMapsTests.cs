using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;

// Runs the parallel route map rebuild against the installed game's real Timberborn.Navigation classes: a road
// graph is built with RoadNavMeshGraph, maps are filled one by one with the game's RoadFlowFieldGenerator and
// again through RouteMaps.FillParallel, and the two sets must match exactly, including node order.
internal static class RouteMapsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Namespace = "Timberborn.Navigation.";

    private static Assembly _navigation;

    public static void Run(Assembly navigation, Action<bool, string> check)
    {
        _navigation = navigation;
        RouteMaps.BindAccessors();

        const int width = 160, height = 160, maps = 420, workers = 7;
        object nodeIdService = CreateNodeIdService(width * height);
        object graph = Create("RoadNavMeshGraph", nodeIdService);
        Call(graph, "Load");
        Random random = new Random(12345);
        List<int> roadNodes = BuildRoads(graph, width, height, random);
        check(roadNodes.Count > 15000, $"test road graph built ({roadNodes.Count} road tiles)");

        // Stands in for the district map: every road tile is inside the district.
        object limiting = Create("AccessFlowField");
        foreach (int nodeId in roadNodes)
        {
            Call(limiting, "AddNode", nodeId, -1, 0f);
        }
        Call(limiting, "MarkAsFilled");

        object heapFactory = Create("BinaryHeapFactory", nodeIdService);
        int[] starts = new int[maps];
        for (int i = 0; i < maps; i++)
        {
            starts[i] = roadNodes[random.Next(roadNodes.Count)];
        }

        object[] single = RouteMaps.CreateWorkerGenerators(heapFactory, 1);
        List<RouteMaps.Work> sequentialWork = CreateWork(starts, limiting);
        Stopwatch sequentialTimer = Stopwatch.StartNew();
        Exception sequentialFailure = RouteMaps.FillParallel(graph, sequentialWork, single);
        sequentialTimer.Stop();
        check(sequentialFailure == null, "sequential fill ran without error");

        object[] pool = RouteMaps.CreateWorkerGenerators(heapFactory, workers);
        List<RouteMaps.Work> parallelWork = CreateWork(starts, limiting);
        Stopwatch parallelTimer = Stopwatch.StartNew();
        Exception parallelFailure = RouteMaps.FillParallel(graph, parallelWork, pool);
        parallelTimer.Stop();
        check(parallelFailure == null, "parallel fill ran without error");

        int mismatches = 0, unfilled = 0;
        long nodesCompared = 0;
        for (int i = 0; i < maps; i++)
        {
            if (!(bool)Get(parallelWork[i].Field, "IsFilled"))
            {
                unfilled++;
            }
            if (!SameMap(sequentialWork[i].Field, parallelWork[i].Field, ref nodesCompared))
            {
                mismatches++;
            }
        }
        check(unfilled == 0, $"all {maps} parallel maps are marked filled");
        check(mismatches == 0, $"parallel maps identical to sequential ones, node order included ({nodesCompared} nodes compared)");

        // Reusing the pool for a second batch must not leak state between batches.
        List<RouteMaps.Work> secondWork = CreateWork(starts, limiting);
        RouteMaps.FillParallel(graph, secondWork, pool);
        int secondMismatches = 0;
        for (int i = 0; i < maps; i++)
        {
            if (!SameMap(sequentialWork[i].Field, secondWork[i].Field, ref nodesCompared))
            {
                secondMismatches++;
            }
        }
        check(secondMismatches == 0, "second batch on the same workers is identical too");

        // A failing worker must be reported, not thrown, and must leave its map unfilled for the game to redo.
        List<RouteMaps.Work> brokenWork = CreateWork(new[] { starts[0], starts[1] }, limiting);
        brokenWork[1] = new RouteMaps.Work { Field = brokenWork[1].Field, LimitingField = null, StartNodeId = starts[1] };
        Exception expected = RouteMaps.FillParallel(graph, brokenWork, pool);
        check(expected != null, "a worker failure is returned instead of thrown");
        check(!(bool)Get(brokenWork[1].Field, "IsFilled"), "the failed map stays unfilled");

        RunOrchestration(graph, heapFactory, limiting, starts, sequentialWork, check);

        Console.WriteLine($"     timing: {maps} maps sequential {sequentialTimer.ElapsedMilliseconds} ms, " +
                          $"{workers} workers {parallelTimer.ElapsedMilliseconds} ms " +
                          $"({(double)sequentialTimer.ElapsedMilliseconds / Math.Max(1, parallelTimer.ElapsedMilliseconds):0.0}x)");
    }

    // Drives the hooks in the order the game does: snapshot before a road change, the game throws maps away,
    // detect which ones, rebuild at the end of NavigationSynchronizer.Tick. Uses a real RoadFlowFieldCache and
    // DistrictMap; only PathfindingService is a bare object holding the four fields the mod reads.
    private static void RunOrchestration(object graph, object heapFactory, object limiting, int[] starts,
        List<RouteMaps.Work> expected, Action<bool, string> check)
    {
        object roadCache = Create("RoadFlowFieldCache");
        object generator = Create("RoadFlowFieldGenerator", heapFactory);
        Call(generator, "Load");
        object districtMap = Create("DistrictMap", new object[] { graph, null, null, null, null, null });
        ConstructorInfo districtConstructor = _navigation.GetType(Namespace + "District", true).GetConstructors(Any)[0];
        object noCoordinates = Activator.CreateInstance(districtConstructor.GetParameters()[1].ParameterType);
        object district = districtConstructor.Invoke(new[] { starts[0], noCoordinates });
        ((IDictionary)GetField(districtMap, "_districtRoadFlowFields"))[district] = limiting;
        IDictionary districtsOnRoads = (IDictionary)GetField(districtMap, "_districtsOnRoads");
        foreach (int start in starts)
        {
            districtsOnRoads[start] = district;
        }
        SetField(districtMap, "_anyRoadFlowFieldDirty", false);

        Type serviceType = _navigation.GetType(Namespace + "PathfindingService", true);
        object service = RuntimeHelpers.GetUninitializedObject(serviceType);
        SetField(service, "_roadFlowFieldCache", roadCache);
        SetField(service, "_roadFlowFieldGenerator", generator);
        SetField(service, "_roadNavMeshGraph", graph);
        SetField(service, "_districtMap", districtMap);

        RouteMaps.CreateFeature(new Config { RouteMapsMinFields = 16, RouteMapsWorkers = 4 });
        RouteMaps.Activate();
        RouteMaps.PathfindingServiceCreatedPostfix(service);

        // Distinct start tiles only: the cache holds one map per tile.
        Dictionary<int, int> indexOfStart = new Dictionary<int, int>();
        for (int i = 0; i < starts.Length; i++)
        {
            indexOfStart[starts[i]] = i;
        }
        List<int> cached = new List<int>(indexOfStart.Keys);
        object[] lookup = { 0, null };
        MethodInfo tryGet = roadCache.GetType().GetMethod("TryGetFlowFieldAtNode", Any);
        Dictionary<int, object> fieldAt = new Dictionary<int, object>();
        foreach (int start in cached)
        {
            Call(roadCache, "StartCachingAtNode", start);
            lookup[0] = start;
            tryGet.Invoke(roadCache, lookup);
            fieldAt[start] = lookup[1];
        }
        // In use: the first 300 are filled. Never used: the rest stay empty and must be left alone.
        int inUse = Math.Min(300, cached.Count);
        for (int i = 0; i < inUse; i++)
        {
            Call(generator, "FillFlowField", graph, fieldAt[cached[i]], limiting, cached[i]);
        }

        RouteMaps.RoadsChangingPrefix(roadCache);
        // The road change: the game throws away the in-use maps, except 20 it did not touch.
        for (int i = 0; i < inUse - 20; i++)
        {
            Call(fieldAt[cached[i]], "Clear");
        }
        // One thrown-away map is dropped from the cache before the rebuild (its building was removed).
        Call(roadCache, "StopCachingAtNode", cached[0]);
        RouteMaps.RoadsChangedPostfix();
        RouteMaps.NavigationTickedPostfix();

        check(RouteMaps.IsActive, "orchestration: feature still active after a rebuild");
        check(RouteMaps.FieldsFilledSinceReport == inUse - 20 - 1,
            $"orchestration: rebuilt exactly the thrown-away, still cached maps ({RouteMaps.FieldsFilledSinceReport})");
        long nodesCompared = 0;
        int wrong = 0, neverUsedTouched = 0;
        for (int i = 1; i < inUse; i++)
        {
            object field = fieldAt[cached[i]];
            if (!(bool)Get(field, "IsFilled") || !SameMap(expected[indexOfStart[cached[i]]].Field, field, ref nodesCompared))
            {
                wrong++;
            }
        }
        for (int i = inUse; i < cached.Count; i++)
        {
            if ((bool)Get(fieldAt[cached[i]], "IsFilled"))
            {
                neverUsedTouched++;
            }
        }
        check(wrong == 0, "orchestration: every in-use map is filled and identical to the game's");
        check(!(bool)Get(fieldAt[cached[0]], "IsFilled"), "orchestration: a map dropped from the cache is not rebuilt");
        check(neverUsedTouched == 0, $"orchestration: {cached.Count - inUse} never-used maps left alone");

        // Below the threshold the game's own on-demand rebuild is left to handle it.
        long before = RouteMaps.FieldsFilledSinceReport;
        RouteMaps.RoadsChangingPrefix(roadCache);
        for (int i = 1; i <= 5; i++)
        {
            Call(fieldAt[cached[i]], "Clear");
        }
        RouteMaps.RoadsChangedPostfix();
        RouteMaps.NavigationTickedPostfix();
        check(RouteMaps.FieldsFilledSinceReport == before && !(bool)Get(fieldAt[cached[1]], "IsFilled"),
            "orchestration: a small change (5 maps) is left to the game");
    }

    private static object GetField(object target, string name)
    {
        return target.GetType().GetField(name, Any).GetValue(target);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, Any).SetValue(target, value);
    }

    private static List<int> BuildRoads(object graph, int width, int height, Random random)
    {
        bool[] present = new bool[width * height];
        for (int i = 0; i < present.Length; i++)
        {
            present[i] = random.Next(100) >= 12;
        }
        MethodInfo connect = graph.GetType().GetMethod("ConnectNodes", Any);
        bool[] connected = new bool[present.Length];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                int id = x * height + y;
                if (!present[id])
                {
                    continue;
                }
                // Mostly unit costs so many routes tie, which is where a wrong pop order would show up.
                if (x + 1 < width && present[id + height])
                {
                    connect.Invoke(graph, new object[] { id, id + height, 0, random.Next(10) == 0 ? 0.35f : 1f });
                    connected[id] = connected[id + height] = true;
                }
                if (y + 1 < height && present[id + 1])
                {
                    connect.Invoke(graph, new object[] { id, id + 1, 0, random.Next(10) == 0 ? 2.5f : 1f });
                    connected[id] = connected[id + 1] = true;
                }
            }
        }
        List<int> roadNodes = new List<int>();
        for (int i = 0; i < connected.Length; i++)
        {
            if (connected[i])
            {
                roadNodes.Add(i);
            }
        }
        return roadNodes;
    }

    private static List<RouteMaps.Work> CreateWork(int[] starts, object limiting)
    {
        List<RouteMaps.Work> work = new List<RouteMaps.Work>();
        foreach (int start in starts)
        {
            work.Add(new RouteMaps.Work { Field = Create("AccessFlowField"), LimitingField = limiting, StartNodeId = start });
        }
        return work;
    }

    private static bool SameMap(object expected, object actual, ref long nodesCompared)
    {
        IEnumerator a = ((IEnumerable)Call(expected, "GetAllNodes")).GetEnumerator();
        IEnumerator b = ((IEnumerable)Call(actual, "GetAllNodes")).GetEnumerator();
        while (true)
        {
            bool moreA = a.MoveNext(), moreB = b.MoveNext();
            if (moreA != moreB)
            {
                return false;
            }
            if (!moreA)
            {
                return true;
            }
            int idA = (int)Get(a.Current, "Id"), idB = (int)Get(b.Current, "Id");
            float distanceA = (float)Get(a.Current, "GScore"), distanceB = (float)Get(b.Current, "GScore");
            if (idA != idB || !distanceA.Equals(distanceB) ||
                (int)Call(expected, "GetParentId", idA) != (int)Call(actual, "GetParentId", idB))
            {
                return false;
            }
            nodesCompared++;
        }
    }

    private static object CreateNodeIdService(int numberOfNodes)
    {
        // Only NumberOfNodes is read by the graph and the heap factory; the real constructor needs map services.
        Type type = _navigation.GetType(Namespace + "NodeIdService", true);
        object service = RuntimeHelpers.GetUninitializedObject(type);
        FieldInfo backing = type.GetField("<NumberOfNodes>k__BackingField", Any);
        backing.SetValue(service, numberOfNodes);
        return service;
    }

    private static object Create(string typeName, params object[] arguments)
    {
        Type type = _navigation.GetType(Namespace + typeName, true);
        return Activator.CreateInstance(type, Any, null, arguments, null);
    }

    private static object Call(object target, string method, params object[] arguments)
    {
        return target.GetType().GetMethod(method, Any).Invoke(target, arguments);
    }

    private static object Get(object target, string property)
    {
        return target.GetType().GetProperty(property, Any).GetValue(target);
    }
}
