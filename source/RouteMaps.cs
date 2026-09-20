using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace LateGamePerformance
{
    // Every building with an entrance keeps a "route map": the road distance from its entrance to every road
    // tile it can reach (Timberborn.Navigation.AccessFlowField). When a road changes, the game throws away every
    // map that contains a changed tile, which in a connected district is nearly all of them, and rebuilds each
    // one on the main thread the next time something asks for it.
    //
    // Here: at the end of every navigation tick, every cached map that is not built gets built, spread over
    // worker threads. This file decides which maps and can wait for the whole batch (kept as the fallback).
    // RouteMapsBackground.cs lets the tick carry on while workers build.
    //
    // Which maps: a map is cached exactly while its building is finished (BuildingCachingFlowField), so the set
    // of cached maps is simulation state and identical on every multiplayer peer. After this runs, all of them
    // that can be built are built, on every peer.
    //
    // Up to 0.4.4 this rebuilt "the maps that were filled before the road change". That was wrong for
    // multiplayer: a map also gets filled when a player's range overlay asks for a route, on that player's
    // computer only, so two peers could rebuild different sets and then disagree about which maps are filled.
    // Whether a map is filled can matter, because a few code paths use a map only "if it is already filled".
    // Building everything that is cached removes the dependence on what anyone looked at: whatever a player's
    // overlays filled, both peers end every navigation tick with the same maps filled.
    //
    // Why building them on other threads is safe:
    //  - Regular road changes are only applied inside NavigationSynchronizer.Tick. This runs in that method's
    //    postfix, so nothing changes the road graph or the district maps while workers read them.
    //  - A map depends only on the road graph, its start tile and its district's map. Each worker runs the
    //    game's own RoadFlowFieldGenerator (a private instance per worker, because the generator keeps scratch
    //    state), so a map has exactly the contents and node order the game would have produced.
    //  - Each map is written by exactly one thread. The road graph and district maps are only read.
    //  - How the work is split (worker count, background or not, the small-batch limit) changes who builds a
    //    map and when, never which maps end up built, so those settings may differ between peers.
    //
    // Difference from the unmodded game: maps are filled before the first request instead of on it, so the
    // "if it is already filled" paths take the cached route more often. Every peer needs RouteMaps on or off
    // alike.
    internal static partial class RouteMaps
    {
        public struct Work
        {
            public object Field;
            public object LimitingField;
            public int StartNodeId;
        }

        private delegate void FillCall(object generator, object graph, object field, object limitingField, int startNodeId);

        private const string Namespace = "Timberborn.Navigation.";

        private static readonly List<Work> WorkScratch = new List<Work>();

        private static Func<object, object> _roadFlowFieldCacheOf;
        private static Func<object, object> _roadFlowFieldGeneratorOf;
        private static Func<object, object> _roadNavMeshGraphOf;
        private static Func<object, object> _districtMapOf;
        private static Func<object, object> _binaryHeapFactoryOf;
        private static Func<object, object> _innerCacheOf;
        private static Func<object, object> _entriesOf;
        private static Func<object, int, object> _districtFieldAt;
        private static Func<object, bool> _isFilled;
        private static Func<object, int, bool> _hasNode;
        private static Func<object, int, bool> _isOnNavMesh;
        private static Func<object, object> _fieldOfEntry;
        private static PropertyInfo _entryKeys;
        private static PropertyInfo _entryValues;
        private static ConstructorInfo _generatorConstructor;
        private static MethodInfo _generatorLoad;
        private static FillCall _fill;

        private static bool _active;
        private static int _minFields;
        private static int _workerCount;
        private static object _pathfindingService;
        private static object[] _workerGenerators;

        private static long _batches;
        private static long _fieldsFilled;
        private static long _builtDirectly;
        private static long _wallStopwatchTicks;
        private static long _fillAllocatedBytes;

        public static int WorkerCount => _workerCount;

        public static bool IsActive => _active;

        public static long FieldsFilledSinceReport => _fieldsFilled;

        public static Feature CreateFeature(Config config)
        {
            _minFields = Math.Max(1, config.RouteMapsMinFields);
            _workerCount = config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
            Type self = typeof(RouteMaps);
            Feature feature = new Feature { Name = "RouteMaps" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "PathfindingService..ctor",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return Reflect.FirstConstructor(Namespace + "PathfindingService");
                },
                Postfix = Reflect.Own(self, nameof(PathfindingServiceCreatedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "NavigationSynchronizer.Tick",
                Required = true,
                Target = () => Reflect.Method(Namespace + "NavigationSynchronizer", "Tick"),
                Postfix = Reflect.Own(self, nameof(NavigationTickedPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        // Every route map is built through here, on the main thread or a worker, so the allocation figure covers
        // all of them. The counter is per thread, hence the interlocked add.
        internal static void Fill(object generator, object graph, Work item)
        {
            long counted = Allocations.Begin();
            try
            {
                _fill(generator, graph, item.Field, item.LimitingField, item.StartNodeId);
            }
            finally
            {
                if (counted >= 0)
                {
                    Interlocked.Add(ref _fillAllocatedBytes, Allocations.End(counted));
                }
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active && _batches == 0)
            {
                return null;
            }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            string left = $"; {_builtDirectly} more built directly on the main thread in batches of fewer than {_minFields}" +
                          Allocations.Describe(Interlocked.Exchange(ref _fillAllocatedBytes, 0)).Replace(", allocating", "; building maps allocated");
            string line = _background
                ? $"RouteMaps: {_batches} background rebuilds of {_fieldsFilled} route maps; main thread spent " +
                  $"{_mainStopwatchTicks * msPerTick:0.0} ms on them, longest single pause " +
                  $"{_longestPauseStopwatchTicks * msPerTick:0.0} ms (built {_builtOnMain} itself, waited for {_waitedOnMain}), " +
                  $"workers were busy {_backgroundStopwatchTicks * msPerTick:0.0} ms alongside the game" + left
                : $"RouteMaps: {_batches} parallel rebuilds of {_fieldsFilled} route maps in " +
                  $"{_wallStopwatchTicks * msPerTick:0.0} ms on {_workerCount} workers" + left;
            _batches = _fieldsFilled = _builtDirectly = _wallStopwatchTicks = 0;
            _mainStopwatchTicks = _backgroundStopwatchTicks = _builtOnMain = _waitedOnMain = 0;
            _longestPauseStopwatchTicks = 0;
            return line;
        }

        // Resolves everything this feature touches. Throws if the game no longer matches, which disables the
        // feature before any patch is applied.
        public static void BindAccessors()
        {
            Type pathfindingService = RequireType("PathfindingService");
            Type roadFlowFieldCache = RequireType("RoadFlowFieldCache");
            Type flowFieldCache = RequireType("FlowFieldCache");
            Type accessFlowField = RequireType("AccessFlowField");
            Type generator = RequireType("RoadFlowFieldGenerator");
            Type districtMap = RequireType("DistrictMap");
            Type binaryHeapFactory = RequireType("BinaryHeapFactory");

            _roadFlowFieldCacheOf = Reflect.FieldGetter<object>(pathfindingService, "_roadFlowFieldCache");
            _roadFlowFieldGeneratorOf = Reflect.FieldGetter<object>(pathfindingService, "_roadFlowFieldGenerator");
            _roadNavMeshGraphOf = Reflect.FieldGetter<object>(pathfindingService, "_roadNavMeshGraph");
            _districtMapOf = Reflect.FieldGetter<object>(pathfindingService, "_districtMap");
            _binaryHeapFactoryOf = Reflect.FieldGetter<object>(generator, "_binaryHeapFactory");
            _innerCacheOf = Reflect.FieldGetter<object>(roadFlowFieldCache, "_flowFields");
            _entriesOf = Reflect.FieldGetter<object>(flowFieldCache, "_flowFields");
            _isFilled = Reflect.PropertyGetter<bool>(accessFlowField, "IsFilled");
            _districtFieldAt = Reflect.InstanceCall<Func<object, int, object>>(
                AccessTools.Method(districtMap, "GetDistrictRoadFlowFieldByRoadNodeId"));
            _fill = Reflect.InstanceCall<FillCall>(AccessTools.Method(generator, "FillFlowField"));
            _hasNode = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(accessFlowField, "HasNode"));
            _isOnNavMesh = Reflect.InstanceCall<Func<object, int, bool>>(
                AccessTools.Method(RequireType("RoadNavMeshGraph"), "IsOnNavMesh"));

            Type cacheEntry = flowFieldCache.GetNestedType("CacheEntry", BindingFlags.NonPublic | BindingFlags.Public);
            if (cacheEntry == null)
            {
                throw new TypeLoadException("FlowFieldCache.CacheEntry");
            }
            _fieldOfEntry = Reflect.PropertyGetter<object>(cacheEntry, "AccessFlowField");
            Type entries = AccessTools.Field(flowFieldCache, "_flowFields").FieldType;
            _entryKeys = entries.GetProperty("Keys");
            _entryValues = entries.GetProperty("Values");
            _generatorConstructor = generator.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { binaryHeapFactory }, null);
            _generatorLoad = AccessTools.Method(generator, "Load");
            if (_entryKeys == null || _entryValues == null || _generatorConstructor == null || _generatorLoad == null)
            {
                throw new MissingMethodException("FlowFieldCache entries or RoadFlowFieldGenerator members");
            }
        }

        // Fills every work item with the game's own generator, spread over the worker generators. Returns the
        // first exception any worker hit, or null. Public to the test harness, which runs it against the real
        // navigation assembly and compares the result with sequential fills.
        public static Exception FillParallel(object graph, IReadOnlyList<Work> work, object[] workerGenerators)
        {
            int workers = Math.Min(workerGenerators.Length, work.Count);
            Exception failure = null;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
            {
                try
                {
                    object generator = workerGenerators[worker];
                    // Strided split: no shared counters, and which worker takes which map is fixed.
                    for (int i = worker; i < work.Count; i += workers)
                    {
                        Fill(generator, graph, work[i]);
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
            });
            return failure;
        }

        public static object[] CreateWorkerGenerators(object binaryHeapFactory, int count)
        {
            object[] generators = new object[count];
            for (int i = 0; i < count; i++)
            {
                generators[i] = _generatorConstructor.Invoke(new[] { binaryHeapFactory });
                _generatorLoad.Invoke(generators[i], null);
            }
            return generators;
        }

        // ReSharper disable InconsistentNaming
        internal static void PathfindingServiceCreatedPostfix(object __instance)
        {
            // A new game scene: forget everything tied to the previous one.
            Land();
            _pathfindingService = __instance;
            _workerGenerators = null;
        }

        internal static void NavigationTickedPostfix()
        {
            if (!_active || _pathfindingService == null)
            {
                return;
            }
            try
            {
                BuildUnbuiltMaps();
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
            finally
            {
                WorkScratch.Clear();
            }
        }
        // ReSharper restore InconsistentNaming

        private static void BuildUnbuiltMaps()
        {
            object roadCache = _roadFlowFieldCacheOf(_pathfindingService);
            object districtMap = _districtMapOf(_pathfindingService);
            object graph = _roadNavMeshGraphOf(_pathfindingService);

            // Every cached map that is not built and can be. Keys and Values of a Dictionary enumerate in the same
            // order, so walking them together pairs each start tile with its map without boxing an entry per map.
            // Main thread only: the district lookup recalculates district maps on demand.
            WorkScratch.Clear();
            object entries = _entriesOf(_innerCacheOf(roadCache));
            IEnumerator<int> keys = ((IEnumerable<int>)_entryKeys.GetValue(entries)).GetEnumerator();
            IEnumerator values = ((IEnumerable)_entryValues.GetValue(entries)).GetEnumerator();
            while (keys.MoveNext() && values.MoveNext())
            {
                object field = _fieldOfEntry(values.Current);
                if (_isFilled(field))
                {
                    continue;
                }
                int nodeId = keys.Current;
                object limitingField = _districtFieldAt(districtMap, nodeId);
                // The conditions under which the game's generator fills a map at all. A map that fails them (its
                // building is not on a road, or not in a district) stays unbuilt in the unmodded game too, and
                // checking here keeps it from being retried as a batch on every tick.
                if (limitingField != null && _isFilled(limitingField) && _hasNode(limitingField, nodeId) &&
                    _isOnNavMesh(graph, nodeId))
                {
                    WorkScratch.Add(new Work { Field = field, LimitingField = limitingField, StartNodeId = nodeId });
                }
            }
            if (WorkScratch.Count == 0)
            {
                return;
            }
            if (_workerGenerators == null)
            {
                object binaryHeapFactory = _binaryHeapFactoryOf(_roadFlowFieldGeneratorOf(_pathfindingService));
                // One per worker, plus one for the main thread when it builds a map itself.
                _workerGenerators = CreateWorkerGenerators(binaryHeapFactory, _workerCount + 1);
            }
            if (WorkScratch.Count < _minFields)
            {
                // A new building or two: starting workers costs more than building these here. They are still
                // built now, not left for later, so every peer ends the tick with the same maps built.
                object mainGenerator = _workerGenerators[_workerGenerators.Length - 1];
                for (int i = 0; i < WorkScratch.Count; i++)
                {
                    Fill(mainGenerator, graph, WorkScratch[i]);
                }
                _builtDirectly += WorkScratch.Count;
                return;
            }
            if (_background)
            {
                BeginBackground(graph, districtMap, WorkScratch, _workerGenerators);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            Exception failure = FillParallel(graph, WorkScratch, _workerGenerators);
            _wallStopwatchTicks += Stopwatch.GetTimestamp() - started;
            _batches++;
            _fieldsFilled += WorkScratch.Count;
            if (failure != null)
            {
                // A map that was not finished is simply not marked as filled; the game builds it on demand.
                Disable(failure);
            }
        }

        private static Type RequireType(string name)
        {
            Type type = Reflect.GameType(Namespace + name);
            if (type == null)
            {
                throw new TypeLoadException(Namespace + name);
            }
            return type;
        }

        private static void Disable(Exception exception)
        {
            _active = false;
            if (_inFlight)
            {
                // Let the workers finish what they hold so nothing is written after this point.
                Flight flight = _flight;
                _inFlight = false;
                _flight = null;
                try
                {
                    Task.WaitAll(flight.Tasks);
                }
                catch (Exception)
                {
                    // Workers never throw; nothing more can be done here either way.
                }
            }
            Log.Warning("RouteMaps failed and turned itself off for this session: " + exception);
        }
    }
}
