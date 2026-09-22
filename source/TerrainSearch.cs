using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Timberborn.Common;
using Timberborn.Navigation;
using UnityEngine;

namespace LateGamePerformance
{
    // The game's terrain path search, resumed instead of restarted, and kept per start tile.
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
    // Kept searches (0.4.30). A beaver pricing a need asks, for every building i, beaver -> i and then i -> the
    // place it has to be back at (DistrictNeedBehaviorService.PickShortestAction through
    // ActionDurationCalculator.DurationWithReturnInHours). The starts alternate, so the game's one field is thrown
    // away on every question and nothing above ever resumes: logged 0.4.28 blocks had 0 to 8 resumes against
    // about 4,000 fresh searches, and single need picks of 96 and 106 ms that were almost all terrain A*. So the
    // one-destination terrain search in PathfindingService (FindTerrainPathUncached, the only reader of the game's
    // default terrain field besides the list-of-destinations search) is replaced: the question goes to one of
    // KeptSearches searches kept by start tile, least recently used replaced first, each with its own
    // PathFlowField and heap, and the same rules as above apply per start tile: answered from that tile's field,
    // resumed in it, or searched from scratch in it. The game's own FlowFieldPathFinder then reads the answer from
    // that field exactly as it reads the default field. Every terrain change reaches every kept field the moment
    // it reaches the game's default field (TerrainFlowFieldCache.OnNavMeshUpdated), through the game's own
    // PathFlowField.OnNodesChanged, so a kept field is emptied exactly when the game's field would be. The
    // list-of-destinations search is left on the game's field as it was: it always starts over, so its answer
    // never depends on what a field held.
    //
    // What is and is not the same as the game: a fresh search is identical. A resumed search, or an answer read
    // from an earlier search from the same tile, gives the same distance, because the game's heuristic never
    // overestimates a terrain step (0.9 per straight tile, 1.273 per diagonal, against costs of 1 and 1.414),
    // except for the last bits of floating-point rounding when the shortest distance is reached by a different but
    // equally short route; and in that case the route itself can be a different one of equal length. Kept searches
    // hand out this same kind of answer for more questions than before (questions from a tile that was searched
    // from a few questions ago, not only from the last one); they add no other kind. Ziplines and tubes carry their
    // own costs, which the heuristic does not know; wherever a search meets a step, in either direction, steeper
    // than the heuristic allows, the guarantee is gone, so such a search is never resumed from, a resumed search
    // that meets one starts over the game's way, and a kept search that met one answers nothing except when it is
    // the very search the game's own field would hold (the last one), exactly as the game would.
    //
    // Every player on this version gets the same answer, because which kept search answers, and what it holds,
    // depends only on the simulation's own sequence of questions and terrain changes, the same on every computer:
    // least-recent use is a counter of questions, the memory limit below counts tiles and heap entries. A
    // multiplayer game needs nothing more: BeaverBuddies has every player, the host included, load the same save
    // file into a new scene, and a new scene starts with no history (and so does the game's save into memory).
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
        private const string PathfindingServiceType = "Timberborn.Navigation.PathfindingService";
        private const string FlowFieldCacheType = "Timberborn.Navigation.TerrainFlowFieldCache";
        private const string PathFinderType = "Timberborn.Navigation.FlowFieldPathFinder";
        private const string PathRequestType = "Timberborn.Navigation.PathRequest";
        private const string NodeIdServiceType = "Timberborn.Navigation.NodeIdService";

        // How many start tiles keep their search. Fixed: which search answers must be the same on every computer.
        internal const int KeptSearches = 32;

        // A kept field costs memory by the most tiles it ever held (the game's dictionary keeps its size when
        // cleared): 20 bytes an entry and 4 a bucket, at up to twice the tiles after its last growth, so 48 is an
        // upper bound. A heap entry is 16 bytes. Above the limit the least recently used kept searches are dropped.
        // A search that explores a whole 258 x 258 map holds about 66,000 tiles per level, 3 to 6 MB with its heap.
        internal const long KeptBytesLimit = 32L << 20;
        private const int BytesPerFieldTile = 48;
        private const int BytesPerHeapEntry = 16;
        // The game sizes its one heap for a quarter of the map's nodes (7 MB on a large map); a kept heap starts
        // small and grows like any List, which changes nothing about the order it pops in.
        private const int InitialHeapCapacity = 256;

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
        // The game's default field, for a search that reaches TerrainAStarPathfinder.FillFlowFieldWithPath directly.
        private static readonly Engine.State State = new Engine.State();
        // Verify mode for kept searches: a shadow field that stands in for the game's default field, which with
        // this mod on no longer sees the one-destination searches. It gets every terrain change the game's gets.
        private static readonly Engine.State KeptShadow = new Engine.State();
        private static readonly Slot[] Slots = new Slot[KeptSearches];
        private static int _slotCount;
        private static long _clock;
        // The kept search the game's own default field would now hold: the one the last question used, unless a
        // list-of-destinations search has overwritten the game's field since.
        private static Slot _lastSlot;
        private static long _keptLimit = KeptBytesLimit;

        private static Func<object, object> _serviceNodeIds;
        private static Func<object, object> _serviceGraph;
        private static Func<object, object> _servicePathfinder;
        private static Func<object, object> _serviceFinder;
        private static Func<object, Vector3, int> _worldToId;
        private static FindInField _findInField;
        private static Func<NavMeshUpdate, ReadOnlyList<int>> _terrainNodeIds;

        private delegate bool FindInField(object finder, object field, Vector3 start, Vector3 destination, out float distance,
            List<PathCorner> pathCorners);

