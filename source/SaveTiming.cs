using System;
using System.Diagnostics;
using System.Globalization;

namespace LateGamePerformance
{
    internal enum SaveStage
    {
        FinishingTheTick,
        Snapshot,
        WorldJson,
        Thumbnail
    }

    // The arithmetic of one save, kept free of Unity and the game so it can be tested on its own.
    //
    // A save is opened, stages report how long they took while it is open, and closing it gives the line.
    // A stage reported while no save is open is ignored: the same game methods are also called by the map
    // editor and by benchmarks. Opening always starts from zero, so a save the game abandoned with an
    // exception (the closing hook never runs then) cannot leak into the next one.
    internal sealed class SaveBreakdown
    {
        private static readonly string[] StageNames =
        {
            "finishing the tick", "snapshot", "world JSON and compression", "thumbnail"
        };

        private readonly double _stopwatchFrequency;
        private readonly long[] _stageStopwatchTicks = new long[StageNames.Length];
        private string _what;

        public SaveBreakdown(double stopwatchFrequency)
        {
            _stopwatchFrequency = stopwatchFrequency;
        }

        public bool IsOpen => _what != null;

        public void Open(string what)
        {
            Array.Clear(_stageStopwatchTicks, 0, _stageStopwatchTicks.Length);
            _what = what;
        }

        public void Add(SaveStage stage, long stopwatchTicks)
        {
            if (IsOpen && stopwatchTicks > 0)
            {
                _stageStopwatchTicks[(int)stage] += stopwatchTicks;
            }
        }

        // Returns null if no save is open, for instance the outer of two nested saves.
        public string Close(long totalStopwatchTicks)
        {
            if (!IsOpen)
            {
                return null;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            double msPerStopwatchTick = 1000 / _stopwatchFrequency;
            long everythingElse = totalStopwatchTicks;
            string parts = "";
            for (int i = 0; i < StageNames.Length; i++)
            {
                everythingElse -= _stageStopwatchTicks[i];
                parts += string.Format(c, "{0} {1:0} ms + ", StageNames[i], _stageStopwatchTicks[i] * msPerStopwatchTick);
            }
            string line = string.Format(c, "{0}: {1:0} ms total = {2}everything else {3:0} ms", _what,
                totalStopwatchTicks * msPerStopwatchTick, parts, Math.Max(0, everythingElse) * msPerStopwatchTick);
            _what = null;
            return line;
        }
    }

    // Where the time of a save goes. Measurement only: the hooks read a clock before and after the game's own
    // methods and change nothing about how or when the game saves. They only run during a save.
    //
    //   GameSaver.Save                           the whole save (autosave, manual save, save on exit)
    //     Ticker.FinishFullTick                  finishing the tick
    //     SerializedWorldFactory.Create          snapshot of every entity and singleton
    //     WorldSerializer.WriteToSaveEntryStream world JSON and compression
    //     ThumbnailSaveEntryWriter               thumbnail (camera render and encoding)
    //     the rest                               small entries, copying to the file, the completion callback
    //
    // An exception here could break a save, so every hook swallows its own.
    internal static class SaveTiming
    {
        private static readonly SaveBreakdown Breakdown = new SaveBreakdown(Stopwatch.Frequency);
        private static bool _finishingTheTick;

