using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace LateGamePerformance
{
    // "Can a beaver standing here reach that at all?" is answered by GlobalReachabilityService: every terrain node gets
    // the number of its area, the set of nodes reachable from it, found by a breadth-first flood over the instant
    // terrain graph the first time a node of that area is asked about. Every instant nav-mesh update (a building
    // placed, a path, a levee) forgets every area, and the next question floods the whole area again: the game first
    // clears a flag for every node of the map (a bool per node, well over a million on a large map), then visits every
    // node of the area and stores its number in a Dictionary, one insert per node. After a road change the district
    // citizen assigner asks for every beaver, so the whole walkable colony is flooded: 74 us per tick on average and
    // single ticks of 5 to 8 ms in the 0.4.23 session.
    //
    // The mod replaces GetAreaOfNode, the one method that reads the areas and starts a flood, with the same flood into
    // arrays: one int per node that holds the area number above a base, and one byte per node that holds the number of
    // the last flood that visited it. Forgetting every area is moving the base past every number handed out, and a new
    // flood is a new number, so nothing is cleared per update or per flood (the bytes once every 255 floods).
    //
    // Why every answer is the game's:
    //   - The flood is the game's: from the same start, through the game's own InstantTerrainNavMeshGraph.GetNeighbors,
    //     called once per node in the same order, first in first out, a neighbour taken when not yet visited in this
    //     flood. It visits exactly the nodes the game's visits, in the game's order, and gives every one of them the
    //     same area number, overwriting an older number where the game's Dictionary would (the game's graph is
    //     symmetric, so that never happens there, but it is kept).
    //   - The numbers are the game's: the next number is the game's own _areaCounter, which the mod keeps in step, and
    //     a node has an area exactly when the game's Dictionary would hold it. ClearAreas, run by the game on every
    //     instant nav-mesh update, still runs and puts the counter back to 0; the mod sees the counter move away from
    //     the value it left there and forgets its areas before it answers. Only ClearAreas and GetAreaOfNode write the
    //     counter, and every flood goes through GetAreaOfNode, so the counter cannot move and come back unseen.
    //   - The Dictionary, the visited flags and the queue are private to the service and only ever read by these
    //     methods; answers depend on the areas alone, and those are the game's, so what the game is told
    //     (AreaReachable) is the same, question for question, on every computer.
    //   - A flood's areas are written only once it has finished. If it throws, the game's Dictionary and counter are
    //     filled with the mod's areas from before it, so the game's own GetAreaOfNode, which runs from then on (this
    //     question included), starts from the state it would have had.
    //   - A patch by another mod on the flood's own methods (CreateAreaReachableFromNode, VisitNeighbors, VisitNode),
    //     or a transpiler on GetAreaOfNode, would not run in the mod's flood: then the game's own code answers, decided
    //     at the first question, before the mod holds any area.
    //
    // ReachabilityVerify lets the game's own flood run into the game's own Dictionary as well (which the game does not
    // read while the mod answers) and compares every node's area, before each answer and after each flood. The mod's
    // answer is the one the game gets.
    //
    // Simulation feature: always on, the same on every computer.
    internal static class Reachability
    {
        private const string ServiceType = "Timberborn.Navigation.GlobalReachabilityService";

        // The game's neighbour list of a node, copied into a buffer: (graph, node, buffer) -> the node's neighbour count
        // (only as many as fit are copied). Compiled over the game's internal node type. Swappable for the tests.
        internal static Func<object, int, int[], int> CopyNeighbors;
        // The first patch by another mod the mod's flood would bypass, or null; swapped by the tests, where Harmony's
        // patch registry cannot run.
        internal static Func<string> ForeignPatch = FindForeignPatch;

        private static Func<object, int> _counterOf;
        private static Action<object, int> _setCounter;
        private static Func<object, Dictionary<int, int>> _areasOf;
        private static Func<object, bool[]> _visitedOf;
        private static Func<object, object> _graphOf;
        private static Action<object, int, int> _gamesFlood;
        private static MethodBase[] _bypassed = new MethodBase[0];
        private static MethodBase _getAreaOfNode;

        // The service the arrays belong to: one per game scene, and a new one gets new arrays at its first question.
        // Held weakly, so that a scene that is gone is not kept alive by it. Nothing is cleared when a scene is created:
        // the areas cannot be read back from the game, so they are only ever replaced together with their service.
        private static WeakReference<object> _service = new WeakReference<object>(null);
        // Per node: _base + area for a node with an area, below _base for one without.
        private static int[] _nodes = new int[0];
        private static int _base = 1;
        // Per node: the number of the last flood that visited it (1 to 255; all cleared when the numbers wrap).
        private static byte[] _visits = new byte[0];
        private static byte _flood;
        // The game's _areaCounter as the mod last left it.
        private static int _counter;
        // Nodes with an area; for verify mode's comparison with the game's Dictionary.
        private static int _withArea;
        // The flood's queue, which ends up holding every node it reached, in the game's visiting order.
        private static int[] _queue = new int[4096];
        private static int[] _neighbors = new int[64];
        // Whether the game's Dictionary holds exactly the mod's areas (verify mode keeps it so).
        private static bool _shadow;

        private static bool _active;
        private static bool _verify;
        private static bool _checkedPatches;
        private static bool _standDown;

        private static long _questions;
        private static long _floods;
        private static long _flooded;
        private static long _clears;
        private static long _leftToGame;
        private static long _stopwatchTicks;
        private static long _longestTicks;
        private static long _verifyMismatches;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        public static Feature CreateFeature(Config config)
        {
            _verify = config.ReachabilityVerify;
            Feature feature = new Feature { Name = "Reachability" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "GlobalReachabilityService.GetAreaOfNode",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return _getAreaOfNode;
                },
                Prefix = Reflect.Own(typeof(Reachability), nameof(AreaPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        internal static void ResetForTests()
        {
            _service = new WeakReference<object>(null);
            _nodes = new int[0];
            _visits = new byte[0];
            _shadow = false;
            _checkedPatches = _standDown = false;
            _questions = _floods = _flooded = _clears = _leftToGame = _stopwatchTicks = _longestTicks = _verifyMismatches = 0;
        }

        // Resolves everything this feature touches; throws if the game no longer matches.
        internal static void Bind()
        {
            Type service = Reflect.GameType(ServiceType);
            if (service == null)
            {
                throw new TypeLoadException(ServiceType + " not found");
            }
            _getAreaOfNode = AccessTools.Method(service, "GetAreaOfNode", new[] { typeof(int) });
            if (_getAreaOfNode == null || ((MethodInfo)_getAreaOfNode).ReturnType != typeof(int))
            {
                throw new MissingMethodException(service.Name, "GetAreaOfNode");
            }
            _counterOf = Reflect.FieldGetter<int>(service, "_areaCounter");
            _setCounter = Reflect.FieldSetter<int>(service, "_areaCounter");
            _areasOf = Reflect.FieldGetter<Dictionary<int, int>>(service, "_nodesWithAssignedArea");
            _visitedOf = Reflect.FieldGetter<bool[]>(service, "_visitedNodes");
            _graphOf = Reflect.FieldGetter<object>(service, "_instantTerrainNavMeshGraph");
            MethodInfo flood = AccessTools.Method(service, "CreateAreaReachableFromNode", new[] { typeof(int), typeof(int) });
            _gamesFlood = Reflect.InstanceCall<Action<object, int, int>>(flood);
            _bypassed = new MethodBase[]
            {
                flood, AccessTools.Method(service, "VisitNeighbors", new[] { typeof(int), typeof(int) }),
                AccessTools.Method(service, "VisitNode", new[] { typeof(int), typeof(int) })
            };
            foreach (MethodBase method in _bypassed)
            {
                if (method == null)
                {
                    throw new MissingMethodException(service.Name, "CreateAreaReachableFromNode / VisitNeighbors / VisitNode");
                }
            }
            CopyNeighbors = CompileCopyNeighbors(AccessTools.Field(service, "_instantTerrainNavMeshGraph").FieldType);
        }

        // (graph, node, buffer) => { var neighbors = graph.GetNeighbors(node); int count = neighbors.Count;
        //   for (int i = 0; i < Math.Min(count, buffer.Length); i++) buffer[i] = neighbors[i].Id; return count; }
        private static Func<object, int, int[], int> CompileCopyNeighbors(Type graphType)
        {
            MethodInfo getNeighbors = AccessTools.Method(graphType, "GetNeighbors", new[] { typeof(int) });
            if (getNeighbors == null)
            {
                throw new MissingMethodException(graphType.Name, "GetNeighbors");
            }
            Type listType = getNeighbors.ReturnType;
            PropertyInfo count = listType.GetProperty("Count");
            PropertyInfo item = listType.GetProperty("Item");
            PropertyInfo id = item?.PropertyType.GetProperty("Id");
            if (count == null || item == null || id == null || id.PropertyType != typeof(int))
            {
                throw new MissingMemberException(listType.Name, "Count / Item / Id");
            }
            ParameterExpression graph = Expression.Parameter(typeof(object), "graph");
            ParameterExpression node = Expression.Parameter(typeof(int), "node");
            ParameterExpression buffer = Expression.Parameter(typeof(int[]), "buffer");
            ParameterExpression neighbors = Expression.Variable(listType, "neighbors");
            ParameterExpression total = Expression.Variable(typeof(int), "total");
            ParameterExpression copied = Expression.Variable(typeof(int), "copied");
            ParameterExpression i = Expression.Variable(typeof(int), "i");
            LabelTarget done = Expression.Label("done");
            Expression body = Expression.Block(new[] { neighbors, total, copied, i },
                Expression.Assign(neighbors, Expression.Call(Expression.Convert(graph, graphType), getNeighbors, node)),
                Expression.Assign(total, Expression.Property(neighbors, count)),
                Expression.Assign(copied, Expression.Condition(Expression.LessThan(total, Expression.ArrayLength(buffer)), total,
                    Expression.ArrayLength(buffer))),
                Expression.Assign(i, Expression.Constant(0)),
                Expression.Loop(
                    Expression.IfThenElse(Expression.LessThan(i, copied),
                        Expression.Block(
                            Expression.Assign(Expression.ArrayAccess(buffer, i),
                                Expression.Property(Expression.Property(neighbors, item, i), id)),
                            Expression.PreIncrementAssign(i)),
                        Expression.Break(done)),
                    done),
                total);
            return Expression.Lambda<Func<object, int, int[], int>>(body, graph, node, buffer).Compile();
        }

        public static string TakeStatsLine()
        {
            if (!_active && _questions == 0 && _leftToGame == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "Reachability: {0} area questions, {1} of which flooded an area ({2} nodes, {3:0.0} ms in total, longest " +
                "{4:0.00} ms); the areas were forgotten {5} times; {6} questions left to the game{7}",
                _questions, _floods, _flooded, ms, _longestTicks * 1000.0 / Stopwatch.Frequency, _clears, _leftToGame,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _questions = _floods = _flooded = _clears = _leftToGame = _stopwatchTicks = _longestTicks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool AreaPrefix(object __instance, int nodeId, ref int __result)
        {
            if (!_active)
            {
                return true;
            }
            if (!_checkedPatches)
            {
                // Once, at the first question, before the mod holds any area, when every mod has patched what it patches.
                _checkedPatches = true;
                string patched = ForeignPatch();
                if (patched != null)
                {
                    _standDown = true;
                    Log.Info($"Reachability: another mod patches {patched}, which the mod's flood would not run. The game's " +
                             "own code answers every question. The answers are the same.");
                }
            }
            if (_standDown)
            {
                _leftToGame++;
                return true;
            }
            try
            {
                if (!IsAttached(__instance) && !Attach(__instance))
                {
                    // Not loaded yet: the game's own method (which would throw on its missing array) runs.
                    _leftToGame++;
                    return true;
                }
                if ((uint)nodeId >= (uint)_nodes.Length)
                {
                    // Not a node: the game finds no area and its flood throws on the first flag, changing nothing.
                    _leftToGame++;
                    return true;
                }
                int counter = _counterOf(__instance);
                if (counter != _counter)
                {
                    Forget(counter);
                }
                if (_verify)
                {
                    MakeShadow(__instance);
                    CompareNode(__instance, nodeId);
                }
                _questions++;
                int value = _nodes[nodeId];
                if (value >= _base)
                {
                    __result = value - _base;
                    return false;
                }

                // CreateAreaReachableFromNode(nodeId, _areaCounter), then _areaCounter++.
                long started = Stopwatch.GetTimestamp();
                int area = _counter;
                if (_base > int.MaxValue - 1 - area)
                {
                    Rebase();
                }
                int flooded = Flood(_graphOf(__instance), nodeId);
                int areaValue = _base + area;
                for (int i = 0; i < flooded; i++)
                {
                    int node = _queue[i];
                    if (_nodes[node] < _base)
                    {
                        _withArea++;
                    }
                    _nodes[node] = areaValue;
                }
                _counter = area + 1;
                _setCounter(__instance, _counter);
                long elapsed = Stopwatch.GetTimestamp() - started;
                _floods++;
                _flooded += flooded;
                _stopwatchTicks += elapsed;
                _longestTicks = Math.Max(_longestTicks, elapsed);
                if (_verify)
                {
                    CompareFlood(__instance, nodeId, area, flooded);
                }
                else
                {
                    // The game's Dictionary did not get this area.
                    _shadow = false;
                }
                __result = area;
                return false;
            }
            catch (Exception exception)
            {
                // Nothing of a flood that did not finish was kept: the areas held before it are the state the game's
                // own method now starts from.
                string handedOver;
                try
                {
                    handedOver = HandOver(__instance);
                }
                catch (Exception again)
                {
                    handedOver = "; handing the areas to the game failed too: " + again.Message;
                }
                _active = false;
                TurnedOff.Report("Reachability",
                    "Reachability failed and turned itself off for this session; the game's own code answers from here" +
                    handedOver + ": " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming

        private static bool IsAttached(object service)
        {
            return _service.TryGetTarget(out object attached) && ReferenceEquals(attached, service);
        }

        // The first question to a service: arrays for its nodes, starting from whatever the game holds (nothing, unless
        // the game answered before the mod did).
        private static bool Attach(object service)
        {
            bool[] visited = _visitedOf(service);
            if (visited == null)
            {
                return false;
            }
            int[] nodes = new int[visited.Length];
            byte[] visits = new byte[visited.Length];
            Dictionary<int, int> areas = _areasOf(service);
            int counter = _counterOf(service);
            int withArea = 0;
            foreach (KeyValuePair<int, int> pair in areas)
            {
                nodes[pair.Key] = 1 + pair.Value;
                withArea++;
            }
            _nodes = nodes;
            _visits = visits;
            _flood = 0;
            _base = 1;
            _counter = counter;
            _withArea = withArea;
            _shadow = true;
            _service.SetTarget(service);
            return true;
        }

        // The game's ClearAreas ran: every area is gone. The base moves past every number handed out since.
        private static void Forget(int counter)
        {
            _base += _counter;
            _counter = counter;
            _withArea = 0;
            _clears++;
            // ClearAreas emptied the game's Dictionary as well.
            _shadow = true;
        }

        // Numbers are about to run out (after some two billion areas): every area held is renumbered from 1 up, in place.
        private static void Rebase()
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                int value = _nodes[i];
                _nodes[i] = value >= _base ? 1 + (value - _base) : 0;
            }
            _base = 1;
        }

        // Breadth first from start, as CreateAreaReachableFromNode: queues every node reached, in the game's visiting
        // order, and returns how many. Assigns nothing.
        private static int Flood(object graph, int start)
        {
            if (++_flood == 0)
            {
                Array.Clear(_visits, 0, _visits.Length);
                _flood = 1;
            }
            byte flood = _flood;
            byte[] visits = _visits;
            int[] queue = _queue;
            int[] neighbors = _neighbors;
            visits[start] = flood;
            queue[0] = start;
            int tail = 1;
            for (int head = 0; head < tail; head++)
            {
                int count = CopyNeighbors(graph, queue[head], neighbors);
                if (count > neighbors.Length)
                {
                    _neighbors = neighbors = new int[count * 2];
                    count = CopyNeighbors(graph, queue[head], neighbors);
                }
                for (int i = 0; i < count; i++)
                {
                    int id = neighbors[i];
                    if (visits[id] != flood)
                    {
                        if (tail == queue.Length)
                        {
                            Array.Resize(ref _queue, queue.Length * 2);
                            queue = _queue;
                        }
                        visits[id] = flood;
                        queue[tail++] = id;
                    }
                }
            }
            return tail;
        }

        // Fills the game's Dictionary and counter with the mod's areas, for the game's own code to go on from.
        private static string HandOver(object service)
        {
            if (!IsAttached(service))
            {
                return "";
            }
            Dictionary<int, int> areas = _areasOf(service);
            areas.Clear();
            for (int i = 0; i < _nodes.Length; i++)
            {
                int value = _nodes[i];
                if (value >= _base)
                {
                    areas[i] = value - _base;
                }
            }
            _setCounter(service, _counter);
            return $"; the areas of {areas.Count} nodes were handed to the game";
        }

        // Verify mode keeps the game's own Dictionary as the game would have it; when it is switched on it starts from
        // the mod's areas.
        private static void MakeShadow(object service)
        {
            if (_shadow)
            {
                return;
            }
            Dictionary<int, int> areas = _areasOf(service);
            areas.Clear();
            for (int i = 0; i < _nodes.Length; i++)
            {
                int value = _nodes[i];
                if (value >= _base)
                {
                    areas[i] = value - _base;
                }
            }
            _shadow = true;
        }

        private static void CompareNode(object service, int nodeId)
        {
            try
            {
                bool games = _areasOf(service).TryGetValue(nodeId, out int gamesArea);
                int value = _nodes[nodeId];
                bool mine = value >= _base;
                if (games != mine || (mine && gamesArea != value - _base))
                {
                    Mismatch($"node {nodeId} has {(mine ? "area " + (value - _base) : "no area")}, in the game's " +
                             $"{(games ? "area " + gamesArea : "no area")}");
                }
            }
            catch (Exception exception)
            {
                VerifyFailed(exception);
            }
        }

        // The game's own flood into the game's own Dictionary, then every node's area compared.
        private static void CompareFlood(object service, int start, int area, int flooded)
        {
            if (!_shadow)
            {
                // A difference was just counted; the next question starts the comparison over.
                return;
            }
            try
            {
                _gamesFlood(service, start, area);
                Dictionary<int, int> areas = _areasOf(service);
                if (areas.Count != _withArea)
                {
                    Mismatch($"after flooding area {area} from node {start} ({flooded} nodes) the game's areas cover " +
                             $"{areas.Count} nodes, the mod's {_withArea}");
                    return;
                }
                for (int i = 0; i < flooded; i++)
                {
                    int node = _queue[i];
                    if (!areas.TryGetValue(node, out int gamesArea) || gamesArea != area)
                    {
                        Mismatch($"after flooding area {area} from node {start} node {node} is in the game's " +
                                 $"{(areas.ContainsKey(node) ? "area " + gamesArea : "no area")}");
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                VerifyFailed(exception);
            }
        }

        private static void Mismatch(string difference)
        {
            // The game's Dictionary is set from the mod's areas again before the next comparison, so that one
            // difference is counted once.
            _shadow = false;
            _verifyMismatches++;
            if (_verifyMismatches <= 10)
            {
                Log.Warning("Reachability verify: " + difference + ".");
            }
        }

        private static void VerifyFailed(Exception exception)
        {
            _verify = false;
            _shadow = false;
            Log.Warning("Reachability verify failed and is off (the answers are not affected): " + exception);
        }

        // For the tests: the areas the mod holds, as the game's Dictionary would hold them, and a way to break one.
        internal static Dictionary<int, int> AreasForTests()
        {
            Dictionary<int, int> areas = new Dictionary<int, int>();
            for (int i = 0; i < _nodes.Length; i++)
            {
                int value = _nodes[i];
                if (value >= _base)
                {
                    areas[i] = value - _base;
                }
            }
            return areas;
        }

        internal static void SetAreaForTests(int nodeId, int area)
        {
            _nodes[nodeId] = _base + area;
        }

        private static string FindForeignPatch()
        {
            foreach (MethodBase method in _bypassed)
            {
                string owner = ForeignOwner(method, false);
                if (owner != null)
                {
                    return $"GlobalReachabilityService.{method.Name} ({owner})";
                }
            }
            string transpiler = ForeignOwner(_getAreaOfNode, true);
            return transpiler == null ? null : $"GlobalReachabilityService.GetAreaOfNode with a transpiler ({transpiler})";
        }

        private static string ForeignOwner(MethodBase method, bool transpilersOnly)
        {
            Patches info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return null;
            }
            List<Patch> patches = new List<Patch>(info.Transpilers);
            if (!transpilersOnly)
            {
                patches.AddRange(info.Prefixes);
                patches.AddRange(info.Postfixes);
                patches.AddRange(info.Finalizers);
            }
            foreach (Patch patch in patches)
            {
                if (!patch.owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                {
                    return patch.owner;
                }
            }
            return null;
        }
    }
}