        private static long _answered;
        private static long _fresh;
        private static long _resumed;
        private static long _restarted;
        private static long _freshNodes;
        private static long _resumedNodes;
        private static long _stopwatchTicks;
        private static long _longestTicks;
        private static long _kept;
        private static long _evicted;
        private static long _emptied;
        private static long _dropped;
        private static long _verifyIdentical;
        private static long _verifyRounding;
        private static long _verifyRoute;
        private static long _verifyDistance;
        private static long _verifyFound;
        private static long _verifyShadowNodes;
        private static long _verifyRealNodes;
        private static int _verifyLogged;

        // For the tests: how the last kept search was answered, and the field that holds it.
        internal static Engine.Outcome LastOutcome;
        internal static object LastField;

        // One start tile's search: its own field and heap, and the resume bookkeeping for them.
        private sealed class Slot
        {
            public object Field;
            public object Heap;
            public readonly Engine.State State = new Engine.State();
            public int Start = -1;
            public long LastUse;
            public int PeakTiles;
            public long Bytes;
        }

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
                // The game's save into memory: its save benchmark (GameSaver.BenchmarkSavingToMemory, run by the
                // -benchmarkSaveCount option) and the save it attaches to a crash report after the first uncaught
                // exception has already stopped the scene. No multiplayer mod calls it: BeaverBuddies sends the save
                // file itself, and every player loads that into a new scene (SceneCreated). So players do not rely
                // on this hook to agree; forgetting the history here is harmless in both cases.
                Name = "GameSaver.SaveWithoutFinishingTick",
                Required = false,
                Target = () => Reflect.Method(GameSaverType, "SaveWithoutFinishingTick"),
                Postfix = Reflect.Own(self, nameof(JoinSavePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // The one-destination terrain search, sent to the search kept for its start tile. The
                // list-of-destinations overload stays the game's (see the top of the file).
                Name = "PathfindingService.FindTerrainPathUncached (one destination)",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return AccessTools.Method(Reflect.GameType(PathfindingServiceType), "FindTerrainPathUncached",
                        new[] { typeof(Vector3), typeof(Vector3), typeof(float).MakeByRefType(), typeof(List<PathCorner>) });
                },
                Prefix = Reflect.Own(self, nameof(UncachedPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // Every terrain change the game's default field sees, every kept field sees too. Required: a kept
                // field that missed one could answer from terrain that is gone.
                Name = "TerrainFlowFieldCache.OnNavMeshUpdated",
                Required = true,
                Target = () => Reflect.Method(FlowFieldCacheType, "OnNavMeshUpdated"),
                Postfix = Reflect.Own(self, nameof(NavMeshUpdatedPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            State.Valid = false;
            DropKept();
            _active = true;
        }

        // A new game or map: the previous scene's field and heap are gone, and node ids mean other tiles.
        public static void SceneCreated()
        {
            State.Forget();
            DropKept();
        }

        internal static bool IsActive => _active;

        internal static int KeptInUse
        {
            get
            {
                int inUse = 0;
                for (int i = 0; i < _slotCount; i++)
                {
                    if (Slots[i].Start != -1)
                    {
                        inUse++;
                    }
                }
                return inUse;
            }
        }

        internal static void SetKeptLimitForTests(long bytes)
        {
            _keptLimit = bytes;
        }

        internal static void Bind()
        {
            if (_engine != null)
            {
                return;
            }
            Type engine = typeof(Engine<,>).MakeGenericType(Reflect.GameType(NavMeshNodeType), Reflect.GameType(AStarNodeType));
            Engine created = (Engine)Activator.CreateInstance(engine);

            Type service = Reflect.GameType(PathfindingServiceType);
            _serviceNodeIds = Reflect.FieldGetter<object>(service, "_nodeIdService");
            _serviceGraph = Reflect.FieldGetter<object>(service, "_terrainNavMeshGraph");
            _servicePathfinder = Reflect.FieldGetter<object>(service, "_terrainAStarPathfinder");
            _serviceFinder = Reflect.FieldGetter<object>(service, "_flowFieldPathFinder");
            _worldToId = Reflect.InstanceCall<Func<object, Vector3, int>>(
                AccessTools.Method(Reflect.GameType(NodeIdServiceType), "WorldToId", new[] { typeof(Vector3) }));
            _findInField = FindInFieldCall();
            ParameterExpression update = Expression.Parameter(typeof(NavMeshUpdate), "update");
            _terrainNodeIds = Expression.Lambda<Func<NavMeshUpdate, ReadOnlyList<int>>>(
                Expression.Property(update, AccessTools.Property(typeof(NavMeshUpdate), "TerrainNodeIds")), update).Compile();
            _engine = created;
        }

        // FlowFieldPathFinder.FindPathInFlowField(PathFlowField, PathRequest.Create(start, destination), out distance,
        // pathCorners): what the game's FindTerrainPathUncached does with its field once the search is done.
        private static FindInField FindInFieldCall()
        {
            Type finder = Reflect.GameType(PathFinderType);
            Type field = Reflect.GameType(FieldType);
            Type request = Reflect.GameType(PathRequestType);
            MethodInfo find = null;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(finder))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "FindPathInFlowField" && parameters.Length == 4 && parameters[0].ParameterType == field &&
                    parameters[1].ParameterType == request)
                {
                    find = method;
                }
            }
            MethodInfo create = AccessTools.Method(request, "Create", new[] { typeof(Vector3), typeof(Vector3) });
            if (find == null || create == null)
            {
                throw new MissingMethodException(PathFinderType, "FindPathInFlowField(PathFlowField, PathRequest, out float, List<PathCorner>)");
            }
            ParameterExpression finderObject = Expression.Parameter(typeof(object), "finder");
            ParameterExpression fieldObject = Expression.Parameter(typeof(object), "field");
            ParameterExpression start = Expression.Parameter(typeof(Vector3), "start");
            ParameterExpression destination = Expression.Parameter(typeof(Vector3), "destination");
            ParameterExpression distance = Expression.Parameter(typeof(float).MakeByRefType(), "distance");
            ParameterExpression corners = Expression.Parameter(typeof(List<PathCorner>), "pathCorners");
            Expression call = Expression.Call(Expression.Convert(finderObject, finder), find, Expression.Convert(fieldObject, field),
                Expression.Call(create, start, destination), distance, corners);
            return Expression.Lambda<FindInField>(call, finderObject, fieldObject, start, destination, distance, corners).Compile();
        }

        public static string TakeStatsLine()
        {
            if (!_active || _answered + _fresh + _resumed == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double longest = _longestTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"TerrainSearch: {_answered + _fresh + _resumed + _restarted} terrain path searches in {ms:0.0} ms " +
                          $"(longest {longest:0.00} ms): {_answered} answered from an earlier search from the same tile, " +
                          $"{_fresh} started from scratch exploring {_freshNodes} tiles, " +
                          $"{_resumed} resumed exploring {_resumedNodes} more tiles, {_restarted} started over after meeting a step " +
                          "cheaper than the heuristic allows";
            if (_kept > 0)
            {
                long bytes = 0;
                for (int i = 0; i < _slotCount; i++)
                {
                    bytes += Slots[i].Bytes;
                }
                line += $"; searches kept for up to {KeptSearches} start tiles, so these answers and resumes also come after " +
                        $"other tiles' searches: {_kept} searches used them, {KeptInUse} kept now (~{bytes / 1048576.0:0.0} MB), " +
                        $"{_evicted} new start tiles replaced the least recently used, {_emptied} kept searches emptied by " +
                        $"terrain changes, {_dropped} dropped to stay under {KeptBytesLimit >> 20} MB";
            }
            if (_verify)
            {
                line += $"; verify: {_verifyIdentical} identical, {_verifyRounding} same distance within rounding, " +
                        $"{_verifyRoute} equally short but different route, {_verifyDistance} DIFFERENT DISTANCE, " +
                        $"{_verifyFound} DIFFERENT REACHABILITY; the game's search explored {_verifyShadowNodes} tiles, " +
                        $"this mod's {_verifyRealNodes}";
            }
            _answered = _fresh = _resumed = _restarted = _freshNodes = _resumedNodes = _stopwatchTicks = _longestTicks = 0;
            _kept = _evicted = _emptied = _dropped = 0;
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
                Engine.Outcome outcome = _engine.Fill(__instance, terrainNavMeshGraph, flowField, _engine.OpenSet(__instance),
                    startNodeId, destinationNodeId, State, true, out int explored);
                Count(outcome, explored, Stopwatch.GetTimestamp() - stamp);
                if (_verify)
                {
                    // A comparison that fails switches only the comparison off: the answer above stands, and a
                    // measurement that one player has on must never change what that player's game does.
                    try
                    {
                        Verify(__instance, terrainNavMeshGraph, flowField, startNodeId, destinationNodeId, explored, State);
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

        // PathfindingService.FindTerrainPathUncached(start, destination, out distance, pathCorners): the game's
        // steps, in the game's order, with the search kept for the start tile in place of the default field.
        [HarmonyPriority(Priority.Last)]
        internal static bool UncachedPrefix(object __instance, Vector3 start, Vector3 destination, ref float distance,
            List<PathCorner> pathCorners, ref bool __result)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                object nodeIds = _serviceNodeIds(__instance);
                object graph = _serviceGraph(__instance);
                int startNodeId = _worldToId(nodeIds, start);
                int destinationNodeId = _worldToId(nodeIds, destination);
                Slot slot = FindSlot(startNodeId);
                if (slot == null && !(_engine.OnNavMesh(graph, startNodeId) && _engine.OnNavMesh(graph, destinationNodeId)))
                {
                    // Nothing to search and no kept search to answer from: the game's own code answers, from its own
                    // field, which it leaves as it is.
                    return true;
                }
                object field = Search(_servicePathfinder(__instance), graph, slot ?? TakeSlot(startNodeId), startNodeId,
                    destinationNodeId);
                __result = _findInField(_serviceFinder(__instance), field, start, destination, out distance, pathCorners);
                return false;
            }
            catch (Exception exception)
            {
                // The game's method runs next on its own default field, which nothing here has touched.
                Fail(exception);
                return true;
            }
        }

        internal static void MultiPostfix(object flowField)
        {
            Touched(flowField);
            // The game's default field now holds this search, not the last kept one.
            _lastSlot = null;
            KeptShadow.ShadowValid = false;
        }

        internal static void NodesChangedPostfix(object __instance)
        {
            Touched(__instance);
        }

        internal static void NavMeshUpdatedPostfix(NavMeshUpdate navMeshUpdate)
        {
            if (!_active || (_slotCount == 0 && KeptShadow.Shadow == null))
            {
                return;
            }
            try
            {
                ReadOnlyList<int> terrainNodeIds = _terrainNodeIds(navMeshUpdate);
                for (int i = 0; i < _slotCount; i++)
                {
                    Slot slot = Slots[i];
                    if (slot.Field == null)
                    {
                        continue;
                    }
                    int before = _engine.NodeCount(slot.Field);
                    _engine.OnNodesChanged(slot.Field, terrainNodeIds);
                    if (before > 0 && _engine.NodeCount(slot.Field) == 0)
                    {
                        _emptied++;
                    }
                    slot.State.Valid = false;
                }
                if (KeptShadow.Shadow != null)
                {
                    try
                    {
                        _engine.OnNodesChanged(KeptShadow.Shadow, terrainNodeIds);
                    }
                    catch (Exception exception)
                    {
                        _verify = false;
                        KeptShadow.Forget();
                        Log.Warning("TerrainSearch verify failed and is off for this session; the search itself is unaffected: " + exception);
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void JoinSavePostfix()
        {
            if (_active)
            {
                _engine.ForgetHistory(State);
                DropKept();
            }
        }
        // ReSharper restore InconsistentNaming

        private static void Count(Engine.Outcome outcome, int explored, long stopwatchTicks)
        {
            _stopwatchTicks += stopwatchTicks;
            if (stopwatchTicks > _longestTicks)
            {
                _longestTicks = stopwatchTicks;
            }
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
        }

        private static Slot FindSlot(int start)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (Slots[i].Start == start)
                {
                    return Slots[i];
                }
            }
            return null;
        }

        // A slot for a start tile with none: a free one, a new one while there are fewer than KeptSearches, or the
        // least recently used.
        private static Slot TakeSlot(int start)
        {
            Slot free = null, oldest = null;
            for (int i = 0; i < _slotCount; i++)
            {
                Slot slot = Slots[i];
                if (slot.Start == -1)
                {
                    free = free ?? slot;
                }
                else if (oldest == null || slot.LastUse < oldest.LastUse)
                {
                    oldest = slot;
                }
            }
            Slot taken = free;
            if (taken == null && _slotCount < KeptSearches)
            {
                taken = new Slot();
                Slots[_slotCount++] = taken;
            }
            if (taken == null)
            {
                taken = oldest;
                _evicted++;
            }
            if (taken.Field == null)
            {
                taken.Field = _engine.NewField();
                taken.Heap = _engine.NewHeap(InitialHeapCapacity);
            }
            // The field still holds another tile's search: nothing in it answers or resumes for this one (its start
            // differs), and the first search here clears it the game's way.
            taken.Start = start;
            taken.State.Valid = false;
            return taken;
        }

        private static object Search(object pathfinder, object graph, Slot slot, int start, int dest)
        {
            _kept++;
            slot.LastUse = ++_clock;
            // A search that met a step steeper than the heuristic allows may not hold shortest distances; the game
            // answers from it only while it is the last search, so only then does this.
            bool mayAnswer = slot.State.Consistent || ReferenceEquals(slot, _lastSlot);
            long stamp = Stopwatch.GetTimestamp();
            Diagnostics.TerrainSearchStarting(slot.Field, out Diagnostics.SearchState timing);
            Engine.Outcome outcome = _engine.Fill(pathfinder, graph, slot.Field, slot.Heap, start, dest, slot.State, mayAnswer,
                out int explored);
            Diagnostics.TerrainSearchDone(slot.Field, timing);
            Count(outcome, explored, Stopwatch.GetTimestamp() - stamp);
            _lastSlot = slot;
            LastOutcome = outcome;
            LastField = slot.Field;
            KeepUnderLimit(slot);
            if (_verify)
            {
                try
                {
                    Verify(pathfinder, graph, slot.Field, start, dest, explored, KeptShadow);
                }
                catch (Exception exception)
                {
                    _verify = false;
                    KeptShadow.Forget();
                    Log.Warning("TerrainSearch verify failed and is off for this session; the search itself is unaffected: " + exception);
                }
            }
            return slot.Field;
        }

        // Memory: drops the least recently used kept searches (not the one just used) while the total is over the
        // limit. The figures are tile and heap-entry counts, the same on every computer.
        private static void KeepUnderLimit(Slot used)
        {
            int tiles = _engine.NodeCount(used.Field);
            if (tiles > used.PeakTiles)
            {
                used.PeakTiles = tiles;
            }
            used.Bytes = (long)used.PeakTiles * BytesPerFieldTile + (long)_engine.HeapCapacity(used.Heap) * BytesPerHeapEntry;
            long total = 0;
            for (int i = 0; i < _slotCount; i++)
            {
                total += Slots[i].Bytes;
            }
            while (total > _keptLimit)
            {
                Slot oldest = null;
                for (int i = 0; i < _slotCount; i++)
                {
                    Slot slot = Slots[i];
                    if (slot != used && slot.Field != null && (oldest == null || slot.LastUse < oldest.LastUse))
                    {
                        oldest = slot;
                    }
                }
                if (oldest == null)
                {
                    break;
                }
                total -= oldest.Bytes;
                Drop(oldest);
                _dropped++;
            }
        }

        // Lets go of the slot's field and heap (their memory goes back with the next collection); the slot is free.
        private static void Drop(Slot slot)
        {
            slot.Field = null;
            slot.Heap = null;
            slot.Start = -1;
            slot.PeakTiles = 0;
            slot.Bytes = 0;
            slot.State.Forget();
            if (ReferenceEquals(slot, _lastSlot))
            {
                _lastSlot = null;
            }
        }

        // Forgets every kept search, as a new scene starts: the same state on a player who has just loaded the save.
        private static void DropKept()
        {
            for (int i = 0; i < _slotCount; i++)
            {
                Slots[i] = null;
            }
            _slotCount = 0;
            _clock = 0;
            _lastSlot = null;
            LastField = null;
            KeptShadow.Forget();
        }

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

        private static void Verify(object pathfinder, object graph, object field, int start, int dest, int explored, Engine.State state)
        {
            Engine.Comparison comparison = _engine.Compare(pathfinder, graph, field, start, dest, state, out int shadowExplored);
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
            DropKept();
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
                // False once a search from this start met a step steeper than the heuristic allows (a zipline, a
                // tube): explored tiles may then not hold their shortest distance, so nothing resumes.
                public bool Consistent;
                // Verify mode: the unmodified algorithm's own field and heap.
                public object Shadow;
                public object ShadowHeap;
                public bool ShadowValid;

                public void Forget()
                {
                    Valid = false;
                    Consistent = false;
                    Field = null;
                    Heap = null;
                    Shadow = null;
                    ShadowHeap = null;
                    ShadowValid = false;
                }
            }

            public float LastReal;
            public float LastShadow;

            // mayAnswer false: search even where the game would answer from the field (see Search).
            public abstract Outcome Fill(object pathfinder, object graph, object field, object heap, int start, int dest,
                State state, bool mayAnswer, out int explored);

            public abstract Comparison Compare(object pathfinder, object graph, object field, int start, int dest, State state,
                out int shadowExplored);

            // Drop the resume state and empty the game's field, so the next question is searched from scratch here
            // as it will be on a player who loads the save being written now.
            public abstract void ForgetHistory(State state);

            public abstract object OpenSet(object pathfinder);
            public abstract object NewField();
            public abstract object NewHeap(int capacity);
            public abstract int HeapCapacity(object heap);
            public abstract int NodeCount(object field);
            public abstract void OnNodesChanged(object field, ReadOnlyList<int> nodeIds);
            public abstract bool OnNavMesh(object graph, int nodeId);
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
            private readonly Func<object, int, float> _distance;
            private readonly Func<object, int, int> _parent;
            private readonly Func<object, int> _fieldStart;
            private readonly Func<object, bool> _refreshed;
            private readonly Func<object, bool> _fullyFilled;
            private readonly Func<object, ICollection> _nodes;
            private readonly Action<object, ReadOnlyList<int>> _nodesChanged;
            private readonly Action<object, TAStar> _push;
            private readonly Action<object> _clearHeap;
            private readonly Func<object, List<TAStar>> _heapNodes;
            private readonly Func<object, int> _heapCount;
            private readonly Action<object, int> _setHeapCount;
            private readonly Func<TAStar, TAStar, bool> _less;
            private readonly Func<int, int, float, float, TAStar> _newEntry;
            private readonly Action<object, int> _setDestination;
            private readonly Func<object, int, float> _h;
            private readonly Func<object, object> _heuristics;
            private readonly Func<object, object> _openSet;
            private readonly Func<object, object> _heapFactory;
            private readonly Func<object, object> _createHeap;
            private readonly RunLoop _run;
            private readonly RepriceLoop _reprice;
            private readonly Type _fieldType;
            private readonly Type _heapType;
            private readonly List<int> _routeA = new List<int>();
            private readonly List<int> _routeB = new List<int>();
            // Set by Run/Expand when a step out of an explored tile, in either direction, is steeper than the
            // heuristic allows (|h(from) - h(to)| > cost).
            private bool _uneven;

            public Engine()
            {
                Type graph = Reflect.GameType(GraphType);
                Type field = Reflect.GameType(FieldType);
                Type heap = Reflect.GameType(HeapType).MakeGenericType(typeof(TAStar));
                Type heuristics = Reflect.GameType(HeuristicsType);
                Type pathfinder = Reflect.GameType(PathfinderType);
                _fieldType = field;
                _heapType = heap;
                _neighbors = Reflect.InstanceCall<Func<object, int, ReadOnlyList<TNode>>>(AccessTools.Method(graph, "GetNeighbors"));
                _onNavMesh = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(graph, "IsOnNavMesh"));
                _nodeId = StructGetter<TNode, int>("Id");
                _nodeCost = StructGetter<TNode, float>("Cost");
                _checkedPath = Reflect.InstanceCall<Func<object, int, int, bool>>(AccessTools.Method(field, "CheckedPath"));
                _foundPath = Reflect.InstanceCall<Func<object, int, int, bool>>(AccessTools.Method(field, "FoundPath"));
                _clearField = Reflect.InstanceCall<Action<object, int>>(AccessTools.Method(field, "Clear", new[] { typeof(int) }));
                _hasNode = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(field, "HasNode"));
                _distance = Reflect.InstanceCall<Func<object, int, float>>(AccessTools.Method(field, "GetDistance"));
                _parent = Reflect.InstanceCall<Func<object, int, int>>(AccessTools.Method(field, "GetParentId"));
                _fieldStart = Reflect.FieldGetter<int>(field, "_startNodeId");
                _refreshed = Reflect.FieldGetter<bool>(field, "_refreshed");
                _fullyFilled = Reflect.FieldGetter<bool>(field, "_fullyFilled");
                _nodes = Reflect.FieldGetter<ICollection>(field, "_nodes");
                _nodesChanged = Reflect.InstanceCall<Action<object, ReadOnlyList<int>>>(AccessTools.Method(field, "OnNodesChanged"));
                _push = Reflect.InstanceCall<Action<object, TAStar>>(AccessTools.Method(heap, "Push"));
                _clearHeap = Reflect.InstanceCall<Action<object>>(AccessTools.Method(heap, "Clear"));
                _heapNodes = Reflect.FieldGetter<List<TAStar>>(heap, "_nodes");
                _heapCount = Reflect.FieldGetter<int>(heap, "_nextFreeIndex");
                _setHeapCount = Reflect.FieldSetter<int>(heap, "_nextFreeIndex");
                ParameterExpression a = Expression.Parameter(typeof(TAStar), "a");
                ParameterExpression b = Expression.Parameter(typeof(TAStar), "b");
                _less = Expression.Lambda<Func<TAStar, TAStar, bool>>(
                    Expression.Call(a, typeof(TAStar).GetMethod("IsLessThan", new[] { typeof(TAStar) }), b), a, b).Compile();
                _newEntry = Constructor();
                _setDestination = Reflect.InstanceCall<Action<object, int>>(AccessTools.Method(heuristics, "SetDestinationNode"));
                _h = Reflect.InstanceCall<Func<object, int, float>>(AccessTools.Method(heuristics, "H"));
                _heuristics = Reflect.FieldGetter<object>(pathfinder, "_heuristicsCalculator");
                _openSet = Reflect.FieldGetter<object>(pathfinder, "_openSet");
                _heapFactory = Reflect.FieldGetter<object>(pathfinder, "_binaryHeapFactory");
                MethodInfo create = AccessTools.Method(Reflect.GameType(HeapFactoryType), "Create").MakeGenericMethod(typeof(TAStar));
                _createHeap = Reflect.InstanceCall<Func<object, object>>(create);
                _run = BuildRun(graph, field, heap, heuristics);
                _reprice = BuildReprice(field, heuristics);
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

            public override object OpenSet(object pathfinder) => _openSet(pathfinder);

            public override object NewField() => Activator.CreateInstance(_fieldType, true);

            public override object NewHeap(int capacity)
            {
                return Activator.CreateInstance(_heapType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { capacity }, null);
            }

            public override int HeapCapacity(object heap) => _heapNodes(heap).Capacity;

            public override int NodeCount(object field) => _nodes(field).Count;

            public override void OnNodesChanged(object field, ReadOnlyList<int> nodeIds) => _nodesChanged(field, nodeIds);

            public override bool OnNavMesh(object graph, int nodeId) => _onNavMesh(graph, nodeId);

            public override Outcome Fill(object pathfinder, object graph, object field, object heap, int start, int dest,
                State state, bool mayAnswer, out int explored)
            {
                explored = 0;
                object heuristics = _heuristics(pathfinder);
                // The game's own first steps, in the game's order.
                _setDestination(heuristics, dest);
                if ((mayAnswer && _checkedPath(field, start, dest)) || !(_onNavMesh(graph, start) && _onNavMesh(graph, dest)))
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
            // since they were pushed are dropped, as the game would skip them. Done in place on the heap's own array
            // and put back in heap order in one pass (Floyd), rather than popping and pushing every entry: equally
            // priced entries may then come out in another order than a rebuild by pushes would give, which only a
            // resumed search sees, and a resumed search already may pick another equally short route.
            private void Reprice(object heap, object field, object heuristics)
            {
                List<TAStar> entries = _heapNodes(heap);
                int kept = _reprice(field, heuristics, entries, _heapCount(heap));
                _setHeapCount(heap, kept);
                for (int i = kept / 2 - 1; i >= 0; i--)
                {
                    SiftDown(entries, i, kept);
                }
            }

            // BinaryHeap.HeapifyFromIndexToEnd: the smaller child moves up while it is smaller than its parent.
            private void SiftDown(List<TAStar> entries, int index, int count)
            {
                while (true)
                {
                    int smallest = index;
                    int left = 2 * index + 1;
                    int right = left + 1;
                    if (left < count && _less(entries[left], entries[smallest]))
                    {
                        smallest = left;
                    }
                    if (right < count && _less(entries[right], entries[smallest]))
                    {
                        smallest = right;
                    }
                    if (smallest == index)
                    {
                        return;
                    }
                    TAStar swap = entries[index];
                    entries[index] = entries[smallest];
                    entries[smallest] = swap;
                    index = smallest;
                }
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
                    else
                    {
                        CheckExplored(heuristics, neighbor, neighborId, hHere);
                    }
                }
            }

            // TerrainAStarPathfinder.FillFlowFieldWithPath from the loop on, operation for operation: one compiled loop
            // (BuildRun) that calls the game's own methods on the game's own types, as the game's loop does.
            private bool Run(object graph, object field, object heap, object heuristics, int dest)
            {
                return _run(graph, field, heap, heuristics, dest, ref _uneven);
            }

            private delegate bool RunLoop(object graph, object field, object heap, object heuristics, int dest, ref bool uneven);

            // The loop below, as an expression tree so that it names the game's internal types and the JIT sees plain
            // calls, as in the game's own method (with a delegate per operation a search took about 1.6 times the
            // game's time per tile in the harness, 1.3 times this way). In C#, with the game's types:
            //   while (!heap.IsEmpty()) {
            //       AStarNode node = heap.Pop(); int id = node.NodeId;
            //       if (field.HasNode(id)) continue;
            //       float g = node.GScore; field.AddNode(id, node.ParentNodeId, g);
            //       if (id == dest) { field.MarkAsPartiallyFilled(); return true; }
            //       float hHere = heuristics.H(id); ReadOnlyList<NavMeshNode> neighbors = graph.GetNeighbors(id);
            //       for (int i = 0; i < neighbors.Count; i++) {
            //           NavMeshNode neighbor = neighbors[i]; int neighborId = neighbor.Id; float cost = neighbor.Cost;
            //           if (!field.HasNode(neighborId)) {
            //               float gScore = g + cost; float h = heuristics.H(neighborId);
            //               heap.Push(new AStarNode(neighborId, id, gScore, gScore + h));
            //               if (Math.Abs(hHere - h) > cost + 0.001f) uneven = true;
            //           } else if (!uneven && Math.Abs(hHere - heuristics.H(neighborId)) > cost + 0.001f) uneven = true;
            //       }
            //   }
            //   field.MarkAsFullyFilled(); return false;
            // The game's VisitNode reads the cost only for a tile it pushes; reading it first changes nothing.
            private static RunLoop BuildRun(Type graphType, Type fieldType, Type heapType, Type heuristicsType)
            {
                ParameterExpression graphObject = Expression.Parameter(typeof(object), "graph");
                ParameterExpression fieldObject = Expression.Parameter(typeof(object), "field");
                ParameterExpression heapObject = Expression.Parameter(typeof(object), "heap");
                ParameterExpression heuristicsObject = Expression.Parameter(typeof(object), "heuristics");
                ParameterExpression dest = Expression.Parameter(typeof(int), "dest");
                ParameterExpression uneven = Expression.Parameter(typeof(bool).MakeByRefType(), "uneven");

                ParameterExpression graph = Expression.Variable(graphType, "g");
                ParameterExpression field = Expression.Variable(fieldType, "f");
                ParameterExpression heap = Expression.Variable(heapType, "open");
                ParameterExpression heuristics = Expression.Variable(heuristicsType, "hc");
                ParameterExpression node = Expression.Variable(typeof(TAStar), "node");
                ParameterExpression id = Expression.Variable(typeof(int), "id");
                ParameterExpression g = Expression.Variable(typeof(float), "gHere");
                ParameterExpression hHere = Expression.Variable(typeof(float), "hHere");
                ParameterExpression neighbors = Expression.Variable(typeof(ReadOnlyList<TNode>), "neighbors");
                ParameterExpression count = Expression.Variable(typeof(int), "count");
                ParameterExpression i = Expression.Variable(typeof(int), "i");
                ParameterExpression neighbor = Expression.Variable(typeof(TNode), "neighbor");
                ParameterExpression neighborId = Expression.Variable(typeof(int), "neighborId");
                ParameterExpression cost = Expression.Variable(typeof(float), "cost");
                ParameterExpression gScore = Expression.Variable(typeof(float), "gScore");
                ParameterExpression h = Expression.Variable(typeof(float), "h");
                ParameterExpression found = Expression.Variable(typeof(bool), "found");

                MethodInfo hasNode = Required(fieldType, "HasNode");
                MethodInfo heuristic = Required(heuristicsType, "H");
                MethodInfo abs = typeof(Math).GetMethod("Abs", new[] { typeof(float) });
                ConstructorInfo entry = typeof(TAStar).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(int), typeof(int), typeof(float), typeof(float) }, null);
                if (entry == null)
                {
                    throw new MissingMethodException(typeof(TAStar).Name, ".ctor(int, int, float, float)");
                }
                Expression Steep(Expression hTo) => Expression.GreaterThan(Expression.Call(abs, Expression.Subtract(hHere, hTo)),
                    Expression.Add(cost, Expression.Constant(0.001f)));

                LabelTarget innerBreak = Expression.Label("innerBreak");
                Expression inner = Expression.Loop(Expression.Block(
                    Expression.IfThen(Expression.GreaterThanOrEqual(i, count), Expression.Break(innerBreak)),
                    Expression.Assign(neighbor, Expression.Property(neighbors, "Item", i)),
                    Expression.Assign(neighborId, Expression.Property(neighbor, "Id")),
                    Expression.Assign(cost, Expression.Property(neighbor, "Cost")),
                    Expression.IfThenElse(Expression.Not(Expression.Call(field, hasNode, neighborId)),
                        Expression.Block(
                            Expression.Assign(gScore, Expression.Add(g, cost)),
                            Expression.Assign(h, Expression.Call(heuristics, heuristic, neighborId)),
                            Expression.Call(heap, Required(heapType, "Push"),
                                Expression.New(entry, neighborId, id, gScore, Expression.Add(gScore, h))),
                            Expression.IfThen(Steep(h), Expression.Assign(uneven, Expression.Constant(true)))),
                        Expression.IfThen(Expression.AndAlso(Expression.Not(uneven),
                                Steep(Expression.Call(heuristics, heuristic, neighborId))),
                            Expression.Assign(uneven, Expression.Constant(true)))),
                    Expression.PostIncrementAssign(i)), innerBreak);

                LabelTarget outerBreak = Expression.Label("outerBreak");
                LabelTarget outerContinue = Expression.Label("outerContinue");
                Expression outer = Expression.Loop(Expression.Block(
                    Expression.IfThen(Expression.Call(heap, Required(heapType, "IsEmpty")), Expression.Break(outerBreak)),
                    Expression.Assign(node, Expression.Call(heap, Required(heapType, "Pop"))),
                    Expression.Assign(id, Expression.Property(node, "NodeId")),
                    Expression.IfThen(Expression.Call(field, hasNode, id), Expression.Continue(outerContinue)),
                    Expression.Assign(g, Expression.Property(node, "GScore")),
                    Expression.Call(field, Required(fieldType, "AddNode"), id, Expression.Property(node, "ParentNodeId"), g),
                    Expression.IfThen(Expression.Equal(id, dest), Expression.Block(
                        Expression.Call(field, Required(fieldType, "MarkAsPartiallyFilled")),
                        Expression.Assign(found, Expression.Constant(true)),
                        Expression.Break(outerBreak))),
                    Expression.Assign(hHere, Expression.Call(heuristics, heuristic, id)),
                    Expression.Assign(neighbors, Expression.Call(graph, Required(graphType, "GetNeighbors"), id)),
                    Expression.Assign(count, Expression.Property(neighbors, "Count")),
                    Expression.Assign(i, Expression.Constant(0)),
                    inner), outerBreak, outerContinue);

                Expression body = Expression.Block(typeof(bool),
                    new[] { graph, field, heap, heuristics, node, id, g, hHere, neighbors, count, i, neighbor, neighborId, cost, gScore, h, found },
                    Expression.Assign(graph, Expression.Convert(graphObject, graphType)),
                    Expression.Assign(field, Expression.Convert(fieldObject, fieldType)),
                    Expression.Assign(heap, Expression.Convert(heapObject, heapType)),
                    Expression.Assign(heuristics, Expression.Convert(heuristicsObject, heuristicsType)),
                    Expression.Assign(found, Expression.Constant(false)),
                    outer,
                    Expression.IfThen(Expression.Not(found), Expression.Call(field, Required(fieldType, "MarkAsFullyFilled"))),
                    found);
                return Expression.Lambda<RunLoop>(body, graphObject, fieldObject, heapObject, heuristicsObject, dest, uneven).Compile();
            }

            private delegate int RepriceLoop(object field, object heuristics, List<TAStar> entries, int count);

            // Reprice's pass over the heap's array, compiled like the search loop. In C#:
            //   int kept = 0;
            //   for (int i = 0; i < count; i++) {
            //       AStarNode node = entries[i]; int id = node.NodeId;
            //       if (!field.HasNode(id)) { float g = node.GScore; entries[kept++] = new AStarNode(id, node.ParentNodeId, g, g + heuristics.H(id)); }
            //   }
            //   return kept;
            private static RepriceLoop BuildReprice(Type fieldType, Type heuristicsType)
            {
                ParameterExpression fieldObject = Expression.Parameter(typeof(object), "field");
                ParameterExpression heuristicsObject = Expression.Parameter(typeof(object), "heuristics");
                ParameterExpression entries = Expression.Parameter(typeof(List<TAStar>), "entries");
                ParameterExpression count = Expression.Parameter(typeof(int), "count");
                ParameterExpression field = Expression.Variable(fieldType, "f");
                ParameterExpression heuristics = Expression.Variable(heuristicsType, "hc");
                ParameterExpression node = Expression.Variable(typeof(TAStar), "node");
                ParameterExpression id = Expression.Variable(typeof(int), "id");
                ParameterExpression g = Expression.Variable(typeof(float), "g");
                ParameterExpression i = Expression.Variable(typeof(int), "i");
                ParameterExpression kept = Expression.Variable(typeof(int), "kept");
                ConstructorInfo entry = typeof(TAStar).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(int), typeof(int), typeof(float), typeof(float) }, null);
                if (entry == null)
                {
                    throw new MissingMethodException(typeof(TAStar).Name, ".ctor(int, int, float, float)");
                }
                LabelTarget done = Expression.Label("done");
                Expression loop = Expression.Loop(Expression.Block(
                    Expression.IfThen(Expression.GreaterThanOrEqual(i, count), Expression.Break(done)),
                    Expression.Assign(node, Expression.Property(entries, "Item", i)),
                    Expression.Assign(id, Expression.Property(node, "NodeId")),
                    Expression.IfThen(Expression.Not(Expression.Call(field, Required(fieldType, "HasNode"), id)), Expression.Block(
                        Expression.Assign(g, Expression.Property(node, "GScore")),
                        Expression.Assign(Expression.Property(entries, "Item", kept), Expression.New(entry, id,
                            Expression.Property(node, "ParentNodeId"), g,
                            Expression.Add(g, Expression.Call(heuristics, Required(heuristicsType, "H"), id)))),
                        Expression.PostIncrementAssign(kept))),
                    Expression.PostIncrementAssign(i)), done);
                Expression body = Expression.Block(typeof(int), new[] { field, heuristics, node, id, g, i, kept },
                    Expression.Assign(field, Expression.Convert(fieldObject, fieldType)),
                    Expression.Assign(heuristics, Expression.Convert(heuristicsObject, heuristicsType)),
                    Expression.Assign(i, Expression.Constant(0)),
                    Expression.Assign(kept, Expression.Constant(0)),
                    loop,
                    kept);
                return Expression.Lambda<RepriceLoop>(body, fieldObject, heuristicsObject, entries, count).Compile();
            }

