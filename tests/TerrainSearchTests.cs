using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using LateGamePerformance;
using Timberborn.Common;
using Vector3Int = UnityEngine.Vector3Int;

// The resumed terrain search against the game's real TerrainAStarPathfinder, PathFlowField, BinaryHeap and
// HeuristicsCalculator on a random terrain graph with the game's default costs (1 straight, 1.414 diagonal, a few
// dearer "stairs" and other groups):
//   1. every search that starts from scratch leaves the field identical to the game's, node for node (parents,
//      distances, marks): the same code path the game runs, on its own heap, so tie-breaks match;
//   2. sequences of searches from one tile (a beaver pricing buildings) give the game's distance, within
//      floating-point rounding, never explore more tiles than the game's restarts, and do resume;
//   3. verify mode, with list-of-destination searches and terrain changes mixed in, reports no distance or
//      reachability difference.
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
        check(modExplored > 0 && modExplored < gameExplored, $"terrain search: explored {modExplored} tiles where the game's restarts explore {gameExplored}");
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
        TerrainSearch.CreateFeature(new Config());
    }

    private static long Number(string line, string pattern)
    {
        Match match = Regex.Match(line ?? "", pattern);
        return match.Success ? long.Parse(match.Groups[1].Value) : -1;
    }

    private static void SetCoordinates(object nodeIdService)
    {
        Vector3Int[] table = new Vector3Int[Width * Height];
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                table[x * Height + y] = new Vector3Int(x, y, 0);
            }
        }
        nodeIdService.GetType().GetField("_idToCoordinatesTable", Any).SetValue(nodeIdService, table);
    }

    private static List<int> BuildTerrain(object graph, Random random)
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
