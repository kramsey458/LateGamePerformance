using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LateGamePerformance;
using Timberborn.MapIndexSystem;
using Timberborn.MapStateSystem;
using Timberborn.TerrainSystemRendering;
using Timberborn.TickSystem;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;
using Random = System.Random;

// The water map copy and the soil scans against the installed game's real classes.
//
// Water map: two real ThreadSafeWaterMaps read one real WaterSimulator. One only ever runs the game's own Tick;
// the other goes through the mod (copy on the "worker" after the simulated water tasks, swap in the tick). After
// every tick both must hold the same bytes, through normal ticks, layout changes, resets and a new water layer.
//
// Soil: two identical real SoilMoistureServices (and SoilContaminationServices) over the same flags; one runs the
// game's UpdateMoistureLevels, the other the mod's scan. The stored levels, the order of the dry/contaminated
// object lookups and the soil texture changes queued must be the same.
internal static class WaterAndSoilTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public class Fake : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return Handler(targetMethod, args);
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
    {
        T proxy = DispatchProxy.Create<T, Fake>();
        ((Fake)(object)proxy).Handler = handler;
        return proxy;
    }

    public static void Run(Action<bool, string> check)
    {
        RunScanModel(check);
        RunWaterMap(check, verify: false);
        RunWaterMap(check, verify: true);
        RunWaterMapVerifyOnlyMeasures(check);
        RunWaterTiming(check);
        RunSoil(check);
        RunPlantWaterOnWorker(check);
        RunPlantWaterVerifyOnlyMeasures(check);
    }

    private static MapIndexService CreateMapIndex(int width, int height, int depth)
    {
        MapSize size = MapSize.NewMap(new Vector2Int(width, height));
        typeof(MapSize).GetProperty("TerrainSize").SetValue(size, new Vector3Int(width, height, depth));
        typeof(MapSize).GetProperty("TotalSize").SetValue(size, new Vector3Int(width, height, depth + 8));
        MapIndexService mapIndex = new MapIndexService(size);
        mapIndex.Load();
        return mapIndex;
    }

    private static TickOnlyArrayService CreateArrayService()
    {
        ITickableSingletonService ticks = Proxy<ITickableSingletonService>((method, _) =>
            method.Name == "get_ParalleTicklIsFinished" || method.Name == "get_IsStartingParallelTick" ? (object)true
            : throw new NotSupportedException(method.Name));
        return new TickOnlyArrayService(ticks);
    }

    private static object CreateArray(TickOnlyArrayService service, Type element, int size)
    {
        return typeof(TickOnlyArrayService).GetMethod("Create").MakeGenericMethod(element).Invoke(service, new object[] { size });
    }

    private static Array ArrayOf(object tickOnlyArray)
    {
        return (Array)tickOnlyArray.GetType().GetField("_array", Any).GetValue(tickOnlyArray);
    }

    // ---- Soil scan, pure ----

    private sealed class RecordingCells : SoilScans.Cells
    {
        public int[] Counts;
        public readonly List<int> Seen = new List<int>();

        public override int ColumnCount(int index2D) => Counts[index2D];

        public override void Changed(int index2D, int index3D) => Seen.Add(index3D);
    }

    private static void RunScanModel(Action<bool, string> check)
    {
        Random random = new Random(31);
        bool same = true, listSame = true;
        long skipped = 0, blocks = 0, listed = 0;
        foreach ((int width, int height) in new[] { (1, 1), (7, 3), (8, 8), (9, 5), (64, 64), (123, 77) })
        {
            int stride = width + 2, verticalStride = stride * (height + 2);
            foreach (int layers in new[] { 1, 2, 4 })
            {
                foreach (int density in new[] { 0, 1, 20, 500, 1000 })
                {
                    bool[] flags = new bool[verticalStride * layers];
                    for (int i = 0; i < flags.Length; i++) flags[i] = random.Next(1000) < density;
                    RecordingCells cells = new RecordingCells { Counts = new int[verticalStride] };
                    for (int i = 0; i < verticalStride; i++) cells.Counts[i] = random.Next(layers + 1);
                    SoilScans.Scan(flags, verticalStride, stride, width, height, cells, out int b, out int s);
                    blocks += b;
                    skipped += s;
                    List<int> expected = new List<int>();
                    for (int y = 1; y <= height; y++)
                    for (int x = 1; x <= width; x++)
                    {
                        int current = y * stride + x;
                        for (int i = 0; i < cells.Counts[current]; i++)
                        {
                            if (flags[current + i * verticalStride]) expected.Add(current + i * verticalStride);
                        }
                    }
                    same &= expected.Count == cells.Seen.Count;
                    for (int i = 0; same && i < expected.Count; i++) same &= expected[i] == cells.Seen[i];

                    // The worker's list over the same flags: every set flag, in the game's order over all tiles.
                    SoilScans.Job job = new SoilScans.Job { Flags = flags, VerticalStride = verticalStride, Stride = stride, Width = width, Height = height, Rows = height };
                    SoilScans.ListChangedCells(job);
                    List<int> all = new List<int>();
                    for (int tile = 0; tile < verticalStride; tile++)
                    for (int layer = 0; layer < layers; layer++)
                    {
                        if (flags[tile + layer * verticalStride]) all.Add(tile + layer * verticalStride);
                    }
                    listSame &= all.Count == job.Count;
                    for (int i = 0; listSame && i < all.Count; i++) listSame &= all[i] == job.Cells[i];
                    listed += job.Count;
                }
            }
        }
        check(same, "soil scan: the same cells in the same order as the game's loop, on 90 random maps");
        check(listSame && listed > 1000, $"soil lists: the worker's list holds every set flag in the game's order, on the same 90 maps ({listed} cells)");
        check(skipped > 0 && skipped < blocks, $"soil scan: groups with nothing set are passed over ({skipped} of {blocks})");

        // Timing on a 256 x 256 map with 3 soil layers and 1 cell in 500 changed, against the game's loop over the
        // same (interface-like, virtual) column count.
        {
            const int width = 256, height = 256, layers = 3;
            int stride = width + 2, verticalStride = stride * (height + 2);
            bool[] flags = new bool[verticalStride * layers];
            for (int i = 0; i < flags.Length; i++) flags[i] = random.Next(500) == 0;
            RecordingCells cells = new RecordingCells { Counts = new int[verticalStride] };
            for (int i = 0; i < verticalStride; i++) cells.Counts[i] = 1 + random.Next(layers);
            long game = 0, mod = 0, list = 0;
            for (int round = 0; round < 40; round++)
            {
                cells.Seen.Clear();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int y = 1; y <= height; y++)
                for (int x = 1; x <= width; x++)
                {
                    int current = y * stride + x;
                    int count = cells.ColumnCount(current);
                    for (int i = 0; i < count; i++)
                    {
                        if (flags[current + i * verticalStride]) cells.Changed(current, current + i * verticalStride);
                    }
                }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                int seen = cells.Seen.Count;
                cells.Seen.Clear();
                SoilScans.Scan(flags, verticalStride, stride, width, height, cells, out _, out _);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                SoilScans.Job job = new SoilScans.Job { Flags = flags, VerticalStride = verticalStride, Stride = stride, Width = width, Height = height, Rows = height };
                SoilScans.ListChangedCells(job);
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                same &= seen == cells.Seen.Count;
                if (round >= 10)
                {
                    game += t1 - t0;
                    mod += t2 - t1;
                    list += t3 - t2;
                }
            }
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / 30;
            Console.WriteLine($"     timing: soil scan of a 256 x 256 map, 3 layers, 1 cell in 500 changed: the game's loop {game * ms:0.000} ms, " +
                              $"the mod's scan {mod * ms:0.000} ms, the worker's list {list * ms:0.000} ms");
            check(same, "soil scan: the full-size map gives the same cells");
        }
    }

    // ---- Water map ----

    private sealed class WaterWorld
    {
        public MapIndexService MapIndex;
        public TickOnlyArrayService Arrays;
        public object Simulator;
        public object Game;
        public object Mod;
        public Type ColumnType;
        public Type OutflowsType;
        public int Layers;
    }

    private static WaterWorld CreateWaterWorld(int width = 48, int height = 40, object terrain = null)
    {
        Assembly water = typeof(ReadOnlyWaterColumn).Assembly;
        WaterWorld world = new WaterWorld { MapIndex = CreateMapIndex(width, height, 12), Arrays = CreateArrayService(), Layers = 3 };
        world.ColumnType = water.GetType("Timberborn.WaterSystem.WaterColumn", true);
        world.OutflowsType = water.GetType("Timberborn.WaterSystem.ColumnOutflows", true);
        Type simType = water.GetType("Timberborn.WaterSystem.WaterSimulator", true);
        object sim = RuntimeHelpers.GetUninitializedObject(simType);
        int verticalStride = world.MapIndex.VerticalStride;
        RouteMapsTests.SetField(sim, "_columnCounts", CreateArray(world.Arrays, typeof(byte), world.MapIndex.MaxIndex));
        RouteMapsTests.SetField(sim, "_waterColumns", CreateArray(world.Arrays, world.ColumnType, verticalStride * world.Layers));
        RouteMapsTests.SetField(sim, "_outflows", CreateArray(world.Arrays, world.OutflowsType, verticalStride * world.Layers));
        Type modification = simType.GetNestedType("Modification", Any);
        RouteMapsTests.SetField(sim, "_modifications", Activator.CreateInstance(typeof(Queue<>).MakeGenericType(modification)));
        simType.GetField("<MaxColumnCount>k__BackingField", Any).SetValue(sim, world.Layers);
        world.Simulator = sim;

        Type calculatorType = water.GetType("Timberborn.WaterSystem.FlowVectorCalculator", true);
        object calculator = Activator.CreateInstance(calculatorType, Any, null, new object[] { world.MapIndex }, null);
        RouteMapsTests.Call(calculator, "Load");
        Type mapType = water.GetType("Timberborn.WaterSystem.ThreadSafeWaterMap", true);
        object[] maps = new object[2];
        for (int i = 0; i < 2; i++)
        {
            maps[i] = Activator.CreateInstance(mapType, Any, null,
                new object[] { world.MapIndex, terrain, new WaterColumnRetriever(), calculator, sim }, null);
            RouteMapsTests.Call(maps[i], "Load");
            RouteMapsTests.Call(maps[i], "PostLoad");
        }
        world.Game = maps[0];
        world.Mod = maps[1];
        return world;
    }

    // What the water tasks do to the simulation's arrays, roughly: depths and flows move everywhere, and the
    // number of columns on some tiles changes too (which the game would flag; the copy must not care).
    private static void SimulateWaterTasks(WaterWorld world, Random random, bool changeCounts)
    {
        Array columns = ArrayOf(RouteMapsTests.GetField(world.Simulator, "_waterColumns"));
        Array outflows = ArrayOf(RouteMapsTests.GetField(world.Simulator, "_outflows"));
        byte[] counts = (byte[])ArrayOf(RouteMapsTests.GetField(world.Simulator, "_columnCounts"));
        int verticalStride = world.MapIndex.VerticalStride;
        int layers = columns.Length / verticalStride;
        FieldInfo floor = world.ColumnType.GetField("Floor"), ceiling = world.ColumnType.GetField("Ceiling"),
            depth = world.ColumnType.GetField("WaterDepth"), old = world.ColumnType.GetField("OldWaterDepth"),
            contamination = world.ColumnType.GetField("Contamination"), overflow = world.ColumnType.GetField("Overflow");
        FieldInfo[] sides =
        {
            world.OutflowsType.GetField("BottomFlow"), world.OutflowsType.GetField("LeftFlow"),
            world.OutflowsType.GetField("TopFlow"), world.OutflowsType.GetField("RightFlow")
        };
        FieldInfo list = world.OutflowsType.GetField("Outflows");
        for (int i = 0; i < columns.Length; i++)
        {
            if (random.Next(3) != 0) continue;
            object column = columns.GetValue(i);
            floor.SetValue(column, (byte)random.Next(5));
            ceiling.SetValue(column, (byte)random.Next(5, 12));
            depth.SetValue(column, (float)random.NextDouble() * 3);
            old.SetValue(column, (float)random.NextDouble() * 3);
            contamination.SetValue(column, (float)random.NextDouble());
            overflow.SetValue(column, (float)random.NextDouble());
            columns.SetValue(column, i);

            object flows = outflows.GetValue(i);
            foreach (FieldInfo side in sides)
            {
                side.SetValue(flows, new TargetedFlow((float)random.NextDouble() - 0.5f,
                    random.Next(4) == 0 ? -1 : random.Next(columns.Length)));
            }
            if (random.Next(6) == 0)
            {
                List<TargetedFlow> extra = new List<TargetedFlow>();
                int tile = i % verticalStride;
                for (int k = random.Next(1, 4); k > 0; k--)
                {
                    extra.Add(new TargetedFlow((float)random.NextDouble(), tile + random.Next(layers) * verticalStride));
                }
                list.SetValue(flows, extra);
            }
            else
            {
                list.SetValue(flows, null);
            }
            outflows.SetValue(flows, i);
        }
        if (changeCounts)
        {
            for (int i = 0; i < counts.Length; i++)
            {
                if (random.Next(4) == 0) counts[i] = (byte)random.Next(layers + 1);
            }
        }
    }

    private static void AddLayer(WaterWorld world)
    {
        // What IncreaseMaxColumnCount does, from the simulation's side.
        world.Layers++;
        int size = world.Layers * world.MapIndex.VerticalStride;
        RouteMapsTests.Call(RouteMapsTests.GetField(world.Simulator, "_waterColumns"), "Resize", size);
        RouteMapsTests.Call(RouteMapsTests.GetField(world.Simulator, "_outflows"), "Resize", size);
        world.Simulator.GetType().GetField("<MaxColumnCount>k__BackingField", Any).SetValue(world.Simulator, world.Layers);
    }

    private static bool SameMaps(WaterWorld world)
    {
        bool same = true;
        foreach (string field in new[] { "_threadSafeWaterColumns", "_waterFlowDirections", "_threadSafeColumnCounts" })
        {
            Array a = (Array)RouteMapsTests.GetField(world.Game, field), b = (Array)RouteMapsTests.GetField(world.Mod, field);
            same &= a.Length == b.Length && Bytes(a).SequenceEqual(Bytes(b));
        }
        same &= (int)RouteMapsTests.Get(world.Game, "MaxColumnCount") == (int)RouteMapsTests.Get(world.Mod, "MaxColumnCount");
        same &= (bool)RouteMapsTests.Get(world.Game, "AnyColumnChanged") == (bool)RouteMapsTests.Get(world.Mod, "AnyColumnChanged");
        return same;
    }

    private static ReadOnlySpan<byte> Bytes(Array array)
    {
        switch (array)
        {
            case ReadOnlyWaterColumn[] columns: return MemoryMarshal.AsBytes(new ReadOnlySpan<ReadOnlyWaterColumn>(columns));
            case Vector2[] flows: return MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2>(flows));
            case byte[] counts: return counts;
            default: throw new NotSupportedException(array.GetType().Name);
        }
    }

    private enum Round { Normal, CountsMove, LayoutChanged, Reset, SimulatorFlagsChange, NewLayer, NoCopy }

    private static void RunWaterMap(Action<bool, string> check, bool verify)
    {
        WaterMapCopy.CreateFeature(new Config { WaterMapCopyVerify = verify }).Patches[0].Target();
        WaterMapCopy.SceneCreated();
        WaterMapCopy.Activate();
        WaterWorld world = CreateWaterWorld();
        Random random = new Random(verify ? 5 : 6);
        Round[] rounds =
        {
            Round.NoCopy, Round.Normal, Round.Normal, Round.CountsMove, Round.Normal, Round.LayoutChanged, Round.Normal,
            Round.Reset, Round.SimulatorFlagsChange, Round.Normal, Round.NewLayer, Round.Normal, Round.CountsMove,
            Round.Normal, Round.NoCopy, Round.Normal
        };
        WaterMapCopy.TakeStatsLine();
        bool same = true, differed = false;
        int expectedSwaps = 0;
        byte[] before = null;
        foreach (Round round in rounds)
        {
            PropertyInfo anyChanged = world.Simulator.GetType().GetProperty("AnyColumnChanged");
            anyChanged.SetValue(world.Simulator, false);
            // The game's StartParallelTick: the mod sets up the copy, then the water tasks run, the last one
            // followed by the mod's copy on its worker.
            WaterMapCopy.StartParallelTickPrefix(world.Simulator);
            SimulateWaterTasks(world, random, round == Round.CountsMove);
            if (round != Round.NoCopy)
            {
                same &= WaterMapCopy.FillForTests();
            }
            // Between the tasks and the next tick: what only the main thread may do to the simulation.
            switch (round)
            {
                case Round.LayoutChanged:
                    // A change was queued and is applied now (ProcessModifications with something in the queue).
                    object queue = RouteMapsTests.GetField(world.Simulator, "_modifications");
                    Type simType = world.Simulator.GetType();
                    object change = Activator.CreateInstance(simType.GetNestedType("Modification", Any), Any, null,
                        new[] { true, new Vector3Int(3, 3, 1), Enum.ToObject(simType.GetNestedType("ChangeType", Any), 0) }, null);
                    queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { change });
                    WaterMapCopy.ProcessModificationsPrefix(world.Simulator);
                    SimulateWaterTasks(world, random, false);
                    break;
                case Round.Reset:
                    WaterMapCopy.SimulationResetPrefix();
                    SimulateWaterTasks(world, random, false);
                    break;
                case Round.SimulatorFlagsChange:
                    anyChanged.SetValue(world.Simulator, true);
                    break;
                case Round.NewLayer:
                    AddLayer(world);
                    SimulateWaterTasks(world, random, true);
                    break;
                case Round.Normal:
                case Round.CountsMove:
                    expectedSwaps++;
                    break;
            }
            if (round == Round.LayoutChanged)
            {
                object queue = RouteMapsTests.GetField(world.Simulator, "_modifications");
                queue.GetType().GetMethod("Clear").Invoke(queue, null);
            }
            before ??= Bytes((Array)RouteMapsTests.GetField(world.Game, "_threadSafeWaterColumns")).ToArray();
            RouteMapsTests.Call(world.Game, "Tick");
            if (WaterMapCopy.MapUpdatePrefix(world.Mod))
            {
                RouteMapsTests.Call(world.Mod, "Tick");
            }
            WaterMapCopy.MapUpdatePostfix(world.Mod);
            same &= SameMaps(world);
            differed |= !Bytes((Array)RouteMapsTests.GetField(world.Game, "_threadSafeWaterColumns")).SequenceEqual(before);
        }
        string stats = WaterMapCopy.TakeStatsLine();
        Console.WriteLine("     " + stats);
        string mode = verify ? "verify mode" : "water map copy";
        check(differed, $"{mode}: the test water actually changes between ticks");
        check(same, $"{mode}: after every tick the map holds the same bytes as the game's own copy " +
                    "(normal ticks, moving column counts, layout changes, a reset, a new water layer, a missing copy)");
        check(WaterMapCopy.IsActive, $"{mode}: the feature is still active");
        if (verify)
        {
            check(stats.StartsWith($"WaterMapCopy: {expectedSwaps} ticks swapped in") &&
                  stats.Contains($"; {expectedSwaps} compared with the game's own copy first, verify mismatches 0"),
                $"verify mode: every usable copy was compared with the game's, none differed, and all {expectedSwaps} were swapped in " +
                "as with verify off");
        }
        else
        {
            check(stats.StartsWith($"WaterMapCopy: {expectedSwaps} ticks swapped in") &&
                  stats.Contains("in 4 ticks where the water layout changed and 2 where no copy was ready"),
                $"water map copy: swapped in on the {expectedSwaps} ordinary ticks, the game copied on the others");
        }
        WaterMapCopy.SceneCreated();
    }

    // A verify key is each player's own, so it must not change the water map the game reads. The worker's copy is made
    // to differ from the game's (one byte changed right after it was made, as a copying bug would): with the key off it
    // is swapped in; with the key on the difference is logged and the same copy is swapped in all the same.
    private static void RunWaterMapVerifyOnlyMeasures(Action<bool, string> check)
    {
        Action<WaterMapCopy.Job> previous = WaterMapCopy.AfterCopy;
        byte[] Handed(bool verify, out byte[] games, out string stats)
        {
            WaterMapCopy.CreateFeature(new Config { WaterMapCopyVerify = verify }).Patches[0].Target();
            WaterMapCopy.SceneCreated();
            WaterMapCopy.Activate();
            WaterMapCopy.AfterCopy = null;
            WaterWorld world = CreateWaterWorld();
            Random random = new Random(41);
            PropertyInfo anyChanged = world.Simulator.GetType().GetProperty("AnyColumnChanged");
            anyChanged.SetValue(world.Simulator, false);
            // A first tick without a copy, in which the mod meets the map; then one with the worker's copy, spoilt.
            for (int tick = 0; tick < 2; tick++)
            {
                WaterMapCopy.StartParallelTickPrefix(world.Simulator);
                SimulateWaterTasks(world, random, false);
                if (tick == 1)
                {
                    WaterMapCopy.AfterCopy = job => MemoryMarshal.AsBytes(job.SpareColumns.AsSpan())[5] ^= 0x5A;
                    WaterMapCopy.FillForTests();
                    WaterMapCopy.AfterCopy = null;
                }
                RouteMapsTests.Call(world.Game, "Tick");
                if (WaterMapCopy.MapUpdatePrefix(world.Mod))
                {
                    RouteMapsTests.Call(world.Mod, "Tick");
                }
                WaterMapCopy.MapUpdatePostfix(world.Mod);
            }
            stats = WaterMapCopy.TakeStatsLine();
            games = State(world.Game);
            return State(world.Mod);
        }

        // Everything the game reads from the map: the columns, the flows, the column counts and the changed flag.
        byte[] State(object map)
        {
            List<byte> state = new List<byte>();
            foreach (string field in new[] { "_threadSafeWaterColumns", "_waterFlowDirections", "_threadSafeColumnCounts" })
            {
                state.AddRange(Bytes((Array)RouteMapsTests.GetField(map, field)).ToArray());
            }
            state.Add((bool)map.GetType().GetProperty("AnyColumnChanged").GetValue(map) ? (byte)1 : (byte)0);
            return state.ToArray();
        }

        byte[] off = Handed(false, out byte[] games, out _);
        byte[] on = Handed(true, out _, out string verifyStats);
        Console.WriteLine("     " + verifyStats);
        check(!off.AsSpan().SequenceEqual(games) && off.AsSpan().SequenceEqual(on) && verifyStats.Contains("verify mismatches 1"),
            "water map copy verify: the worker's copy differed from the game's and that was logged, and the map holds the " +
            "worker's copy, flows, column counts and changed flag, as with verify off");
        WaterMapCopy.AfterCopy = previous;
        WaterMapCopy.SceneCreated();
    }

    // A full-size map (256 x 256, three water layers), filled once: what the game's copy costs on the main thread,
    // what the mod's costs on the worker, and what is left on the main thread.
    private static void RunWaterTiming(Action<bool, string> check)
    {
        WaterMapCopy.CreateFeature(new Config()).Patches[0].Target();
        WaterMapCopy.SceneCreated();
        WaterMapCopy.Activate();
        WaterWorld world = CreateWaterWorld(256, 256);
        Random random = new Random(8);
        SimulateWaterTasks(world, random, true);
        const int rounds = 60, warmUp = 10;
        long game = 0, worker = 0, main = 0;
        bool same = true;
        WaterMapCopy.MapUpdatePrefix(world.Mod);
        RouteMapsTests.Call(world.Mod, "Tick");
        for (int round = 0; round < rounds; round++)
        {
            WaterMapCopy.StartParallelTickPrefix(world.Simulator);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            same &= WaterMapCopy.FillForTests();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            RouteMapsTests.Call(world.Game, "Tick");
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            same &= !WaterMapCopy.MapUpdatePrefix(world.Mod);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (round >= warmUp)
            {
                worker += t1 - t0;
                game += t2 - t1;
                main += t3 - t2;
            }
        }
        same &= SameMaps(world);
        double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / (rounds - warmUp);
        Console.WriteLine($"     timing: water map copy on a 256 x 256 map with 3 layers: the game {game * ms:0.000} ms on the main " +
                          $"thread; the mod {worker * ms:0.000} ms on a worker and {main * ms:0.0000} ms on the main thread");
        check(same, "water map copy: a full-size map is swapped in every tick and ends up identical to the game's");
        WaterMapCopy.TakeStatsLine();
        WaterMapCopy.SceneCreated();
    }

    // ---- Soil ----

    // The game's per-cell method pulls in the soil texture map, whose static constructor calls into Unity's native
    // code, so it cannot run here. The test records the calls the mod makes to it instead, and compares them with
    // the calls the game's loop (UpdateMoistureLevels / UpdateContaminationLevels, as decompiled) makes over the
    // same real services.
    private static void RunSoil(Action<bool, string> check)
    {
        SoilScans.CreateFeature(new Config { SoilScansVerify = true }).Patches[0].Target();
        SoilScans.Activate();
        List<string> calls = new List<string>();
        SoilScans.SetMoistureForTests((_, coordinates, index, level) => calls.Add("m " + coordinates + " " + index + " " + level));
        SoilScans.SetContaminationForTests((_, coordinates, index, level) => calls.Add("c " + coordinates + " " + index + " " + level));

        const int width = 70, height = 50, depth = 10, layers = 3;
        MapIndexService mapIndex = CreateMapIndex(width, height, depth);
        TickOnlyArrayService arrays = CreateArrayService();
        int verticalStride = mapIndex.VerticalStride;
        Random random = new Random(12);
        int[] columnCounts = new int[verticalStride];
        for (int i = 0; i < columnCounts.Length; i++) columnCounts[i] = random.Next(layers + 1);
        Func<int, int> ceiling = index3D => 1 + (index3D * 7 + index3D / verticalStride * 3) % (depth - 1);
        Func<MethodInfo, object[], object> terrain = (method, args) =>
            method.Name == "GetColumnCount" ? columnCounts[(int)args[0]]
            : method.Name == "GetColumnCeiling" ? ceiling((int)args[0])
            : throw new NotSupportedException(method.Name);

        Assembly moistureAssembly = Assembly.Load("Timberborn.SoilMoistureSystem");
        Assembly contaminationAssembly = Assembly.Load("Timberborn.SoilContaminationSystem");
        object mSim = RuntimeHelpers.GetUninitializedObject(
            moistureAssembly.GetType("Timberborn.SoilMoistureSystem.SoilMoistureSimulator", true));
        object mFlags = CreateArray(arrays, typeof(bool), verticalStride * layers);
        object mLevels = CreateArray(arrays, typeof(float), verticalStride * layers);
        RouteMapsTests.SetField(mSim, "_moistureLevelsChangedLastTick", mFlags);
        RouteMapsTests.SetField(mSim, "_moistureLevels", mLevels);
        object moisture = RuntimeHelpers.GetUninitializedObject(
            moistureAssembly.GetType("Timberborn.SoilMoistureSystem.SoilMoistureService", true));
        RouteMapsTests.SetField(moisture, "_soilMoistureSimulator", mSim);
        RouteMapsTests.SetField(moisture, "_mapIndexService", mapIndex);
        RouteMapsTests.SetField(moisture, "_threadSafeColumnTerrainMap", Proxy<Timberborn.TerrainSystem.IThreadSafeColumnTerrainMap>(terrain));

        object cSim = RuntimeHelpers.GetUninitializedObject(
            contaminationAssembly.GetType("Timberborn.SoilContaminationSystem.SoilContaminationSimulator", true));
        object cFlags = CreateArray(arrays, typeof(bool), verticalStride * layers);
        object cLevels = CreateArray(arrays, typeof(float), verticalStride * layers);
        RouteMapsTests.SetField(cSim, "_contaminationsChangedLastTick", cFlags);
        RouteMapsTests.SetField(cSim, "_contaminationLevels", cLevels);
        object contamination = RuntimeHelpers.GetUninitializedObject(
            contaminationAssembly.GetType("Timberborn.SoilContaminationSystem.SoilContaminationService", true));
        RouteMapsTests.SetField(contamination, "_soilContaminationSimulator", cSim);
        RouteMapsTests.SetField(contamination, "_mapIndexService", mapIndex);
        RouteMapsTests.SetField(contamination, "_terrainService", Proxy<Timberborn.TerrainSystem.ITerrainService>(terrain));

        bool same = true, handled = true;
        int changes = 0;
        SoilScans.TakeStatsLine();
        foreach (int density in new[] { 0, 2, 30, 300, 1000, 2 })
        {
            foreach ((string kind, object flagArray, object levelArray) in new[] { ("m", mFlags, mLevels), ("c", cFlags, cLevels) })
            {
                bool[] flags = (bool[])ArrayOf(flagArray);
                float[] levels = (float[])ArrayOf(levelArray);
                for (int i = 0; i < flags.Length; i++)
                {
                    flags[i] = random.Next(1000) < density;
                    levels[i] = (float)random.NextDouble() * 8;
                }
                // The game's loop.
                List<string> expected = new List<string>();
                Index2DEnumerator enumerator = mapIndex.Indices2D.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    int current = enumerator.Current;
                    for (int i = 0; i < columnCounts[current]; i++)
                    {
                        int index = current + i * verticalStride;
                        if (flags[index])
                        {
                            expected.Add(kind + " " + mapIndex.IndexToCoordinates(current, ceiling(index)) + " " + index + " " + levels[index]);
                        }
                    }
                }
                calls.Clear();
                handled &= kind == "m" ? !SoilScans.MoisturePrefix(moisture) : !SoilScans.ContaminationPrefix(contamination);
                same &= expected.Count == calls.Count;
                for (int i = 0; same && i < expected.Count; i++) same &= expected[i] == calls[i];
                changes += expected.Count;
            }
        }
        string stats = SoilScans.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(changes > 1000, $"soil: the test soil actually changes ({changes} cells over 12 passes)");
        RunSoilSample(check, contamination, cFlags, cLevels, mapIndex, random, columnCounts, ceiling, calls, ref handled, ref same);
        RunSoilLists(check, mSim, cSim, moisture, contamination, mapIndex, arrays, random, columnCounts, ceiling, calls);
        check(handled, "soil: every pass over the real services went through the mod");
        check(same, "soil: the game's per-cell method is called for the same cells, with the same coordinates and levels, " +
                    "in the same order as the game's own loop");
        check(stats.Contains("verify mismatches 0") && SoilScans.IsActive, "soil: verify mode agrees and the feature is still active");
    }

    // Measurement only (0.4.28): in every 8th contamination pass the mod counts the changed cells that neither enter nor
    // leave the contaminated state and whose soil colour value is bit for bit unchanged, the ones a "write the level
    // only" fast path would take (not built: not exact, see SoilScans). Counted against the game's own rule as
    // decompiled (SetContaminationLevel's two branches, GetMapSoilContamination) over the service's own levels, which
    // the recorded per-cell call writes as the game's does last. The calls themselves must stay the game's.
    private static void RunSoilSample(Action<bool, string> check, object contamination, object cFlags, object cLevels,
        MapIndexService mapIndex, Random random, int[] columnCounts, Func<int, int> ceiling, List<string> calls, ref bool handled, ref bool same)
    {
        const float threshold = 0.5f, maxMap = 0.8f;
        int verticalStride = mapIndex.VerticalStride;
        bool[] flags = (bool[])ArrayOf(cFlags);
        float[] levels = (float[])ArrayOf(cLevels);
        float[] stored = new float[levels.Length];
        RouteMapsTests.SetField(contamination, "_threadSafeContaminationLevels", stored);
        RouteMapsTests.SetField(contamination, "_contaminationThreshold", threshold);
        RouteMapsTests.SetField(contamination, "_maxMapContamination", maxMap);
        SoilScans.SetContaminationForTests((_, coordinates, index, level) =>
        {
            calls.Add("c " + coordinates + " " + index + " " + level);
            stored[index] = level;
        });
        float Look(float contaminationLevel)
        {
            if (contaminationLevel > 0f)
            {
                if (contaminationLevel <= threshold)
                {
                    float num = 1f - contaminationLevel / threshold;
                    return 1f - maxMap * num;
                }
                return 1f;
            }
            return 0f;
        }
        float[] choices = { 0f, 0f, -0.25f, 0.2f, 0.25f, 0.5f, 0.9f, 1.4f, 3f };
        long expectedSame = 0, expectedSampled = 0;
        SoilScans.TakeStatsLine();
        for (int pass = 0; pass < 2 * SoilScans.SampleEvery; pass++)
        {
            for (int i = 0; i < flags.Length; i++)
            {
                flags[i] = random.Next(1000) < 300;
                levels[i] = random.Next(4) == 0 ? (float)random.NextDouble() : choices[random.Next(choices.Length)];
            }
            List<string> expected = new List<string>();
            bool sampled = SoilScans.NextContaminationPassSampled;
            Index2DEnumerator enumerator = mapIndex.Indices2D.GetEnumerator();
            while (enumerator.MoveNext())
            {
                int current = enumerator.Current;
                for (int i = 0; i < columnCounts[current]; i++)
                {
                    int index = current + i * verticalStride;
                    if (!flags[index]) continue;
                    expected.Add("c " + mapIndex.IndexToCoordinates(current, ceiling(index)) + " " + index + " " + levels[index]);
                    if (!sampled) continue;
                    float before = stored[index], after = levels[index];
                    bool enters = after > 0f && before <= 0f, leaves = !enters && after <= 0f && before > 0f;
                    expectedSampled++;
                    if (!enters && !leaves && BitConverter.SingleToInt32Bits(Look(before)) == BitConverter.SingleToInt32Bits(Look(after)))
                    {
                        expectedSame++;
                    }
                }
            }
            calls.Clear();
            handled &= !SoilScans.ContaminationPrefix(contamination);
            same &= calls.SequenceEqual(expected);
        }
        string stats = SoilScans.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(expectedSame > 0 && expectedSame < expectedSampled &&
              stats.Contains($"in every 8th contamination pass {expectedSame} of {expectedSampled} changed cells kept their contaminated state"),
            $"soil: the sampled count of contamination cells a level-only fast path would take matches the game's rule ({expectedSame} of " +
            $"{expectedSampled}), and the per-cell calls stay the game's");
    }

    private static void RunSoilLists(Action<bool, string> check, object mSim, object cSim, object moisture, object contamination,
        MapIndexService mapIndex, TickOnlyArrayService arrays, Random random, int[] columnCounts, Func<int, int> ceiling, List<string> calls)
    {
        int verticalStride = mapIndex.VerticalStride;

        // The lists made on the workers: the simulator publishes a job before its tasks, the worker that finishes
        // the last row lists the changed cells, the service goes through the list. Same calls as the game's loop;
        // and no list is used after a main-thread edit, when it is not finished, or when the flag array changed.
        SoilScans.CreateListsFeature().Patches[0].Target();
        SoilScans.ActivateLists();
        foreach (object sim in new[] { mSim, cSim })
        {
            RouteMapsTests.SetField(sim, "_mapIndexService", mapIndex);
            foreach (string field in new[] { "_actions", "_terrainHeightChanges" })
            {
                RouteMapsTests.SetField(sim, field, Activator.CreateInstance(sim.GetType().GetField(field, Any).FieldType));
            }
            RouteMapsTests.SetField(sim, "_simulationController", new Timberborn.SimulationSystem.SimulationController());
        }
        bool listSame = true, listHandled = true;
        int listChanges = 0;
        SoilScans.TakeStatsLine();
        string[] listRounds = { "list", "list", "edit", "list", "unfinished", "list", "replaced", "list", "reset", "list", "list" };
        foreach (string round in listRounds)
        {
            foreach ((string kind, object sim, SoilScans.Kind soilKind, object service) in new[]
                     {
                         ("m", mSim, SoilScans.MoistureKind, moisture), ("c", cSim, SoilScans.ContaminationKind, contamination)
                     })
            {
                string flagField = kind == "m" ? "_moistureLevelsChangedLastTick" : "_contaminationsChangedLastTick";
                string levelField = kind == "m" ? "_moistureLevels" : "_contaminationLevels";
                bool[] flags = (bool[])ArrayOf(RouteMapsTests.GetField(sim, flagField));
                float[] levels = (float[])ArrayOf(RouteMapsTests.GetField(sim, levelField));
                int density = round == "list" ? 1 + random.Next(40) : 25;
                for (int i = 0; i < flags.Length; i++)
                {
                    flags[i] = random.Next(1000) < density;
                    levels[i] = (float)random.NextDouble() * 8;
                }
                // The game's StartParallelTick, the soil task's rows on the workers.
                if (kind == "m") SoilScans.MoistureStartPrefix(sim);
                else SoilScans.ContaminationStartPrefix(sim);
                if (round != "unfinished") listHandled &= SoilScans.FinishJobForTests(soilKind);
                switch (round)
                {
                    case "edit":
                    {
                        // The simulator's own tick has a terrain height change to apply: a main-thread edit.
                        object changes = RouteMapsTests.GetField(sim, "_terrainHeightChanges");
                        changes.GetType().GetMethod("Add").Invoke(changes, new[] { Activator.CreateInstance(changes.GetType().GetGenericArguments()[0]) });
                        if (kind == "m") SoilScans.MoistureTickPrefix(sim);
                        else SoilScans.ContaminationTickPrefix(sim);
                        changes.GetType().GetMethod("Clear").Invoke(changes, null);
                        break;
                    }
                    case "replaced":
                    {
                        object fresh = CreateArray(arrays, typeof(bool), flags.Length);
                        Array.Copy(flags, (bool[])ArrayOf(fresh), flags.Length);
                        RouteMapsTests.SetField(sim, flagField, fresh);
                        flags = (bool[])ArrayOf(fresh);
                        break;
                    }
                    case "reset":
                    {
                        Timberborn.SimulationSystem.SimulationController controller =
                            (Timberborn.SimulationSystem.SimulationController)RouteMapsTests.GetField(sim, "_simulationController");
                        controller.ResetSimulation();
                        if (kind == "m") SoilScans.MoistureTickPrefix(sim);
                        else SoilScans.ContaminationTickPrefix(sim);
                        controller.Tick();
                        controller.Tick();
                        break;
                    }
                }
                List<string> expected = new List<string>();
                Index2DEnumerator enumerator = mapIndex.Indices2D.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    int current = enumerator.Current;
                    for (int i = 0; i < columnCounts[current]; i++)
                    {
                        int index = current + i * verticalStride;
                        if (flags[index])
                        {
                            expected.Add(kind + " " + mapIndex.IndexToCoordinates(current, ceiling(index)) + " " + index + " " + levels[index]);
                        }
                    }
                }
                calls.Clear();
                listHandled &= kind == "m" ? !SoilScans.MoisturePrefix(service) : !SoilScans.ContaminationPrefix(service);
                listSame &= expected.Count == calls.Count;
                for (int i = 0; listSame && i < expected.Count; i++) listSame &= expected[i] == calls[i];
                listChanges += expected.Count;
            }
        }
        string listStats = SoilScans.TakeStatsLine();
        Console.WriteLine("     " + listStats);
        check(listChanges > 1000 && listHandled, $"soil lists: every pass went through the mod and the soil changed ({listChanges} cells)");
        check(listSame, "soil lists: the game's per-cell method is called for the same cells, in the same order, whether from a worker's list or the scan");
        check(listStats.Contains("22 moisture and contamination passes") && listStats.Contains("; 14 went through a list of changed cells made on a worker") &&
              listStats.Contains(", 8 scanned the map on the main thread") && listStats.Contains("lists not usable: 2 not finished, 4 after a main-thread edit, 2 after the flag array was replaced") &&
              listStats.Contains("verify mismatches 0") && SoilScans.ListsActive,
            "soil lists: fourteen passes used a worker's list, eight fell back for the right reasons, verify agreed, lists still active");
        SoilScans.SceneCreated();
    }

    // ---- Plant water levels computed on the water worker ----

    // Two sets of the game's real WaterObjects over the two real maps of a water world: one set is ticked by the
    // game's own WaterObjectService over the map that only ever runs the game's Tick, the other by the mod over
    // the map that goes through the copy and the swap. The levels stored and the events raised must be the same,
    // through ordinary ticks (levels from the worker), a tick where an object left the list after the snapshot,
    // a tick where the water layout changed (no copy to use), a tick where the mirror was wrong (rebuilt from the
    // game's list) and ticks in verify mode, where the worker's levels are compared with the real map's method.
    private static void RunPlantWaterOnWorker(Action<bool, string> check)
    {
        const int width = 64, height = 56, count = 3000;
        object terrain = Proxy<Timberborn.TerrainSystem.ITerrainService>((method, args) =>
            method.Name == "Contains" && args[0] is Vector2Int xy
                ? (object)(xy.x >= 0 && xy.x < width && xy.y >= 0 && xy.y < height)
                : throw new NotSupportedException(method.Name));
        WaterMapCopy.CreateFeature(new Config()).Patches[0].Target();
        WaterMapCopy.SceneCreated();
        WaterMapCopy.Activate();
        PlantWater.CreateFeature(new Config()).Patches[0].Target();
        PlantWater.Activate();
        PlantWater.UseWaterMapCopy();
        WaterWorld world = CreateWaterWorld(width, height, terrain);
        Random random = new Random(21);
        // Column counts: the game copies them into its map when the simulation says a column changed.
        SimulateWaterTasks(world, random, true);
        PropertyInfo anyChanged = world.Simulator.GetType().GetProperty("AnyColumnChanged");
        anyChanged.SetValue(world.Simulator, true);
        RouteMapsTests.Call(world.Game, "Tick");
        if (WaterMapCopy.MapUpdatePrefix(world.Mod)) RouteMapsTests.Call(world.Mod, "Tick");
        anyChanged.SetValue(world.Simulator, false);

        Type objectType = typeof(WaterObject);
        FieldInfo mapField = objectType.GetField("_threadSafeWaterMap", Any), tileField = objectType.GetField("_baseCoordinates", Any);
        PropertyInfo level = objectType.GetProperty("WaterAboveBase");
        EventInfo changed = objectType.GetEvent("WaterAboveBaseChanged");
        MethodInfo currentLevel = objectType.GetMethod("CurrentWaterAboveBase", Any);
        MethodInfo storeLevel = objectType.GetMethod("UpdateWaterAboveBase", Any, null, new[] { typeof(int) }, null);
        WaterObjectService[] services = { new WaterObjectService(), new WaterObjectService() };
        List<WaterObject>[] objects = { new List<WaterObject>(), new List<WaterObject>() };
        List<string>[] events = { new List<string>(), new List<string>() };
        Random tiles = new Random(22);
        // What WaterObject.OnEnterFinishedPostLoadState does after registering: store the level the map shows now.
        void StoreLevelLikeTheGame(WaterObject waterObject)
        {
            storeLevel.Invoke(waterObject, new[] { currentLevel.Invoke(waterObject, new object[] { tileField.GetValue(waterObject) }) });
        }
        void AddObject(Vector3Int tile, bool likeTheGame = false)
        {
            for (int w = 0; w < 2; w++)
            {
                WaterObject waterObject = (WaterObject)RuntimeHelpers.GetUninitializedObject(objectType);
                mapField.SetValue(waterObject, w == 0 ? world.Game : world.Mod);
                tileField.SetValue(waterObject, tile);
                int index = objects[w].Count, which = w;
                changed.AddEventHandler(waterObject, new EventHandler((sender, _) => events[which].Add(index + "=" + level.GetValue(sender))));
                services[w].RegisterWaterObject(waterObject);
                if (w == 1) PlantWater.RegisterPostfix(waterObject);
                if (likeTheGame) StoreLevelLikeTheGame(waterObject);
                objects[w].Add(waterObject);
            }
        }
        for (int i = 0; i < count; i++)
        {
            // Mostly on the map, a few beyond its edge, at heights around the water.
            AddObject(new Vector3Int(tiles.Next(-1, width + 2), tiles.Next(-1, height + 2), tiles.Next(0, 7)));
        }
        PlantWater.TakeStatsLine();
        WaterMapCopy.TakeStatsLine();

        string[] rounds =
        {
            "normal", "normal", "normal", "left", "normal", "layout", "normal", "wrong mirror", "normal", "joined", "flood", "normal",
            "registered", "normal", "re-registered", "normal", "game loop", "normal", "verify", "verify", "verify"
        };
        bool same = true, handled = true;
        int totalEvents = 0;
        long knownMismatchesBefore = PlantWater.KnownMismatches;   // counted over the session, like verify mismatches
        foreach (string round in rounds)
        {
            PlantWater.SetVerifyForTests(round == "verify");
            // The game's StartParallelTick: the copy is set up (and the snapshot taken), the water tasks run, the
            // last one followed by the copy and the levels on its worker.
            WaterMapCopy.StartParallelTickPrefix(world.Simulator);
            SimulateWaterTasks(world, random, false);
            same &= WaterMapCopy.FillForTests();
            switch (round)
            {
                case "left":
                {
                    // An object leaves both worlds after the snapshot was taken.
                    int victim = random.Next(objects[0].Count);
                    services[0].UnregisterWaterObject(objects[0][victim]);
                    services[1].UnregisterWaterObject(objects[1][victim]);
                    PlantWater.UnregisterPostfix(objects[1][victim]);
                    objects[0].RemoveAt(victim);
                    objects[1].RemoveAt(victim);
                    break;
                }
                case "layout":
                {
                    object queue = RouteMapsTests.GetField(world.Simulator, "_modifications");
                    Type simType = world.Simulator.GetType();
                    object change = Activator.CreateInstance(simType.GetNestedType("Modification", Any), Any, null,
                        new[] { true, new Vector3Int(3, 3, 1), Enum.ToObject(simType.GetNestedType("ChangeType", Any), 0) }, null);
                    queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { change });
                    WaterMapCopy.ProcessModificationsPrefix(world.Simulator);
                    SimulateWaterTasks(world, random, false);
                    queue.GetType().GetMethod("Clear").Invoke(queue, null);
                    break;
                }
                case "joined":
                {
                    // An object joins both worlds after the snapshot was taken: the worker has no level for it, so
                    // it is read on the main thread in the tick, beside the worker's levels for the rest.
                    AddObject(new Vector3Int(5, 5, 1));
                    break;
                }
                case "flood":
                {
                    // More than a quarter of the list joins after the snapshot: read on the main thread, and the
                    // mirror is rebuilt from the game's list as a precaution.
                    for (int k = 0; k < 1000; k++) AddObject(new Vector3Int(tiles.Next(0, width), tiles.Next(0, height), tiles.Next(0, 7)));
                    break;
                }
                case "wrong mirror":
                {
                    // An object leaves without the mod's hook seeing it: the mirror still holds it, and so does the
                    // next snapshot. The tick matches the game's list against the snapshot and passes it over.
                    int victim = random.Next(objects[0].Count);
                    services[0].UnregisterWaterObject(objects[0][victim]);
                    services[1].UnregisterWaterObject(objects[1][victim]);
                    objects[0].RemoveAt(victim);
                    objects[1].RemoveAt(victim);
                    break;
                }
                case "registered":
                {
                    // Three objects are built as the game builds them: registered, then their level stored from the map
                    // as it is now (before this tick's swap). The mod has no known level for them and reads it.
                    for (int k = 0; k < 3; k++) AddObject(new Vector3Int(tiles.Next(0, width), tiles.Next(0, height), tiles.Next(0, 4)), true);
                    break;
                }
                case "re-registered":
                {
                    // The last object leaves and comes back (unregistered, registered, level stored): the list looks
                    // as before, but the mod treats the entries registered since its last pass as unknown.
                    for (int w = 0; w < 2; w++)
                    {
                        WaterObject last = objects[w][objects[w].Count - 1];
                        services[w].UnregisterWaterObject(last);
                        if (w == 1) PlantWater.UnregisterPostfix(last);
                        services[w].RegisterWaterObject(last);
                        if (w == 1) PlantWater.RegisterPostfix(last);
                        StoreLevelLikeTheGame(last);
                    }
                    break;
                }
            }
            events[0].Clear();
            events[1].Clear();
            RouteMapsTests.Call(world.Game, "Tick");
            services[0].Tick();                                          // the game's own loop over the game's map
            if (WaterMapCopy.MapUpdatePrefix(world.Mod)) RouteMapsTests.Call(world.Mod, "Tick");
            if (round == "game loop")
            {
                // The game's own loop runs over the mod's world in place of the mod (the fallback, or another mod's
                // prefix): the postfix sees a call the prefix did not handle and every known level is forgotten.
                services[1].Tick();
                PlantWater.TickPostfix();
            }
            else
            {
                handled &= !PlantWater.TickPrefix(services[1]);          // the mod over the swapped-in copy
                PlantWater.TickPostfix();
            }
            same &= events[0].Count == events[1].Count;
            for (int i = 0; same && i < events[0].Count; i++) same &= events[0][i] == events[1][i];
            for (int i = 0; i < objects[0].Count; i++) same &= (int)level.GetValue(objects[0][i]) == (int)level.GetValue(objects[1][i]);
            totalEvents += events[0].Count;
        }
        string stats = PlantWater.TakeStatsLine();
        Console.WriteLine("     " + stats);
        Console.WriteLine("     " + WaterMapCopy.TakeStatsLine());
        check(totalEvents > 300, $"plant water on the worker: the test water actually moves ({totalEvents} level changes over 21 ticks)");
        check(same && handled, "plant water on the worker: the same levels and events as the game's own loop over the real water map, " +
                               "through ordinary ticks, an object leaving, a layout change, a wrong mirror, objects joining, objects " +
                               "registered as the game does it, an object registered again, a tick of the game's own loop and verify mode");
        check(stats.StartsWith("PlantWater: 20 passes over") && stats.Contains("; 19 used the water worker's levels") &&
              stats.Contains("1004 that joined the list after the snapshot were read on the main thread") &&
              stats.Contains(", 1 read every level inside the tick") && stats.Contains("1 had no copy to use") &&
              stats.Contains("the list changed in 3 ticks, the mirror was rebuilt 1 times") && stats.Contains("which happened 1 times") &&
              stats.Contains("verify mismatches 0, known levels that differed from the stored ones " + knownMismatchesBefore) &&
              PlantWater.KnownMismatches == knownMismatchesBefore && PlantWater.IsActive,
            "plant water on the worker: nineteen ticks used the worker's levels (objects leaving, seen or unseen by the hook, are matched " +
            "around), one fell back for the layout change, joiners were read on the main thread, the game's own loop made the mod " +
            "forget its known levels once, verify agreed");
        PlantWater.SetVerifyForTests(false);
        WaterMapCopy.SceneCreated();
        PlantWater.SceneCreated();
    }

    // A verify key is each player's own, so it must not change the levels the game stores. One object's level from the
    // water worker is made wrong right after the worker wrote it (as a lookup bug would): with the key off the tick
    // stores it; with the key on the main thread's own read differs, that is logged, and the same level is stored.
    // Once more with the water map's verify key on as well, as 'Verify every feature' sets them: the worker's copy then
    // goes in from the map's postfix, and the plant water pass must still get the worker's levels.
    private static void RunPlantWaterVerifyOnlyMeasures(Action<bool, string> check)
    {
        const int width = 32, height = 28, count = 600, spoilt = 3;
        Type objectType = typeof(WaterObject);
        FieldInfo mapField = objectType.GetField("_threadSafeWaterMap", Any), tileField = objectType.GetField("_baseCoordinates", Any);
        PropertyInfo level = objectType.GetProperty("WaterAboveBase");
        int[] Stored(bool verify, bool mapVerify, out int workers, out string stats)
        {
            object terrain = Proxy<Timberborn.TerrainSystem.ITerrainService>((method, args) =>
                method.Name == "Contains" && args[0] is Vector2Int xy
                    ? (object)(xy.x >= 0 && xy.x < width && xy.y >= 0 && xy.y < height)
                    : throw new NotSupportedException(method.Name));
            WaterMapCopy.CreateFeature(new Config { WaterMapCopyVerify = mapVerify }).Patches[0].Target();
            WaterMapCopy.SceneCreated();
            WaterMapCopy.Activate();
            PlantWater.CreateFeature(new Config()).Patches[0].Target();
            PlantWater.Activate();
            PlantWater.UseWaterMapCopy();
            PlantWater.SetVerifyForTests(verify);
            Action<WaterMapCopy.Job> levels = WaterMapCopy.AfterCopy;
            WaterWorld world = CreateWaterWorld(width, height, terrain);
            Random random = new Random(43), tiles = new Random(44);
            SimulateWaterTasks(world, random, true);
            PropertyInfo anyChanged = world.Simulator.GetType().GetProperty("AnyColumnChanged");
            anyChanged.SetValue(world.Simulator, true);
            if (WaterMapCopy.MapUpdatePrefix(world.Mod)) RouteMapsTests.Call(world.Mod, "Tick");
            WaterMapCopy.MapUpdatePostfix(world.Mod);
            anyChanged.SetValue(world.Simulator, false);

            WaterObjectService service = new WaterObjectService();
            List<WaterObject> objects = new List<WaterObject>();
            for (int i = 0; i < count; i++)
            {
                WaterObject waterObject = (WaterObject)RuntimeHelpers.GetUninitializedObject(objectType);
                mapField.SetValue(waterObject, world.Mod);
                tileField.SetValue(waterObject, new Vector3Int(tiles.Next(0, width), tiles.Next(0, height), tiles.Next(0, 4)));
                service.RegisterWaterObject(waterObject);
                PlantWater.RegisterPostfix(waterObject);
                objects.Add(waterObject);
            }
            PlantWater.TakeStatsLine();

            int worker = 0;
            WaterMapCopy.AfterCopy = job =>
            {
                levels(job);
                int[] written = (int[])job.Extra.GetType().GetField("Levels", Any).GetValue(job.Extra);
                written[spoilt] += 7;
                worker = written[spoilt];
            };
            WaterMapCopy.StartParallelTickPrefix(world.Simulator);
            SimulateWaterTasks(world, random, false);
            WaterMapCopy.FillForTests();
            WaterMapCopy.AfterCopy = levels;
            if (WaterMapCopy.MapUpdatePrefix(world.Mod)) RouteMapsTests.Call(world.Mod, "Tick");
            WaterMapCopy.MapUpdatePostfix(world.Mod);
            bool handled = !PlantWater.TickPrefix(service);
            workers = worker;
            stats = PlantWater.TakeStatsLine();
            int[] stored = new int[count];
            for (int i = 0; i < count; i++) stored[i] = handled ? (int)level.GetValue(objects[i]) : int.MinValue;
            return stored;
        }

        int[] off = Stored(false, false, out int spoiltLevel, out _);
        int[] on = Stored(true, false, out _, out string verifyStats);
        Console.WriteLine("     " + verifyStats);
        check(off[spoilt] == spoiltLevel && off.AsSpan().SequenceEqual(on) && verifyStats.Contains("; 1 used the water worker's levels") &&
              verifyStats.Contains("verify mismatches 1"),
            "plant water verify: the main thread read another level than the worker and that was logged, and every object stores " +
            $"the worker's level, as with verify off (object {spoilt}: {on[spoilt]}, the worker's {spoiltLevel})");
        int[] all = Stored(true, true, out _, out string allStats);
        Console.WriteLine("     " + allStats);
        check(off.AsSpan().SequenceEqual(all) && allStats.Contains("; 1 used the water worker's levels") &&
              allStats.Contains("verify mismatches 2"),
            "plant water verify with the water map's verify on as well: the worker's copy went in from the map's postfix, the " +
            $"difference was logged, and every object stores the worker's level (object {spoilt}: {all[spoilt]})");
        WaterMapCopy.VerifyEnabled = false;
        PlantWater.SetVerifyForTests(false);
        WaterMapCopy.SceneCreated();
        PlantWater.SceneCreated();
    }
}
