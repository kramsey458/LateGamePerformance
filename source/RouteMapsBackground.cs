using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LateGamePerformance
{
    // 0.2.0 made the main thread wait for the whole batch of route maps. Here the workers rebuild in the background
    // and the tick carries on. The main thread only ever waits for the one map it is about to use:
    //
    //   - already rebuilt: no wait
    //   - not started: the main thread builds that one map itself, right there
    //   - a worker is on it: wait for that single map
    //
    // Why the results cannot differ from waiting for the whole batch:
    //  - The game reaches a cached route map only through RoadFlowFieldCache.TryGetFlowFieldAtNode and
    //    GetFlowFieldAtNode. Both are gated, so whenever the game looks at a map from the batch it is complete,
    //    which is exactly what it would see if the whole batch had been waited for. Timing decides who builds a
    //    map and when, never what any game code observes.
    //  - Workers read the road graph and the district maps. Every method that changes those is gated to land the
    //    whole batch first, as is the start of the next navigation tick and a new game scene.
    //  - A map is built by exactly one thread: claiming is a compare-and-swap on a per-map state.
    internal static partial class RouteMaps
    {
        private sealed class Flight
        {
            public Work[] Items;
            public int[] State;
            public Dictionary<int, int> IndexByNode;
            public object Graph;
            public object DistrictMap;
            public object MainGenerator;
            public Task[] Tasks;
            public int Cursor;
            public int Remaining;
            public Exception Failure;
            public long Launched;
            public long MainStopwatchTicks;
            public int BuiltOnMain;
            public int WaitedOnMain;
        }

        private const int Pending0 = 0;
        private const int Running = 1;
        private const int Done = 2;

        // Main thread only.
        private static Flight _flight;
        private static bool _inFlight;
        private static bool _background;

        private static long _mainStopwatchTicks;
        private static long _backgroundStopwatchTicks;
        private static long _builtOnMain;
        private static long _waitedOnMain;
        private static long _longestPauseStopwatchTicks;

        public static bool IsInFlight => _inFlight;

        public static double MainThreadMsSinceReport => _mainStopwatchTicks * 1000.0 / Stopwatch.Frequency;

        public static long BuiltOnMainSinceReport => _builtOnMain;

        public static double LongestPauseMsSinceReport => _longestPauseStopwatchTicks * 1000.0 / Stopwatch.Frequency;

        public static Feature CreateBackgroundFeature()
        {
            Type self = typeof(RouteMaps);
            Feature feature = new Feature { Name = "RouteMapsBackground" };
            // Every one of these is required: a missing gate would let the game see a half-built map.
            AddGate(feature, "RoadFlowFieldCache.TryGetFlowFieldAtNode", "RoadFlowFieldCache", "TryGetFlowFieldAtNode",
                nameof(MapRequestedPrefix));
            AddGate(feature, "RoadFlowFieldCache.GetFlowFieldAtNode", "RoadFlowFieldCache", "GetFlowFieldAtNode",
                nameof(MapRequestedPrefix));
            AddGate(feature, "NavigationSynchronizer.Tick (land)", "NavigationSynchronizer", "Tick", nameof(LandPrefix));
            AddGate(feature, "RoadNavMeshGraph.ConnectNodes", "RoadNavMeshGraph", "ConnectNodes", nameof(SharedStateChangingPrefix));
            AddGate(feature, "RoadNavMeshGraph.DisconnectNodes", "RoadNavMeshGraph", "DisconnectNodes", nameof(SharedStateChangingPrefix));
            AddGate(feature, "DistrictMap.AddDistrictCenter", "DistrictMap", "AddDistrictCenter", nameof(SharedStateChangingPrefix));
            AddGate(feature, "DistrictMap.RemoveDistrictCenter", "DistrictMap", "RemoveDistrictCenter", nameof(SharedStateChangingPrefix));
            AddGate(feature, "DistrictMap.OnObstacleChanged", "DistrictMap", "OnObstacleChanged", nameof(SharedStateChangingPrefix));
            AddGate(feature, "DistrictMap.OnNavMeshUpdated", "DistrictMap", "OnNavMeshUpdated", nameof(SharedStateChangingPrefix));
            return feature;
        }

        public static void SetBackground(bool background)
        {
            _background = background;
        }

        private static void AddGate(Feature feature, string name, string type, string method, string prefix)
        {
            feature.Patches.Add(new PatchSpec
            {
                Name = name,
                Required = true,
                Target = () => Reflect.Method(Namespace + type, method),
                Prefix = Reflect.Own(typeof(RouteMaps), prefix)
            });
        }

        // ReSharper disable InconsistentNaming
        // Hot path: one static read when nothing is in flight.
        internal static void MapRequestedPrefix(int nodeId)
        {
            if (_inFlight)
            {
                EnsureBuilt(nodeId);
            }
        }

        internal static void LandPrefix()
        {
            if (_inFlight)
            {
                Land();
            }
        }

        internal static void SharedStateChangingPrefix(object __instance)
        {
            // The instant and preview graphs and district maps are separate objects that workers never read.
            if (_inFlight && (ReferenceEquals(__instance, _flight.Graph) || ReferenceEquals(__instance, _flight.DistrictMap)))
            {
                Land();
            }
        }
        // ReSharper restore InconsistentNaming

        internal static void BeginBackground(object graph, object districtMap, List<Work> work, object[] generators)
        {
            // generators: one per worker, plus a last one reserved for the main thread.
            int workers = Math.Min(generators.Length - 1, work.Count);
            Flight flight = new Flight
            {
                Items = work.ToArray(),
                State = new int[work.Count],
                IndexByNode = new Dictionary<int, int>(work.Count),
                Graph = graph,
                DistrictMap = districtMap,
                MainGenerator = generators[generators.Length - 1],
                Tasks = new Task[workers],
                Remaining = work.Count,
                Launched = Stopwatch.GetTimestamp()
            };
            for (int i = 0; i < flight.Items.Length; i++)
            {
                flight.IndexByNode[flight.Items[i].StartNodeId] = i;
            }
            _flight = flight;
            _inFlight = true;
            for (int worker = 0; worker < workers; worker++)
            {
                object generator = generators[worker];
                flight.Tasks[worker] = Task.Run(() => RunWorker(flight, generator));
            }
            flight.MainStopwatchTicks += Stopwatch.GetTimestamp() - flight.Launched;
        }

        // Builds whatever is left together with the workers and closes the flight. Safe to call at any time.
        internal static void Land()
        {
            Flight flight = _flight;
            if (flight == null)
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < flight.State.Length; i++)
                {
                    if (Interlocked.CompareExchange(ref flight.State[i], Running, Pending0) == Pending0)
                    {
                        Build(flight, i, flight.MainGenerator);
                        flight.BuiltOnMain++;
                    }
                }
                Task.WaitAll(flight.Tasks);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref flight.Failure, exception, null);
            }
            NotePause(flight, Stopwatch.GetTimestamp() - started);
            Close(flight);
        }

        private static void EnsureBuilt(int nodeId)
        {
            try
            {
                EnsureBuiltCore(nodeId);
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        private static void EnsureBuiltCore(int nodeId)
        {
            Flight flight = _flight;
            if (flight.IndexByNode.TryGetValue(nodeId, out int index) && Volatile.Read(ref flight.State[index]) != Done)
            {
                long started = Stopwatch.GetTimestamp();
                if (Interlocked.CompareExchange(ref flight.State[index], Running, Pending0) == Pending0)
                {
                    Build(flight, index, flight.MainGenerator);
                    flight.BuiltOnMain++;
                }
                else
                {
                    SpinWait spin = new SpinWait();
                    while (Volatile.Read(ref flight.State[index]) != Done)
                    {
                        spin.SpinOnce();
                    }
                    flight.WaitedOnMain++;
                }
                NotePause(flight, Stopwatch.GetTimestamp() - started);
            }
            if (Volatile.Read(ref flight.Remaining) == 0)
            {
                Land();
            }
        }

        // One uninterrupted stretch the main thread spent on route maps instead of the game.
        private static void NotePause(Flight flight, long stopwatchTicks)
        {
            flight.MainStopwatchTicks += stopwatchTicks;
            if (stopwatchTicks > _longestPauseStopwatchTicks)
            {
                _longestPauseStopwatchTicks = stopwatchTicks;
            }
        }

        private static void RunWorker(Flight flight, object generator)
        {
            int count = flight.Items.Length;
            while (true)
            {
                int index = Interlocked.Increment(ref flight.Cursor) - 1;
                if (index >= count)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref flight.State[index], Running, Pending0) == Pending0)
                {
                    Build(flight, index, generator);
                }
            }
        }

        // Never throws: a map that fails is left unfilled, which the game rebuilds on demand, and the failure is
        // reported when the flight closes. The state always reaches Done so nobody waits forever.
        private static void Build(Flight flight, int index, object generator)
        {
            try
            {
                Work item = flight.Items[index];
                _fill(generator, flight.Graph, item.Field, item.LimitingField, item.StartNodeId);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref flight.Failure, exception, null);
            }
            finally
            {
                Volatile.Write(ref flight.State[index], Done);
                Interlocked.Decrement(ref flight.Remaining);
            }
        }

        private static void Close(Flight flight)
        {
            _inFlight = false;
            _flight = null;
            _batches++;
            _fieldsFilled += flight.Items.Length;
            _mainStopwatchTicks += flight.MainStopwatchTicks;
            _backgroundStopwatchTicks += Stopwatch.GetTimestamp() - flight.Launched;
            _builtOnMain += flight.BuiltOnMain;
            _waitedOnMain += flight.WaitedOnMain;
            if (flight.Failure != null)
            {
                Disable(flight.Failure);
            }
        }
    }
}
