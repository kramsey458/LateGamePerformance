using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace LateGamePerformance
{
    // Every building that works the land around it (lumberjack flag, gatherer, farmhouse, forester...) keeps a
    // second route map next to its road one: the walking distance over open ground from its entrance to every
    // tile within 20 steps (TerrainFlowFieldCache). Every search for a tree or a plant reads it. When the ground
    // changes anywhere inside one, the game throws that map away and rebuilds it on the main thread the next time
    // its building searches, one building at a time.
    //
    // Here: at the end of every navigation tick, every terrain map that is cached and not built gets built, with
    // the game's own generator, one private instance per worker thread, and the main thread waits for the batch.
    // It is a few tens of maps of at most a few thousand tiles, so waiting costs little and needs none of the
    // gates the road maps' background rebuild has. If the stats line ever shows this pause matter, the same
    // background scheme can be put under it.
    //
    // Why it is safe, and the same for every multiplayer peer:
    //  - Ground changes are applied inside NavigationSynchronizer.Tick. This runs in that method's postfix, and the
    //    main thread is inside this call until every worker is done, so nothing changes the terrain graph while
    //    it is read.
    //  - A map depends only on the terrain graph, its start tile and the range. The range is the game's own
    //    (NavigationDistance.ResourceBuildings, read from the game's NavigationService), and it is the only range
    //    the game ever fills these maps with. Each map is written by exactly one thread.
    //  - Which maps are built is simulation state: a terrain map is cached exactly while its building is finished,
    //    and unlike road maps nothing a player looks at fills one (the range overlay uses a map of its own). After
    //    this runs every peer has the same maps built, whatever the thread count.
    //
    // Difference from the unmodded game, the same one the road maps have: maps are built before the first request
    // instead of on it, so the few code paths that use a terrain map only "if it is already filled" find it
    // filled. Every player on the same version of this mod gets the same, which is why this is not a setting.
    internal static class TerrainMaps
    {
        public struct Work
        {
            public object Field;
            public int StartNodeId;
        }

        private delegate void FillCall(object generator, object graph, object field, float maxDistance, int startNodeId);

        private const string Namespace = "Timberborn.Navigation.";
        // Fewer than this are built on the main thread: starting workers costs more.
        private const int MinMapsForWorkers = 4;

        private static readonly List<Work> WorkScratch = new List<Work>();

        private static Func<object, object> _cacheOf;
        private static Func<object, object> _generatorOf;
        private static Func<object, object> _graphOf;
        private static Func<object, object> _innerCacheOf;
        private static Func<object, object> _entriesOf;
        private static Func<object, object> _fieldOfEntry;
        private static Func<object, object> _heapFactoryOf;
        private static Func<object, object> _groupServiceOf;
        private static Func<object, object> _navigationDistanceOf;
        private static Func<object, float> _resourceBuildingsOf;
        private static Func<object, bool> _isFilled;
        private static Func<object, int, bool> _isOnNavMesh;
        private static PropertyInfo _entryKeys;
        private static PropertyInfo _entryValues;
        private static ConstructorInfo _generatorConstructor;
        private static MethodInfo _generatorLoad;
        private static FillCall _fill;

        private static Func<object, int> _numberOfNodes;

        private static bool _active;
        private static int _workerCount;
        private static object _pathfindingService;
        private static object[] _workerGenerators;
        private static object _mainGenerator;
        private static float _maxDistance;
        // Scanning the cache only after something could have changed (MapChanges), with a full scan every so many
        // ticks that reports anything the hooks missed.
        internal static bool ScanOnlyWhenChanged;
        private const int SelfCheckEveryTicks = 200;
        private static bool _changed = true;
        private static int _ticksSinceScan;
        private static long _scans;
        private static long _scansSkipped;
        private static long _missedByHooks;
        private static int _cachedMaps;
        private static int _largestMapNodes;

        private static long _batches;
        private static long _mapsBuilt;
        private static long _builtDirectly;
        private static long _stopwatchTicks;
        private static long _longestStopwatchTicks;

        public static bool IsActive => _active;

        internal static object PathfindingServiceInstance => _pathfindingService;

        internal static void MarkChanged()
        {
            _changed = true;
        }

        internal static void SetRangeForTests(float range)
        {
            _maxDistance = range;
        }

        public static Feature CreateFeature(Config config)
        {
            _workerCount = config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
            Type self = typeof(TerrainMaps);
            Feature feature = new Feature { Name = "TerrainMaps" };
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
                Name = "NavigationService..ctor",
                Required = true,
                Target = () => Reflect.FirstConstructor(Namespace + "NavigationService"),
                Postfix = Reflect.Own(self, nameof(NavigationServiceCreatedPostfix))
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
            if (!_active && _batches == 0 && _builtDirectly == 0)
            {
                return null;
            }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "TerrainMaps: {0} rebuilds of {1} terrain route maps on {2} workers; the main thread waited {3:0.0} ms " +
                "in total, longest {4:0.0} ms; {5} more built directly in batches of fewer than {6}; {7} maps cached, the " +
                "largest {8} tiles; the cache was scanned {9} times and left alone {10} times because nothing had changed, " +
                "{11} unbuilt maps were found by the periodic check alone",
                _batches, _mapsBuilt, _workerCount, _stopwatchTicks * msPerTick, _longestStopwatchTicks * msPerTick,
                _builtDirectly, MinMapsForWorkers, _cachedMaps, _largestMapNodes, _scans, _scansSkipped, _missedByHooks);
            _batches = _mapsBuilt = _builtDirectly = _stopwatchTicks = _longestStopwatchTicks = 0;
            _scans = _scansSkipped = _missedByHooks = 0;
            return line;
        }

        // Resolves everything this feature touches. Throws if the game no longer matches, which disables the
        // feature before any patch is applied.
        public static void BindAccessors()
        {
            Type pathfindingService = RequireType("PathfindingService");
            Type terrainCache = RequireType("TerrainFlowFieldCache");
            Type flowFieldCache = RequireType("FlowFieldCache");
            Type accessFlowField = RequireType("AccessFlowField");
            Type generator = RequireType("TerrainFlowFieldGenerator");
            Type graph = RequireType("TerrainNavMeshGraph");
            Type navigationService = RequireType("NavigationService");
            Type navigationDistance = RequireType("NavigationDistance");

            _cacheOf = Reflect.FieldGetter<object>(pathfindingService, "_terrainFlowFieldCache");
            _generatorOf = Reflect.FieldGetter<object>(pathfindingService, "_terrainFlowFieldGenerator");
            _graphOf = Reflect.FieldGetter<object>(pathfindingService, "_terrainNavMeshGraph");
            _innerCacheOf = Reflect.FieldGetter<object>(terrainCache, "_flowFields");
            _entriesOf = Reflect.FieldGetter<object>(flowFieldCache, "_flowFields");
            _heapFactoryOf = Reflect.FieldGetter<object>(generator, "_binaryHeapFactory");
            _groupServiceOf = Reflect.FieldGetter<object>(generator, "_navMeshGroupService");
            _navigationDistanceOf = Reflect.FieldGetter<object>(navigationService, "_navigationDistance");
            _resourceBuildingsOf = Reflect.PropertyGetter<float>(navigationDistance, "ResourceBuildings");
            _isFilled = Reflect.PropertyGetter<bool>(accessFlowField, "IsFilled");
            _numberOfNodes = Reflect.PropertyGetter<int>(accessFlowField, "NumberOfNodes");
            _isOnNavMesh = Reflect.InstanceCall<Func<object, int, bool>>(AccessTools.Method(graph, "IsOnNavMesh"));
            _fill = Reflect.InstanceCall<FillCall>(AccessTools.Method(generator, "FillFlowFieldUpToDistance"));

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
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { RequireType("BinaryHeapFactory"), RequireType("NavMeshGroupService") }, null);
            _generatorLoad = AccessTools.Method(generator, "Load");
            if (_entryKeys == null || _entryValues == null || _generatorConstructor == null || _generatorLoad == null)
            {
                throw new MissingMethodException("FlowFieldCache entries or TerrainFlowFieldGenerator members");
            }
        }

        // Fills every work item with the game's own generator, spread over the worker generators. Returns the
        // first exception any worker hit, or null. Public to the test harness, which runs it against the real
        // navigation assembly and compares the result with one-by-one fills.
        public static Exception FillParallel(object graph, IReadOnlyList<Work> work, object[] workerGenerators, float maxDistance)
        {
            int workers = Math.Min(workerGenerators.Length, work.Count);
            // This mod's own worker threads; strided split: no shared counters, one generator per worker.
            return TickWorkers.Run(workers, (worker, sharing) =>
            {
                object generator = workerGenerators[worker];
                for (int i = worker; i < work.Count; i += sharing)
                {
                    _fill(generator, graph, work[i].Field, maxDistance, work[i].StartNodeId);
                }
            });
        }

        public static object[] CreateWorkerGenerators(object binaryHeapFactory, object navMeshGroupService, int count)
        {
            object[] generators = new object[count];
            for (int i = 0; i < count; i++)
            {
                generators[i] = _generatorConstructor.Invoke(new[] { binaryHeapFactory, navMeshGroupService });
                _generatorLoad.Invoke(generators[i], null);
            }
            return generators;
        }

        // ReSharper disable InconsistentNaming
        internal static void PathfindingServiceCreatedPostfix(object __instance)
        {
            // A new game scene: forget everything tied to the previous one.
            _changed = true;
            _pathfindingService = __instance;
            _workerGenerators = null;
            _mainGenerator = null;
            _maxDistance = 0;
        }

        internal static void NavigationServiceCreatedPostfix(object __instance)
        {
            try
            {
                _maxDistance = _resourceBuildingsOf(_navigationDistanceOf(__instance));
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        internal static void NavigationTickedPostfix()
        {
            // Without the game's range nothing is built, and the game builds on demand as always.
            if (!_active || _pathfindingService == null || _maxDistance <= 0)
            {
                return;
            }
            bool selfCheck = false;
            if (ScanOnlyWhenChanged && !_changed)
            {
                if (++_ticksSinceScan < SelfCheckEveryTicks)
                {
                    _scansSkipped++;
                    return;
                }
                selfCheck = true;
            }
            _changed = false;
            _ticksSinceScan = 0;
            try
            {
                int found = BuildUnbuiltMaps(_pathfindingService, _maxDistance);
                _scans++;
                if (selfCheck)
                {
                    _missedByHooks += found;
                }
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

        // Internal so the test harness can drive it with a stand-in for the pathfinding service.
        internal static int BuildUnbuiltMaps(object pathfindingService, float maxDistance)
        {
            object graph = _graphOf(pathfindingService);

            // Every cached map that is not built and can be. Keys and Values of a Dictionary enumerate in the same
            // order, so walking them together pairs each start tile with its map.
            WorkScratch.Clear();
            object entries = _entriesOf(_innerCacheOf(_cacheOf(pathfindingService)));
            IEnumerator<int> keys = ((IEnumerable<int>)_entryKeys.GetValue(entries)).GetEnumerator();
            IEnumerator values = ((IEnumerable)_entryValues.GetValue(entries)).GetEnumerator();
            int cached = 0, largest = 0;
            while (keys.MoveNext() && values.MoveNext())
            {
                object field = _fieldOfEntry(values.Current);
                cached++;
                if (_isFilled(field))
                {
                    int nodes = _numberOfNodes(field);
                    if (nodes > largest)
                    {
                        largest = nodes;
                    }
                    continue;
                }
                // The game's generator leaves a map unbuilt when its start tile is not on the terrain graph, so
                // such a map is not retried on every tick here either.
                if (_isOnNavMesh(graph, keys.Current))
                {
                    WorkScratch.Add(new Work { Field = field, StartNodeId = keys.Current });
                }
            }
            _cachedMaps = cached;
            _largestMapNodes = largest;
            int found = WorkScratch.Count;
            if (found == 0)
            {
                return 0;
            }
            if (_workerGenerators == null)
            {
                object gameGenerator = _generatorOf(pathfindingService);
                // One per worker, plus one for the main thread when it builds a small batch itself. The game's own
                // generator is never used: its scratch state belongs to the game's on-demand fills.
                object heapFactory = _heapFactoryOf(gameGenerator);
                object groupService = _groupServiceOf(gameGenerator);
                _workerGenerators = CreateWorkerGenerators(heapFactory, groupService, _workerCount);
                _mainGenerator = CreateWorkerGenerators(heapFactory, groupService, 1)[0];
            }
            long started = Stopwatch.GetTimestamp();
            if (WorkScratch.Count < MinMapsForWorkers)
            {
                for (int i = 0; i < WorkScratch.Count; i++)
                {
                    _fill(_mainGenerator, graph, WorkScratch[i].Field, maxDistance, WorkScratch[i].StartNodeId);
                }
                _builtDirectly += WorkScratch.Count;
            }
            else
            {
                Exception failure = FillParallel(graph, WorkScratch, _workerGenerators, maxDistance);
                _batches++;
                _mapsBuilt += WorkScratch.Count;
                if (failure != null)
                {
                    // A map that was not finished is not marked as filled; the game builds it on demand.
                    throw failure;
                }
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            _stopwatchTicks += elapsed;
            _longestStopwatchTicks = Math.Max(_longestStopwatchTicks, elapsed);
            return found;
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
            Log.Warning("TerrainMaps failed and turned itself off for this session: " + exception);
        }
    }
}
