using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Timberborn.MapIndexSystem;
using Timberborn.SimulationSystem;
using Timberborn.TerrainSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace LateGamePerformance
{
    // Every tick the game walks every tile of the map, every soil layer of it, to find the few whose moisture or
    // contamination changed (SoilMoistureService.UpdateMoistureLevels, SoilContaminationService
    // .UpdateContaminationLevels), and for each one it finds updates the soil's look and wakes plants that dry
    // out or recover. The simulation marks what changed in a flag array, one flag per soil cell.
    //
    // Since 0.4.23 the list of changed cells is made where the flags are written: the soil simulation runs on the
    // game's worker threads during the tick, one row of the map per call, and the worker that finishes the last
    // row reads the whole flag array (eight flags at a time) and writes the changed cells down in the game's loop
    // order (tile by tile as the game walks them, then layer by layer). In the next tick the service only goes
    // through that list, calling the game's own method for each cell, with the game's own filter (a cell above a
    // tile's column count is passed over, as the game's loop never reaches it). Nothing else may touch the flags
    // between the last row and the service's tick except the simulator's own tick on the main thread (a column
    // moved, terrain raised or lowered, a reset), which is counted; a list is not used after such an edit, when
    // the flag array was replaced, or when it was not finished, and the scan below runs instead.
    //
    // The scan (0.4.14) walks the same tiles in the same order as the game, but first looks at eight tiles' flags
    // at once, in every layer, and passes over the eight when none is set. For the others it does exactly what
    // the game's loop does. A tile passed over is one where the game's loop would only have read flags that are
    // not set, so the same cells are updated in the same order with the same values.
    //
    // SoilScansVerify also walks every cell the game's way and compares the list of cells updated.
    internal static class SoilScans
    {
        private const int Block = 8;

        // What the scan needs to know about one kind of soil state. Allocated once per kind.
        internal abstract class Cells
        {
            public int ChangedCount;

            public abstract int ColumnCount(int index2D);

            public abstract void Changed(int index2D, int index3D);
        }

        private sealed class MoistureCells : Cells
        {
            public object Service;
            public IThreadSafeColumnTerrainMap Terrain;
            public MapIndexService MapIndex;
            public float[] Levels;
            public List<int> Visited;

            public override int ColumnCount(int index2D) => Terrain.GetColumnCount(index2D);

            public override void Changed(int index2D, int index3D)
            {
                ChangedCount++;
                Visited?.Add(index3D);
                Vector3Int coordinates = MapIndex.IndexToCoordinates(index2D, Terrain.GetColumnCeiling(index3D));
                _setMoisture(Service, coordinates, index3D, Levels[index3D]);
            }
        }

        private sealed class ContaminationCells : Cells
        {
            public object Service;
            public ITerrainService Terrain;
            public MapIndexService MapIndex;
            public float[] Levels;
            public List<int> Visited;

            public override int ColumnCount(int index2D) => Terrain.GetColumnCount(index2D);

            public override void Changed(int index2D, int index3D)
            {
                ChangedCount++;
                Visited?.Add(index3D);
                Vector3Int coordinates = MapIndex.IndexToCoordinates(index2D, Terrain.GetColumnCeiling(index3D));
                _setContamination(Service, coordinates, index3D, Levels[index3D]);
            }
        }

        // One tick's list of changed cells: published by the main thread before the soil tasks exist, finished by
        // the worker that runs the last row, read by the main thread in the next tick.
        internal sealed class Job
        {
            public bool[] Flags;
            public int Rows;
            public int RowsDone;
            public int VerticalStride;
            public int Stride;
            public int Width;
            public int Height;
            public int[] Cells = new int[256];
            public int Count;
            public volatile bool Done;
            public Exception Failure;
            public long WorkerStopwatchTicks;
        }

        // One kind of soil state (moisture or contamination) and its job.
        internal sealed class Kind
        {
            public string Name;
            public Job Current;
            // Main-thread edits of the flags since the job was published: the simulator's own tick processing a
            // column move, a terrain height change or a reset.
            public int MainEdits;
            private readonly Job[] _ring = { new Job(), new Job(), new Job() };
            private int _cursor;

            public Job Next()
            {
                Job job = _ring[_cursor];
                _cursor = (_cursor + 1) % _ring.Length;
                return job;
            }
        }

        private static Func<object, object> _moistureSimulator;
        private static Func<object, MapIndexService> _moistureMapIndex;
        private static Func<object, IThreadSafeColumnTerrainMap> _moistureTerrain;
        private static Func<object, TickOnlyArray<bool>> _moistureFlags;
        private static Func<object, TickOnlyArray<float>> _moistureLevels;
        private static Action<object, Vector3Int, int, float> _setMoisture;
        private static Func<object, MapIndexService> _moistureSimMapIndex;
        private static Func<object, ICollection> _moistureActions;
        private static Func<object, ICollection> _moistureHeightChanges;
        private static Func<object, SimulationController> _moistureController;

        private static Func<object, object> _contaminationSimulator;
        private static Func<object, MapIndexService> _contaminationMapIndex;
        private static Func<object, ITerrainService> _contaminationTerrain;
        private static Func<object, TickOnlyArray<bool>> _contaminationFlags;
        private static Func<object, TickOnlyArray<float>> _contaminationLevels;
        private static Action<object, Vector3Int, int, float> _setContamination;
        private static Func<object, MapIndexService> _contaminationSimMapIndex;
        private static Func<object, ICollection> _contaminationActions;
        private static Func<object, ICollection> _contaminationHeightChanges;
        private static Func<object, SimulationController> _contaminationController;

        private static Func<object, bool[]> _boolArray;
        private static Func<object, float[]> _floatArray;

        private static readonly MoistureCells Moisture = new MoistureCells();
        private static readonly ContaminationCells Contamination = new ContaminationCells();
        private static readonly List<int> Expected = new List<int>();
        internal static readonly Kind MoistureKind = new Kind { Name = "moisture" };
        internal static readonly Kind ContaminationKind = new Kind { Name = "contamination" };

        private static bool _active;
        private static bool _listsActive;
        private static bool _verify;

        private static long _passes;
        private static long _listPasses;
        private static long _scanPasses;
        private static long _blocks;
        private static long _blocksSkipped;
        private static long _changed;
        private static long _stopwatchTicks;
        private static long _workerStopwatchTicks;
        private static long _notReady;
        private static long _editedOnMain;
        private static long _arrayChanged;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.SoilScansVerify;
            if (_verify)
            {
                Moisture.Visited = new List<int>();
                Contamination.Visited = new List<int>();
            }
            Type self = typeof(SoilScans);
            Feature feature = new Feature { Name = "SoilScans" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilMoistureService.UpdateMoistureLevels",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return Reflect.Method("Timberborn.SoilMoistureSystem.SoilMoistureService", "UpdateMoistureLevels");
                },
                Prefix = Reflect.Own(self, nameof(MoisturePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilContaminationService.UpdateContaminationLevels",
                Required = true,
                Target = () => Reflect.Method("Timberborn.SoilContaminationSystem.SoilContaminationService",
                    "UpdateContaminationLevels"),
                Prefix = Reflect.Own(self, nameof(ContaminationPrefix))
            });
            return feature;
        }

        // The lists made on the workers. A separate feature so that, should any of its hooks fail to install, the
        // scan above still runs; all six are required together because the edit count is what makes a list safe.
        public static Feature CreateListsFeature()
        {
            Type self = typeof(SoilScans);
            Feature feature = new Feature { Name = "SoilLists" };
            const string moistureSim = "Timberborn.SoilMoistureSystem.SoilMoistureSimulator";
            const string contaminationSim = "Timberborn.SoilContaminationSystem.SoilContaminationSimulator";
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilMoistureSimulator.StartParallelTick",
                Required = true,
                Target = () =>
                {
                    BindListAccessors();
                    return Reflect.Method(moistureSim, "StartParallelTick");
                },
                Prefix = Reflect.Own(self, nameof(MoistureStartPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "MoistureCalculationTask.Run",
                Required = true,
                Target = () => Reflect.Method("Timberborn.SoilMoistureSystem.MoistureCalculationTask", "Run"),
                Postfix = Reflect.Own(self, nameof(MoistureRowPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilMoistureSimulator.Tick",
                Required = true,
                Target = () => Reflect.Method(moistureSim, "Tick"),
                Prefix = Reflect.Own(self, nameof(MoistureTickPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilContaminationSimulator.StartParallelTick",
                Required = true,
                Target = () => Reflect.Method(contaminationSim, "StartParallelTick"),
                Prefix = Reflect.Own(self, nameof(ContaminationStartPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "ContaminationsUpdateTask.Run",
                Required = true,
                Target = () => Reflect.Method("Timberborn.SoilContaminationSystem.ContaminationsUpdateTask", "Run"),
                Postfix = Reflect.Own(self, nameof(ContaminationRowPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoilContaminationSimulator.Tick",
                Required = true,
                Target = () => Reflect.Method(contaminationSim, "Tick"),
                Prefix = Reflect.Own(self, nameof(ContaminationTickPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static void ActivateLists()
        {
            SceneCreated();
            _listsActive = true;
        }

        public static bool IsActive => _active;

        public static bool ListsActive => _listsActive;

        public static void SceneCreated()
        {
            Volatile.Write(ref MoistureKind.Current, null);
            Volatile.Write(ref ContaminationKind.Current, null);
            MoistureKind.MainEdits = 0;
            ContaminationKind.MainEdits = 0;
        }

        public static void BindAccessors()
        {
            Type moisture = Reflect.GameType("Timberborn.SoilMoistureSystem.SoilMoistureService");
            Type moistureSim = Reflect.GameType("Timberborn.SoilMoistureSystem.SoilMoistureSimulator");
            Type contamination = Reflect.GameType("Timberborn.SoilContaminationSystem.SoilContaminationService");
            Type contaminationSim = Reflect.GameType("Timberborn.SoilContaminationSystem.SoilContaminationSimulator");
            if (moisture == null || moistureSim == null || contamination == null || contaminationSim == null)
            {
                throw new TypeLoadException("soil types not found");
            }
            _moistureSimulator = Reflect.FieldGetter<object>(moisture, "_soilMoistureSimulator");
            _moistureMapIndex = Reflect.FieldGetter<MapIndexService>(moisture, "_mapIndexService");
            _moistureTerrain = Reflect.FieldGetter<IThreadSafeColumnTerrainMap>(moisture, "_threadSafeColumnTerrainMap");
            _moistureFlags = Reflect.FieldGetter<TickOnlyArray<bool>>(moistureSim, "_moistureLevelsChangedLastTick");
            _moistureLevels = Reflect.FieldGetter<TickOnlyArray<float>>(moistureSim, "_moistureLevels");
            _setMoisture = Reflect.InstanceCall<Action<object, Vector3Int, int, float>>(
                HarmonyLib.AccessTools.Method(moisture, "SetMoistureLevel"));

            _contaminationSimulator = Reflect.FieldGetter<object>(contamination, "_soilContaminationSimulator");
            _contaminationMapIndex = Reflect.FieldGetter<MapIndexService>(contamination, "_mapIndexService");
            _contaminationTerrain = Reflect.FieldGetter<ITerrainService>(contamination, "_terrainService");
            _contaminationFlags = Reflect.FieldGetter<TickOnlyArray<bool>>(contaminationSim, "_contaminationsChangedLastTick");
            _contaminationLevels = Reflect.FieldGetter<TickOnlyArray<float>>(contaminationSim, "_contaminationLevels");
            _setContamination = Reflect.InstanceCall<Action<object, Vector3Int, int, float>>(
                HarmonyLib.AccessTools.Method(contamination, "SetContaminationLevel"));

            _boolArray = Reflect.FieldGetter<bool[]>(typeof(TickOnlyArray<bool>), "_array");
            _floatArray = Reflect.FieldGetter<float[]>(typeof(TickOnlyArray<float>), "_array");
        }

        public static void BindListAccessors()
        {
            Type moistureSim = Reflect.GameType("Timberborn.SoilMoistureSystem.SoilMoistureSimulator");
            Type contaminationSim = Reflect.GameType("Timberborn.SoilContaminationSystem.SoilContaminationSimulator");
            if (moistureSim == null || contaminationSim == null)
            {
                throw new TypeLoadException("soil simulator types not found");
            }
            if (_boolArray == null)
            {
                BindAccessors();
            }
            _moistureSimMapIndex = Reflect.FieldGetter<MapIndexService>(moistureSim, "_mapIndexService");
            _moistureActions = Reflect.FieldGetter<ICollection>(moistureSim, "_actions");
            _moistureHeightChanges = Reflect.FieldGetter<ICollection>(moistureSim, "_terrainHeightChanges");
            _moistureController = Reflect.FieldGetter<SimulationController>(moistureSim, "_simulationController");
            _contaminationSimMapIndex = Reflect.FieldGetter<MapIndexService>(contaminationSim, "_mapIndexService");
            _contaminationActions = Reflect.FieldGetter<ICollection>(contaminationSim, "_actions");
            _contaminationHeightChanges = Reflect.FieldGetter<ICollection>(contaminationSim, "_terrainHeightChanges");
            _contaminationController = Reflect.FieldGetter<SimulationController>(contaminationSim, "_simulationController");
        }

        // For the tests, where the game's per-cell methods cannot run (they touch Unity's native code).
        internal static void SetMoistureForTests(Action<object, Vector3Int, int, float> set) => _setMoisture = set;

        internal static void SetContaminationForTests(Action<object, Vector3Int, int, float> set) => _setContamination = set;

        public static string TakeStatsLine()
        {
            if (!_active && _passes == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double workerMs = _workerStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "SoilScans: {0} moisture and contamination passes in {1:0.0} ms ({2:0.000} ms each); {3} went through a list of " +
                "changed cells made on a worker ({4:0.000} ms there per list), {5} scanned the map on the main thread " +
                "({6:0.0}% of {7}-tile groups had nothing changed and were passed over); lists not usable: {8} not finished, " +
                "{9} after a main-thread edit, {10} after the flag array was replaced; {11} changed cells updated{12}",
                _passes, ms, _passes > 0 ? ms / _passes : 0, _listPasses, _listPasses > 0 ? workerMs / _listPasses : 0,
                _scanPasses, _blocks > 0 ? 100.0 * _blocksSkipped / _blocks : 0, Block, _notReady, _editedOnMain, _arrayChanged,
                _changed, _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _passes = _listPasses = _scanPasses = _blocks = _blocksSkipped = _changed = _stopwatchTicks = 0;
            _workerStopwatchTicks = _notReady = _editedOnMain = _arrayChanged = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static bool MoisturePrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            MoistureCells cells = Moisture;
            try
            {
                object simulator = _moistureSimulator(__instance);
                cells.Service = __instance;
                cells.Terrain = _moistureTerrain(__instance);
                cells.MapIndex = _moistureMapIndex(__instance);
                cells.Levels = _floatArray(_moistureLevels(simulator));
                return Run(MoistureKind, cells, _boolArray(_moistureFlags(simulator)), cells.MapIndex, cells.Visited);
            }
            catch (Exception exception)
            {
                return Fail(exception);
            }
            finally
            {
                cells.Service = null;
                cells.Levels = null;
            }
        }

        internal static bool ContaminationPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            ContaminationCells cells = Contamination;
            try
            {
                object simulator = _contaminationSimulator(__instance);
                cells.Service = __instance;
                cells.Terrain = _contaminationTerrain(__instance);
                cells.MapIndex = _contaminationMapIndex(__instance);
                cells.Levels = _floatArray(_contaminationLevels(simulator));
                return Run(ContaminationKind, cells, _boolArray(_contaminationFlags(simulator)), cells.MapIndex, cells.Visited);
            }
            catch (Exception exception)
            {
                return Fail(exception);
            }
            finally
            {
                cells.Service = null;
                cells.Levels = null;
            }
        }

        // Main thread, in the simulator's StartParallelTick, before its tasks exist.
        internal static void MoistureStartPrefix(object __instance)
        {
            StartJob(MoistureKind, () => _boolArray(_moistureFlags(__instance)), () => _moistureSimMapIndex(__instance));
        }

        internal static void ContaminationStartPrefix(object __instance)
        {
            StartJob(ContaminationKind, () => _boolArray(_contaminationFlags(__instance)), () => _contaminationSimMapIndex(__instance));
        }

        // A worker, after one row of the soil task. The one that finishes the last row makes the list.
        internal static void MoistureRowPostfix()
        {
            RowDone(MoistureKind);
        }

        internal static void ContaminationRowPostfix()
        {
            RowDone(ContaminationKind);
        }

        // Main thread, before the simulator's own tick edits the flags.
        internal static void MoistureTickPrefix(object __instance)
        {
            NoteMainEdits(MoistureKind, () => _moistureActions(__instance), () => _moistureHeightChanges(__instance),
                () => _moistureController(__instance));
        }

        internal static void ContaminationTickPrefix(object __instance)
        {
            NoteMainEdits(ContaminationKind, () => _contaminationActions(__instance), () => _contaminationHeightChanges(__instance),
                () => _contaminationController(__instance));
        }
        // ReSharper restore InconsistentNaming

        private static void StartJob(Kind kind, Func<bool[]> flagsOf, Func<MapIndexService> mapIndexOf)
        {
            kind.MainEdits = 0;
            Volatile.Write(ref kind.Current, null);
            if (!_listsActive)
            {
                return;
            }
            try
            {
                bool[] flags = flagsOf();
                MapIndexService mapIndex = mapIndexOf();
                Vector3Int size = mapIndex.TerrainSize;
                int stride = mapIndex.Stride;
                int verticalStride = mapIndex.VerticalStride;
                if (stride != size.x + 2 || verticalStride != stride * (size.y + 2) || flags.Length % verticalStride != 0 || size.y < 1)
                {
                    return;
                }
                Job job = kind.Next();
                job.Flags = flags;
                job.Rows = size.y;
                job.RowsDone = 0;
                job.VerticalStride = verticalStride;
                job.Stride = stride;
                job.Width = size.x;
                job.Height = size.y;
                job.Count = 0;
                job.Failure = null;
                job.WorkerStopwatchTicks = 0;
                job.Done = false;
                Volatile.Write(ref kind.Current, job);
            }
            catch (Exception exception)
            {
                ListsFail(exception);
            }
        }

        private static void RowDone(Kind kind)
        {
            Job job = Volatile.Read(ref kind.Current);
            if (job == null || Interlocked.Increment(ref job.RowsDone) != job.Rows)
            {
                return;
            }
            try
            {
                long started = Stopwatch.GetTimestamp();
                ListChangedCells(job);
                job.WorkerStopwatchTicks = Stopwatch.GetTimestamp() - started;
            }
            catch (Exception exception)
            {
                job.Failure = exception;
            }
            job.Done = true;
        }

        private static void NoteMainEdits(Kind kind, Func<ICollection> actionsOf, Func<ICollection> heightChangesOf,
            Func<SimulationController> controllerOf)
        {
            if (!_listsActive)
            {
                return;
            }
            try
            {
                if (actionsOf().Count > 0 || heightChangesOf().Count > 0 || controllerOf().ShouldResetSimulation)
                {
                    kind.MainEdits++;
                }
            }
            catch (Exception exception)
            {
                ListsFail(exception);
            }
        }

        // For the tests: the worker's part, run directly for every row.
        internal static bool FinishJobForTests(Kind kind)
        {
            Job job = Volatile.Read(ref kind.Current);
            if (job == null)
            {
                return false;
            }
            for (int row = 0; row < job.Rows; row++)
            {
                RowDone(kind);
            }
            return job.Done && job.Failure == null;
        }

        // Every set flag, in the game's loop order: tiles as the game walks them (rows 1..height, columns 1..width),
        // layers bottom to top within a tile. The flags lie layer-major in memory (index3D = index2D + layer *
        // verticalStride), so they are read that way, eight at a time, and the few found are sorted by
        // (tile, layer). Cells outside the walked tiles or above a tile's column count are left to the reader,
        // which applies the game's filter.
        internal static void ListChangedCells(Job job)
        {
            bool[] flags = job.Flags;
            int verticalStride = job.VerticalStride;
            int layers = flags.Length / verticalStride;
            int[] cells = job.Cells;
            int count = 0;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<bool>(flags));
            int i = 0;
            for (; i + Block <= bytes.Length; i += Block)
            {
                if (MemoryMarshal.Read<ulong>(bytes.Slice(i, Block)) == 0)
                {
                    continue;
                }
                for (int k = 0; k < Block; k++)
                {
                    if (bytes[i + k] != 0)
                    {
                        if (count == cells.Length)
                        {
                            Array.Resize(ref cells, cells.Length * 2);
                        }
                        cells[count++] = i + k;
                    }
                }
            }
            for (; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    if (count == cells.Length)
                    {
                        Array.Resize(ref cells, cells.Length * 2);
                    }
                    cells[count++] = i;
                }
            }
            for (int k = 0; k < count; k++)
            {
                int index3D = cells[k];
                cells[k] = index3D % verticalStride * layers + index3D / verticalStride;
            }
            Array.Sort(cells, 0, count);
            for (int k = 0; k < count; k++)
            {
                int key = cells[k];
                cells[k] = key / layers + key % layers * verticalStride;
            }
            job.Cells = cells;
            job.Count = count;
        }

        private static bool Run(Kind kind, Cells cells, bool[] flags, MapIndexService mapIndex, List<int> visited)
        {
            Vector3Int size = mapIndex.TerrainSize;
            int stride = mapIndex.Stride;
            int verticalStride = mapIndex.VerticalStride;
            if (stride != size.x + 2 || verticalStride != stride * (size.y + 2) || flags.Length % verticalStride != 0)
            {
                // Not the layout this was written for; the game's loop runs.
                return true;
            }
            visited?.Clear();
            cells.ChangedCount = 0;
            long started = Stopwatch.GetTimestamp();
            Job job = UsableJob(kind, flags, verticalStride, stride, size.x, size.y);
            if (job != null)
            {
                Apply(job, cells);
                _listPasses++;
                _workerStopwatchTicks += job.WorkerStopwatchTicks;
            }
            else
            {
                Scan(flags, verticalStride, stride, size.x, size.y, cells, out int blocks, out int skipped);
                _scanPasses++;
                _blocks += blocks;
                _blocksSkipped += skipped;
            }
            _stopwatchTicks += Stopwatch.GetTimestamp() - started;
            _passes++;
            _changed += cells.ChangedCount;
            if (visited != null)
            {
                Verify(cells, flags, mapIndex, visited);
            }
            return false;
        }

        // The list for these flags, finished and untouched since, or null.
        private static Job UsableJob(Kind kind, bool[] flags, int verticalStride, int stride, int width, int height)
        {
            if (!_listsActive)
            {
                return null;
            }
            Job job = Volatile.Read(ref kind.Current);
            if (job == null)
            {
                return null;
            }
            if (!job.Done)
            {
                _notReady++;
                return null;
            }
            if (job.Failure != null)
            {
                ListsFail(job.Failure);
                return null;
            }
            if (!ReferenceEquals(job.Flags, flags) || job.VerticalStride != verticalStride || job.Stride != stride ||
                job.Width != width || job.Height != height)
            {
                _arrayChanged++;
                return null;
            }
            if (kind.MainEdits > 0)
            {
                _editedOnMain++;
                return null;
            }
            return job;
        }

        // The game's loop over the list: only tiles the game walks, only layers below the tile's column count.
        private static void Apply(Job job, Cells cells)
        {
            int[] list = job.Cells;
            int verticalStride = job.VerticalStride;
            int stride = job.Stride;
            for (int k = 0; k < job.Count; k++)
            {
                int index3D = list[k];
                int index2D = index3D % verticalStride;
                int x = index2D % stride;
                int y = index2D / stride;
                if (x < 1 || x > job.Width || y < 1 || y > job.Height)
                {
                    continue;
                }
                int layer = index3D / verticalStride;
                if (layer < cells.ColumnCount(index2D))
                {
                    cells.Changed(index2D, index3D);
                }
            }
        }

        // Tiles in the game's order (MapIndexService.Indices2D: rows 1..height, columns 1..width, stride width + 2),
        // layers bottom to top, the game's inner loop for every tile of a group with any flag set.
        internal static void Scan(bool[] flags, int verticalStride, int stride, int width, int height, Cells cells,
            out int blocks, out int skipped)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<bool>(flags));
            int layers = flags.Length / verticalStride;
            blocks = 0;
            skipped = 0;
            for (int y = 1; y <= height; y++)
            {
                int row = y * stride;
                for (int x = 1; x <= width; x += Block)
                {
                    int tiles = Math.Min(Block, width - x + 1);
                    blocks++;
                    bool any = false;
                    for (int layer = 0; layer < layers && !any; layer++)
                    {
                        int start = layer * verticalStride + row + x;
                        if (start + Block <= bytes.Length)
                        {
                            any = MemoryMarshal.Read<ulong>(bytes.Slice(start, Block)) != 0;
                        }
                        else
                        {
                            for (int k = 0; k < tiles && !any; k++)
                            {
                                any = bytes[start + k] != 0;
                            }
                        }
                    }
                    if (!any)
                    {
                        skipped++;
                        continue;
                    }
                    for (int k = 0; k < tiles; k++)
                    {
                        int current = row + x + k;
                        int columnCount = cells.ColumnCount(current);
                        for (int i = 0; i < columnCount; i++)
                        {
                            int index3D = current + i * verticalStride;
                            if (flags[index3D])
                            {
                                cells.Changed(current, index3D);
                            }
                        }
                    }
                }
            }
        }

        // The game's loop, reading only, to compare which cells it would have updated.
        private static void Verify(Cells cells, bool[] flags, MapIndexService mapIndex, List<int> visited)
        {
            Expected.Clear();
            int verticalStride = mapIndex.VerticalStride;
            Index2DEnumerator enumerator = mapIndex.Indices2D.GetEnumerator();
            while (enumerator.MoveNext())
            {
                int current = enumerator.Current;
                int columnCount = cells.ColumnCount(current);
                for (int i = 0; i < columnCount; i++)
                {
                    int index3D = current + i * verticalStride;
                    if (flags[index3D])
                    {
                        Expected.Add(index3D);
                    }
                }
            }
            bool same = Expected.Count == visited.Count;
            for (int i = 0; same && i < Expected.Count; i++)
            {
                same = Expected[i] == visited[i];
            }
            if (!same)
            {
                _verifyMismatches++;
                if (_verifyMismatches <= 10)
                {
                    Log.Warning($"SoilScans verify: the game would have updated {Expected.Count} cells, the mod updated " +
                                $"{visited.Count}.");
                }
            }
        }

        private static bool Fail(Exception exception)
        {
            // The game's loop now runs over the same flags. A cell this pass already updated stores the new level
            // already, so the game passes it over except for asking the soil's look to change again to the same
            // value.
            _active = false;
            Log.Warning("SoilScans failed and turned itself off for this session: " + exception);
            return true;
        }

        private static void ListsFail(Exception exception)
        {
            // The scan on the main thread carries on; only the lists from the workers are given up.
            _listsActive = false;
            Volatile.Write(ref MoistureKind.Current, null);
            Volatile.Write(ref ContaminationKind.Current, null);
            Log.Warning("SoilScans: the lists of changed cells from the workers failed and are off for this session; the " +
                        "map is scanned on the main thread instead: " + exception);
        }
    }
}
