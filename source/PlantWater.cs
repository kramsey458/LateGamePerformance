using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;

namespace LateGamePerformance
{
    // Reads that may run on any thread, results that are used in the original order on the calling thread.
    // Free of game types so the tests can drive it.
    internal static class OrderedParallel
    {
        // results[i] = read(items[i]) for every i below count. Which worker reads which item is fixed (strided),
        // and no worker touches anything but its own result slots. Returns the first exception a worker hit, or
        // null; it never throws.
        public static Exception Read<T>(IList<T> items, int count, int[] results, Func<T, int> read, int workers)
        {
            workers = Math.Max(1, Math.Min(workers, count));
            return TickWorkers.Run(workers, (worker, sharing) =>
            {
                for (int i = worker; i < count; i += sharing)
                {
                    results[i] = read(items[i]);
                }
            });
        }
    }

    // Every tick the game asks, for every plant and every other object that cares about flooding (about 8000 in a
    // late game colony), how high the water stands at its tile (WaterObjectService.Tick -> WaterObject
    // .UpdateWaterAboveBase), one after another on the main thread: 1.3 ms per tick, 8% of all tick time, in the
    // colony this was measured in. Almost none of them change from one tick to the next.
    //
    // The question is a pure read: the object's fixed tile, looked up in the water map the game keeps for readers
    // on other threads (ThreadSafeWaterMap, only rewritten in its own tick, never during this one). Since 0.4.23
    // the answers are computed where that map is made: WaterMapCopy fills the next tick's copy of the water map
    // on the water worker thread, right after the last water task, and this feature reads every object's level
    // from that copy on the same thread, before the game swaps it in. When the tick then asks, the levels are
    // already there, and the main thread only goes through them in the game's own order and, for each object
    // whose level changed, does exactly what the game does: stores the new level and raises
    // WaterAboveBaseChanged. Those handlers (a plant starting to drown, a building flooding) run on the main
    // thread, in the same order, with the same values as without the mod.
    //
    // The worker computes the levels for a snapshot of the game's list taken before the water tasks start. By the
    // time the tick reads them, objects may have left the list (a tree cut) or joined it (a crop planted), which
    // happens in about a third of the ticks in a large colony. The list only ever changes by adding at the end and
    // removing one object, so the objects that stayed keep their order: the tick walks the game's list and the
    // snapshot side by side, takes the worker's level for every object found in the snapshot, and reads the few
    // that were not (the new ones) with the game's own method on the main thread. Exact: every object's level is
    // either precomputed from the very copy the tick reads, or read now. (0.4.23 fell back to reading everything
    // inside the tick whenever the list had changed.)
    //
    // Why reading first gives the same values: a handler cannot change the water map (only its tick does) or an
    // object's tile, and it cannot add or remove an object from the list, because the game walks that list with
    // foreach and would throw if one did. So what object N reads does not depend on what happened for objects
    // before it. The result is the same on every computer whatever the thread count.
    //
    // The levels from the worker are used only while the map's current arrays are exactly the copy they were
    // computed from (WaterMapCopy swapped it in and nothing has rewritten it since) and the worker finished;
    // otherwise the tick reads the levels itself on this mod's worker threads, as 0.4.12 did.
    //
    // Since 0.4.28 the main thread no longer reads every object's stored level (WaterAboveBase, one object after
    // another all over memory) to find the few that changed. It keeps the level each object was left at by this
    // mod's last pass, in an int array index-aligned with the game's list as it was then, and compares the new
    // levels with that array. Only an object whose new level differs from its known one, or whose known one is
    // missing, is looked at, in list order, and treated exactly as the game's loop treats it: its stored level is
    // read at that moment and, if the new level differs, the game's own UpdateWaterAboveBase(int) stores it and
    // raises the event. Exact because the stored level has one writer, the private UpdateWaterAboveBase(int), and
    // it is reached from two places only: WaterObjectService.Tick (through the public UpdateWaterAboveBase(),
    // which stores what the map shows at that moment) and registration (OnEnterFinishedPostLoadState, which stores
    // what the map shows at that moment). So an object passed over has a new level equal to the level this mod
    // left it at, and its stored level is still that one, unless:
    //   - it registered since the last pass: the objects that registered since are at the end of the game's list
    //     (the list only appends), so the last N entries, N the registrations seen since, are never known;
    //   - the game's own loop ran in between (fewer than 512 objects, a failure, another mod's prefix skipping this
    //     one, which a postfix notices as a call this mod did not handle): every known level is forgotten and the
    //     next pass reads them all from the objects, as 0.4.27 did every pass;
    //   - an earlier handler in the same pass called the public UpdateWaterAboveBase() on it: that stores what the
    //     map shows, which is the new level this pass computed, so the game's loop would find it equal and so does
    //     this one (the stored level is read before anything is changed).
    // The array follows the game's list: while the list's own change counter has not moved since the last pass it
    // is used as it is; otherwise the list and the list as it was are walked side by side (objects that stay keep
    // their order), every object found keeps its known level and any other is read from the object. In a large
    // colony 0 to 146 levels change per 1000 ticks, so a pass reads a few objects instead of 6,800.
    //
    // PlantWaterVerify reads everything again on the main thread with the game's own method and compares, and also
    // compares every known level with the object's stored level; the levels read ahead are stored all the same, and
    // an object whose known level is wrong is still passed over. If anything throws, the feature switches itself off
    // and the game's own loop runs; nothing has been changed at that point, or what was changed is what the game's
    // loop would have changed first, so it simply carries on.
    internal static class PlantWater
    {
        // Below this many objects the game's own loop is as fast as anything else.
        private const int MinObjects = 512;
        private const int SnapshotBuffers = 4;
        private const int BatchBuffers = 3;

