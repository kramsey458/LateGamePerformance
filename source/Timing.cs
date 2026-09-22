using System;
using System.Diagnostics;
using System.Globalization;

namespace LateGamePerformance
{
    // Where the main thread's time goes, from one hook on Ticker.Update (called once per frame, and the only
    // place the game advances the simulation during normal play).
    //
    //   simulation      = time inside Ticker.Update
    //   everything else = the rest of each frame: rendering, animation, UI, other mods' per-frame work
    //
    // Rates describe the game while it is actually running, so paused frames are left out of them. The wall clock
    // and the paused time are reported next to them, because a multiplayer mod can set the speed to zero without
    // logging it, and that would otherwise be invisible here.
    //
    // Garbage collections: with incremental collection off, a collection stops every thread until it is done.
    // The collection counter is sampled at the start of each frame, so a frame during which it moved is a frame
    // that contained a collection. Their lengths show what collections cost.
    //
    // Everything else, split: the game runs its own per-frame systems from two calls, SingletonLifecycleService
    // .UpdateAll and .LateUpdateAll (input, camera, UI panels, and anything that reacts to what the last tick
    // changed), and mods that register systems of their own run there too. Both are timed, so "everything else"
    // divides into those two and a remainder that is Unity itself: rendering, animation, physics. In a multiplayer
    // session one computer spent 40 ms per tick in "everything else", more than in the simulation, and nothing
    // could say in which part.
    //
    // Memory: how long a collection takes depends on how much is still in use (all of it is marked again every
    // time), and how often one happens depends on how fast memory is allocated. Both are sampled once per frame:
    // growth between two frames is allocation, a drop is what a collection freed. They are measured over the
    // whole wall clock, paused and uncounted frames included, because the collector does not care.
    //
    // Saves: a save freezes the game for most of a second and usually contains a collection, so its frame would
    // be the longest frame and would be blamed on the collection. SaveTiming reports each save here; the frame
    // it ran in is left out of every figure and reported on its own.
    internal sealed class TimingStats
    {
        // A gap this long between frames is loading, a save dialog or the window in the background, not play.
        private const double IgnoreFrameGapSeconds = 2;

        private readonly double _stopwatchFrequency;
        private long _lastFrameStamp;
        private int _lastCollections;
        private long _wallStopwatchTicks;
        private long _pausedStopwatchTicks;
        private long _activeStopwatchTicks;
        private long _simulationStopwatchTicks;
        private long _longestFrameStopwatchTicks;
        private long _longestSimulationStopwatchTicks;
        private long _collectionFrameStopwatchTicks;
        private long _longestCollectionFrameStopwatchTicks;
        private long _collectionFrames;
        private long _pendingSimulationStopwatchTicks;
        private long _pendingSaveStartStamp;
        private long _pendingSaveStopwatchTicks;
        private int _pendingSaves;
        private long _saveStopwatchTicks;
        private long _saveFrameStopwatchTicks;
        private int _saves;
        private int _collectionsInSaveFrames;
        private long _lastHeapBytes;
        private long _updateStopwatchTicks;
        private long _lateUpdateStopwatchTicks;
        private long _lowestHeapBytes;
        private long _highestHeapBytes;
        private long _allocatedBytes;
        private long _freedBytes;
        private double _speedTimesSeconds;
        private long _frames;

        public TimingStats(double stopwatchFrequency)
        {
            _stopwatchFrequency = stopwatchFrequency;
        }

        // A save that started at startStamp (same clock as the frame stamps) and took stopwatchTicks.
        public void Save(long startStamp, long stopwatchTicks)
        {
            if (_pendingSaves == 0)
            {
                _pendingSaveStartStamp = startStamp;
            }
            _pendingSaves++;
            _pendingSaveStopwatchTicks += stopwatchTicks;
        }

