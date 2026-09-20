using System;
using System.Diagnostics;
using System.Globalization;

namespace LateGamePerformance
{
    // How much collection work a frame may carry, chosen from how much room recent frames had.
    //
    // With incremental collection on, Unity does up to a fixed slice of marking per frame while a collection cycle
    // is running (3 ms from boot.config), whether or not the frame had room. A full cycle is the same amount of
    // work either way: about 600 ms of marking for 1.7 GB in use. Doing more of it in frames that are short, or
    // while paused, finishes cycles sooner, and a cycle that cannot keep up with allocation ends in one blocking
    // collection, which is the freeze incremental collection exists to avoid.
    //
    // Measured in one session each way (0.4.6 fixed, 0.4.7 paced): no difference that could be told from noise.
    // It stays experimental and off by default.
    //
    // Pure: no Unity, so the tests can drive it.
    internal sealed class GcSlicePolicy
    {
        // Never below what boot.config asks for. 0.4.7 went down to 1 ms in slow frames; on a computer whose
        // frames are always slow that pinned the slice at 1 ms, which only makes every cycle three times as long
        // on the machine that can least afford one that falls behind.
        public const ulong DefaultSlice = 3000000;
        public const ulong RoomyFrameSlice = 6000000;
        public const ulong PausedSlice = 8000000;         // nothing is moving; get the cycle done

        public const double RoomyFrameMs = 14;

        // A single long frame should not swing the slice, a run of them should.
        private const double Smoothing = 0.2;
        // A gap this long is loading or the window in the background, and says nothing about play.
        private const double IgnoreFrameMs = 2000;

        private double _smoothedFrameMs;

        public double SmoothedFrameMs => _smoothedFrameMs;

        public ulong Next(double frameMs, bool paused)
        {
            if (frameMs > 0 && frameMs < IgnoreFrameMs)
            {
                _smoothedFrameMs = _smoothedFrameMs <= 0
                    ? frameMs
                    : _smoothedFrameMs + Smoothing * (frameMs - _smoothedFrameMs);
            }
            if (paused)
            {
                return PausedSlice;
            }
            if (_smoothedFrameMs <= 0)
            {
                return DefaultSlice;
            }
            return _smoothedFrameMs >= RoomyFrameMs ? DefaultSlice : RoomyFrameSlice;
        }
    }

    // Applies GcSlicePolicy once per frame. Off unless the player ticks "Adaptive garbage collection pacing", and
    // it does nothing when collection is not incremental. Only the length of a slice changes: when a collection
    // starts, and what it collects, stay Unity's decisions. Turning it off puts back the slice it found.
    // Does not touch the simulation.
    internal static class GcPacing
    {
        // Replaced by the test harness, where UnityEngine is not callable.
        public static Func<bool> IsIncremental = () => UnityEngine.Scripting.GarbageCollector.isIncremental;
        public static Func<ulong> GetSlice = () => UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds;
        public static Action<ulong> SetSlice = value => UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = value;

        // Set from the settings page.
        public static volatile bool Enabled;

        private static GcSlicePolicy _policy = new GcSlicePolicy();
        private static long _lastFrameStamp;
        private static ulong _originalSlice;
        private static ulong _currentSlice;
        private static bool _applied;
        private static bool _failed;
        private static ulong _lowestSlice;
        private static ulong _highestSlice;
        private static double _sliceSum;
        private static long _frames;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "GcPacing" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "Ticker.Update",
                Required = true,
                Target = () => Reflect.Method("Timberborn.TickSystem.Ticker", "Update"),
                Prefix = Reflect.Own(typeof(GcPacing), nameof(UpdatePrefix))
            });
            return feature;
        }

        private static void UpdatePrefix()
        {
            Frame(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        }

        // Separate from the hook so the tests can feed it a clock. Never throws.
        internal static void Frame(long stamp, double stopwatchFrequency)
        {
            try
            {
                long previous = _lastFrameStamp;
                _lastFrameStamp = stamp;
                if (_failed)
                {
                    return;
                }
                if (!Enabled)
                {
                    Restore();
                    return;
                }
                if (previous == 0 || !IsIncremental())
                {
                    return;
                }
                if (!_applied)
                {
                    _originalSlice = GetSlice();
                    _currentSlice = _originalSlice;
                    _applied = true;
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        "GC pacing: on. The slice was {0:0.0} ms; it is now {1:0} ms, or {2:0} ms while frames are " +
                        "fast and {3:0} ms while paused.", _originalSlice / 1e6, GcSlicePolicy.DefaultSlice / 1e6,
                        GcSlicePolicy.RoomyFrameSlice / 1e6, GcSlicePolicy.PausedSlice / 1e6));
                }
                double frameMs = (stamp - previous) * 1000.0 / stopwatchFrequency;
                ulong wanted = _policy.Next(frameMs, Timing.CurrentSpeed() <= 0);
                if (wanted != _currentSlice)
                {
                    SetSlice(wanted);
                    _currentSlice = wanted;
                }
                _lowestSlice = _frames == 0 ? wanted : Math.Min(_lowestSlice, wanted);
                _highestSlice = Math.Max(_highestSlice, wanted);
                _sliceSum += wanted;
                _frames++;
            }
            catch (Exception exception)
            {
                // Unity's own pacing carries on.
                _failed = true;
                Log.Warning("GC pacing failed and turned itself off for this session: " + exception.Message);
            }
        }

        private static void Restore()
        {
            if (!_applied)
            {
                return;
            }
            _applied = false;
            if (_currentSlice != _originalSlice)
            {
                SetSlice(_originalSlice);
            }
            _policy = new GcSlicePolicy();
            Log.Info(string.Format(CultureInfo.InvariantCulture, "GC pacing: off. The slice is back to {0:0.0} ms.",
                _originalSlice / 1e6));
        }

        // For the Timing line: "" when pacing did nothing in the interval.
        public static string TakeText()
        {
            if (_frames == 0)
            {
                return "";
            }
            string text = string.Format(CultureInfo.InvariantCulture,
                ", paced by this mod between {0:0} and {1:0} ms (average {2:0.0} ms)",
                _lowestSlice / 1e6, _highestSlice / 1e6, _sliceSum / _frames / 1e6);
            _frames = 0;
            _sliceSum = 0;
            _lowestSlice = _highestSlice = 0;
            return text;
        }

        // For the tests.
        internal static void ResetForTests()
        {
            _policy = new GcSlicePolicy();
            _lastFrameStamp = 0;
            _applied = _failed = false;
            _frames = 0;
            _sliceSum = 0;
            _lowestSlice = _highestSlice = _originalSlice = _currentSlice = 0;
        }
    }
}