        // The game's list at one moment, with each object's tile: what a worker computes levels for. A buffer is
        // rewritten only when the list changed, at most once per tick, and the ring is four deep, while a batch is
        // read within two ticks of being made; every batch also remembers the version it was made for.
        private sealed class Snapshot
        {
            public WaterObject[] Objects = new WaterObject[0];
            public Vector3Int[] Tiles = new Vector3Int[0];
            public int Count;
            public long Version = -1;
        }

        // One tick's levels, computed on the water worker for one snapshot against the copy the game will swap in.
        private sealed class Batch
        {
            public Snapshot Snapshot;
            public long Version;
            public int[] Levels = new int[0];
            public int Count;
            public volatile bool Done;
        }

        private static Func<object, List<WaterObject>> _objectsOf;
        private static Func<object, WaterObject[]> _itemsOf;
        private static Func<WaterObject, int> _currentLevel;
        private static Action<WaterObject, int> _setLevel;
        private static Func<object, Vector3Int> _tileOf;
        private static Func<object, int> _versionOf;

        private static int[] _levels = new int[0];

        // What this mod's last pass left: the game's list as it was (object for object) and each object's level,
        // Unknown where the object has to be asked. Only the main thread touches these.
        private const int Unknown = int.MinValue;
        private static int[] _known = new int[0];
        private static int[] _knownSpare = new int[0];
        private static WaterObject[] _knownObjects = new WaterObject[0];
        private static int _knownCount;
        private static List<WaterObject> _knownList;
        private static int _knownVersion;
        private static int _registeredSincePass;
        private static int[] _candidates = new int[0];
        // Calls of the game's Tick this mod handled, and calls its postfix saw; they differ when anything else ran.
        private static long _handledCalls;
        private static long _seenCalls;
        private static bool _active;
        private static bool _verify;
        private static bool _usingCopy;
        private static int _workers;

        // The mirror of the game's list. Only the main thread touches it.
        private static readonly List<WaterObject> Mirror = new List<WaterObject>();
        private static readonly List<Vector3Int> MirrorTiles = new List<Vector3Int>();
        private static long _mirrorVersion;
        private static readonly Snapshot[] Snapshots = { new Snapshot(), new Snapshot(), new Snapshot(), new Snapshot() };
        private static int _snapshotCursor;
        private static readonly Batch[] Batches = { new Batch(), new Batch(), new Batch() };
        private static int _batchCursor;