        // Called once per frame. frameStartStamp and collectionsAtFrameStart are taken at the start of the frame's
        // simulation call, so the gap to the previous call is exactly the previous frame, and a change in the
        // collection counter happened during it. simulationStopwatchTicks belongs to the frame that is starting,
        // so it is held back until that frame's length is known.
        // heapBytes: managed memory in use at the start of the frame, 0 if unknown.
        // updateStopwatchTicks, lateUpdateStopwatchTicks: time in the game's per-frame systems during the frame that
        // just ended, 0 if not measured.
        public void Frame(long frameStartStamp, long simulationStopwatchTicks, float speed, int collectionsAtFrameStart,
            long heapBytes = 0, long updateStopwatchTicks = 0, long lateUpdateStopwatchTicks = 0)
        {
            NoteHeap(heapBytes);
            long previous = _lastFrameStamp;
            int collectionsDuringFrame = collectionsAtFrameStart - _lastCollections;
            long simulation = _pendingSimulationStopwatchTicks;
            _lastFrameStamp = frameStartStamp;
            _lastCollections = collectionsAtFrameStart;
            _pendingSimulationStopwatchTicks = simulationStopwatchTicks;
            if (previous == 0)
            {
                return;
            }
            long gap = frameStartStamp - previous;
            if (gap <= 0)
            {
                return;
            }
            _wallStopwatchTicks += gap;
            if (_pendingSaves > 0 && _pendingSaveStartStamp < frameStartStamp)
            {
                // The frame that just ended contained a save (one started during the frame that is starting now
                // stays pending). Whatever else happened in it, the save is what made it long.
                _saves += _pendingSaves;
                _saveStopwatchTicks += _pendingSaveStopwatchTicks;
                _saveFrameStopwatchTicks += gap;
                _collectionsInSaveFrames += Math.Max(0, collectionsDuringFrame);
                _pendingSaves = 0;
                _pendingSaveStopwatchTicks = 0;
                return;
            }
            if (gap > IgnoreFrameGapSeconds * _stopwatchFrequency)
            {
                return;
            }
            if (speed <= 0)
            {
                _pausedStopwatchTicks += gap;
                return;
            }
            _frames++;
            _activeStopwatchTicks += gap;
            _simulationStopwatchTicks += simulation;
            _updateStopwatchTicks += updateStopwatchTicks;
            _lateUpdateStopwatchTicks += lateUpdateStopwatchTicks;
            _speedTimesSeconds += speed * (gap / _stopwatchFrequency);
            _longestFrameStopwatchTicks = Math.Max(_longestFrameStopwatchTicks, gap);
            _longestSimulationStopwatchTicks = Math.Max(_longestSimulationStopwatchTicks, simulation);
            if (collectionsDuringFrame > 0)
            {
                _collectionFrames++;
                _collectionFrameStopwatchTicks += gap;
                _longestCollectionFrameStopwatchTicks = Math.Max(_longestCollectionFrameStopwatchTicks, gap);
            }
        }

        private void NoteHeap(long heapBytes)
        {
            if (heapBytes <= 0)
            {
                return;
            }
            if (_lastHeapBytes > 0)
            {
                long change = heapBytes - _lastHeapBytes;
                if (change > 0)
                {
                    _allocatedBytes += change;
                }
                else
                {
                    _freedBytes -= change;
                }
            }
            _lastHeapBytes = heapBytes;
            _lowestHeapBytes = _lowestHeapBytes == 0 ? heapBytes : Math.Min(_lowestHeapBytes, heapBytes);
            _highestHeapBytes = Math.Max(_highestHeapBytes, heapBytes);
        }

        // Empty when the heap was never sampled.
        private string MemoryText(CultureInfo c, double wallSeconds, int garbageCollections)
        {
            if (_highestHeapBytes == 0 || wallSeconds <= 0)
            {
                return "";
            }
            const double mb = 1024 * 1024;
            string freed = garbageCollections > 0
                ? string.Format(c, ", {0:0.0} collection(s) per minute freeing about {1:0} MB each",
                    garbageCollections * 60 / wallSeconds, _freedBytes / mb / garbageCollections)
                : "";
            return string.Format(c, "; managed memory {0:0}-{1:0} MB in use, allocating {2:0.0} MB/s{3}",
                _lowestHeapBytes / mb, _highestHeapBytes / mb, _allocatedBytes / mb / wallSeconds, freed);
        }

