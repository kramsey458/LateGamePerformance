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
        RunBackground(graph, heapFactory, limiting, starts, sequentialWork, workers, parallelTimer.ElapsedMilliseconds, check);

        Console.WriteLine($"     timing: {maps} maps sequential {sequentialTimer.ElapsedMilliseconds} ms, " +
                          $"{workers} workers {parallelTimer.ElapsedMilliseconds} ms " +
                          $"({(double)sequentialTimer.ElapsedMilliseconds / Math.Max(1, parallelTimer.ElapsedMilliseconds):0.0}x)");
    }

    // Drives the navigation tick hook against a real RoadFlowFieldCache and DistrictMap; only PathfindingService
    // is a bare object holding the four fields the mod reads. The rule under test: after the hook has run, every
    // cached map that can be built is built, whatever was or was not filled beforehand.
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
        RouteMaps.SetBackground(false);
        RouteMaps.Activate();
        RouteMaps.PathfindingServiceCreatedPostfix(service);
        RouteMaps.TakeStatsLine();

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
        object FieldAt(int node)
        {
            lookup[0] = node; lookup[1] = null;
            tryGet.Invoke(roadCache, lookup);
            return lookup[1];
        }
        foreach (int start in cached)
        {
            Call(roadCache, "StartCachingAtNode", start);
            fieldAt[start] = FieldAt(start);
        }
        // A building that is not on any road: the game never fills its map, and neither may the mod.
        int offRoad = 0;
        while ((bool)Call(graph, "IsOnNavMesh", offRoad))
        {
            offRoad++;
        }
        Call(roadCache, "StartCachingAtNode", offRoad);
        object offRoadField = FieldAt(offRoad);

        // One player's range overlay filled a handful of maps on this computer only. The other peer has none
        // filled. Both must end up in the same state, so this must not change which maps are built.
        for (int i = 0; i < 25; i++)
        {
            Call(generator, "FillFlowField", graph, fieldAt[cached[i]], limiting, cached[i]);
        }

        RouteMaps.NavigationTickedPostfix();

        long nodesCompared = 0;
        int wrong = 0;
        foreach (int start in cached)
        {
            object field = fieldAt[start];
            if (!(bool)Get(field, "IsFilled") || !SameMap(expected[indexOfStart[start]].Field, field, ref nodesCompared))
            {
                wrong++;
            }
        }
        check(RouteMaps.IsActive, "orchestration: feature still active after building");
        check(wrong == 0, $"orchestration: all {cached.Count} cached maps are built and identical to the game's, whatever was filled before");
        check(RouteMaps.FieldsFilledSinceReport == cached.Count - 25,
            $"orchestration: only the unbuilt ones were built ({RouteMaps.FieldsFilledSinceReport} of {cached.Count})");
        check(!(bool)Get(offRoadField, "IsFilled"), "orchestration: a map whose building is not on a road is left alone");

        // Nothing to do on the next tick, and it must not retry the map that cannot be built as a batch.
        long before = RouteMaps.FieldsFilledSinceReport;
        RouteMaps.NavigationTickedPostfix();
        check(RouteMaps.FieldsFilledSinceReport == before, "orchestration: a second tick builds nothing");

        // A road change throws most maps away: all of them come back.
        for (int i = 0; i < cached.Count - 20; i++)
        {
            Call(fieldAt[cached[i]], "Clear");
        }
        // One building is removed before the tick: its map leaves the cache and is not built.
        Call(roadCache, "StopCachingAtNode", cached[0]);
        RouteMaps.NavigationTickedPostfix();
        int unbuilt = 0;
        for (int i = 1; i < cached.Count; i++)
        {
            if (!(bool)Get(fieldAt[cached[i]], "IsFilled"))
            {
                unbuilt++;
            }
        }
        check(unbuilt == 0, "orchestration: after a road change every cached map is built again");
        check(!(bool)Get(fieldAt[cached[0]], "IsFilled"), "orchestration: a map dropped from the cache is not built");

        // A new building or two: built now, directly, not left for later.
        for (int i = 1; i <= 5; i++)
        {
            Call(fieldAt[cached[i]], "Clear");
        }
        long batchesBefore = RouteMaps.FieldsFilledSinceReport;
        RouteMaps.NavigationTickedPostfix();
        bool smallBuilt = true;
        for (int i = 1; i <= 5; i++)
        {
            smallBuilt &= (bool)Get(fieldAt[cached[i]], "IsFilled")
                          && SameMap(expected[indexOfStart[cached[i]]].Field, fieldAt[cached[i]], ref nodesCompared);
        }
        check(smallBuilt && RouteMaps.FieldsFilledSinceReport == batchesBefore,
            "orchestration: a small batch (5 maps) is built directly on the main thread, identical to the game's");
    }

    // Background rebuild: workers fill while this thread plays the game's part, asking for maps in a shuffled
    // order through the same gate the game goes through. Every map must be complete and correct at the moment it
    // is asked for, whoever ended up building it. Repeated to give races a chance to show.
    private static void RunBackground(object graph, object heapFactory, object limiting, int[] starts,
        List<RouteMaps.Work> expected, int workers, long waitForAllMs, Action<bool, string> check)
    {
        Dictionary<int, int> indexOfStart = new Dictionary<int, int>();
        for (int i = 0; i < starts.Length; i++)
        {
            indexOfStart[starts[i]] = i;
        }
        int[] distinct = new int[indexOfStart.Count];
        indexOfStart.Keys.CopyTo(distinct, 0);
        object[] generators = RouteMaps.CreateWorkerGenerators(heapFactory, workers + 1);
        object districtMapStandIn = new object();
        Random random = new Random(777);
        RouteMaps.TakeStatsLine();

        const int rounds = 25;
        int notReadyWhenAsked = 0, wrong = 0, leftInFlight = 0;
        long nodesCompared = 0;
        for (int round = 0; round < rounds; round++)
        {
            List<RouteMaps.Work> work = CreateWork(distinct, limiting);
            Dictionary<int, object> fieldAt = new Dictionary<int, object>();
            foreach (RouteMaps.Work item in work)
            {
                fieldAt[item.StartNodeId] = item.Field;
            }
            int[] order = (int[])distinct.Clone();
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            RouteMaps.BeginBackground(graph, districtMapStandIn, work, generators);
            // Odd rounds ask for everything; even rounds ask for a third and then land, like a new navigation tick.
            int asked = round % 2 == 1 ? order.Length : order.Length / 3;
            for (int i = 0; i < asked; i++)
            {
                RouteMaps.MapRequestedPrefix(order[i]);
                if (!(bool)Get(fieldAt[order[i]], "IsFilled"))
                {
                    notReadyWhenAsked++;
                }
            }
            // A change to something workers do not read must not land the flight; a change to the graph must.
            RouteMaps.SharedStateChangingPrefix(new object());
            RouteMaps.SharedStateChangingPrefix(graph);
            if (RouteMaps.IsInFlight)
            {
                leftInFlight++;
            }
            // Full comparison is slow through reflection, so check a rotating sixth of the maps each round.
            for (int i = round % 6; i < distinct.Length; i += 6)
            {
                object field = fieldAt[distinct[i]];
                if (!(bool)Get(field, "IsFilled") || !SameMap(expected[indexOfStart[distinct[i]]].Field, field, ref nodesCompared))
                {
                    wrong++;
                }
            }
        }
        check(notReadyWhenAsked == 0, $"background: every map was complete at the moment it was asked for ({rounds} rounds)");
        check(wrong == 0, $"background: maps identical to the game's, whoever built them ({nodesCompared} nodes compared)");
        check(leftInFlight == 0, "background: a road graph change lands the rebuild first");
        check(RouteMaps.IsActive, "background: feature still active");
        Console.WriteLine($"     background: main thread spent {RouteMaps.MainThreadMsSinceReport / rounds:0.0} ms per rebuild of " +
                          $"{distinct.Length} maps and built {RouteMaps.BuiltOnMainSinceReport / rounds} of them itself " +
                          "(worst case: this test asks for maps back to back with no game work in between)");

        // Closer to the game: other work happens between map requests (here 0.25 ms of spinning per request),
        // which is the time the workers use to get ahead of the main thread.
        RouteMaps.TakeStatsLine();
        const int pacedRounds = 5;
        for (int round = 0; round < pacedRounds; round++)
        {
            List<RouteMaps.Work> work = CreateWork(distinct, limiting);
            int[] pacedOrder = (int[])distinct.Clone();
            for (int i = pacedOrder.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (pacedOrder[i], pacedOrder[j]) = (pacedOrder[j], pacedOrder[i]);
            }
            RouteMaps.BeginBackground(graph, districtMapStandIn, work, generators);
            foreach (int node in pacedOrder)
            {
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4000;
                while (Stopwatch.GetTimestamp() < until)
                {
                }
                RouteMaps.MapRequestedPrefix(node);
            }
            RouteMaps.Land();
        }
        Console.WriteLine($"     background, paced: main thread spent {RouteMaps.MainThreadMsSinceReport / pacedRounds:0.0} ms per rebuild of " +
                          $"{distinct.Length} maps and built {RouteMaps.BuiltOnMainSinceReport / pacedRounds} of them itself; " +
                          $"longest single pause {RouteMaps.LongestPauseMsSinceReport:0.0} ms (waiting for the whole batch: about {waitForAllMs} ms)");

        // A worker that fails must be contained: nobody waits forever, and the feature switches itself off.
        List<RouteMaps.Work> broken = CreateWork(distinct, limiting);
        for (int i = 0; i < broken.Count; i += 9)
        {
            broken[i] = new RouteMaps.Work { Field = broken[i].Field, LimitingField = null, StartNodeId = broken[i].StartNodeId };
        }
        RouteMaps.BeginBackground(graph, districtMapStandIn, broken, generators);
        foreach (int node in distinct)
        {
            RouteMaps.MapRequestedPrefix(node);
        }
        RouteMaps.Land();
        check(!RouteMaps.IsInFlight && !RouteMaps.IsActive, "background: failing maps end the rebuild and switch the feature off");
    }

    internal static object GetField(object target, string name)
    {
        return target.GetType().GetField(name, Any).GetValue(target);
    }

    internal static void SetField(object target, string name, object value)
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

    internal static bool SameMap(object expected, object actual, ref long nodesCompared)
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

    internal static object CreateNodeIdService(int numberOfNodes)
    {
        // Only NumberOfNodes is read by the graph and the heap factory; the real constructor needs map services.
        Type type = _navigation.GetType(Namespace + "NodeIdService", true);
        object service = RuntimeHelpers.GetUninitializedObject(type);
        FieldInfo backing = type.GetField("<NumberOfNodes>k__BackingField", Any);
        backing.SetValue(service, numberOfNodes);
        return service;
    }

    internal static object Create(string typeName, params object[] arguments)
    {
        Type type = _navigation.GetType(Namespace + typeName, true);
        return Activator.CreateInstance(type, Any, null, arguments, null);
    }

    internal static object Call(object target, string method, params object[] arguments)
    {
        return target.GetType().GetMethod(method, Any).Invoke(target, arguments);
    }

    internal static object Get(object target, string property)
    {
        return target.GetType().GetProperty(property, Any).GetValue(target);
    }
}