            private static MethodInfo Required(Type type, string name)
            {
                MethodInfo method = AccessTools.Method(type, name);
                if (method == null)
                {
                    throw new MissingMethodException(type.Name, name);
                }
                return method;
            }

            private void Push(object heap, object heuristics, int id, int parent, float g)
            {
                float f = g + _h(heuristics, id);
                _push(heap, _newEntry(id, parent, g, f));
            }

            // The same push (same float operations, same entry) plus the consistency check: a step whose cost is
            // below the change in the heuristic, either way (the terrain graph's steps go both ways at one cost),
            // breaks the shortest-distance guarantee the resume and the kept answers rely on.
            private void PushChecked(object heap, object heuristics, int id, int parent, float g, float hFrom, float cost)
            {
                float h = _h(heuristics, id);
                float f = g + h;
                _push(heap, _newEntry(id, parent, g, f));
                if (Math.Abs(hFrom - h) > cost + 0.001f)
                {
                    _uneven = true;
                }
            }

            // The game pushes nothing towards an explored neighbour; only the check looks at that step, read-only,
            // so a steep step between two explored tiles (a zipline whose far end was reached on foot) is seen too.
            private void CheckExplored(object heuristics, TNode neighbor, int neighborId, float hFrom)
            {
                if (!_uneven && Math.Abs(hFrom - _h(heuristics, neighborId)) > _nodeCost(neighbor) + 0.001f)
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