        // tickSeconds: game seconds per tick at speed 1 (0 if unknown).
        public string TakeLine(int ticks, float tickSeconds, int garbageCollections)
        {
            if (_frames == 0 || _activeStopwatchTicks == 0)
            {
                Reset();
                return null;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            double msPerStopwatchTick = 1000 / _stopwatchFrequency;
            double seconds = _activeStopwatchTicks / _stopwatchFrequency;
            double wallSeconds = _wallStopwatchTicks / _stopwatchFrequency;
            double pausedSeconds = _pausedStopwatchTicks / _stopwatchFrequency;
            double simulationSeconds = Math.Min(seconds, _simulationStopwatchTicks / _stopwatchFrequency);
            double otherSeconds = seconds - simulationSeconds;
            double averageSpeed = _speedTimesSeconds / seconds;
            double typicalFrameMs = seconds * 1000 / _frames;
            string wanted = tickSeconds > 0
                ? string.Format(c, " (game time scale {0:0.0}, which is the speed buttons after the game's large-colony " +
                                   "throttle, asks for {1:0.0})", averageSpeed, averageSpeed / tickSeconds)
                : "";
            int collections = Math.Max(0, garbageCollections - _collectionsInSaveFrames);
            string collectionsText = _collectionFrames > 0
                ? string.Format(c, "{0} garbage collection(s): the {1} frame(s) containing one took {2:0} ms in total, " +
                                   "longest {3:0} ms (an average frame is {4:0.0} ms)",
                    collections, _collectionFrames, _collectionFrameStopwatchTicks * msPerStopwatchTick,
                    _longestCollectionFrameStopwatchTicks * msPerStopwatchTick, typicalFrameMs)
                : string.Format(c, "{0} garbage collection(s), none during counted frames", collections);
            string saves = _saves > 0
                ? string.Format(c, "; {0} save(s): {1:0} ms, in frame(s) of {2:0} ms with {3} garbage collection(s), " +
                                   "all left out of the other figures",
                    _saves, _saveStopwatchTicks * msPerStopwatchTick, _saveFrameStopwatchTicks * msPerStopwatchTick,
                    _collectionsInSaveFrames)
                : "";
            double updateMs = _updateStopwatchTicks * msPerStopwatchTick / _frames;
            double lateUpdateMs = _lateUpdateStopwatchTicks * msPerStopwatchTick / _frames;
            string otherParts = _updateStopwatchTicks + _lateUpdateStopwatchTicks > 0
                ? string.Format(c, " (per-frame systems of the game and mods {0:0.0} ms, their late-update systems {1:0.0} ms, " +
                                   "the rest {2:0.0} ms: rendering, animation and Unity itself)",
                    updateMs, lateUpdateMs, Math.Max(0, otherSeconds * 1000 / _frames - updateMs - lateUpdateMs))
                : "";
            string line = string.Format(c,
                "Timing: {0} ticks in {1:0.0} s unpaused = {2:0.0} ticks/s{3}; wall clock {4:0.0} s, of which paused " +
                "{5:0.0} s and not counted {6:0.0} s (loading, saving, window in the background); {7} frames = {8:0} fps; " +
                "simulation {9:0.0} ms per tick = {10:0}% of the main thread, everything else {11:0.0} ms per frame = {12:0}%{18}; " +
                "longest frame {13:0} ms, longest simulation slice {14:0} ms; {15}{16}{17}",
                ticks, seconds, ticks / seconds, wanted,
                wallSeconds, pausedSeconds, Math.Max(0, wallSeconds - pausedSeconds - seconds),
                _frames, _frames / seconds,
                simulationSeconds * 1000 / ticks, 100 * simulationSeconds / seconds,
                otherSeconds * 1000 / _frames, 100 * otherSeconds / seconds,
                _longestFrameStopwatchTicks * msPerStopwatchTick,
                _longestSimulationStopwatchTicks * msPerStopwatchTick,
                collectionsText, saves, MemoryText(c, wallSeconds, garbageCollections), otherParts);
            Reset();
            return line;
        }

        private void Reset()
        {
            _wallStopwatchTicks = _pausedStopwatchTicks = _activeStopwatchTicks = _simulationStopwatchTicks = 0;
            _longestFrameStopwatchTicks = _longestSimulationStopwatchTicks = 0;
            _collectionFrameStopwatchTicks = _longestCollectionFrameStopwatchTicks = _collectionFrames = 0;
            _saveStopwatchTicks = _saveFrameStopwatchTicks = 0;
            _updateStopwatchTicks = _lateUpdateStopwatchTicks = 0;
            // _lastHeapBytes is kept: the next interval's first frame is compared with this one's last.
            _lowestHeapBytes = _highestHeapBytes = _allocatedBytes = _freedBytes = 0;
            _saves = _collectionsInSaveFrames = 0;
            _speedTimesSeconds = 0;
            _frames = 0;
        }
    }

    internal static class Timing
    {
        // Replaced by the test harness, where UnityEngine.Time is not callable.
        public static Func<float> CurrentSpeed = () => UnityEngine.Time.timeScale;

        // Managed memory in use. Replaced by the test harness like CurrentSpeed.
        public static Func<long> HeapBytes = () => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

