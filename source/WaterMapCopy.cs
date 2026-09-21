using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Timberborn.MapIndexSystem;
using Timberborn.TickSystem;
using Timberborn.WaterSystem;
using UnityEngine;

namespace LateGamePerformance
{
    // Every tick the game copies the whole water map into the copy that readers on other threads use
    // (ThreadSafeWaterMap.Tick -> Update): every column of every tile, then a flow direction for every column,
    // on the main thread. 0.70 ms per tick in the colony this was measured in.
    //
    // What that copy holds is known earlier than the game makes it. The water simulation runs on worker threads
    // during the tick (WaterSimulator.StartParallelTick) and ends with UpdateWaterChangesTask; after that nothing
    // touches the simulation's arrays until the main thread's next tick. So right after that last task, on the
    // same worker thread, the mod makes the same copy into a second set of arrays, with the game's own code for
    // the flow directions. In the next tick, where the game would copy, the main thread only swaps the two sets.
    //
    // Nobody can read the second set while it is being written: the soil, water rendering and other parallel
    // tasks read the set that was current when their tick started, and the main thread reads it through the
    // map's own fields, which only change in the swap. The set that is swapped out was last read by the previous
    // tick's tasks, which the game has waited for.
    //
    // The copy is not used, and the game's own copy runs as without the mod, whenever the simulation's arrays may
    // have changed after the copy was made: a pending change to the water layout was applied (a building, a dam
    // or terrain changed: WaterSimulator.ProcessModifications with anything queued), the simulation was reset,
    // the number of water layers grew, the game says a column changed, or the copy was not finished or not made.
    // Those are the only ways the arrays change outside the water tasks.
    //
    // WaterMapCopyVerify lets the game copy every tick as well and compares, byte for byte, with what the worker
    // made. Nothing is swapped in that mode.
    internal static class WaterMapCopy
    {
        private delegate Vector2 FlowAtTop<T>(ref T outflows);

        private delegate void FlowFiller(Delegate flowAtTop, Array outflows, byte[] counts, Vector2[] live,
            Vector2[] spare, MapIndexService mapIndex);

        // One tick's copy. Written by the main thread before the water tasks are scheduled, filled by the worker
        // that ran the last of them, read by the main thread after the game has waited for all tasks.
        private sealed class Job
        {
            public object Map;
            public object Simulator;
            public Array SimColumns;
            public Array SimOutflows;
            public byte[] SimCounts;
            public ReadOnlyWaterColumn[] LiveColumns;
            public Vector2[] LiveFlows;
            public ReadOnlyWaterColumn[] SpareColumns;
            public Vector2[] SpareFlows;
            public Delegate FlowAtTop;
            public MapIndexService MapIndex;
            public int Started;
            public volatile bool Done;
            public Exception Failure;
            public long WorkerStopwatchTicks;
        }

        private static Func<object, object> _mapSimulator;
        private static Func<object, MapIndexService> _mapIndex;
        private static Func<object, object> _mapCalculator;
        private static Func<object, ReadOnlyWaterColumn[]> _mapColumns;
        private static Action<object, ReadOnlyWaterColumn[]> _setMapColumns;
        private static Func<object, Vector2[]> _mapFlows;
        private static Action<object, Vector2[]> _setMapFlows;
        private static Func<object, int> _mapMaxCount;
        private static Action<object, bool> _setMapAnyChanged;
        private static Func<object, int> _simMaxCount;
        private static Func<object, bool> _simAnyChanged;
        private static Func<object, object> _simColumnsHolder;
        private static Func<object, object> _simOutflowsHolder;
        private static Func<object, object> _simCountsHolder;
        private static Func<object, Array> _columnsArray;
        private static Func<object, Array> _outflowsArray;
        private static Func<object, byte[]> _countsArray;
        private static Func<object, ICollection> _simModifications;
        private static Action<Array, ReadOnlyWaterColumn[]> _copyColumns;
        private static FlowFiller _fillFlows;
        private static Type _flowAtTopType;
        private static MethodInfo _flowAtTopMethod;

        private static bool _active;
        private static bool _verify;
        private static Job _job;
        private static Job _verifyJob;
        private static bool _layoutChanged;
        private static object _map;
        private static object _spareOwner;
        private static ReadOnlyWaterColumn[] _spareColumns;
        private static Vector2[] _spareFlows;
        private static Delegate _ownerFlowAtTop;

