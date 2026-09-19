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
    // Paused frames are left out, so the rates describe the game while it is actually running. If the game
    // cannot keep up, ticks per second falls below what the speed setting asks for; this shows by how much
    // and which side of the frame is responsible.
    internal sealed class TimingStats
    {
        // A gap this long between frames is loading, a save dialog or the window in the background, not play.
        private const double IgnoreFrameGapSeconds = 2;

        private readonly double _stopwatchFrequency;
        private long _lastFrameStamp;
        private long _activeStopwatchTicks;
        private long _simulationStopwatchTicks;
        private long _longestFrameStopwatchTicks;
        private long _longestSimulationStopwatchTicks;
        private double _speedTimesSeconds;
        private long _frames;

        public TimingStats(double stopwatchFrequency)
        {
            _stopwatchFrequency = stopwatchFrequency;
        }

        // Called once per frame, after the simulation slice of that frame has run.
        public void Frame(long frameStartStamp, long simulationStopwatchTicks, float speed)
        {
            long previous = _lastFrameStamp;
            _lastFrameStamp = frameStartStamp;
            if (previous == 0)
            {
                return;
            }
            long gap = frameStartStamp - previous;
            if (speed <= 0 || gap <= 0 || gap > IgnoreFrameGapSeconds * _stopwatchFrequency)
            {
                return;
            }
            _frames++;
            _activeStopwatchTicks += gap;
            _simulationStopwatchTicks += simulationStopwatchTicks;
            _speedTimesSeconds += speed * (gap / _stopwatchFrequency);
            _longestFrameStopwatchTicks = Math.Max(_longestFrameStopwatchTicks, gap);
            _longestSimulationStopwatchTicks = Math.Max(_longestSimulationStopwatchTicks, simulationStopwatchTicks);
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
            double seconds = _activeStopwatchTicks / _stopwatchFrequency;
            double simulationSeconds = Math.Min(seconds, _simulationStopwatchTicks / _stopwatchFrequency);
            double otherSeconds = seconds - simulationSeconds;
            double averageSpeed = _speedTimesSeconds / seconds;
            string wanted = tickSeconds > 0
                ? string.Format(c, " (average speed setting {0:0.0} asks for {1:0.0})", averageSpeed, averageSpeed / tickSeconds)
                : "";
            string line = string.Format(c,
                "Timing: {0} ticks in {1:0.0} s unpaused = {2:0.0} ticks/s{3}; {4} frames = {5:0} fps; " +
                "simulation {6:0.0} ms per tick = {7:0}% of the main thread, everything else {8:0.0} ms per frame = {9:0}%; " +
                "longest frame {10:0} ms, longest simulation slice {11:0} ms; {12} garbage collections",
                ticks, seconds, ticks / seconds, wanted, _frames, _frames / seconds,
                simulationSeconds * 1000 / ticks, 100 * simulationSeconds / seconds,
                otherSeconds * 1000 / _frames, 100 * otherSeconds / seconds,
                _longestFrameStopwatchTicks * 1000 / _stopwatchFrequency,
                _longestSimulationStopwatchTicks * 1000 / _stopwatchFrequency,
                garbageCollections);
            Reset();
            return line;
        }

        private void Reset()
        {
            _activeStopwatchTicks = _simulationStopwatchTicks = 0;
            _longestFrameStopwatchTicks = _longestSimulationStopwatchTicks = 0;
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

        // ReSharper disable InconsistentNaming
        private static void UpdatePrefix(object __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (_tickSeconds == 0)
            {
                ReadTickSeconds(__instance);
            }
        }

        private static void UpdatePostfix(long __state)
        {
            Stats.Frame(__state, Stopwatch.GetTimestamp() - __state, CurrentSpeed());
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
