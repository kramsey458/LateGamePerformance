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
    // Here: the maps that were in use and just got thrown away are rebuilt straight away, spread over worker
    // threads, and the main thread waits for all of them before the tick continues.
    //
    // Why this is safe:
    //  - Regular road changes are only applied inside NavigationSynchronizer.Tick. This runs in that method's
    //    postfix, so nothing changes the road graph or the district maps while workers read them, and every map
    //    is complete before any other game code runs.
    //  - A map depends only on the road graph, its start tile and its district's map. Each worker runs the
    //    game's own RoadFlowFieldGenerator (a private instance per worker, because the generator keeps scratch
    //    state), so a rebuilt map has exactly the contents and node order the game would have produced.
    //  - Each map is written by exactly one worker. The road graph and district maps are only read.
    //  - Which maps get rebuilt is decided from game state alone, never from timing or thread count, so every
    //    multiplayer peer ends up with the same maps filled.
    //
    // Difference from the unmodded game: a map is now filled earlier than the first request for it. A few code
    // paths only use a map "if it is already filled", so they can take the cached route where the unmodded game
    // would have searched again. Every peer therefore needs the same RouteMaps settings.
    internal static class RouteMaps
    {
        public struct Work
        {
            public object Field;
            public object LimitingField;
            public int StartNodeId;
        }

        private delegate void FillCall(object generator, object graph, object field, object limitingField, int startNodeId);

        private const string Namespace = "Timberborn.Navigation.";

        private static readonly List<KeyValuePair<int, object>> FilledBeforeUpdate = new List<KeyValuePair<int, object>>();
        private static readonly List<KeyValuePair<int, object>> Pending = new List<KeyValuePair<int, object>>();
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
        private static Func<object, int, object> _cachedFieldAt;
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
        private static long _skippedSmall;
        private static long _wallStopwatchTicks;

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
                Name = "RoadFlowFieldCache.OnNavMeshUpdated",
                Required = true,
                Target = () => Reflect.Method(Namespace + "RoadFlowFieldCache", "OnNavMeshUpdated"),
                Prefix = Reflect.Own(self, nameof(RoadsChangingPrefix)),
                Postfix = Reflect.Own(self, nameof(RoadsChangedPostfix))
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

        public static string TakeStatsLine()
        {
            if (!_active && _batches == 0)
            {
                return null;
            }
            double wallMs = _wallStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line =
                $"RouteMaps: {_batches} parallel rebuilds of {_fieldsFilled} route maps in {wallMs:0.0} ms " +
                $"on {_workerCount} workers; {_skippedSmall} road changes left to the game (fewer than {_minFields} maps)";
            _batches = _fieldsFilled = _skippedSmall = _wallStopwatchTicks = 0;
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

            _cachedFieldAt = CompileCachedFieldAt(roadFlowFieldCache, accessFlowField);
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
                        Work item = work[i];
                        _fill(generator, graph, item.Field, item.LimitingField, item.StartNodeId);
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
            _pathfindingService = __instance;
            _workerGenerators = null;
            FilledBeforeUpdate.Clear();
            Pending.Clear();
        }

        internal static void RoadsChangingPrefix(object __instance)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                FilledBeforeUpdate.Clear();
                // Keys and Values of a Dictionary enumerate in the same order, so walking them together pairs
                // each start tile with its map without boxing an entry per map.
                object entries = _entriesOf(_innerCacheOf(__instance));
                IEnumerator<int> keys = ((IEnumerable<int>)_entryKeys.GetValue(entries)).GetEnumerator();
                IEnumerator values = ((IEnumerable)_entryValues.GetValue(entries)).GetEnumerator();
                while (keys.MoveNext() && values.MoveNext())
                {
                    object field = _fieldOfEntry(values.Current);
                    if (_isFilled(field))
                    {
                        FilledBeforeUpdate.Add(new KeyValuePair<int, object>(keys.Current, field));
                    }
                }
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        internal static void RoadsChangedPostfix()
        {
            if (!_active)
            {
                return;
            }
            try
            {
                // In use before the change and thrown away by it: these are the maps worth rebuilding now.
                for (int i = 0; i < FilledBeforeUpdate.Count; i++)
                {
                    if (!_isFilled(FilledBeforeUpdate[i].Value))
                    {
                        Pending.Add(FilledBeforeUpdate[i]);
                    }
                }
                FilledBeforeUpdate.Clear();
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        internal static void NavigationTickedPostfix()
        {
            if (!_active || Pending.Count == 0)
            {
                return;
            }
            try
            {
                RebuildPending();
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
            finally
            {
                Pending.Clear();
                WorkScratch.Clear();
            }
        }
        // ReSharper restore InconsistentNaming

        private static void RebuildPending()
        {
            if (_pathfindingService == null)
            {
                return;
            }
            if (Pending.Count < _minFields)
            {
                _skippedSmall++;
                return;
            }
            object roadCache = _roadFlowFieldCacheOf(_pathfindingService);
            object districtMap = _districtMapOf(_pathfindingService);
            object graph = _roadNavMeshGraphOf(_pathfindingService);

            // Main thread only: the district lookup recalculates district maps on demand.
            WorkScratch.Clear();
            for (int i = 0; i < Pending.Count; i++)
            {
                int nodeId = Pending[i].Key;
                object field = Pending[i].Value;
                // Skip maps that were dropped from the cache or already rebuilt since the change.
                if (!ReferenceEquals(_cachedFieldAt(roadCache, nodeId), field) || _isFilled(field))
                {
                    continue;
                }
                object limitingField = _districtFieldAt(districtMap, nodeId);
                // Same condition the game uses before filling on demand (PathfindingService.TryFillRoadFlowField).
                if (limitingField != null && _isFilled(limitingField))
                {
                    WorkScratch.Add(new Work { Field = field, LimitingField = limitingField, StartNodeId = nodeId });
                }
            }
            if (WorkScratch.Count < _minFields)
            {
                _skippedSmall++;
                return;
            }
            if (_workerGenerators == null)
            {
                object binaryHeapFactory = _binaryHeapFactoryOf(_roadFlowFieldGeneratorOf(_pathfindingService));
                _workerGenerators = CreateWorkerGenerators(binaryHeapFactory, _workerCount);
            }

            long started = Stopwatch.GetTimestamp();
            Exception failure = FillParallel(graph, WorkScratch, _workerGenerators);
            _wallStopwatchTicks += Stopwatch.GetTimestamp() - started;
            _batches++;
            _fieldsFilled += WorkScratch.Count;
            if (failure != null)
            {
                // A map that was not finished is simply not marked as filled; the game rebuilds it on demand.
                Disable(failure);
            }
        }

        // (cache, nodeId) => cache.TryGetFlowFieldAtNode(nodeId, out field) ? field : null
        private static Func<object, int, object> CompileCachedFieldAt(Type roadFlowFieldCache, Type accessFlowField)
        {
            MethodInfo tryGet = AccessTools.Method(roadFlowFieldCache, "TryGetFlowFieldAtNode");
            if (tryGet == null)
            {
                throw new MissingMethodException("RoadFlowFieldCache.TryGetFlowFieldAtNode");
            }
            ParameterExpression cache = Expression.Parameter(typeof(object), "cache");
            ParameterExpression nodeId = Expression.Parameter(typeof(int), "nodeId");
            ParameterExpression field = Expression.Variable(accessFlowField, "field");
            Expression body = Expression.Block(
                new[] { field },
                Expression.Call(Expression.Convert(cache, roadFlowFieldCache), tryGet, nodeId, field),
                Expression.Convert(field, typeof(object)));
            return Expression.Lambda<Func<object, int, object>>(body, cache, nodeId).Compile();
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
            FilledBeforeUpdate.Clear();
            Pending.Clear();
            Log.Warning("RouteMaps failed and turned itself off for this session: " + exception);
        }
    }
}
