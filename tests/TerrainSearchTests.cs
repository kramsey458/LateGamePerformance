using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LateGamePerformance;
using Timberborn.Common;
using Timberborn.Navigation;
using Vector3 = UnityEngine.Vector3;
using Vector3Int = UnityEngine.Vector3Int;

// The resumed terrain search against the game's real TerrainAStarPathfinder, PathFlowField, BinaryHeap and
// HeuristicsCalculator on a random terrain graph with the game's default costs (1 straight, 1.414 diagonal, a few
// dearer "stairs" and other groups):
//   1. every search that starts from scratch leaves the field identical to the game's, node for node (parents,
//      distances, marks): the same code path the game runs, on its own heap, so tie-breaks match;
//   2. sequences of searches from one tile (a beaver pricing buildings) give the game's distance, within
//      floating-point rounding, explore in total about half the tiles the game's restarts do, and do resume;
//   3. verify mode, with list-of-destination searches and terrain changes mixed in, reports no distance or
//      reachability difference;
//   4. on a terrain with steps cheaper than the heuristic allows (as a zipline is), searches that met one are not
//      resumed from, and verify mode still reports no distance difference.
internal static class TerrainSearchTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Width = 90;
    private const int Height = 90;

    public static void Run(Action<bool, string> check)
    {
        object nodeIdService = RouteMapsTests.CreateNodeIdService(Width * Height);
        SetCoordinates(nodeIdService);
        object groupService = RouteMapsTests.Create("NavMeshGroupService");
        object graph = RouteMapsTests.Create("TerrainNavMeshGraph", nodeIdService, groupService);
        RouteMapsTests.Call(graph, "Load");
        Random random = new Random(1812);
        List<int> nodes = BuildTerrain(graph, random);
        check(nodes.Count > 6000, $"terrain search: test graph built ({nodes.Count} walkable tiles)");

        object heapFactory = RouteMapsTests.Create("BinaryHeapFactory", nodeIdService);
        object distance = RouteMapsTests.Create("DistanceCalculator", nodeIdService);
        object heuristics = RouteMapsTests.Create("HeuristicsCalculator", distance, nodeIdService);
        object modPathfinder = RouteMapsTests.Create("TerrainAStarPathfinder", heuristics, heapFactory);
        object gamePathfinder = RouteMapsTests.Create("TerrainAStarPathfinder", heuristics, heapFactory);
        RouteMapsTests.Call(modPathfinder, "Load");
        RouteMapsTests.Call(gamePathfinder, "Load");
        object modField = RouteMapsTests.Create("PathFlowField");
        object gameField = RouteMapsTests.Create("PathFlowField");
        MethodInfo gameFill = null, gameMulti = null;
        foreach (MethodInfo method in gamePathfinder.GetType().GetMethods(Any))
        {
            if (method.Name != "FillFlowFieldWithPath") continue;
            if (method.GetParameters().Length == 4) gameFill = method; else gameMulti = method;
        }
        check(gameFill != null && gameMulti != null, "terrain search: both overloads of the game's search found");

        TerrainSearch.CreateFeature(new Config());
        TerrainSearch.Bind();
        TerrainSearch.Activate();

        // 1. Fresh searches: a new start every time.
        int identical = 0, handled = 0, previous = -1;
        for (int i = 0; i < 300; i++)
        {
            int start = nodes[random.Next(nodes.Count)];
            while (start == previous) start = nodes[random.Next(nodes.Count)];
            int dest = nodes[random.Next(nodes.Count)];
            if (!TerrainSearch.FillPrefix(modPathfinder, graph, modField, start, dest)) handled++;
            gameFill.Invoke(gamePathfinder, new object[] { graph, gameField, start, dest });
            if (SameField(modField, gameField)) identical++;
            previous = start;
        }
        check(handled == 300 && identical == 300, $"terrain search: {identical} of 300 fresh searches leave the field identical to the game's, parents and distances included");
        string line = TerrainSearch.TakeStatsLine();
        check(line != null && line.Contains("300 started from scratch"), "terrain search stats: " + line);

        // 2. Pricing runs: several destinations from one tile.
        int total = 0, exact = 0, rounding = 0, sameRoute = 0, beyond = 0, foundMismatch = 0;
        long gameExplored = 0;
        for (int round = 0; round < 150; round++)
        {
            int start = nodes[random.Next(nodes.Count)];
            for (int k = 0; k < 8; k++)
            {
                int dest = nodes[random.Next(nodes.Count)];
                TerrainSearch.FillPrefix(modPathfinder, graph, modField, start, dest);
                bool gameSearches = !(bool)RouteMapsTests.Call(gameField, "CheckedPath", start, dest);
                gameFill.Invoke(gamePathfinder, new object[] { graph, gameField, start, dest });
                if (gameSearches) gameExplored += Count(gameField);
                bool modFound = (bool)RouteMapsTests.Call(modField, "FoundPath", start, dest);
                bool gameFound = (bool)RouteMapsTests.Call(gameField, "FoundPath", start, dest);
                total++;
                if (modFound != gameFound)
                {
                    foundMismatch++;
                }
                else if (modFound)
                {
                    float dm = (float)RouteMapsTests.Call(modField, "GetDistance", dest);
                    float dg = (float)RouteMapsTests.Call(gameField, "GetDistance", dest);
                    if (dm == dg)
                    {
                        exact++;
                        if (SameRoute(modField, gameField, start, dest)) sameRoute++;
                    }
                    else if (Math.Abs(dm - dg) <= 0.001f * Math.Max(1f, Math.Abs(dg)))
                    {
                        rounding++;
                    }
                    else
                    {
                        beyond++;
                    }
                }
            }
        }
        line = TerrainSearch.TakeStatsLine();
        long resumed = Number(line, @"(\d+) resumed");
        long modExplored = Number(line, @"exploring (\d+) tiles") + Number(line, @"exploring (\d+) more tiles");
        check(foundMismatch == 0, $"terrain search: reachability agrees with the game in all {total} searches");
        check(beyond == 0, $"terrain search: distance is the game's in all searches ({exact} bit for bit, {rounding} within rounding, {beyond} beyond)");
        check(sameRoute > 0 && sameRoute <= exact, $"terrain search: the route is the game's in {sameRoute} of {exact} equal-distance searches; the rest are equally short routes the game would not have picked");
        check(resumed > 200, $"terrain search: {resumed} searches resumed the previous one ({line})");
        check(modExplored > 0 && modExplored < gameExplored, $"terrain search: explored {modExplored} tiles in total where the game's restarts explore {gameExplored}");
        check(TerrainSearch.IsActive, "terrain search: still active");

        // 3. Verify mode with list-of-destination searches and terrain changes mixed in.
        TerrainSearch.CreateFeature(new Config { TerrainSearchVerify = true });
        TerrainSearch.Activate();
        int changes = 0, multis = 0;
        for (int round = 0; round < 120; round++)
        {
            int start = nodes[random.Next(nodes.Count)];
            for (int k = 0; k < 5; k++)
            {
                int dest = nodes[random.Next(nodes.Count)];
                TerrainSearch.FillPrefix(modPathfinder, graph, modField, start, dest);
            }
            if (round % 7 == 3)
            {
                List<int> targets = new List<int> { nodes[random.Next(nodes.Count)], nodes[random.Next(nodes.Count)] };
                gameMulti.Invoke(modPathfinder, new object[] { graph, modField, start, targets, 0 });
                TerrainSearch.MultiPostfix(modField);
                multis++;
            }
            if (round % 11 == 5)
            {
                int a = nodes[random.Next(nodes.Count)];
                object list = RouteMapsTests.Call(graph, "GetNeighbors", a);
                int count = (int)list.GetType().GetProperty("Count").GetValue(list);
                if (count > 0)
                {
                    object first = list.GetType().GetProperty("Item").GetValue(list, new object[] { 0 });
                    int b = (int)first.GetType().GetProperty("Id").GetValue(first);
                    RouteMapsTests.Call(graph, "DisconnectNodes", a, b);
                    List<int> changed = new List<int> { a, b };
                    RouteMapsTests.Call(modField, "OnNodesChanged", changed.AsReadOnlyList());
                    TerrainSearch.NodesChangedPostfix(modField);
                    changes++;
                }
            }
        }
        line = TerrainSearch.TakeStatsLine();
        check(line != null && line.Contains("verify:"), "terrain search verify: stats line has the comparison (" + line + ")");
        check(Number(line, @"(\d+) DIFFERENT DISTANCE") == 0 && Number(line, @"(\d+) DIFFERENT REACHABILITY") == 0,
            $"terrain search verify: no distance or reachability difference after {multis} list searches and {changes} terrain changes ({line})");
        check(Number(line, @"verify: (\d+) identical") > 0 && TerrainSearch.IsActive, "terrain search verify: identical answers counted, feature still active");

        // 4. Steps cheaper than the heuristic allows: a second terrain where one edge in twelve costs 0.3.
        object cheapGraph = RouteMapsTests.Create("TerrainNavMeshGraph", nodeIdService, groupService);
        RouteMapsTests.Call(cheapGraph, "Load");
        List<int> cheapNodes = BuildTerrain(cheapGraph, random, cheapEdges: true);
        object cheapField = RouteMapsTests.Create("PathFlowField");
        TerrainSearch.CreateFeature(new Config { TerrainSearchVerify = true });
        TerrainSearch.Activate();
        for (int round = 0; round < 150; round++)
        {
            int start = cheapNodes[random.Next(cheapNodes.Count)];
            for (int k = 0; k < 6; k++)
            {
                TerrainSearch.FillPrefix(modPathfinder, cheapGraph, cheapField, start, cheapNodes[random.Next(cheapNodes.Count)]);
            }
        }
        line = TerrainSearch.TakeStatsLine();
        long restarted = Number(line, @"(\d+) started over");
        check(Number(line, @"(\d+) DIFFERENT DISTANCE") == 0 && Number(line, @"(\d+) DIFFERENT REACHABILITY") == 0,
            $"terrain search on uneven costs: verify reports no distance or reachability difference ({line})");
        check(restarted > 0 && Number(line, @"(\d+) resumed") >= 0, $"terrain search on uneven costs: {restarted} resumed searches met a cheap step and started over the game's way");
        check(TerrainSearch.IsActive, "terrain search on uneven costs: feature still active");

        // Forgetting: after the game's save into memory (its save benchmark) the field answers nothing and the next
        // search is fresh.
        TerrainSearch.CreateFeature(new Config());
        TerrainSearch.Activate();
        int s0 = nodes[random.Next(nodes.Count)];
        TerrainSearch.FillPrefix(modPathfinder, graph, modField, s0, nodes[random.Next(nodes.Count)]);
        TerrainSearch.TakeStatsLine();
        TerrainSearch.JoinSavePostfix();
        check(Count(modField) == 0 && (int)modField.GetType().GetField("_startNodeId", Any).GetValue(modField) == -1,
            "terrain search: the save into memory empties the field");
        TerrainSearch.FillPrefix(modPathfinder, graph, modField, s0, nodes[random.Next(nodes.Count)]);
        line = TerrainSearch.TakeStatsLine();
        check(line != null && line.Contains("1 started from scratch") && line.Contains("0 resumed"), "terrain search: and the next search is fresh (" + line + ")");
        TerrainSearch.SceneCreated();

        KeptSearchTests.Run(check, nodeIdService, groupService, graph, cheapGraph, heuristics, heapFactory, nodes, cheapNodes, random);
    }

    // Kept searches (0.4.30): PathfindingService.FindTerrainPathUncached replaced by the search kept for the start
    // tile, driven through the real prefix on a PathfindingService built around the game's own classes, against the
    // game's own method on two more services: one whose default field is emptied before every question (the game's
    // search from scratch, the ground truth) and one left to answer as the game does (the game with this mod off).
    private static class KeptSearchTests
    {
        private static object _nodeIds;
        private static MethodInfo _vanilla;
        private static MethodInfo _vanillaMulti;
        private static MethodInfo _cacheUpdated;
        private static MethodInfo _clear;
        private static ConstructorInfo _update;
        private static Vector3[] _world;

        private sealed class Tally
        {
            public int Queries, FreshExact, FreshWrong, Same, SameRoute, Rounding, Distance, Found;
            public int GameDistance, GameFound;
            public long GameExplored;
            public double KeptMs, GameMs;

            public string Text => $"{Queries} questions: {Same} same distance ({SameRoute} same route), {Rounding} within rounding, " +
                                  $"{Distance} DIFFERENT DISTANCE, {Found} DIFFERENT REACHABILITY; searches from scratch " +
                                  $"{FreshExact} identical to the game's, {FreshWrong} not";
        }

        public static void Run(Action<bool, string> check, object nodeIdService, object groupService, object graph, object cheapGraph, object heuristics,
            object heapFactory, List<int> nodes, List<int> cheapNodes, Random random)
        {
            _nodeIds = nodeIdService;
            Type serviceType = RouteMapsTests.Create("TerrainFlowFieldCache").GetType().Assembly
                .GetType("Timberborn.Navigation.PathfindingService", true);
            _vanilla = serviceType.GetMethod("FindTerrainPathUncached", Any, null,
                new[] { typeof(Vector3), typeof(Vector3), typeof(float).MakeByRefType(), typeof(List<PathCorner>) }, null);
            _vanillaMulti = serviceType.GetMethod("FindTerrainPathUncached", Any, null,
                new[] { typeof(Vector3), typeof(IReadOnlyList<int>), typeof(float).MakeByRefType(), typeof(List<PathCorner>) }, null);
            _cacheUpdated = RouteMapsTests.Create("TerrainFlowFieldCache").GetType().GetMethod("OnNavMeshUpdated", Any);
            _clear = RouteMapsTests.Create("PathFlowField").GetType().GetMethod("Clear", Any, null, new[] { typeof(int) }, null);
            _update = typeof(NavMeshUpdate).GetConstructors(Any)[0];
            _world = new Vector3[Width * Height];
            bool roundTrip = true;
            for (int id = 0; id < _world.Length; id++)
            {
                _world[id] = (Vector3)RouteMapsTests.Call(nodeIdService, "IdToWorld", id);
                roundTrip &= (int)RouteMapsTests.Call(nodeIdService, "WorldToId", _world[id]) == id;
            }
            check(_vanilla != null && _vanillaMulti != null && roundTrip,
                "kept terrain searches: the game's FindTerrainPathUncached found, and tile positions map back to their ids");

            object kept = Service(serviceType, nodeIdService, graph, heuristics, heapFactory, true);
            object fresh = Service(serviceType, nodeIdService, graph, heuristics, heapFactory, true);
            object game = Service(serviceType, nodeIdService, graph, heuristics, heapFactory, true);

            // 1 + 5. Need picks: beavers pricing buildings, each price a trip there and on to the place the beaver
            // has to be back at, with terrain changes now and then (2).
            TerrainSearch.CreateFeature(new Config());
            TerrainSearch.Activate();
            Tally tally = new Tally();
            List<int[]> removed = new List<int[]>();
            int changes = Picks(kept, fresh, game, graph, nodes, random, 220, tally, removed, null);
            Restore(graph, new[] { kept, fresh, game }, removed);
            string line = TerrainSearch.TakeStatsLine();
            long keptExplored = Number(line, @"exploring (\d+) tiles") + Number(line, @"exploring (\d+) more tiles");
            check(tally.FreshWrong == 0 && tally.FreshExact > 0,
                $"kept terrain searches: every search from scratch leaves the field identical to the game's and gives its answer ({tally.Text})");
            check(tally.Distance == 0 && tally.Found == 0,
                $"kept terrain searches: distance and reachability are the game's in every question, {changes} terrain changes mixed in ({tally.Text})");
            check(Number(line, @"(\d+) resumed") > 0 && Number(line, @"(\d+) answered") > 0 && Number(line, @"(\d+) kept now") > 0,
                "kept terrain searches: questions from tiles taking turns are answered and resumed from kept searches (" + line + ")");
            check(keptExplored > 0 && keptExplored * 2 < tally.GameExplored,
                $"kept terrain searches: need picks explored {keptExplored} tiles in {tally.KeptMs:0} ms where the game explores " +
                $"{tally.GameExplored} in {tally.GameMs:0} ms ({100.0 * keptExplored / tally.GameExplored:0}% of the tiles)");
            check(Number(line, @"(\d+) kept searches emptied by terrain changes") > 0,
                "kept terrain searches: terrain changes emptied kept fields that held a changed tile");

            // 2. A terrain change reaches a kept field exactly as it reaches the game's field.
            TerrainSearch.SceneCreated();
            bool sameAfterChange = true;
            int emptied = 0, kept2 = 0;
            for (int round = 0; round < 40; round++)
            {
                TerrainSearch.SceneCreated();
                int start = nodes[random.Next(nodes.Count)];
                int dest = nodes[random.Next(nodes.Count)];
                Kept(kept, start, dest);
                object keptField = TerrainSearch.LastField;
                Clear(Default(game), -1);
                Ask(game, start, dest, out _, out _);
                sameAfterChange &= SameField(keptField, Default(game));
                int a = random.Next(2) == 0 ? start : nodes[random.Next(nodes.Count)];
                int[] edge = Disconnect(graph, a);
                if (edge == null) continue;
                Update(new[] { kept, fresh, game }, edge[0], edge[1]);
                sameAfterChange &= SameField(keptField, Default(game));
                if (Count(keptField) == 0) emptied++; else kept2++;
                Reconnect(graph, edge);
                Update(new[] { kept, fresh, game }, edge[0], edge[1]);
                sameAfterChange &= SameField(keptField, Default(game));
            }
            check(sameAfterChange && emptied > 0 && kept2 > 0,
                $"kept terrain searches: after each terrain change the kept field is the game's field after the same change ({emptied} emptied, {kept2} kept)");

            // 3. Least recently used replaced first.
            TerrainSearch.SceneCreated();
            TerrainSearch.TakeStatsLine();
            List<int> starts = new List<int>();
            while (starts.Count < TerrainSearch.KeptSearches + 1)
            {
                int start = nodes[random.Next(nodes.Count)];
                if (!starts.Contains(start)) starts.Add(start);
            }
            int[] dests = new int[starts.Count];
            for (int i = 0; i < starts.Count; i++)
            {
                dests[i] = nodes[random.Next(nodes.Count)];
                Kept(kept, starts[i], dests[i]);
            }
            bool full = TerrainSearch.KeptInUse == TerrainSearch.KeptSearches;
            Kept(kept, starts[0], dests[0]);
            bool firstGone = TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Fresh;
            Kept(kept, starts[2], dests[2]);
            bool thirdKept = TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Answered;
            Kept(kept, starts[1], dests[1]);
            bool secondGone = TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Fresh;
            Kept(kept, starts[TerrainSearch.KeptSearches], dests[TerrainSearch.KeptSearches]);
            bool lastKept = TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Answered;
            line = TerrainSearch.TakeStatsLine();
            check(full && firstGone && thirdKept && secondGone && lastKept &&
                  Number(line, @"(\d+) new start tiles replaced the least recently used") == 3,
                $"kept terrain searches: {TerrainSearch.KeptSearches} start tiles kept, the least recently used replaced first ({line})");

            // The memory limit: with a small one, the least recently used are dropped and the rest keep answering.
            TerrainSearch.SceneCreated();
            TerrainSearch.SetKeptLimitForTests(400 * 1024);
            Tally small = new Tally();
            Picks(kept, fresh, game, graph, nodes, random, 40, small, removed, null);
            Restore(graph, new[] { kept, fresh, game }, removed);
            line = TerrainSearch.TakeStatsLine();
            TerrainSearch.SetKeptLimitForTests(TerrainSearch.KeptBytesLimit);
            check(Number(line, @"(\d+) dropped to stay under") > 0 && small.Distance == 0 && small.Found == 0 && small.FreshWrong == 0,
                $"kept terrain searches: over the memory limit the least recently used are dropped, answers unchanged ({small.Text}; {line})");

            // Verify mode, list-of-destinations searches and terrain changes mixed in; and it only measures: the
            // answers are the same with it off. Each run on a terrain of its own built the same way (a step put back
            // goes to the end of a tile's list, so a terrain that has been changed is not the same terrain again).
            List<string> plain = new List<string>(), verified = new List<string>();
            foreach (bool verify in new[] { false, true })
            {
                TerrainSearch.CreateFeature(new Config { TerrainSearchVerify = verify });
                TerrainSearch.Activate();
                object sameGraph = RouteMapsTests.Create("TerrainNavMeshGraph", nodeIdService, groupService);
                RouteMapsTests.Call(sameGraph, "Load");
                List<int> sameNodes = BuildTerrain(sameGraph, new Random(4242));
                foreach (object service in new[] { kept, fresh, game })
                {
                    Set(service, "_terrainNavMeshGraph", sameGraph);
                    Clear(Default(service), -1);
                }
                Tally withVerify = new Tally();
                Picks(kept, fresh, game, sameGraph, sameNodes, new Random(77), 60, withVerify, new List<int[]>(), verify ? verified : plain);
                line = TerrainSearch.TakeStatsLine();
            }
            check(line != null && line.Contains("verify:") && Number(line, @"(\d+) DIFFERENT DISTANCE") == 0 &&
                  Number(line, @"(\d+) DIFFERENT REACHABILITY") == 0 && Number(line, @"verify: (\d+) identical") > 0,
                "kept terrain searches, verify: no distance or reachability difference with list searches and terrain changes mixed in (" + line + ")");
            check(plain.Count > 0 && string.Join("|", plain) == string.Join("|", verified),
                $"kept terrain searches, verify only measures: the {plain.Count} answers are the same with it on and off");

            // Uneven costs (ziplines): the same need picks on the terrain where one step in twelve is cheap.
            TerrainSearch.CreateFeature(new Config { TerrainSearchVerify = true });
            TerrainSearch.Activate();
            foreach (object service in new[] { kept, fresh, game })
            {
                Set(service, "_terrainNavMeshGraph", cheapGraph);
                Clear(Default(service), -1);
            }
            Tally uneven = new Tally();
            Picks(kept, fresh, game, cheapGraph, cheapNodes, random, 120, uneven, new List<int[]>(), null);
            line = TerrainSearch.TakeStatsLine();
            check(uneven.Distance == 0 && uneven.Found == 0 && uneven.FreshWrong == 0 && Number(line, @"(\d+) DIFFERENT DISTANCE") == 0 &&
                  Number(line, @"(\d+) DIFFERENT REACHABILITY") == 0,
                $"kept terrain searches on uneven costs: the game's answer every time ({uneven.Text}; the game's own answers differ from its " +
                $"searches from scratch in {uneven.GameDistance} distances and {uneven.GameFound} reachabilities; {line})");

            // Memory: one kept search that explores a whole 258 x 258 map (a question about a tile it cannot reach).
            TerrainSearch.CreateFeature(new Config());
            TerrainSearch.Activate();
            LargeMap(check, groupService, serviceType);
            foreach (object service in new[] { kept, fresh, game })
            {
                Set(service, "_terrainNavMeshGraph", graph);
            }

            // 4. A failure hands the question back to the game and turns the feature off.
            TerrainSearch.CreateFeature(new Config());
            TerrainSearch.Activate();
            object broken = Service(serviceType, nodeIdService, graph, heuristics, heapFactory, false);
            float distance = 0f;
            bool result = false;
            int s0 = nodes[random.Next(nodes.Count)], d0 = nodes[random.Next(nodes.Count)];
            bool handedBack = TerrainSearch.UncachedPrefix(broken, _world[s0], _world[d0], ref distance, null, ref result);
            bool off = !TerrainSearch.IsActive && TerrainSearch.KeptInUse == 0 && Array.IndexOf(TurnedOff.Names(), "TerrainSearch") >= 0;
            bool passes = TerrainSearch.UncachedPrefix(kept, _world[s0], _world[d0], ref distance, null, ref result);
            check(handedBack && off && passes && Count(Default(broken)) == 0,
                "kept terrain searches: a failure runs the game's own method on its untouched field, turns TerrainSearch off and reports it");
            TerrainSearch.SceneCreated();
        }

        // Rounds of need picks: a beaver (who moves now and then) prices a dozen of the buildings, each a trip there
        // and on to one of a few essential places. Terrain changes (an edge removed, one put back) every few rounds.
        private static int Picks(object kept, object fresh, object game, object graph, List<int> nodes, Random random, int rounds,
            Tally tally, List<int[]> removed, List<string> answers)
        {
            int[] beavers = Pick(nodes, random, 30);
            int[] buildings = Pick(nodes, random, 24);
            int[] essentials = Pick(nodes, random, 5);
            int changes = 0;
            for (int round = 0; round < rounds; round++)
            {
                int b = random.Next(beavers.Length);
                if (random.Next(4) == 0) beavers[b] = nodes[random.Next(nodes.Count)];
                int beaver = beavers[b];
                int essential = essentials[random.Next(essentials.Length)];
                for (int k = 0; k < 12; k++)
                {
                    int building = buildings[random.Next(buildings.Length)];
                    Question(kept, fresh, game, beaver, building, tally, answers);
                    Question(kept, fresh, game, building, essential, tally, answers);
                }
                if (round % 9 == 4)
                {
                    int[] edge = Disconnect(graph, random.Next(3) == 0 ? beaver : nodes[random.Next(nodes.Count)]);
                    if (edge != null)
                    {
                        removed.Add(edge);
                        Update(new[] { kept, fresh, game }, edge[0], edge[1]);
                        changes++;
                    }
                }
                if (round % 9 == 8 && removed.Count > 0)
                {
                    int[] edge = removed[0];
                    removed.RemoveAt(0);
                    Reconnect(graph, edge);
                    Update(new[] { kept, fresh, game }, edge[0], edge[1]);
                    changes++;
                }
                if (round % 13 == 6 && answers != null)
                {
                    // A list-of-destinations search on the game's field, as FindRoadSpillOrTerrainPath runs one.
                    List<int> targets = new List<int> { nodes[random.Next(nodes.Count)], nodes[random.Next(nodes.Count)] };
                    _vanillaMulti.Invoke(kept, new object[] { _world[beaver], targets, 0f, null });
                    TerrainSearch.MultiPostfix(Default(kept));
                }
            }
            return changes;
        }

        private static void Question(object kept, object fresh, object game, int start, int dest, Tally tally, List<string> answers)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            float keptDistance = 0f;
            bool keptFound = false;
            bool handled = !TerrainSearch.UncachedPrefix(kept, _world[start], _world[dest], ref keptDistance, null, ref keptFound);
            tally.KeptMs += watch.Elapsed.TotalMilliseconds;
            if (!handled)
            {
                keptFound = Ask(kept, start, dest, out keptDistance, out _);
            }
            answers?.Add(keptFound + ":" + keptDistance.ToString("R"));

            Clear(Default(fresh), -1);
            bool freshFound = Ask(fresh, start, dest, out float freshDistance, out _);
            bool gameSearches = !(bool)RouteMapsTests.Call(Default(game), "CheckedPath", start, dest);
            watch.Restart();
            bool gameFound = Ask(game, start, dest, out float gameDistance, out _);
            tally.GameMs += watch.Elapsed.TotalMilliseconds;
            if (gameSearches) tally.GameExplored += Count(Default(game));
            tally.Queries++;
            if (handled && (TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Fresh ||
                            TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Restarted))
            {
                if (SameField(TerrainSearch.LastField, Default(fresh)) && keptFound == freshFound && keptDistance.Equals(freshDistance))
                    tally.FreshExact++;
                else
                    tally.FreshWrong++;
            }
            if (gameFound != freshFound) tally.GameFound++;
            else if (gameFound && Math.Abs(gameDistance - freshDistance) > 0.001f * Math.Max(1f, freshDistance)) tally.GameDistance++;
            // Against the game with this mod off.
            if (keptFound != gameFound)
            {
                tally.Found++;
            }
            else if (keptFound)
            {
                if (keptDistance == gameDistance)
                {
                    tally.Same++;
                    if (handled && SameRoute(TerrainSearch.LastField, Default(game), start, dest)) tally.SameRoute++;
                }
                else if (Math.Abs(keptDistance - gameDistance) <= 0.001f * Math.Max(1f, Math.Abs(gameDistance))) tally.Rounding++;
                else tally.Distance++;
            }
        }

        // A 258 x 258 map, one level, one tile in eight blocked, with a two-tile island nobody can reach: one kept
        // search is asked for the island from the far corner and explores everything else. Reports what that search
        // holds (the field's dictionary and the heap's array, as allocated by .NET here) against the mod's estimate.
        private static void LargeMap(Action<bool, string> check, object groupService, Type serviceType)
        {
            const int size = 258;
            object ids = RouteMapsTests.CreateNodeIdService(size * size);
            Vector3Int[] table = new Vector3Int[size * size];
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++) table[x * size + y] = new Vector3Int(x - 1, y - 1, -1);
            }
            ids.GetType().GetField("_idToCoordinatesTable", Any).SetValue(ids, table);
            ids.GetType().GetField("_size", Any).SetValue(ids, new Vector3Int(size, size, 1));
            object map = RouteMapsTests.Create("TerrainNavMeshGraph", ids, groupService);
            RouteMapsTests.Call(map, "Load");
            Random random = new Random(258);
            bool[] walkable = new bool[size * size];
            for (int i = 0; i < walkable.Length; i++) walkable[i] = random.Next(8) != 0;
            // The island: tiles (10, 10) and (10, 11), everything around them blocked.
            for (int x = 9; x <= 11; x++)
            {
                for (int y = 9; y <= 12; y++) walkable[x * size + y] = false;
            }
            int islandA = 10 * size + 10, islandB = 10 * size + 11;
            walkable[islandA] = walkable[islandB] = true;
            MethodInfo connect = map.GetType().GetMethod("ConnectNodes", Any);
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    int id = x * size + y;
                    if (!walkable[id]) continue;
                    void Connect(int other, float cost)
                    {
                        if (walkable[other]) connect.Invoke(map, new object[] { id, other, 0, cost });
                    }
                    if (x + 1 < size) Connect(id + size, 1f);
                    if (y + 1 < size) Connect(id + 1, 1f);
                    if (x + 1 < size && y + 1 < size) Connect(id + size + 1, 1.4142135f);
                    if (x + 1 < size && y > 0) Connect(id + size - 1, 1.4142135f);
                }
            }
            int start = (size - 3) * size + (size - 3);
            while (!walkable[start]) start--;
            object heapFactory = RouteMapsTests.Create("BinaryHeapFactory", ids);
            object heuristics = RouteMapsTests.Create("HeuristicsCalculator", RouteMapsTests.Create("DistanceCalculator", ids), ids);
            object service = Service(serviceType, ids, map, heuristics, heapFactory, true);
            Vector3 from = (Vector3)RouteMapsTests.Call(ids, "IdToWorld", start);
            Vector3 to = (Vector3)RouteMapsTests.Call(ids, "IdToWorld", islandA);
            float distance = 0f;
            bool found = true;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            bool handled = !TerrainSearch.UncachedPrefix(service, from, to, ref distance, null, ref found);
            double ms = watch.Elapsed.TotalMilliseconds;
            object field = TerrainSearch.LastField;
            IDictionary nodes = (IDictionary)field.GetType().GetField("_nodes", Any).GetValue(field);
            Array buckets = (Array)nodes.GetType().GetField("_buckets", Any).GetValue(nodes);
            Array entries = (Array)nodes.GetType().GetField("_entries", Any).GetValue(nodes);
            long fieldBytes = buckets.Length * 4L + entries.Length * 20L;
            string line = TerrainSearch.TakeStatsLine();
            Match estimate = Regex.Match(line ?? "", @"~([\d.]+) MB");
            double estimated = estimate.Success ? double.Parse(estimate.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
            // The heap: the mod's figure less the field's estimate is the heap's array.
            long tiles = nodes.Count;
            check(handled && !found && TerrainSearch.LastOutcome == TerrainSearch.Engine.Outcome.Fresh && tiles > 50000 &&
                  estimated * 1048576 >= fieldBytes && estimated < 8,
                $"kept terrain searches, memory: a search over a whole {size} x {size} map ({tiles} tiles, {ms:0} ms) holds " +
                $"{fieldBytes / 1048576.0:0.0} MB in its field's dictionary here; the mod counts ~{estimated:0.0} MB for field and heap " +
                $"together, so {TerrainSearch.KeptSearches} such searches would be ~{estimated * TerrainSearch.KeptSearches:0} MB " +
                $"and the {TerrainSearch.KeptBytesLimit >> 20} MB limit keeps about {(TerrainSearch.KeptBytesLimit >> 20) / Math.Max(0.1, estimated):0} of them");
        }

        // The prefix, as Harmony would call it; true if it handled the question.
        private static bool Kept(object kept, int start, int dest)
        {
            float distance = 0f;
            bool found = false;
            return !TerrainSearch.UncachedPrefix(kept, _world[start], _world[dest], ref distance, null, ref found);
        }

        // Puts back the removed steps, newest first, each with its navmesh update.
        private static void Restore(object graph, object[] services, List<int[]> removed)
        {
            for (int i = removed.Count - 1; i >= 0; i--)
            {
                Reconnect(graph, removed[i]);
                Update(services, removed[i][0], removed[i][1]);
            }
            removed.Clear();
        }

        // The game's own FindTerrainPathUncached (not patched in the harness) on a service.
        private static bool Ask(object service, int start, int dest, out float distance, out object field)
        {
            object[] arguments = { _world[start], _world[dest], 0f, null };
            bool found = (bool)_vanilla.Invoke(service, arguments);
            distance = (float)arguments[2];
            field = Default(service);
            return found;
        }

        private static object Service(Type type, object nodeIdService, object graph, object heuristics, object heapFactory, bool withFinder)
        {
            object service = RuntimeHelpers.GetUninitializedObject(type);
            object pathfinder = RouteMapsTests.Create("TerrainAStarPathfinder", heuristics, heapFactory);
            RouteMapsTests.Call(pathfinder, "Load");
            Set(service, "_terrainFlowFieldCache", RouteMapsTests.Create("TerrainFlowFieldCache"));
            Set(service, "_terrainAStarPathfinder", pathfinder);
            Set(service, "_nodeIdService", nodeIdService);
            Set(service, "_terrainNavMeshGraph", graph);
            Set(service, "_flowFieldPathFinder", withFinder ? RouteMapsTests.Create("FlowFieldPathFinder", nodeIdService, null, null) : null);
            return service;
        }

        private static void Set(object target, string field, object value)
        {
            target.GetType().GetField(field, Any).SetValue(target, value);
        }

        private static object Default(object service)
        {
            object cache = service.GetType().GetField("_terrainFlowFieldCache", Any).GetValue(service);
            return RouteMapsTests.Call(cache, "GetDefaultFlowField");
        }

        private static void Clear(object field, int start)
        {
            _clear.Invoke(field, new object[] { start });
        }

        private static int[] Pick(List<int> nodes, Random random, int count)
        {
            int[] picked = new int[count];
            for (int i = 0; i < count; i++) picked[i] = nodes[random.Next(nodes.Count)];
            return picked;
        }

        // Removes one of the tile's steps: { a, b, group, cost bits }, or null if it has none.
        private static int[] Disconnect(object graph, int a)
        {
            object list = RouteMapsTests.Call(graph, "GetNeighbors", a);
            int count = (int)list.GetType().GetProperty("Count").GetValue(list);
            if (count == 0) return null;
            object first = list.GetType().GetProperty("Item").GetValue(list, new object[] { 0 });
            int b = (int)first.GetType().GetProperty("Id").GetValue(first);
            int group = (int)first.GetType().GetProperty("GroupId").GetValue(first);
            float cost = (float)first.GetType().GetProperty("Cost").GetValue(first);
            RouteMapsTests.Call(graph, "DisconnectNodes", a, b);
            return new[] { a, b, group, BitConverter.SingleToInt32Bits(cost) };
        }

        private static void Reconnect(object graph, int[] edge)
        {
            graph.GetType().GetMethod("ConnectNodes", Any).Invoke(graph,
                new object[] { edge[0], edge[1], edge[2], BitConverter.Int32BitsToSingle(edge[3]) });
        }

        // The game's navmesh update for a changed step: both tiles, to the terrain field caches of every service
        // (the game's own code empties their default fields) and to TerrainSearch's postfix.
        private static void Update(object[] services, int a, int b)
        {
            ParameterInfo[] parameters = _update.GetParameters();
            object update = _update.Invoke(new[]
            {
                Activator.CreateInstance(parameters[0].ParameterType), Activator.CreateInstance(parameters[1].ParameterType),
                new List<int> { a, b }.AsReadOnlyList(), new List<int>().AsReadOnlyList()
            });
            foreach (object service in services)
            {
                object cache = service.GetType().GetField("_terrainFlowFieldCache", Any).GetValue(service);
                _cacheUpdated.Invoke(cache, new[] { update });
            }
            TerrainSearch.NavMeshUpdatedPostfix((NavMeshUpdate)update);
        }
    }

    private static long Number(string line, string pattern)
    {
        Match match = Regex.Match(line ?? "", pattern);
        return match.Success ? long.Parse(match.Groups[1].Value) : -1;
    }

    // Tile x * Height + y is at (x - 1, y - 1, -1), which is where NodeIdService.GridToId puts it on a map of
    // Width x Height x 1 with the game's one-tile boundary, so world positions map back to the same ids (the
    // heuristic reads only differences in x and y).
    private static void SetCoordinates(object nodeIdService)
    {
        Vector3Int[] table = new Vector3Int[Width * Height];
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                table[x * Height + y] = new Vector3Int(x - 1, y - 1, -1);
            }
        }
        nodeIdService.GetType().GetField("_idToCoordinatesTable", Any).SetValue(nodeIdService, table);
        nodeIdService.GetType().GetField("_size", Any).SetValue(nodeIdService, new Vector3Int(Width, Height, 1));
    }

    private static List<int> BuildTerrain(object graph, Random random, bool cheapEdges = false)
    {
        bool[] walkable = new bool[Width * Height];
        for (int i = 0; i < walkable.Length; i++) walkable[i] = random.Next(100) >= 12;
        MethodInfo connect = graph.GetType().GetMethod("ConnectNodes", Any);
        bool[] connected = new bool[walkable.Length];
        void Connect(int a, int b, float cost)
        {
            int group = random.Next(20) == 0 ? 1 : 0;
            connect.Invoke(graph, new object[] { a, b, group, cost });
            connected[a] = connected[b] = true;
        }
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                int id = x * Height + y;
                if (!walkable[id]) continue;
                float straight = random.Next(20) == 0 ? 3f : 1f;
                if (cheapEdges && random.Next(12) == 0) straight = 0.3f;
                if (x + 1 < Width && walkable[id + Height]) Connect(id, id + Height, straight);
                if (y + 1 < Height && walkable[id + 1]) Connect(id, id + 1, straight);
                if (x + 1 < Width && y + 1 < Height && walkable[id + Height + 1]) Connect(id, id + Height + 1, 1.4142135f);
                if (x + 1 < Width && y > 0 && walkable[id + Height - 1]) Connect(id, id + Height - 1, 1.4142135f);
            }
        }
        List<int> nodes = new List<int>();
        for (int i = 0; i < connected.Length; i++)
        {
            if (connected[i]) nodes.Add(i);
        }
        return nodes;
    }

    private static int Count(object field)
    {
        return ((ICollection)field.GetType().GetField("_nodes", Any).GetValue(field)).Count;
    }

    private static bool SameField(object a, object b)
    {
        foreach (string name in new[] { "_startNodeId", "_refreshed", "_fullyFilled" })
        {
            FieldInfo info = a.GetType().GetField(name, Any);
            if (!Equals(info.GetValue(a), info.GetValue(b))) return false;
        }
        IDictionary na = (IDictionary)a.GetType().GetField("_nodes", Any).GetValue(a);
        IDictionary nb = (IDictionary)b.GetType().GetField("_nodes", Any).GetValue(b);
        if (na.Count != nb.Count) return false;
        PropertyInfo parent = null, dist = null;
        foreach (DictionaryEntry entry in na)
        {
            if (!nb.Contains(entry.Key)) return false;
            object va = entry.Value, vb = nb[entry.Key];
            parent = parent ?? va.GetType().GetProperty("ParentNodeId");
            dist = dist ?? va.GetType().GetProperty("Distance");
            if (!Equals(parent.GetValue(va), parent.GetValue(vb)) || !Equals(dist.GetValue(va), dist.GetValue(vb))) return false;
        }
        return true;
    }

    private static bool SameRoute(object a, object b, int start, int dest)
    {
        int na = dest, nb = dest, guard = 0;
        while (na != start && nb != start && guard++ < Width * Height)
        {
            na = (int)RouteMapsTests.Call(a, "GetParentId", na);
            nb = (int)RouteMapsTests.Call(b, "GetParentId", nb);
            if (na != nb) return false;
        }
        return na == nb;
    }
}
