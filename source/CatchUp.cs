using System;

namespace LateGamePerformance
{
    // After a long frame the game runs the simulation for all the time that frame took: Ticker.Update gets
    // Time.deltaTime, which Unity caps at a third of a second, and turns it into tick buckets. At speed 7 a
    // 300 ms frame (an autosave, a garbage collection) is 2.1 s of game time, three and a half ticks, so the next
    // frame takes 130 ms, the one after that 60 ms, and so on: one hitch becomes a second of stutter. This limits
    // how much simulation time one frame may catch up to twice the recent frame time (at least a thirtieth of a
    // second); the rest is not run. The simulation loses a fraction of a second of wall clock per hitch and runs
    // exactly the same ticks in the same order, only spread over ordinary frames.
    //
    // Not part of the simulation: how many buckets a frame runs already differs between players and machines.
    internal static class CatchUp
    {
        // The pure rule, tested on its own.
        internal sealed class Policy
        {
            public const float FloorSeconds = 1f / 30f;
            public const float Multiple = 2f;
            public const float Smoothing = 0.05f;

            private float _average = FloorSeconds;

            public float AverageSeconds => _average;

            // The unscaled frame time the simulation may advance by for a frame that took unscaledSeconds.
            public float Allow(float unscaledSeconds)
            {
                if (unscaledSeconds <= 0f)
                {
                    return unscaledSeconds;
                }
                float cap = Math.Max(_average * Multiple, FloorSeconds);
                float allowed = Math.Min(unscaledSeconds, cap);
                _average += Smoothing * (allowed - _average);
                return allowed;
            }
        }

        // Swappable for the test harness, which has no Unity.
        internal static Func<float> UnscaledDeltaTime = () => UnityEngine.Time.unscaledDeltaTime;

        private static Policy _policy = new Policy();
        private static bool _active;
        private static long _limitedFrames;
        private static double _droppedGameSeconds;
        private static double _droppedWallSeconds;
        private static float _longestFrameSeconds;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "CatchUp" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "Ticker.Update",
                Required = true,
                Target = () => Reflect.Method("Timberborn.TickSystem.Ticker", "Update"),
                Prefix = Reflect.Own(typeof(CatchUp), nameof(UpdatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _policy = new Policy();
            _active = true;
        }

        internal static bool IsActive => _active;

        public static string TakeStatsLine()
        {
            if (!_active || _limitedFrames == 0)
            {
                return null;
            }
            string line = $"CatchUp: after {_limitedFrames} long frame(s) the simulation did not catch up; " +
                          $"{_droppedGameSeconds:0.0} s of game time ({_droppedWallSeconds * 1000:0} ms of wall clock) " +
                          $"was left out, longest such frame {_longestFrameSeconds * 1000:0} ms, " +
                          $"ordinary frame {_policy.AverageSeconds * 1000:0.0} ms";
            _limitedFrames = 0;
            _droppedGameSeconds = _droppedWallSeconds = 0;
            _longestFrameSeconds = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static void UpdatePrefix(ref float deltaTimeInSeconds)
        {
            if (!_active || deltaTimeInSeconds <= 0f)
            {
                return;
            }
            try
            {
                float unscaled = UnscaledDeltaTime();
                if (unscaled <= 0f)
                {
                    return;
                }
                float allowed = _policy.Allow(unscaled);
                if (allowed < unscaled)
                {
                    // deltaTimeInSeconds is the unscaled time (already capped by Unity) times the game speed;
                    // scaling it down by the same ratio keeps whatever cap and speed Unity applied.
                    float ratio = allowed / unscaled;
                    float dropped = deltaTimeInSeconds * (1f - ratio);
                    deltaTimeInSeconds *= ratio;
                    _limitedFrames++;
                    _droppedGameSeconds += dropped;
                    _droppedWallSeconds += unscaled - allowed;
                    if (unscaled > _longestFrameSeconds)
                    {
                        _longestFrameSeconds = unscaled;
                    }
                }
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("CatchUp failed and is off for this session: " + exception);
            }
        }
        // ReSharper restore InconsistentNaming
    }
}