        private static readonly TimingStats Stats = new TimingStats(Stopwatch.Frequency);
        private static Func<object, object> _tickServiceOf;
        private static float _tickSeconds;
        private static bool _active;
        private static int _collectionsAtLastReport;
        private static int _collectionsAtFrameStart;
        private static long _heapAtFrameStart;
        private static bool _heapUnavailable;
        private static long _updateTicksThisFrame;
        private static long _lateUpdateTicksThisFrame;
        private static long _updateTicksOfEndedFrame;
        private static long _lateUpdateTicksOfEndedFrame;

        public static Feature CreateFeature()
        {
            Type self = typeof(Timing);
            Feature feature = new Feature { Name = "Timing" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "Ticker.Update",
                Required = true,
                Target = () =>
                {
                    Type ticker = Reflect.GameType("Timberborn.TickSystem.Ticker");
                    _tickServiceOf = Reflect.FieldGetter<object>(ticker, "_tickService");
                    return HarmonyLib.AccessTools.Method(ticker, "Update");
                },
                Prefix = Reflect.Own(self, nameof(UpdatePrefix)),
                Postfix = Reflect.Own(self, nameof(UpdatePostfix))
            });
            // Optional: without them the line simply has no split.
            feature.Patches.Add(new PatchSpec
            {
                Name = "SingletonLifecycleService.UpdateAll",
                Required = false,
                Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateAll"),
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(UpdateAllPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SingletonLifecycleService.LateUpdateAll",
                Required = false,
                Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateAll"),
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(LateUpdateAllPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
            _collectionsAtLastReport = GC.CollectionCount(0);
        }

        public static string TakeStatsLine(int ticks)
        {
            if (!_active)
            {
                return null;
            }
            int collections = GC.CollectionCount(0);
            string line = Stats.TakeLine(ticks, _tickSeconds, collections - _collectionsAtLastReport);
            _collectionsAtLastReport = collections;
            return line == null ? null : line + CollectorText();
        }

        // Called by SaveTiming; startStamp is a Stopwatch timestamp, like the frame stamps.
        public static void SaveFinished(long startStamp, long stopwatchTicks)
        {
            if (_active)
            {
                Stats.Save(startStamp, stopwatchTicks);
            }
        }

        // ReSharper disable InconsistentNaming
        private static void UpdatePrefix(object __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            UnityMarkers.Sample();
            _collectionsAtFrameStart = GC.CollectionCount(0);
            _heapAtFrameStart = ReadHeap();
            // This call is the frame boundary: what the per-frame systems took since the last one belongs to
            // the frame that is ending, wherever in the frame Unity ran them.
            _updateTicksOfEndedFrame = _updateTicksThisFrame;
            _lateUpdateTicksOfEndedFrame = _lateUpdateTicksThisFrame;
            _updateTicksThisFrame = _lateUpdateTicksThisFrame = 0;
            if (_tickSeconds == 0)
            {
                ReadTickSeconds(__instance);
            }
        }

        private static void UpdatePostfix(long __state)
        {
            Stats.Frame(__state, Stopwatch.GetTimestamp() - __state, CurrentSpeed(), _collectionsAtFrameStart,
                _heapAtFrameStart, _updateTicksOfEndedFrame, _lateUpdateTicksOfEndedFrame);
        }

        private static void StampPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void UpdateAllPostfix(long __state)
        {
            _updateTicksThisFrame += Stopwatch.GetTimestamp() - __state;
        }

        private static void LateUpdateAllPostfix(long __state)
        {
            _lateUpdateTicksThisFrame += Stopwatch.GetTimestamp() - __state;
        }
        // ReSharper restore InconsistentNaming

        // Any log someone sends should answer "was it incremental?" without the startup lines.
        internal static string CollectorText()
        {
            try
            {
                if (!GcReport.IsIncremental())
                {
                    return "; garbage collection is NOT incremental, so every collection freezes the game (setting: " +
                           "Incremental garbage collection)";
                }
                return string.Format(CultureInfo.InvariantCulture, "; garbage collection is incremental, slice {0:0.0} ms",
                    GcReport.SliceNanoseconds() / 1e6);
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static long ReadHeap()
        {
            if (_heapUnavailable)
            {
                return 0;
            }
            try
            {
                return HeapBytes();
            }
            catch (Exception)
            {
                // The line is still useful without the memory figures.
                _heapUnavailable = true;
                return 0;
            }
        }

        private static void ReadTickSeconds(object ticker)
        {
            try
            {
                _tickSeconds = ((Timberborn.TickSystem.ITickService)_tickServiceOf(ticker)).TickIntervalInSeconds;
            }
            catch (Exception)
            {
                // Only used to say what the speed setting asks for; the line is still useful without it.
                _tickSeconds = -1;
            }
        }
    }
}
