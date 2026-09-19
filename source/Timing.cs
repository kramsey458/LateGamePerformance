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
        public void Frame(long frameStartStamp, long simulationStopwatchTicks, float speed, int collectionsAtFrameStart)
        {
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
            string line = string.Format(c,
                "Timing: {0} ticks in {1:0.0} s unpaused = {2:0.0} ticks/s{3}; wall clock {4:0.0} s, of which paused " +
                "{5:0.0} s and not counted {6:0.0} s (loading, saving, window in the background); {7} frames = {8:0} fps; " +
                "simulation {9:0.0} ms per tick = {10:0}% of the main thread, everything else {11:0.0} ms per frame = {12:0}%; " +
                "longest frame {13:0} ms, longest simulation slice {14:0} ms; {15}{16}",
                ticks, seconds, ticks / seconds, wanted,
                wallSeconds, pausedSeconds, Math.Max(0, wallSeconds - pausedSeconds - seconds),
                _frames, _frames / seconds,
                simulationSeconds * 1000 / ticks, 100 * simulationSeconds / seconds,
                otherSeconds * 1000 / _frames, 100 * otherSeconds / seconds,
                _longestFrameStopwatchTicks * msPerStopwatchTick,
                _longestSimulationStopwatchTicks * msPerStopwatchTick,
                collectionsText, saves);
            Reset();
            return line;
        }

        private void Reset()
        {
            _wallStopwatchTicks = _pausedStopwatchTicks = _activeStopwatchTicks = _simulationStopwatchTicks = 0;
            _longestFrameStopwatchTicks = _longestSimulationStopwatchTicks = 0;
            _collectionFrameStopwatchTicks = _longestCollectionFrameStopwatchTicks = _collectionFrames = 0;
            _saveStopwatchTicks = _saveFrameStopwatchTicks = 0;
            _saves = _collectionsInSaveFrames = 0;
            _speedTimesSeconds = 0;
            _frames = 0;
        }
    }

    internal static class Timing
    {
        // Replaced by the test harness, where UnityEngine.Time is not callable.
        public static Func<float> CurrentSpeed = () => UnityEngine.Time.timeScale;

        private static readonly TimingStats Stats = new TimingStats(Stopwatch.Frequency);
        private static Func<object, object> _tickServiceOf;
        private static float _tickSeconds;
        private static bool _active;
        private static int _collectionsAtLastReport;
        private static int _collectionsAtFrameStart;

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
            return line;
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
            _collectionsAtFrameStart = GC.CollectionCount(0);
            if (_tickSeconds == 0)
            {
                ReadTickSeconds(__instance);
            }
        }

        private static void UpdatePostfix(long __state)
        {
            Stats.Frame(__state, Stopwatch.GetTimestamp() - __state, CurrentSpeed(), _collectionsAtFrameStart);
        }
        // ReSharper restore InconsistentNaming

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