        private static long _passes;
        private static long _objects;
        private static long _changes;
        private static long _fromWorker;
        private static long _fromWorkerObjects;
        private static long _directObjects;
        private static long _inTick;
        private static long _noCopy;
        private static long _listChanged;
        private static long _resyncs;
        private static long _workerStopwatchTicks;
        private static long _readStopwatchTicks;
        private static long _matchStopwatchTicks;
        private static long _compareStopwatchTicks;
        private static long _applyStopwatchTicks;
        private static long _mainStopwatchTicks;
        private static long _readFromObjects;
        private static long _realigned;
        private static long _forgotten;
        private static long _verifyMismatches;
        private static long _knownMismatches;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        // For the tests: verify's count of known levels that differed from the stored ones, over the session.
        internal static long KnownMismatches => _knownMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.PlantWaterVerify;
            _workers = config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
            Type self = typeof(PlantWater);
            Feature feature = new Feature { Name = "PlantWater" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterObjectService.Tick",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return HarmonyLib.AccessTools.Method(typeof(WaterObjectService), "Tick");
                },
                Prefix = Reflect.Own(self, nameof(TickPrefix)),
                // Runs whether or not the prefix did (a skipped original still gets its postfixes): a call the prefix
                // did not handle means the game's own loop, or another mod, may have changed levels behind the known
                // ones, so they are forgotten.
                Postfix = Reflect.Own(self, nameof(TickPostfix))
            });
            // The mirror's hooks. Without them the snapshot never matches the game's list, and every object is read
            // on the main thread; the tick notices that and rebuilds the mirror, so the result is still the game's.
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterObjectService.RegisterWaterObject",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(WaterObjectService), "RegisterWaterObject"),
                Postfix = Reflect.Own(self, nameof(RegisterPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterObjectService.UnregisterWaterObject",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(WaterObjectService), "UnregisterWaterObject"),
                Postfix = Reflect.Own(self, nameof(UnregisterPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            SceneCreated();
            _active = true;
        }

        // Once both this and WaterMapCopy run: the levels are computed on the water worker right after the copy.
        public static void UseWaterMapCopy()
        {
            WaterMapCopy.JobCreated = OnJobCreated;
            WaterMapCopy.AfterCopy = OnCopied;
            _usingCopy = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            Mirror.Clear();
            MirrorTiles.Clear();
            _mirrorVersion = 0;
            foreach (Snapshot snapshot in Snapshots)
            {
                Array.Clear(snapshot.Objects, 0, snapshot.Objects.Length);
                snapshot.Count = 0;
                snapshot.Version = -1;
            }
            foreach (Batch batch in Batches)
            {
                batch.Snapshot = null;
                batch.Count = 0;
                batch.Done = false;
            }
            _knownList = null;
            _knownCount = 0;
            _registeredSincePass = 0;
            Array.Clear(_knownObjects, 0, _knownObjects.Length);
            _handledCalls = _seenCalls = 0;
        }

        // For the tests, which cannot set the .cfg value before the feature exists.
        internal static void SetVerifyForTests(bool verify) => _verify = verify;

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            Type service = typeof(WaterObjectService);
            Type waterObject = typeof(WaterObject);
            _objectsOf = Reflect.FieldGetter<List<WaterObject>>(service, "_waterObjects");

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo baseCoordinates = waterObject.GetField("_baseCoordinates", any);
            MethodInfo currentLevel = null, setLevel = null;
            foreach (MethodInfo method in waterObject.GetMethods(any))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "CurrentWaterAboveBase" && parameters.Length == 1 && method.ReturnType == typeof(int))
                {
                    currentLevel = method;
                }
                else if (method.Name == "UpdateWaterAboveBase" && parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    setLevel = method;
                }
            }
            if (baseCoordinates == null || baseCoordinates.FieldType != typeof(Vector3Int) || currentLevel == null ||
                setLevel == null || currentLevel.GetParameters()[0].ParameterType != baseCoordinates.FieldType ||
                waterObject.GetProperty("WaterAboveBase") == null)
            {
                throw new MissingMemberException("WaterObject", "_baseCoordinates / CurrentWaterAboveBase / UpdateWaterAboveBase");
            }

            // o => o.CurrentWaterAboveBase(o._baseCoordinates): the game's own question, asked the game's own way.
            ParameterExpression o = Expression.Parameter(waterObject, "o");
            _currentLevel = Expression.Lambda<Func<WaterObject, int>>(
                Expression.Call(o, currentLevel, Expression.Field(o, baseCoordinates)), o).Compile();
            // (o, level) => o.UpdateWaterAboveBase(level): stores the level and raises the event.
            ParameterExpression level = Expression.Parameter(typeof(int), "level");
            _setLevel = Expression.Lambda<Action<WaterObject, int>>(Expression.Call(o, setLevel, level), o, level).Compile();
            _tileOf = Reflect.FieldGetter<Vector3Int>(waterObject, "_baseCoordinates");
            // The list's own array, read once per tick instead of through the indexer per object. Optional: a
            // runtime whose List<T> has no such field goes through the indexer.
            try
            {
                _itemsOf = Reflect.FieldGetter<WaterObject[]>(typeof(List<WaterObject>), "_items");
            }
            catch (Exception)
            {
                _itemsOf = null;
            }
            // The list's own change counter, which the runtime moves on every addition and removal: while it has not
            // moved since the last pass, the known levels are aligned as they are. Optional: without it every pass
            // lines them up with the list again.
            try
            {
                _versionOf = Reflect.FieldGetter<int>(typeof(List<WaterObject>), "_version");
            }
            catch (Exception)
            {
                _versionOf = null;
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active && _passes == 0)
            {
                return null;
            }
            double ms = 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "PlantWater: {0} passes over {1} objects; {2} used the water worker's levels ({3:0.000} ms there per pass; " +
                "{4} objects matched, {5} that joined the list after the snapshot were read on the main thread; matching " +
                "{6:0.000} ms per pass), {7} read every level inside the tick on {8} workers ({9:0.000} ms per pass), {10} had " +
                "no copy to use; the list changed in {11} ticks, the mirror was rebuilt {12} times; {13} level changes applied; " +
                "main thread {15:0.000} ms per pass, of which comparing with the levels known from the last pass {14:0.000} ms " +
                "and applying {17:0.000} ms; {18} stored levels read from the objects themselves (new objects, and all of " +
                "them after the game's own loop ran, which happened {19} times), the known levels lined up with a changed " +
                "list in {20} passes{16}",
                _passes, _passes > 0 ? _objects / _passes : 0, _fromWorker,
                _fromWorker > 0 ? _workerStopwatchTicks * ms / _fromWorker : 0, _fromWorkerObjects, _directObjects,
                _fromWorker > 0 ? _matchStopwatchTicks * ms / _fromWorker : 0, _inTick, _workers,
                _inTick > 0 ? _readStopwatchTicks * ms / _inTick : 0, _noCopy, _listChanged, _resyncs, _changes,
                _passes > 0 ? _compareStopwatchTicks * ms / _passes : 0, _passes > 0 ? _mainStopwatchTicks * ms / _passes : 0,
                _verify ? $"; verify mismatches {_verifyMismatches}, known levels that differed from the stored ones {_knownMismatches}" : "",
                _passes > 0 ? _applyStopwatchTicks * ms / _passes : 0, _readFromObjects, _forgotten, _realigned);
            _passes = _objects = _changes = _fromWorker = _fromWorkerObjects = _directObjects = _inTick = _noCopy = 0;
            _listChanged = _resyncs = _workerStopwatchTicks = _readStopwatchTicks = _matchStopwatchTicks = 0;
            _compareStopwatchTicks = _applyStopwatchTicks = _mainStopwatchTicks = _readFromObjects = _forgotten = _realigned = 0;
            return line;
        }

        // The game's CeiledWaterHeight (ThreadSafeWaterMap) and CurrentWaterAboveBase (WaterObject) over the given
        // arrays, so a copy that is not yet the map's can be read: the tile's column at its height (the game's
        // WaterColumnRetriever.GetColumn), the ceiling of its water surface, the part above the tile.
        internal static int LevelAt(ITerrainService terrain, MapIndexService mapIndex, byte[] counts,
            ReadOnlyWaterColumn[] columns, int verticalStride, Vector3Int tile)
        {
            Vector2Int xy = new Vector2Int(tile.x, tile.y);
            if (!terrain.Contains(xy))
            {
                return 0;
            }
            int index = mapIndex.CellToIndex(xy);
            int count = counts[index];
            float depth = 0f;
            byte floor = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly ReadOnlyWaterColumn column = ref columns[i * verticalStride + index];
                if (tile.z < column.Floor)
                {
                    break;
                }
                if (tile.z < column.Ceiling)
                {
                    depth = column.WaterDepth;
                    floor = column.Floor;
                    break;
                }
            }
            if (!(depth > 0f))
            {
                return 0;
            }
            float surface = (float)floor + depth;
            int above = (int)Math.Ceiling(surface) - tile.z;
            return above > 0 ? above : 0;
        }

        // ReSharper disable InconsistentNaming
        internal static void RegisterPostfix(WaterObject waterObject)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                Mirror.Add(waterObject);
                MirrorTiles.Add(_tileOf(waterObject));
                _mirrorVersion++;
                // The game stores its level right after this; the next pass reads it from the object.
                _registeredSincePass++;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void UnregisterPostfix(WaterObject waterObject)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                // The game's List.Remove: the first occurrence.
                int index = Mirror.IndexOf(waterObject);
                if (index >= 0)
                {
                    Mirror.RemoveAt(index);
                    MirrorTiles.RemoveAt(index);
                }
                _mirrorVersion++;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        [HarmonyPriority(Priority.Last)]
        internal static bool TickPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                List<WaterObject> objects = _objectsOf(__instance);
                int count = objects.Count;
                if (count < MinObjects)
                {
                    // The game's loop stores levels this mod does not see.
                    Forget();
                    return true;
                }
                long started = Stopwatch.GetTimestamp();
                int version = _versionOf != null ? _versionOf(objects) : 0;
                if (_levels.Length < count)
                {
                    _levels = new int[count + count / 4];
                }
                int[] levels = _levels;
                WaterObject[] items = _itemsOf != null ? _itemsOf(objects) : null;
                Batch batch = _usingCopy ? ReadyBatch() : null;
                if (batch != null)
                {
                    Match(batch, objects, items, count, levels);
                    _fromWorker++;
                }
                else
                {
                    Exception failure = OrderedParallel.Read(objects, count, levels, _currentLevel, _workers);
                    if (failure != null)
                    {
                        throw failure;
                    }
                    _inTick++;
                }
                long read = Stopwatch.GetTimestamp();
                if (batch != null)
                {
                    _matchStopwatchTicks += read - started;
                }
                else
                {
                    _readStopwatchTicks += read - started;
                }
                if (_verify)
                {
                    Verify(objects, count, levels);
                }

                // The objects whose level may have changed: new level different from the known one, or none known.
                long comparing = Stopwatch.GetTimestamp();
                int[] known = AlignKnown(objects, items, count, version);
                if (_candidates.Length < count)
                {
                    _candidates = new int[count + count / 4];
                }
                int[] candidates = _candidates;
                int candidateCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (levels[i] != known[i])
                    {
                        candidates[candidateCount++] = i;
                    }
                }
                long compared = Stopwatch.GetTimestamp();
                if (_verify)
                {
                    VerifyKnown(objects, items, count, known);
                }

                // The game's loop for those, in list order: the stored level read now, the new one stored (and the
                // event raised) only if it differs.
                long applying = Stopwatch.GetTimestamp();
                for (int k = 0; k < candidateCount; k++)
                {
                    int i = candidates[k];
                    WaterObject waterObject = items != null ? items[i] : objects[i];
                    int level = levels[i];
                    if (known[i] == Unknown)
                    {
                        _readFromObjects++;
                    }
                    if (level != waterObject.WaterAboveBase)
                    {
                        _setLevel(waterObject, level);
                        _changes++;
                        if (_versionOf != null ? _versionOf(objects) != version : objects.Count != count)
                        {
                            // A handler changed the list, which the game's own foreach would refuse: this feature
                            // refuses too and hands the tick to the game's loop, which then throws as it would.
                            throw new InvalidOperationException("the water object list changed inside a level change handler");
                        }
                    }
                    known[i] = level;
                }
                _knownVersion = version;
                _registeredSincePass = 0;
                long applied = Stopwatch.GetTimestamp();
                _compareStopwatchTicks += compared - comparing;
                _applyStopwatchTicks += applied - applying;
                _mainStopwatchTicks += applied - started;
                _passes++;
                _objects += count;
                _handledCalls++;
                return false;
            }
            catch (Exception exception)
            {
                // The game's loop now runs over the same list. Objects this pass already updated compare equal
                // there and are passed over, so every change is still applied once, in order.
                Fail(exception);
                return true;
            }
        }

        internal static void TickPostfix()
        {
            // Every call of the game's Tick passes here, handled by the prefix or not; the prefix counts the ones it
            // handled. A difference means levels may have been stored behind this mod's back.
            _seenCalls++;
            if (_seenCalls != _handledCalls)
            {
                _seenCalls = _handledCalls;
                Forget();
            }
        }
        // ReSharper restore InconsistentNaming

        // The known levels, index-aligned with the game's list: as they are while the list's change counter has not
        // moved since the last pass, otherwise lined up again by walking the list and the list as it was side by
        // side. An object not found keeps no level, and neither do the entries that registered since the last pass
        // (the last ones of the list, as the list only appends).
        private static int[] AlignKnown(List<WaterObject> objects, WaterObject[] items, int count, int version)
        {
            bool sameList = ReferenceEquals(_knownList, objects);
            if (sameList && _versionOf != null && _knownVersion == version && _knownCount == count && _registeredSincePass == 0)
            {
                return _known;
            }
            if (_knownSpare.Length < count)
            {
                _knownSpare = new int[count + count / 4];
            }
            int[] aligned = _knownSpare;
            int[] previous = _known;
            WaterObject[] before = _knownObjects;
            int end = sameList ? _knownCount : 0;
            int j = 0;
            for (int i = 0; i < count; i++)
            {
                WaterObject waterObject = items != null ? items[i] : objects[i];
                while (j < end && !ReferenceEquals(before[j], waterObject))
                {
                    j++;
                }
                aligned[i] = j < end ? previous[j++] : Unknown;
            }
            for (int i = Math.Max(0, count - _registeredSincePass); i < count; i++)
            {
                aligned[i] = Unknown;
            }
            _knownSpare = previous;
            _known = aligned;
            if (_knownObjects.Length < count)
            {
                _knownObjects = new WaterObject[count + count / 4];
            }
            WaterObject[] now = _knownObjects;
            if (items != null)
            {
                Array.Copy(items, now, count);
            }
            else
            {
                objects.CopyTo(0, now, 0, count);
            }
            if (_knownCount > count)
            {
                Array.Clear(now, count, _knownCount - count);
            }
            _knownList = objects;
            _knownCount = count;
            _realigned++;
            return aligned;
        }

        // Every known level forgotten: the next pass reads each object's stored level from the object.
        private static void Forget()
        {
            if (_knownList != null)
            {
                _forgotten++;
            }
            _knownList = null;
            _registeredSincePass = 0;
        }

        // Main thread, in WaterMapCopy's set-up of the tick's copy, before the water tasks exist.
        private static void OnJobCreated(WaterMapCopy.Job job)
        {
            if (!_active || Mirror.Count < MinObjects)
            {
                return;
            }
            try
            {
                Snapshot snapshot = Snapshots[_snapshotCursor];
                if (snapshot.Version != _mirrorVersion)
                {
                    _snapshotCursor = (_snapshotCursor + 1) % SnapshotBuffers;
                    snapshot = Snapshots[_snapshotCursor];
                    int count = Mirror.Count;
                    if (snapshot.Objects.Length < count)
                    {
                        snapshot.Objects = new WaterObject[count + count / 4];
                        snapshot.Tiles = new Vector3Int[count + count / 4];
                    }
                    Mirror.CopyTo(snapshot.Objects);
                    MirrorTiles.CopyTo(snapshot.Tiles);
                    snapshot.Count = count;
                    snapshot.Version = _mirrorVersion;
                }
                Batch batch = Batches[_batchCursor];
                _batchCursor = (_batchCursor + 1) % BatchBuffers;
                if (batch.Levels.Length < snapshot.Count)
                {
                    batch.Levels = new int[snapshot.Count + snapshot.Count / 4];
                }
                batch.Snapshot = snapshot;
                batch.Version = snapshot.Version;
                batch.Count = snapshot.Count;
                batch.Done = false;
                job.Extra = batch;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        // The water worker, right after the copy was filled. Reads the copy and the snapshot, writes the batch.
        private static void OnCopied(WaterMapCopy.Job job)
        {
            if (!(job.Extra is Batch batch))
            {
                return;
            }
            Snapshot snapshot = batch.Snapshot;
            int verticalStride = job.MapIndex.VerticalStride;
            for (int i = 0; i < batch.Count; i++)
            {
                batch.Levels[i] = LevelAt(job.Terrain, job.MapIndex, job.Counts, job.SpareColumns, verticalStride, snapshot.Tiles[i]);
            }
            batch.Done = true;
        }

        // The batch whose copy is the map's current arrays, or null.
        private static Batch ReadyBatch()
        {
            WaterMapCopy.Job job = WaterMapCopy.SwappedJob;
            if (job == null)
            {
                _noCopy++;
                return null;
            }
            if (job.ExtraFailure != null)
            {
                throw job.ExtraFailure;
            }
            if (!(job.Extra is Batch batch) || !batch.Done)
            {
                _noCopy++;
                return null;
            }
            if (batch.Version != batch.Snapshot.Version)
            {
                // The snapshot buffer was rewritten under it (cannot happen within the ring's depth; checked anyway).
                _noCopy++;
                return null;
            }
            _workerStopwatchTicks += job.ExtraStopwatchTicks;
            return batch;
        }

        // The game's list and the snapshot side by side. Objects that stayed keep their order, so one index over
        // each suffices: an object found in the snapshot takes the worker's level, one that is not (it joined the
        // list after the snapshot, or the mirror missed something) is read now with the game's own method.
        private static void Match(Batch batch, List<WaterObject> objects, WaterObject[] items, int count, int[] levels)
        {
            WaterObject[] snapshot = batch.Snapshot.Objects;
            int[] known = batch.Levels;
            int end = batch.Count;
            int j = 0;
            int direct = 0;
            for (int i = 0; i < count; i++)
            {
                WaterObject waterObject = items != null ? items[i] : objects[i];
                while (j < end && !ReferenceEquals(snapshot[j], waterObject))
                {
                    j++;
                }
                if (j < end)
                {
                    levels[i] = known[j++];
                }
                else
                {
                    levels[i] = _currentLevel(waterObject);
                    direct++;
                }
            }
            _fromWorkerObjects += count - direct;
            _directObjects += direct;
            if (direct > 0)
            {
                _listChanged++;
            }
            if (direct > count / 4)
            {
                // Far more than a tick's registrations: the mirror is out of step with the game's list.
                Resync(objects);
            }
        }

        private static void Resync(List<WaterObject> objects)
        {
            Mirror.Clear();
            MirrorTiles.Clear();
            for (int i = 0; i < objects.Count; i++)
            {
                Mirror.Add(objects[i]);
                MirrorTiles.Add(_tileOf(objects[i]));
            }
            _mirrorVersion++;
            _resyncs++;
        }

        // Counted and logged only: the levels read ahead are stored either way, because the setting is each player's
        // own and in co-op the one player who has it on must store the same levels as the others (up to 0.4.26 a
        // difference stored the main thread's).
        private static void Verify(List<WaterObject> objects, int count, int[] levels)
        {
            for (int i = 0; i < count; i++)
            {
                int level = _currentLevel(objects[i]);
                if (level != levels[i])
                {
                    _verifyMismatches++;
                    if (_verifyMismatches <= 10)
                    {
                        Log.Warning($"PlantWater verify: object {i} read {levels[i]} ahead of the tick and {level} on the main " +
                                    "thread.");
                    }
                }
            }
        }

        // Counted and logged only, like the check above: every level this pass takes as known is compared with the
        // level the object stores, before anything is changed. An object whose known level is wrong is still passed
        // over when its new level equals the known one, as it is with verify off.
        private static void VerifyKnown(List<WaterObject> objects, WaterObject[] items, int count, int[] known)
        {
            for (int i = 0; i < count; i++)
            {
                if (known[i] == Unknown)
                {
                    continue;
                }
                int stored = (items != null ? items[i] : objects[i]).WaterAboveBase;
                if (stored != known[i])
                {
                    _knownMismatches++;
                    if (_knownMismatches <= 10)
                    {
                        Log.Warning($"PlantWater verify: object {i} stores level {stored} where the mod knew {known[i]} from " +
                                    "its last pass.");
                    }
                }
            }
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            Forget();
            TurnedOff.Report("PlantWater", "PlantWater failed and turned itself off for this session: " + exception);
        }
    }
}