        public static Feature CreateFeature()
        {
            Type self = typeof(SaveTiming);
            Feature feature = new Feature { Name = "SaveTiming" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.Save",
                Required = true,
                Target = () => Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "Save"),
                Prefix = Reflect.Own(self, nameof(SavePrefix)),
                Postfix = Reflect.Own(self, nameof(SavePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // Writes a save into a stream without a file; a multiplayer mod uses it for a joining player.
                Name = "GameSaver.SaveWithoutFinishingTick",
                Required = false,
                Target = () => Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "SaveWithoutFinishingTick"),
                Prefix = Reflect.Own(self, nameof(SaveToStreamPrefix)),
                Postfix = Reflect.Own(self, nameof(SavePostfix))
            });
            // Two hooks for one stage. The one on Ticker is so short that the runtime may inline it into Save, where
            // a patch cannot reach it; the one on the bucket service is called through an interface, which another
            // mod may have replaced. Whichever is entered first counts, the one nested inside it does not.
            AddStage(feature, "Ticker.FinishFullTick",
                () => Reflect.Method("Timberborn.TickSystem.Ticker", "FinishFullTick"),
                nameof(FinishFullTickPostfix), nameof(FinishFullTickPrefix));
            AddStage(feature, "TickableBucketService.FinishFullTick",
                () => Reflect.Method("Timberborn.TickSystem.TickableBucketService", "FinishFullTick"),
                nameof(FinishFullTickPostfix), nameof(FinishFullTickPrefix));
            AddStage(feature, "SerializedWorldFactory.Create",
                () =>
                {
                    // The overload without parameters; the other one only saves chosen singletons.
                    Type factory = Reflect.GameType("Timberborn.WorldPersistence.SerializedWorldFactory");
                    return factory == null ? null : HarmonyLib.AccessTools.Method(factory, "Create", Type.EmptyTypes);
                }, nameof(SnapshotPostfix));
            AddStage(feature, "WorldSerializer.WriteToSaveEntryStream",
                () => Reflect.Method("Timberborn.WorldSerialization.WorldSerializer", "WriteToSaveEntryStream"),
                nameof(WorldJsonPostfix));
            AddStage(feature, "ThumbnailSaveEntryWriter.WriteToSaveEntryStream",
                () => Reflect.Method("Timberborn.ThumbnailCapturing.ThumbnailSaveEntryWriter", "WriteToSaveEntryStream"),
                nameof(ThumbnailPostfix));
            return feature;
        }

        // A stage that cannot be hooked ends up in "everything else"; the line is still worth having.
        private static void AddStage(Feature feature, string name, Func<System.Reflection.MethodBase> target, string postfix,
            string prefix = nameof(StagePrefix))
        {
            feature.Patches.Add(new PatchSpec
            {
                Name = name,
                Required = false,
                Target = target,
                Prefix = Reflect.Own(typeof(SaveTiming), prefix),
                Postfix = Reflect.Own(typeof(SaveTiming), postfix)
            });
        }

        // ReSharper disable InconsistentNaming
        private static void SavePrefix(out long __state)
        {
            __state = Begin("Save");
        }

        private static void SaveToStreamPrefix(out long __state)
        {
            __state = Begin("Save to a stream (no file, not finishing the tick)");
        }

        private static void SavePostfix(long __state)
        {
            try
            {
                if (__state == 0)
                {
                    return;
                }
                long elapsed = Stopwatch.GetTimestamp() - __state;
                string line = Breakdown.Close(elapsed);
                if (line != null)
                {
                    Timing.SaveFinished(__state, elapsed);
                    Log.Info(line);
                }
            }
            catch (Exception)
            {
                // Never let a measurement break a save.
            }
        }

        private static void StagePrefix(out long __state)
        {
            __state = 0;
            try
            {
                if (Breakdown.IsOpen)
                {
                    __state = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception)
            {
                // Never let a measurement break a save.
            }
        }

        private static void FinishFullTickPrefix(out long __state)
        {
            __state = 0;
            if (Breakdown.IsOpen && !_finishingTheTick)
            {
                _finishingTheTick = true;
                StagePrefix(out __state);
            }
        }

        private static void FinishFullTickPostfix(long __state)
        {
            if (__state != 0)
            {
                _finishingTheTick = false;
                EndStage(SaveStage.FinishingTheTick, __state);
            }
        }

        private static void SnapshotPostfix(long __state)
        {
            EndStage(SaveStage.Snapshot, __state);
        }

        private static void WorldJsonPostfix(long __state)
        {
            EndStage(SaveStage.WorldJson, __state);
        }

        private static void ThumbnailPostfix(long __state)
        {
            EndStage(SaveStage.Thumbnail, __state);
        }
        // ReSharper restore InconsistentNaming

        private static long Begin(string what)
        {
            try
            {
                Breakdown.Open(what);
                _finishingTheTick = false;
                return Stopwatch.GetTimestamp();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void EndStage(SaveStage stage, long startStamp)
        {
            try
            {
                if (startStamp != 0)
                {
                    Breakdown.Add(stage, Stopwatch.GetTimestamp() - startStamp);
                }
            }
            catch (Exception)
            {
                // Never let a measurement break a save.
            }
        }
    }
}
