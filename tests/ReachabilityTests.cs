using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using LateGamePerformance;

// The reachability areas against the game's real GlobalReachabilityService, two of them over one real
// InstantTerrainNavMeshGraph: one answers with the game's own code, the other through the mod's prefix, as Harmony
// would call it (the game's method runs only when the prefix says so). A random terrain of three levels with holes,
// so that there are many areas, a few one-way connections (the game's graph has none, but the mod keeps the game's
// rule of a later flood taking over nodes an earlier one reached), random questions, and nav-mesh updates that change
// the graph and make both services forget their areas. Every area number the game hands out must be the mod's, and at
// checkpoints the game's Dictionary must equal the mod's areas node for node, with the same area counter.
internal static class ReachabilityTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Width = 48, Height = 48, Levels = 3;

    private sealed class World
    {
        public object Graph;
        public object NodeIds;
        public MethodInfo Connect, Disconnect, RemoveOneWay, GetArea, OnUpdate;
        public object Update;
        public readonly List<int> Walkable = new List<int>();
    }

    public static void Run(Action<bool, string> check)
    {
        World world = BuildWorld(new Random(424242));
        check(world.Walkable.Count > 4000, $"reachability: test terrain built ({world.Walkable.Count} connected nodes of {Width * Height * Levels})");
        Reachability.CreateFeature(new Config()).Patches[0].Target();
        Func<object, int, int[], int> copyNeighbors = Reachability.CopyNeighbors;
        Reachability.Activate();

        // Another mod's patch on the flood's own methods: the game's code answers every question, from the first.
        Reachability.ResetForTests();
        Reachability.ForeignPatch = () => "GlobalReachabilityService.VisitNode (another.mod)";
        object standGame = NewService(world), standMod = NewService(world);
        Random random = new Random(7);
        bool allGames = true, same = true;
        for (int i = 0; i < 200; i++)
        {
            int node = world.Walkable[random.Next(world.Walkable.Count)];
            int result = 0;
            allGames &= Reachability.AreaPrefix(standMod, node, ref result);
            same &= GameArea(world, standGame, node) == GameArea(world, standMod, node);
        }
        check(allGames && same && Reachability.TakeStatsLine().Contains("200 questions left to the game"),
            "reachability: with another mod's patch on the flood, every question is answered by the game's own code");
        Reachability.ResetForTests();
        Reachability.ForeignPatch = () => null;
        Benchmark(check);
        Reachability.ResetForTests();

        // The main run. The mod's service first answers a few questions with the game's own code, as if the mod had
        // come late; the mod then starts from the game's areas.
        object game = NewService(world), mod = NewService(world);
        random = new Random(99);
        int questions = 0, updates = 0, checkpoints = 0, differences = 0;
        string firstDifference = null;
        for (int op = 0; op < 6000 && differences == 0; op++)
        {
            Reachability.VerifyEnabled = op >= 3000 && op < 4500;
            int roll = random.Next(100);
            if (roll < 7)
            {
                ChangeTerrain(world, random);
                world.OnUpdate.Invoke(game, new[] { world.Update });
                world.OnUpdate.Invoke(mod, new[] { world.Update });
                updates++;
                if (random.Next(3) == 0)
                {
                    // Many walkable nodes asked right after the update, in a random order, as the district citizen
                    // assigner does for every beaver.
                    List<int> order = world.Walkable.OrderBy(_ => random.Next()).Take(600).ToList();
                    List<int> gameAreas = order.Select(node => GameArea(world, game, node)).ToList();
                    List<int> modAreas = order.Select(node => ModArea(world, mod, node, op < 40)).ToList();
                    questions += order.Count;
                    if (!gameAreas.SequenceEqual(modAreas))
                    {
                        differences++;
                        firstDifference ??= $"op {op}: after an update the areas of 600 nodes differ";
                    }
                }
                continue;
            }
            // AreaReachable(a, b): the area of a, then of b. Mostly walkable nodes, sometimes any node of the map.
            int a = random.Next(5) == 0 ? random.Next(Width * Height * Levels) : world.Walkable[random.Next(world.Walkable.Count)];
            int b = random.Next(5) == 0 ? random.Next(Width * Height * Levels) : world.Walkable[random.Next(world.Walkable.Count)];
            int gameA = GameArea(world, game, a), gameB = GameArea(world, game, b);
            int modA = ModArea(world, mod, a, op < 40), modB = ModArea(world, mod, b, op < 40);
            questions += 2;
            if (gameA != modA || gameB != modB)
            {
                differences++;
                firstDifference ??= $"op {op}: nodes {a}, {b} in areas {modA}, {modB}, the game's {gameA}, {gameB}";
            }
            if (op % 250 == 0)
            {
                checkpoints++;
                string state = SameState(game, mod, Reachability.VerifyEnabled || op < 40);
                if (state != null)
                {
                    differences++;
                    firstDifference ??= $"op {op}: {state}";
                }
            }
        }
        string stats = Reachability.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(differences == 0 && questions > 10000 && updates > 300,
            $"reachability: {questions} area questions and {updates} nav-mesh updates with graph changes, every area number the " +
            $"game's, and the game's Dictionary and counter equal to the mod's areas at {checkpoints} checkpoints" +
            (firstDifference == null ? "" : "; first difference: " + firstDifference));
        check(stats.StartsWith("Reachability: ") && !stats.Contains(" 0 of which flooded") && !stats.Contains("verify mismatches"),
            "reachability stats: " + stats);
        Reachability.VerifyEnabled = true;
        string verifyStats = Reachability.TakeStatsLine();
        Reachability.VerifyEnabled = false;
        check(verifyStats.EndsWith("verify mismatches 0"),
            "reachability verify: the game's own flood, run into the game's own Dictionary beside the mod's, agreed on every node for 1500 operations");

        // Verify only measures: an area broken behind the mod's back is counted and logged, and the mod's answer is the
        // one handed out. A nav-mesh update then makes both forget, and they agree again.
        Reachability.VerifyEnabled = true;
        world.OnUpdate.Invoke(game, new[] { world.Update });
        world.OnUpdate.Invoke(mod, new[] { world.Update });
        int probe = world.Walkable[0];
        int gameProbe = GameArea(world, game, probe);
        int modProbe = ModArea(world, mod, probe, false);
        // Another area, asked of both services alike, and the probe's area set to it behind the mod's back.
        int otherArea = gameProbe;
        foreach (int node in world.Walkable)
        {
            int gameArea = GameArea(world, game, node);
            ModArea(world, mod, node, false);
            if (gameArea != gameProbe)
            {
                otherArea = gameArea;
                break;
            }
        }
        Reachability.SetAreaForTests(probe, otherArea);
        int handed = ModArea(world, mod, probe, false);
        string brokenStats = Reachability.TakeStatsLine();
        Console.WriteLine("     " + brokenStats);
        world.OnUpdate.Invoke(game, new[] { world.Update });
        world.OnUpdate.Invoke(mod, new[] { world.Update });
        bool againSame = true;
        foreach (int node in world.Walkable.Take(300))
        {
            againSame &= GameArea(world, game, node) == ModArea(world, mod, node, false);
        }
        Reachability.VerifyEnabled = false;
        check(modProbe == gameProbe && otherArea != gameProbe && handed == otherArea && brokenStats.EndsWith("verify mismatches 1") && againSame &&
              SameState(game, mod, false) == null,
            "reachability verify: a broken area is counted as a mismatch and the mod's answer is handed out all the same; " +
            "after the next nav-mesh update both agree again");

        // A flood that throws: its marks are undone, the game's Dictionary and counter get the mod's areas, the feature
        // turns itself off, and from then on the game's own code answers from there, exactly as the other service.
        for (int i = 0; i < 200; i++)
        {
            int node = world.Walkable[random.Next(world.Walkable.Count)];
            GameArea(world, game, node);
            ModArea(world, mod, node, false);
        }
        Reachability.CopyNeighbors = (graph, node, buffer) =>
        {
            if (random.Next(50) == 0)
            {
                throw new InvalidOperationException("forced by the test");
            }
            return copyNeighbors(graph, node, buffer);
        };
        world.OnUpdate.Invoke(game, new[] { world.Update });
        world.OnUpdate.Invoke(mod, new[] { world.Update });
        bool afterFailure = true;
        int handedOver = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (i % 400 == 0)
            {
                ChangeTerrain(world, random);
                world.OnUpdate.Invoke(game, new[] { world.Update });
                world.OnUpdate.Invoke(mod, new[] { world.Update });
            }
            int node = random.Next(3) == 0 ? random.Next(Width * Height * Levels) : world.Walkable[random.Next(world.Walkable.Count)];
            bool wasActive = Reachability.IsActive;
            afterFailure &= GameArea(world, game, node) == ModArea(world, mod, node, false);
            if (wasActive && !Reachability.IsActive)
            {
                handedOver++;
                afterFailure &= SameState(game, mod, true) == null;
            }
        }
        Reachability.CopyNeighbors = copyNeighbors;
        check(handedOver == 1 && !Reachability.IsActive && afterFailure && SameState(game, mod, true) == null &&
              TurnedOff.Names().Contains("Reachability"),
            "reachability: a flood that throws hands the game the mod's areas and counter and turns the feature off " +
            "(reported); the game's own code answers from there exactly as the other service does");
    }

    // A map the size of a large one (258 x 258 x 24 nodes, as NodeIdService counts them) with one walkable level of
    // 256 x 256 tiles: the whole area flooded after each nav-mesh update, by the game's code and by the mod's.
    private static void Benchmark(Action<bool, string> check)
    {
        const int side = 256;
        World world = new World { NodeIds = RouteMapsTests.CreateNodeIdService(258 * 258 * 24) };
        object groups = RouteMapsTests.Create("NavMeshGroupService");
        world.Graph = RouteMapsTests.Create("InstantTerrainNavMeshGraph", world.NodeIds, groups);
        RouteMapsTests.Call(world.Graph, "Load");
        Type graphType = world.Graph.GetType();
        world.Connect = graphType.GetMethod("ConnectNodes", Any);
        Type service = graphType.Assembly.GetType("Timberborn.Navigation.GlobalReachabilityService", true);
        world.GetArea = service.GetMethod("GetAreaOfNode", Any);
        world.OnUpdate = service.GetMethod("OnInstantNavMeshUpdated", Any);
        world.Update = Activator.CreateInstance(world.OnUpdate.GetParameters()[0].ParameterType);
        Random random = new Random(3);
        int Node(int x, int y) => ((x + 1) * 258 + (y + 1)) * 24 + 5;
        for (int x = 0; x < side; x++)
        {
            for (int y = 0; y < side; y++)
            {
                if (x + 1 < side && random.Next(30) > 0) world.Connect.Invoke(world.Graph, new object[] { Node(x, y), Node(x + 1, y), 0, 1f });
                if (y + 1 < side && random.Next(30) > 0) world.Connect.Invoke(world.Graph, new object[] { Node(x, y), Node(x, y + 1), 0, 1f });
            }
        }
        object game = NewService(world), mod = NewService(world);
        int start = Node(side / 2, side / 2);
        // Alternating, and the fastest of 20 each: the least disturbed by whatever else the computer is doing.
        long gameTicks = long.MaxValue, modTicks = long.MaxValue;
        bool same = true;
        for (int round = 0; round < 20; round++)
        {
            world.OnUpdate.Invoke(game, new[] { world.Update });
            world.OnUpdate.Invoke(mod, new[] { world.Update });
            long started = Stopwatch.GetTimestamp();
            int gameArea = GameArea(world, game, start);
            gameTicks = Math.Min(gameTicks, Stopwatch.GetTimestamp() - started);
            started = Stopwatch.GetTimestamp();
            int modArea = ModArea(world, mod, start, false);
            modTicks = Math.Min(modTicks, Stopwatch.GetTimestamp() - started);
            same &= gameArea == modArea && GameArea(world, game, Node(3, 7)) == ModArea(world, mod, Node(3, 7), false);
        }
        same &= SameState(game, mod, false) == null;
        string stats = Reachability.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(same, $"reachability: flooding the area of {Reachability.AreasForTests().Count} nodes on a map of {258 * 258 * 24} nodes " +
                    $"takes {gameTicks * 1000.0 / Stopwatch.Frequency:0.00} ms with the game's code and " +
                    $"{modTicks * 1000.0 / Stopwatch.Frequency:0.00} ms through the mod (fastest of 20, on this runtime, not the game's), " +
                    "with the same areas");
    }

    // AreaReachable asks GetAreaOfNode; Harmony runs the mod's prefix first and the game's method only if it says so.
    private static int ModArea(World world, object service, int node, bool gameOnly)
    {
        int result = 0;
        if (gameOnly || Reachability.AreaPrefix(service, node, ref result))
        {
            return GameArea(world, service, node);
        }
        return result;
    }

    private static int GameArea(World world, object service, int node)
    {
        return (int)world.GetArea.Invoke(service, new object[] { node });
    }

    // The game's Dictionary and counter against the mod's: with `shadow`, the mod's service's own Dictionary (verify mode
    // keeps it, and a hand-over fills it) must equal the game's too.
    private static string SameState(object game, object mod, bool shadow)
    {
        Dictionary<int, int> games = (Dictionary<int, int>)game.GetType().GetField("_nodesWithAssignedArea", Any).GetValue(game);
        int gameCounter = (int)game.GetType().GetField("_areaCounter", Any).GetValue(game);
        int modCounter = (int)mod.GetType().GetField("_areaCounter", Any).GetValue(mod);
        Dictionary<int, int> mods = shadow
            ? (Dictionary<int, int>)mod.GetType().GetField("_nodesWithAssignedArea", Any).GetValue(mod)
            : Reachability.AreasForTests();
        if (gameCounter != modCounter)
        {
            return $"area counter {modCounter}, the game's {gameCounter}";
        }
        if (games.Count != mods.Count || games.Any(pair => !mods.TryGetValue(pair.Key, out int area) || area != pair.Value))
        {
            return $"{mods.Count} nodes with an area, the game's {games.Count}, or different areas";
        }
        return null;
    }

    private static object NewService(World world)
    {
        object service = RouteMapsTests.Create("GlobalReachabilityService", world.NodeIds, world.Graph);
        RouteMapsTests.Call(service, "Load");
        return service;
    }

    private static int Id(int x, int y, int z)
    {
        return (x * Height + y) * Levels + z;
    }

    private static World BuildWorld(Random random)
    {
        World world = new World { NodeIds = RouteMapsTests.CreateNodeIdService(Width * Height * Levels) };
        object groups = RouteMapsTests.Create("NavMeshGroupService");
        world.Graph = RouteMapsTests.Create("InstantTerrainNavMeshGraph", world.NodeIds, groups);
        RouteMapsTests.Call(world.Graph, "Load");
        Type graphType = world.Graph.GetType();
        world.Connect = graphType.GetMethod("ConnectNodes", Any);
        world.Disconnect = graphType.GetMethod("DisconnectNodes", Any);
        world.RemoveOneWay = graphType.BaseType.GetMethod("RemoveOneWayConnection", Any);
        Type service = graphType.Assembly.GetType("Timberborn.Navigation.GlobalReachabilityService", true);
        world.GetArea = service.GetMethod("GetAreaOfNode", Any);
        world.OnUpdate = service.GetMethod("OnInstantNavMeshUpdated", Any);
        world.Update = Activator.CreateInstance(world.OnUpdate.GetParameters()[0].ParameterType);
        HashSet<int> walkable = new HashSet<int>();
        List<(int, int)> edges = new List<(int, int)>();
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Levels; z++)
                {
                    int id = Id(x, y, z);
                    if (x + 1 < Width && random.NextDouble() < 0.6) edges.Add((id, Id(x + 1, y, z)));
                    if (y + 1 < Height && random.NextDouble() < 0.6) edges.Add((id, Id(x, y + 1, z)));
                    if (z + 1 < Levels && random.NextDouble() < 0.05) edges.Add((id, Id(x, y, z + 1)));
                }
            }
        }
        foreach ((int a, int b) in edges)
        {
            world.Connect.Invoke(world.Graph, new object[] { a, b, 0, 1f });
            walkable.Add(a);
            walkable.Add(b);
        }
        // A few one-way connections: b no longer leads back to a.
        for (int i = 0; i < 40; i++)
        {
            (int a, int b) = edges[random.Next(edges.Count)];
            world.RemoveOneWay.Invoke(world.Graph, new object[] { b, a });
        }
        world.Walkable.AddRange(walkable.OrderBy(id => id));
        return world;
    }

    // A few connections made or broken, as a building, a path or a levee does.
    private static void ChangeTerrain(World world, Random random)
    {
        for (int i = 0; i < 6; i++)
        {
            int x = random.Next(Width - 1), y = random.Next(Height - 1), z = random.Next(Levels);
            int a = Id(x, y, z), b = random.Next(2) == 0 ? Id(x + 1, y, z) : Id(x, y + 1, z);
            if (random.Next(2) == 0)
            {
                world.Connect.Invoke(world.Graph, new object[] { a, b, 0, 1f });
            }
            else
            {
                world.Disconnect.Invoke(world.Graph, new object[] { a, b });
            }
        }
    }
}
