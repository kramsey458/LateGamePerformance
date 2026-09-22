using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Timberborn.Common;

namespace LateGamePerformance
{
    // The game's terrain path search, resumed instead of restarted.
    //
    // When no route map answers a path question (a beaver standing off the road network pricing a building that
    // could satisfy a need, or walking to a random spot) the game runs an A* search over the terrain graph, on
    // the main thread, inside that beaver's tick: TerrainAStarPathfinder.FillFlowFieldWithPath. It keeps the
    // last search in one PathFlowField, and answers the next question from it only if that search happened to
    // reach the new target. Otherwise it clears the field and starts again from the same tile. Pricing thirty
    // buildings from one spot can mean thirty searches over the same ground.
    //
    // This runs the game's own algorithm, on the game's own heap and flow field, so a search that has to start
    // from scratch is the game's search to the bit. When the next question starts from the same tile and nothing
    // else has touched the field or the heap since, it keeps the explored tiles and the frontier, re-prices the
    // frontier for the new target, and carries on. Explored tiles are never explored again, so a resumed search
    // never does more than the game's restart would; what it saves is the ground already covered.
    //
    // What is and is not the same as the game: a fresh search is identical. A resumed search gives the same
    // distance, because the game's heuristic never overestimates a terrain step (0.9 per straight tile, 1.273
    // per diagonal, against costs of 1 and 1.414), except for the last bits of floating-point rounding when the
    // shortest distance is reached by a different but equally short route; and in that case the route itself can
    // be a different one of equal length. Ziplines and tubes carry their own costs, which the heuristic does not
    // know; wherever a search pushes across a step cheaper than the heuristic allows, the guarantee is gone, so
    // such a search is never resumed from and a resumed search that meets one starts over the game's way. Every
    // player on this version gets the same answer, because the search state depends only on the simulation's
    // own sequence of questions (the only other caller is the game's debug-mode cursor tool); when the host
    // writes the save a joining player loads, both forget their history so the next question is fresh on both.
    // TerrainSearchVerify runs the unmodified algorithm alongside on a shadow field and counts every difference;
    // it never changes what the game is told, so it may differ between players, and a failure in it only switches
    // the comparison off.
    internal static class TerrainSearch
    {
        private const string PathfinderType = "Timberborn.Navigation.TerrainAStarPathfinder";
        private const string GraphType = "Timberborn.Navigation.TerrainNavMeshGraph";
        private const string FieldType = "Timberborn.Navigation.PathFlowField";
        private const string HeapType = "Timberborn.Navigation.BinaryHeap`1";
        private const string HeapFactoryType = "Timberborn.Navigation.BinaryHeapFactory";
        private const string AStarNodeType = "Timberborn.Navigation.AStarNode";
        private const string NavMeshNodeType = "Timberborn.Navigation.NavMeshNode";
        private const string HeuristicsType = "Timberborn.Navigation.HeuristicsCalculator";
        private const string GameSaverType = "Timberborn.GameSaveRuntimeSystem.GameSaver";

        private static Engine _engine;
        private static bool _active;
        private static volatile bool _verify;