        private static long _swapped;
        private static long _fellBackLayout;
        private static long _fellBackNotReady;
        private static long _workerStopwatchTicks;
        private static long _verified;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.WaterMapCopyVerify;
            Type self = typeof(WaterMapCopy);
            const string mapType = "Timberborn.WaterSystem.ThreadSafeWaterMap";
            const string simType = "Timberborn.WaterSystem.WaterSimulator";
            Feature feature = new Feature { Name = "WaterMapCopy" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "ThreadSafeWaterMap.Tick",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return Reflect.Method(mapType, "Tick");
                },
                Prefix = Reflect.Own(self, nameof(MapTickPrefix)),
                Postfix = Reflect.Own(self, nameof(MapTickPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterSimulator.StartParallelTick",
                Required = true,
                Target = () => Reflect.Method(simType, "StartParallelTick"),
                Prefix = Reflect.Own(self, nameof(StartParallelTickPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "UpdateWaterChangesTask.Run",
                Required = true,
                Target = () => Reflect.Method("Timberborn.WaterSystem.UpdateWaterChangesTask", "Run"),
                Postfix = Reflect.Own(self, nameof(LastWaterTaskPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterSimulator.ProcessModifications",
                Required = true,
                Target = () => Reflect.Method(simType, "ProcessModifications"),
                Prefix = Reflect.Own(self, nameof(ProcessModificationsPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterSimulator.Reset",
                Required = true,
                Target = () => Reflect.Method(simType, "Reset"),
                Prefix = Reflect.Own(self, nameof(SimulationResetPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            _job = null;
            _verifyJob = null;
            _map = null;
            _spareOwner = null;
            _spareColumns = null;
            _spareFlows = null;
            _ownerFlowAtTop = null;
        }

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            Type map = Reflect.GameType("Timberborn.WaterSystem.ThreadSafeWaterMap");
            Type sim = Reflect.GameType("Timberborn.WaterSystem.WaterSimulator");
            Type column = Reflect.GameType("Timberborn.WaterSystem.WaterColumn");
            Type outflows = Reflect.GameType("Timberborn.WaterSystem.ColumnOutflows");
            Type calculator = Reflect.GameType("Timberborn.WaterSystem.FlowVectorCalculator");
            if (map == null || sim == null || column == null || outflows == null || calculator == null)
            {
                throw new TypeLoadException("water map types not found");
            }
            _mapSimulator = Reflect.FieldGetter<object>(map, "_waterSimulator");
            _mapIndex = Reflect.FieldGetter<MapIndexService>(map, "_mapIndexService");
            _mapCalculator = Reflect.FieldGetter<object>(map, "_flowVectorCalculator");
            _mapColumns = Reflect.FieldGetter<ReadOnlyWaterColumn[]>(map, "_threadSafeWaterColumns");
            _setMapColumns = Reflect.FieldSetter<ReadOnlyWaterColumn[]>(map, "_threadSafeWaterColumns");
            _mapFlows = Reflect.FieldGetter<Vector2[]>(map, "_waterFlowDirections");
            _setMapFlows = Reflect.FieldSetter<Vector2[]>(map, "_waterFlowDirections");
            _mapMaxCount = Reflect.PropertyGetter<int>(map, "MaxColumnCount");
            _setMapAnyChanged = Reflect.InstanceCall<Action<object, bool>>(Reflect.Setter(map, "AnyColumnChanged"));
            _simMaxCount = Reflect.PropertyGetter<int>(sim, "MaxColumnCount");
            _simAnyChanged = Reflect.PropertyGetter<bool>(sim, "AnyColumnChanged");
            _simColumnsHolder = Reflect.FieldGetter<object>(sim, "_waterColumns");
            _simOutflowsHolder = Reflect.FieldGetter<object>(sim, "_outflows");
            _simCountsHolder = Reflect.FieldGetter<object>(sim, "_columnCounts");
            _simModifications = Reflect.FieldGetter<ICollection>(sim, "_modifications");
            _columnsArray = Reflect.FieldGetter<Array>(typeof(TickOnlyArray<>).MakeGenericType(column), "_array");
            _outflowsArray = Reflect.FieldGetter<Array>(typeof(TickOnlyArray<>).MakeGenericType(outflows), "_array");
            _countsArray = Reflect.FieldGetter<byte[]>(typeof(TickOnlyArray<byte>), "_array");

            // The game reinterprets its own columns as the public read-only ones; they must still be the same size.
            MethodInfo sameSize = Reflect.Own(typeof(WaterMapCopy), nameof(CastsOneToOne)).MakeGenericMethod(column);
            if (!(bool)sameSize.Invoke(null, null))
            {
                throw new InvalidOperationException("WaterColumn and ReadOnlyWaterColumn no longer have the same size");
            }
            _copyColumns = (Action<Array, ReadOnlyWaterColumn[]>)Reflect.Own(typeof(WaterMapCopy), nameof(CopyColumns))
                .MakeGenericMethod(column).CreateDelegate(typeof(Action<Array, ReadOnlyWaterColumn[]>));
            _fillFlows = (FlowFiller)Reflect.Own(typeof(WaterMapCopy), nameof(FillFlows))
                .MakeGenericMethod(outflows).CreateDelegate(typeof(FlowFiller));
            _flowAtTopType = typeof(FlowAtTop<>).MakeGenericType(outflows);
            _flowAtTopMethod = AccessTools.Method(calculator, "GetFlowVectorAtTop");
            if (_flowAtTopMethod == null || _flowAtTopMethod.ReturnType != typeof(Vector2))
            {
                throw new MissingMethodException("FlowVectorCalculator", "GetFlowVectorAtTop");
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active && _swapped == 0 && _fellBackLayout == 0)
            {
                return null;
            }
            long used = _verify ? _verified : _swapped;
            double workerMs = _workerStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "WaterMapCopy: {0} ticks {1} the copy made on a worker thread ({2:0.000} ms there per tick); the game " +
                "copied on the main thread in {3} ticks where the water layout changed and {4} where no copy was ready{5}",
                used, _verify ? "compared with" : "swapped in", used > 0 ? workerMs / used : 0, _fellBackLayout,
                _fellBackNotReady, _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _swapped = _fellBackLayout = _fellBackNotReady = _workerStopwatchTicks = _verified = 0;
            return line;
        }

        // Main thread, before any water task is scheduled.
        // ReSharper disable once InconsistentNaming
        internal static void StartParallelTickPrefix(object __instance)
        {
            if (!_active)
            {
                return;
            }
            Volatile.Write(ref _job, null);
            _layoutChanged = false;
            try
            {
                object map = _map;
                if (map == null || !ReferenceEquals(_mapSimulator(map), __instance))
                {
                    return;
                }
                Array simColumns = _columnsArray(_simColumnsHolder(__instance));
                ReadOnlyWaterColumn[] liveColumns = _mapColumns(map);
                Vector2[] liveFlows = _mapFlows(map);
                if (liveColumns.Length != simColumns.Length || liveFlows.Length != simColumns.Length)
                {
                    // The game resizes its copy in its next tick; let it.
                    return;
                }
                if (!ReferenceEquals(_spareOwner, map))
                {
                    _spareOwner = map;
                    _spareColumns = null;
                    _spareFlows = null;
                    _ownerFlowAtTop = Delegate.CreateDelegate(_flowAtTopType, _mapCalculator(map), _flowAtTopMethod);
                }
                if (_spareColumns == null || _spareColumns.Length != liveColumns.Length)
                {
                    _spareColumns = new ReadOnlyWaterColumn[liveColumns.Length];
                }
                if (_spareFlows == null || _spareFlows.Length != liveFlows.Length)
                {
                    _spareFlows = new Vector2[liveFlows.Length];
                }
                Job job = new Job
                {
                    Map = map,
                    Simulator = __instance,
                    SimColumns = simColumns,
                    SimOutflows = _outflowsArray(_simOutflowsHolder(__instance)),
                    SimCounts = _countsArray(_simCountsHolder(__instance)),
                    LiveColumns = liveColumns,
                    LiveFlows = liveFlows,
                    SpareColumns = _spareColumns,
                    SpareFlows = _spareFlows,
                    FlowAtTop = _ownerFlowAtTop,
                    MapIndex = _mapIndex(map)
                };
                // Published before the tasks exist; the game's task queue orders it before the worker's read.
                Volatile.Write(ref _job, job);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        // Worker thread, right after the last water task of the tick. Only writes the job's own arrays.
        internal static void LastWaterTaskPostfix()
        {
            Job job = Volatile.Read(ref _job);
            if (job == null || Interlocked.Exchange(ref job.Started, 1) != 0)
            {
                return;
            }
            try
            {
                long started = Stopwatch.GetTimestamp();
                Fill(job);
                job.WorkerStopwatchTicks = Stopwatch.GetTimestamp() - started;
            }
            catch (Exception exception)
            {
                job.Failure = exception;
            }
            job.Done = true;
        }

        // ReSharper disable once InconsistentNaming
        internal static void ProcessModificationsPrefix(object __instance)
        {
            if (_active && _simModifications(__instance).Count > 0)
            {
                _layoutChanged = true;
            }
        }

        internal static void SimulationResetPrefix()
        {
            _layoutChanged = true;
        }

        // ReSharper disable once InconsistentNaming
        internal static bool MapTickPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            _verifyJob = null;
            try
            {
                _map = __instance;
                Job job = Volatile.Read(ref _job);
                Volatile.Write(ref _job, null);
                if (job == null || !job.Done || !ReferenceEquals(job.Map, __instance))
                {
                    _fellBackNotReady++;
                    return true;
                }
                if (job.Failure != null)
                {
                    throw job.Failure;
                }
                object sim = job.Simulator;
                if (_layoutChanged || _simAnyChanged(sim) || _simMaxCount(sim) != _mapMaxCount(__instance) ||
                    !ReferenceEquals(_mapSimulator(__instance), sim) ||
                    !ReferenceEquals(_columnsArray(_simColumnsHolder(sim)), job.SimColumns) ||
                    !ReferenceEquals(_mapColumns(__instance), job.LiveColumns) ||
                    !ReferenceEquals(_mapFlows(__instance), job.LiveFlows))
                {
                    _fellBackLayout++;
                    return true;
                }
                _workerStopwatchTicks += job.WorkerStopwatchTicks;
                if (_verify)
                {
                    _verifyJob = job;
                    return true;
                }
                // The game's Update from here, with both copies already made: the layer count is unchanged (no
                // resize, no event), and with no column changed the game would not copy the column counts.
                _setMapAnyChanged(__instance, false);
                _setMapColumns(__instance, job.SpareColumns);
                _setMapFlows(__instance, job.SpareFlows);
                _spareColumns = job.LiveColumns;
                _spareFlows = job.LiveFlows;
                _swapped++;
                return false;
            }
            catch (Exception exception)
            {
                // Nothing was swapped; the game's own copy runs.
                Fail(exception);
                return true;
            }
        }

        // ReSharper disable once InconsistentNaming
        internal static void MapTickPostfix(object __instance)
        {
            Job job = _verifyJob;
            _verifyJob = null;
            if (job == null)
            {
                return;
            }
            _verified++;
            bool same = MemoryMarshal.AsBytes(new ReadOnlySpan<ReadOnlyWaterColumn>(job.SpareColumns))
                            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ReadOnlyWaterColumn>(_mapColumns(__instance)))) &&
                        MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2>(job.SpareFlows))
                            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2>(_mapFlows(__instance))));
            if (!same)
            {
                _verifyMismatches++;
                if (_verifyMismatches <= 10)
                {
                    Log.Warning("WaterMapCopy verify: the copy made on a worker differs from the game's. The game's is used.");
                }
            }
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            _job = null;
            _verifyJob = null;
            Log.Warning("WaterMapCopy failed and turned itself off for this session; the game copies the water map " +
                        "itself: " + exception);
        }

        private static void Fill(Job job)
        {
            _copyColumns(job.SimColumns, job.SpareColumns);
            _fillFlows(job.FlowAtTop, job.SimOutflows, job.SimCounts, job.LiveFlows, job.SpareFlows, job.MapIndex);
        }

        // For the tests: the worker's part, run directly.
        internal static bool FillForTests()
        {
            Job job = Volatile.Read(ref _job);
            if (job == null)
            {
                return false;
            }
            LastWaterTaskPostfix();
            return job.Done && job.Failure == null;
        }

        private static bool CastsOneToOne<TColumn>() where TColumn : struct
        {
            return MemoryMarshal.Cast<TColumn, ReadOnlyWaterColumn>(new TColumn[1]).Length == 1 &&
                   MemoryMarshal.Cast<TColumn, ReadOnlyWaterColumn>(new TColumn[7]).Length == 7;
        }

        // The game's copy: MemoryMarshal.Cast<WaterColumn, ReadOnlyWaterColumn>(columns).CopyTo(copy).
        private static void CopyColumns<TColumn>(Array columns, ReadOnlyWaterColumn[] copy) where TColumn : struct
        {
            MemoryMarshal.Cast<TColumn, ReadOnlyWaterColumn>(new ReadOnlySpan<TColumn>((TColumn[])columns)).CopyTo(copy);
        }

        // The game's UpdateWaterFlowDirections, into the spare array. The game only writes the columns that exist;
        // the others keep what the current array holds, so they start as a copy of it.
        private static void FillFlows<TOutflows>(Delegate flowAtTop, Array outflows, byte[] counts, Vector2[] live,
            Vector2[] spare, MapIndexService mapIndex) where TOutflows : struct
        {
            Array.Copy(live, spare, live.Length);
            FlowAtTop<TOutflows> vectorAtTop = (FlowAtTop<TOutflows>)flowAtTop;
            TOutflows[] all = (TOutflows[])outflows;
            int verticalStride = mapIndex.VerticalStride;
            Index2DEnumerator enumerator = mapIndex.Indices2D.GetEnumerator();
            while (enumerator.MoveNext())
            {
                int current = enumerator.Current;
                byte count = counts[current];
                for (int i = 0; i < count; i++)
                {
                    int index = i * verticalStride + current;
                    spare[index] = vectorAtTop(ref all[index]);
                }
            }
        }
    }
}
