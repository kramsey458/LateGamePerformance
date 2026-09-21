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
        private string _note;

        public SaveBreakdown(double stopwatchFrequency)
        {
            _stopwatchFrequency = stopwatchFrequency;
        }

        public bool IsOpen => _what != null;

        // No stage has reported anything yet.
        public bool IsEmpty
        {
            get
            {
                foreach (long stage in _stageStopwatchTicks)
                {
                    if (stage != 0)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public void Open(string what)
        {
            Array.Clear(_stageStopwatchTicks, 0, _stageStopwatchTicks.Length);
            _what = what;
            _note = null;
        }

        // Something the line should say about this save, after the numbers.
        public void Note(string note)
        {
            if (IsOpen)
            {
                _note = note;
            }
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
            if (_note != null)
            {
                line += " (" + _note + ")";
            }
            _what = null;
            _note = null;
            return line;
        }
    }

    // Where the time of a save goes. Measurement only: the hooks read a clock before and after the game's own
    // methods and change nothing about how or when the game saves. They only run during a save.
    //
    //   GameSaver.SaveQueued / SaveInstantly...  the whole save (autosave, manual save, save on exit)
    //     Ticker.FinishFullTick                  finishing the tick
    //     SerializedWorldFactory.Create          snapshot of every entity and singleton
    //     WorldSerializer.WriteToSaveEntryStream world JSON and compression
    //     ThumbnailSaveEntryWriter               thumbnail (camera render and encoding)
    //     the rest                               small entries, copying to the file, the completion callback
    //
    // An exception here could break a save, so every hook swallows its own.
    //
    // GameSaver.Save itself must NOT be patched: it has a catch with an exception filter ("when"), which Harmony
    // cannot regenerate under Mono. The attempt fails and leaves a half-built dynamic type behind, and the game
    // then crashes while it scans the loaded assemblies (that was 0.4.3). Its callers are hooked instead, and
    // the tests refuse any patch target with an exception filter.
    internal static class SaveTiming
    {
        private static readonly SaveBreakdown Breakdown = new SaveBreakdown(Stopwatch.Frequency);
        private static bool _finishingTheTick;
        private static Func<object, object> _queuedSaveOf;
        private static long _openedStamp;

        // A save still open after this long was abandoned by an exception, not nested.
        private const double AbandonedAfterSeconds = 60;

        public static Feature CreateFeature()
        {
            Type self = typeof(SaveTiming);
            Feature feature = new Feature { Name = "SaveTiming" };
            feature.Patches.Add(new PatchSpec
            {
                // Called every frame; it saves only when a save is queued (autosave, manual save).
                Name = "GameSaver.SaveQueued",
                Required = true,
                Target = () =>
                {
                    Type saver = Reflect.GameType("Timberborn.GameSaveRuntimeSystem.GameSaver");
                    // A nullable struct: boxed it is null while nothing is queued, so looking costs no allocation.
                    _queuedSaveOf = Reflect.FieldGetter<object>(saver, "_queuedSave");
                    return HarmonyLib.AccessTools.Method(saver, "SaveQueued");
                },
                Prefix = Reflect.Own(self, nameof(SaveQueuedPrefix)),
                Postfix = Reflect.Own(self, nameof(SavePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "GameSaver.SaveInstantlySkippingNameValidation",
                Required = false,
                Target = () => Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "SaveInstantlySkippingNameValidation"),
                Prefix = Reflect.Own(self, nameof(SavePrefix)),
                Postfix = Reflect.Own(self, nameof(SavePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // Inside every save. On its own only if something saves without going through the methods above
                // (the map editor, or a mod calling the game's private Save directly).
                Name = "SaveWriter.WriteToSaveStream",
                Required = false,
                Target = () => Reflect.Method("Timberborn.SaveSystem.SaveWriter", "WriteToSaveStream"),
                Prefix = Reflect.Own(self, nameof(WriteOnlyPrefix)),
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
        private static void SaveQueuedPrefix(object __instance, out long __state)
        {
            __state = 0;
            try
            {
                if (_queuedSaveOf(__instance) != null)
                {
                    __state = Begin("Save");
                }
            }
            catch (Exception)
            {
                // Never let a measurement break a save.
            }
        }

        private static void SavePrefix(out long __state)
        {
            __state = Begin("Save");
        }

        private static void WriteOnlyPrefix(out long __state)
        {
            __state = Begin("Save (writing only; the caller is not one this mod knows)");
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
                bool nothingHappened = Breakdown.IsEmpty && elapsed < Stopwatch.Frequency / 100;
                string line = Breakdown.Close(elapsed);
                // A queued save that another mod deferred returns at once with no stage run; that is not a save.
                if (line != null && !nothingHappened)
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

        internal static void Note(string note)
        {
            Breakdown.Note(note);
        }

        private static void StagePrefix(out long __state)
        {
            __state = 0;
            try
            {
                // A stage on BackgroundSave's worker thread is not part of what the game thread spends.
                if (Breakdown.IsOpen && BackgroundSave.OnGameThread())
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
                // The outermost hook owns the save; the ones nested inside it get 0 and do nothing.
                long now = Stopwatch.GetTimestamp();
                if (Breakdown.IsOpen && now - _openedStamp < AbandonedAfterSeconds * Stopwatch.Frequency)
                {
                    return 0;
                }
                Breakdown.Open(what);
                _finishingTheTick = false;
                _openedStamp = now;
                return now;
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