        // The shadow comparison, from the settings page box "Verify terrain path searches" or the .cfg key. It never
        // changes the answer, so it can be switched at any time.
        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }
        private static readonly Engine.State State = new Engine.State();
        private static long _answered;
        private static long _fresh;
        private static long _resumed;
        private static long _restarted;
        private static long _freshNodes;
        private static long _resumedNodes;
        private static long _stopwatchTicks;
        private static long _verifyIdentical;
        private static long _verifyRounding;
        private static long _verifyRoute;
        private static long _verifyDistance;
        private static long _verifyFound;
        private static long _verifyShadowNodes;
        private static long _verifyRealNodes;
        private static int _verifyLogged;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.TerrainSearchVerify;
            Type self = typeof(TerrainSearch);
            Feature feature = new Feature { Name = "TerrainSearch" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TerrainAStarPathfinder.FillFlowFieldWithPath (one destination)",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Overload(PathfinderType, "FillFlowFieldWithPath", 4);
                },
                Prefix = Reflect.Own(self, nameof(FillPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // The list-of-destinations search always starts over; afterwards there is nothing to resume.
                Name = "TerrainAStarPathfinder.FillFlowFieldWithPath (several destinations)",
                Required = true,
                Target = () => Reflect.Overload(PathfinderType, "FillFlowFieldWithPath", 5),
                Postfix = Reflect.Own(self, nameof(MultiPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // A terrain change: the game clears the field if a changed tile is in it. Costs of frontier tiles
                // may have changed either way, so nothing is resumed across one.
                Name = "PathFlowField.OnNodesChanged",
                Required = true,
                Target = () => Reflect.Method(FieldType, "OnNodesChanged"),
                Postfix = Reflect.Own(self, nameof(NodesChangedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // The save a multiplayer host writes for a joining player. The joiner starts with an empty field, so
                // the host forgets its own history too: the next question is a fresh search on both.
                Name = "GameSaver.SaveWithoutFinishingTick",
                Required = false,
                Target = () => Reflect.Method(GameSaverType, "SaveWithoutFinishingTick"),
                Postfix = Reflect.Own(self, nameof(JoinSavePostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            State.Valid = false;
            _active = true;
        }

        // A new game or map: the previous scene's field and heap are gone, and node ids mean other tiles.
        public static void SceneCreated()
        {
            State.Forget();
        }

        internal static bool IsActive => _active;

        internal static void Bind()
        {
            if (_engine != null)
            {
                return;
            }
            Type engine = typeof(Engine<,>).MakeGenericType(Reflect.GameType(NavMeshNodeType), Reflect.GameType(AStarNodeType));
            _engine = (Engine)Activator.CreateInstance(engine);
        }

        public static string TakeStatsLine()
        {
            if (!_active || _answered + _fresh + _resumed == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"TerrainSearch: {_answered + _fresh + _resumed + _restarted} terrain path searches in {ms:0.0} ms: " +
                          $"{_answered} answered from the previous search, {_fresh} started from scratch exploring {_freshNodes} tiles, " +
                          $"{_resumed} resumed exploring {_resumedNodes} more tiles, {_restarted} started over after meeting a step " +
                          "cheaper than the heuristic allows";
            if (_verify)
            {
                line += $"; verify: {_verifyIdentical} identical, {_verifyRounding} same distance within rounding, " +
                        $"{_verifyRoute} equally short but different route, {_verifyDistance} DIFFERENT DISTANCE, " +
                        $"{_verifyFound} DIFFERENT REACHABILITY; the game's search explored {_verifyShadowNodes} tiles, " +
                        $"this mod's {_verifyRealNodes}";
            }
            _answered = _fresh = _resumed = _restarted = _freshNodes = _resumedNodes = _stopwatchTicks = 0;
            _verifyIdentical = _verifyRounding = _verifyRoute = _verifyDistance = _verifyFound = 0;
            _verifyShadowNodes = _verifyRealNodes = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool FillPrefix(object __instance, object terrainNavMeshGraph, object flowField, int startNodeId,
            int destinationNodeId)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                long stamp = Stopwatch.GetTimestamp();
                Engine.Outcome outcome = _engine.Fill(__instance, terrainNavMeshGraph, flowField, startNodeId, destinationNodeId,
                    State, out int explored);
                _stopwatchTicks += Stopwatch.GetTimestamp() - stamp;
                switch (outcome)
                {
                    case Engine.Outcome.Answered:
                        _answered++;
                        break;
                    case Engine.Outcome.Fresh:
                        _fresh++;
                        _freshNodes += explored;
                        break;
                    case Engine.Outcome.Restarted:
                        _restarted++;
                        _freshNodes += explored;
                        break;
                    default:
                        _resumed++;
                        _resumedNodes += explored;
                        break;
                }
                if (_verify)
                {
                    // A comparison that fails switches only the comparison off: the answer above stands, and a
                    // measurement that one player has on must never change what that player's game does.
                    try
                    {
                        Verify(__instance, terrainNavMeshGraph, flowField, startNodeId, destinationNodeId, explored);
                    }
                    catch (Exception exception)
                    {
                        _verify = false;
                        Log.Warning("TerrainSearch verify failed and is off for this session; the search itself is unaffected: " + exception);
                    }
                }
                return false;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
        }

        internal static void MultiPostfix(object flowField)
        {
            Touched(flowField);
        }

        internal static void NodesChangedPostfix(object __instance)
        {
            Touched(__instance);
        }

        internal static void JoinSavePostfix()
        {
            if (_active)
            {
                _engine.ForgetHistory(State);
            }
        }
        // ReSharper restore InconsistentNaming

        private static void Touched(object field)
        {
            if (!_active || !ReferenceEquals(State.Field, field))
            {
                return;
            }
            State.Valid = false;
            // The game's own field would have gone through the same change from its own contents; the two may now
            // differ in what they hold, so the comparison (if it is on, or gets switched on later) starts again
            // from this mod's state.
            State.ShadowValid = false;
        }

        private static void Verify(object pathfinder, object graph, object field, int start, int dest, int explored)
        {
            Engine.Comparison comparison = _engine.Compare(pathfinder, graph, field, start, dest, State, out int shadowExplored);
            _verifyRealNodes += explored;
            _verifyShadowNodes += shadowExplored;
            switch (comparison)
            {
                case Engine.Comparison.Identical:
                    _verifyIdentical++;
                    break;
                case Engine.Comparison.Rounding:
                    _verifyRounding++;
                    break;
                case Engine.Comparison.Route:
                    _verifyRoute++;
                    break;
                case Engine.Comparison.Distance:
                    _verifyDistance++;
                    LogMismatch($"distance differs from the game's for a search from tile {start} to {dest}: " +
                                $"{_engine.LastReal} against {_engine.LastShadow}");
                    break;
                default:
                    _verifyFound++;
                    LogMismatch($"reachability differs from the game's for a search from tile {start} to {dest}");
                    break;
            }
        }

        private static void LogMismatch(string what)
        {
            if (_verifyLogged++ < 10)
            {
                Log.Warning("TerrainSearch verify: " + what);
            }
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            State.Valid = false;
            TurnedOff.Report("TerrainSearch", "TerrainSearch failed and turned itself off for this session; the game's " +
                                              "own search runs: " + exception);
        }

        // The algorithm, generic over the game's two internal structs so that nothing is boxed per tile.
        internal abstract class Engine
        {
            public enum Outcome
            {
                Answered,
                Fresh,
                Resumed,
                // Resumed, met a step cheaper than the heuristic allows, and started over the game's way.
                Restarted
            }

            public enum Comparison
            {
                Identical,
                Rounding,
                Route,
                Distance,
                Found
            }

            public sealed class State
            {
                public bool Valid;
                public object Field;
                public object Heap;
                public int Start;
                public int NodeCount;
                // The tile the last search stopped at: it was explored, but its neighbours were never pushed.
                public int LastDestination;
                // False once a search from this start pushed across a step cheaper than the heuristic allows (a
                // zipline, a tube): explored tiles may then not hold their shortest distance, so nothing resumes.
                public bool Consistent;
                // Verify mode: the unmodified algorithm's own field and heap.
                public object Shadow;
                public object ShadowHeap;
                public bool ShadowValid;

                public void Forget()
                {
                    Valid = false;
                    Field = null;
                    Heap = null;
                    Shadow = null;
                    ShadowHeap = null;
                    ShadowValid = false;
                }
            }

            public float LastReal;
            public float LastShadow;

            public abstract Outcome Fill(object pathfinder, object graph, object field, int start, int dest, State state,
                out int explored);

            public abstract Comparison Compare(object pathfinder, object graph, object field, int start, int dest, State state,
                out int shadowExplored);

            // Drop the resume state and empty the game's field, so the next question is searched from scratch here
            // as it will be on a player who loads the save being written now.
            public abstract void ForgetHistory(State state);
        }

        internal sealed class Engine<TNode, TAStar> : Engine
        {
            private readonly Func<object, int, ReadOnlyList<TNode>> _neighbors;
            private readonly Func<object, int, bool> _onNavMesh;
            private readonly Func<TNode, int> _nodeId;
            private readonly Func<TNode, float> _nodeCost;
            private readonly Func<object, int, int, bool> _checkedPath;
            private readonly Func<object, int, int, bool> _foundPath;
            private readonly Action<object, int> _clearField;
            private readonly Func<object, int, bool> _hasNode;
            private readonly Action<object, int, int, float> _addNode;
            private readonly Action<object> _markPartial;
            private readonly Action<object> _markFull;
            private readonly Func<object, int, float> _distance;
            private readonly Func<object, int, int> _parent;
            private readonly Func<object, int> _fieldStart;
            private readonly Func<object, bool> _refreshed;
            private readonly Func<object, bool> _fullyFilled;
            private readonly Func<object, ICollection> _nodes;
            private readonly Action<object, TAStar> _push;
            private readonly Func<object, TAStar> _pop;
            private readonly Func<object, bool> _isEmpty;
            private readonly Action<object> _clearHeap;
            private readonly Func<int, int, float, float, TAStar> _newEntry;
            private readonly Func<TAStar, int> _entryId;
            private readonly Func<TAStar, int> _entryParent;
            private readonly Func<TAStar, float> _entryG;
            private readonly Action<object, int> _setDestination;
            private readonly Func<object, int, float> _h;
            private readonly Func<object, object> _heuristics;
            private readonly Func<object, object> _openSet;
            private readonly Func<object, object> _heapFactory;
            private readonly Func<object, object> _createHeap;
            private readonly Type _fieldType;
            private readonly List<TAStar> _rekey = new List<TAStar>();
            private readonly List<int> _routeA = new List<int>();
            private readonly List<int> _routeB = new List<int>();
            // Set by Run/Expand when a pushed step is cheaper than the heuristic allows (h(from) - h(to) > cost).
            private bool _uneven;

            public Engine()
            {
                Type graph = Reflect.GameType(GraphType);
                Type field = Reflect.GameType(FieldType);
                Type heap = Reflect.GameType(HeapType).MakeGenericType(typeof(TAStar));
                Type heuristics = Reflect.GameType(HeuristicsType);
                Type pathfinder = Reflect.GameType(PathfinderType);
                _fieldType = field;
                _neighbors = Reflect.InstanceCall<Func<object, int, ReadOnlyList<TNode>>>(AccessTools.Method(graph, "GetNeighbors"));
                _onNavMesh = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(graph, "IsOnNavMesh"));
                _nodeId = StructGetter<TNode, int>("Id");
                _nodeCost = StructGetter<TNode, float>("Cost");
                _checkedPath = Reflect.InstanceCall<Func<object, int, int, bool>>(AccessTools.Method(field, "CheckedPath"));
                _foundPath = Reflect.InstanceCall<Func<object, int, int, bool>>(AccessTools.Method(field, "FoundPath"));
                _clearField = Reflect.InstanceCall<Action<object, int>>(AccessTools.Method(field, "Clear", new[] { typeof(int) }));
                _hasNode = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(field, "HasNode"));
                _addNode = Reflect.InstanceCall<Action<object, int, int, float>>(AccessTools.Method(field, "AddNode"));
                _markPartial = Reflect.InstanceCall<Action<object>>(AccessTools.Method(field, "MarkAsPartiallyFilled"));
                _markFull = Reflect.InstanceCall<Action<object>>(AccessTools.Method(field, "MarkAsFullyFilled"));
                _distance = Reflect.InstanceCall<Func<object, int, float>>(AccessTools.Method(field, "GetDistance"));
                _parent = Reflect.InstanceCall<Func<object, int, int>>(AccessTools.Method(field, "GetParentId"));
                _fieldStart = Reflect.FieldGetter<int>(field, "_startNodeId");
                _refreshed = Reflect.FieldGetter<bool>(field, "_refreshed");
                _fullyFilled = Reflect.FieldGetter<bool>(field, "_fullyFilled");
                _nodes = Reflect.FieldGetter<ICollection>(field, "_nodes");
                _push = Reflect.InstanceCall<Action<object, TAStar>>(AccessTools.Method(heap, "Push"));
                _pop = Reflect.InstanceCall<Func<object, TAStar>>(AccessTools.Method(heap, "Pop"));
                _isEmpty = Reflect.InstanceCall<Func<object, bool>>(AccessTools.Method(heap, "IsEmpty"));
                _clearHeap = Reflect.InstanceCall<Action<object>>(AccessTools.Method(heap, "Clear"));
                _newEntry = Constructor();
                _entryId = StructGetter<TAStar, int>("NodeId");
                _entryParent = StructGetter<TAStar, int>("ParentNodeId");
                _entryG = StructGetter<TAStar, float>("GScore");
                _setDestination = Reflect.InstanceCall<Action<object, int>>(AccessTools.Method(heuristics, "SetDestinationNode"));
                _h = Reflect.InstanceCall<Func<object, int, float>>(AccessTools.Method(heuristics, "H"));
                _heuristics = Reflect.FieldGetter<object>(pathfinder, "_heuristicsCalculator");
                _openSet = Reflect.FieldGetter<object>(pathfinder, "_openSet");
                _heapFactory = Reflect.FieldGetter<object>(pathfinder, "_binaryHeapFactory");
                MethodInfo create = AccessTools.Method(Reflect.GameType(HeapFactoryType), "Create").MakeGenericMethod(typeof(TAStar));
                _createHeap = Reflect.InstanceCall<Func<object, object>>(create);
            }

            private static Func<TStruct, TValue> StructGetter<TStruct, TValue>(string property)
            {
                ParameterExpression value = Expression.Parameter(typeof(TStruct), "value");
                return Expression.Lambda<Func<TStruct, TValue>>(Expression.Property(value, property), value).Compile();
            }

            private static Func<int, int, float, float, TAStar> Constructor()
            {
                ConstructorInfo constructor = typeof(TAStar).GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(int), typeof(int), typeof(float), typeof(float) }, null);
                if (constructor == null)
                {
                    throw new MissingMethodException(typeof(TAStar).Name, ".ctor(int, int, float, float)");
                }
                ParameterExpression id = Expression.Parameter(typeof(int), "id");
                ParameterExpression parent = Expression.Parameter(typeof(int), "parent");
                ParameterExpression g = Expression.Parameter(typeof(float), "g");
                ParameterExpression f = Expression.Parameter(typeof(float), "f");
                return Expression.Lambda<Func<int, int, float, float, TAStar>>(
                    Expression.New(constructor, id, parent, g, f), id, parent, g, f).Compile();
            }

            public override Outcome Fill(object pathfinder, object graph, object field, int start, int dest, State state,
                out int explored)
            {
                explored = 0;
                object heuristics = _heuristics(pathfinder);
                object heap = _openSet(pathfinder);
                // The game's own first steps, in the game's order.
                _setDestination(heuristics, dest);
                if (_checkedPath(field, start, dest) || !(_onNavMesh(graph, start) && _onNavMesh(graph, dest)))
                {
                    return Outcome.Answered;
                }
                bool resume = state.Valid && state.Consistent && ReferenceEquals(state.Field, field) &&
                              ReferenceEquals(state.Heap, heap) && _fieldStart(field) == start && _refreshed(field) &&
                              !_fullyFilled(field) && _nodes(field).Count == state.NodeCount;
                int before = _nodes(field).Count;
                bool found;
                Outcome outcome;
                _uneven = false;
                if (resume)
                {
                    Reprice(heap, field, heuristics);
                    Expand(graph, field, heap, heuristics, state.LastDestination);
                    found = Run(graph, field, heap, heuristics, dest);
                    outcome = Outcome.Resumed;
                    if (_uneven)
                    {
                        // Somewhere in the continued search the distances may no longer be the shortest ones the
                        // game's own restart would find. Do it the game's way.
                        _uneven = false;
                        before = 0;
                        found = Fresh(graph, field, heap, heuristics, start, dest);
                        outcome = Outcome.Restarted;
                    }
                }
                else
                {
                    before = 0;
                    found = Fresh(graph, field, heap, heuristics, start, dest);
                    outcome = Outcome.Fresh;
                }
                explored = _nodes(field).Count - before;
                state.Field = field;
                state.Heap = heap;
                state.Start = start;
                state.NodeCount = _nodes(field).Count;
                state.LastDestination = dest;
                state.Consistent = !_uneven;
                // After a full exploration the game answers every later question itself (CheckedPath).
                state.Valid = found && !_uneven;
                return outcome;
            }

            // The game's search from scratch: clear, push the start, run.
            private bool Fresh(object graph, object field, object heap, object heuristics, int start, int dest)
            {
                _clearField(field, start);
                _clearHeap(heap);
                Push(heap, heuristics, start, -1, 0f);
                return Run(graph, field, heap, heuristics, dest);
            }

            public override void ForgetHistory(State state)
            {
                if (state.Field != null)
                {
                    // The game's initial state: no start, no tiles. Its next question cannot be answered from it.
                    _clearField(state.Field, -1);
                }
                state.Forget();
            }

            // The frontier was priced for the previous target; price it for this one. Entries for tiles explored
            // since they were pushed are dropped, as the game would skip them.
            private void Reprice(object heap, object field, object heuristics)
            {
                _rekey.Clear();
                while (!_isEmpty(heap))
                {
                    TAStar entry = _pop(heap);
                    if (!_hasNode(field, _entryId(entry)))
                    {
                        _rekey.Add(entry);
                    }
                }
                for (int i = 0; i < _rekey.Count; i++)
                {
                    TAStar entry = _rekey[i];
                    Push(heap, heuristics, _entryId(entry), _entryParent(entry), _entryG(entry));
                }
                _rekey.Clear();
            }

            // The game returns as soon as it pops its target, before that tile's neighbours are pushed. A search that
            // continues from there has to push them, or a route through the previous target is never found.
            private void Expand(object graph, object field, object heap, object heuristics, int id)
            {
                float g = _distance(field, id);
                float hHere = _h(heuristics, id);
                ReadOnlyList<TNode> neighbors = _neighbors(graph, id);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    TNode neighbor = neighbors[i];
                    int neighborId = _nodeId(neighbor);
                    if (!_hasNode(field, neighborId))
                    {
                        float cost = _nodeCost(neighbor);
                        PushChecked(heap, heuristics, neighborId, id, g + cost, hHere, cost);
                    }
                }
            }

            // TerrainAStarPathfinder.FillFlowFieldWithPath from the loop on, operation for operation.
            private bool Run(object graph, object field, object heap, object heuristics, int dest)
            {
                while (!_isEmpty(heap))
                {
                    TAStar entry = _pop(heap);
                    int id = _entryId(entry);
                    if (_hasNode(field, id))
                    {
                        continue;
                    }
                    float g = _entryG(entry);
                    _addNode(field, id, _entryParent(entry), g);
                    if (id == dest)
                    {
                        _markPartial(field);
                        return true;
                    }
                    float hHere = _h(heuristics, id);
                    ReadOnlyList<TNode> neighbors = _neighbors(graph, id);
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        TNode neighbor = neighbors[i];
                        int neighborId = _nodeId(neighbor);
                        if (!_hasNode(field, neighborId))
                        {
                            float cost = _nodeCost(neighbor);
                            float gScore = g + cost;
                            PushChecked(heap, heuristics, neighborId, id, gScore, hHere, cost);
                        }
                    }
                }
                _markFull(field);
                return false;
            }

            private void Push(object heap, object heuristics, int id, int parent, float g)
            {
                float f = g + _h(heuristics, id);
                _push(heap, _newEntry(id, parent, g, f));
            }

            // The same push (same float operations, same entry) plus the consistency check: a step whose cost is
            // below the drop in the heuristic breaks the shortest-distance guarantee the resume relies on.
            private void PushChecked(object heap, object heuristics, int id, int parent, float g, float hFrom, float cost)
            {
                float h = _h(heuristics, id);
                float f = g + h;
                _push(heap, _newEntry(id, parent, g, f));
                if (hFrom - h > cost + 0.001f)
                {
                    _uneven = true;
                }
            }

            // Verify mode: the unmodified algorithm on its own field and heap, then a comparison of what the game
            // would be told. Called after Fill, with the heuristic still set to dest.
            public override Comparison Compare(object pathfinder, object graph, object field, int start, int dest, State state,
                out int shadowExplored)
            {
                shadowExplored = 0;
                if (state.Shadow == null)
                {
                    state.Shadow = Activator.CreateInstance(_fieldType, true);
                    state.ShadowHeap = _createHeap(_heapFactory(pathfinder));
                }
                object shadow = state.Shadow;
                if (!state.ShadowValid)
                {
                    // Start the comparison from a clean slate that matches this mod's field: the shadow gets the
                    // same start and, on its next question, searches from scratch as the game would.
                    _clearField(shadow, _fieldStart(field));
                    state.ShadowValid = true;
                }
                object heuristics = _heuristics(pathfinder);
                _setDestination(heuristics, dest);
                if (!(_checkedPath(shadow, start, dest) || !(_onNavMesh(graph, start) && _onNavMesh(graph, dest))))
                {
                    _clearField(shadow, start);
                    _clearHeap(state.ShadowHeap);
                    Push(state.ShadowHeap, heuristics, start, -1, 0f);
                    Run(graph, shadow, state.ShadowHeap, heuristics, dest);
                    shadowExplored = _nodes(shadow).Count;
                }
                bool realFound = _foundPath(field, start, dest);
                bool shadowFound = _foundPath(shadow, start, dest);
                if (realFound != shadowFound)
                {
                    return Comparison.Found;
                }
                if (!realFound)
                {
                    return Comparison.Identical;
                }
                LastReal = _distance(field, dest);
                LastShadow = _distance(shadow, dest);
                if (LastReal != LastShadow)
                {
                    float tolerance = 0.001f * Math.Max(1f, Math.Abs(LastShadow));
                    return Math.Abs(LastReal - LastShadow) <= tolerance ? Comparison.Rounding : Comparison.Distance;
                }
                Route(field, start, dest, _routeA);
                Route(shadow, start, dest, _routeB);
                if (_routeA.Count != _routeB.Count)
                {
                    return Comparison.Route;
                }
                for (int i = 0; i < _routeA.Count; i++)
                {
                    if (_routeA[i] != _routeB[i])
                    {
                        return Comparison.Route;
                    }
                }
                return Comparison.Identical;
            }

            private void Route(object field, int start, int dest, List<int> route)
            {
                route.Clear();
                int node = dest;
                int limit = _nodes(field).Count + 1;
                while (node != start && node != -1 && route.Count < limit)
                {
                    route.Add(node);
                    node = _parent(field, node);
                }
                route.Add(node);
            }
        }
    }
}
