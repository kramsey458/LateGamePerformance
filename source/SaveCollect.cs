using System;
using System.Diagnostics;

namespace LateGamePerformance
{
    // A save allocates a lot: the snapshot of the world, then the JSON tree and the compressed bytes. In a logged
    // 354-beaver session every autosave was followed by a garbage collection within about ten ticks, so each save
    // was two hitches: the save frame, then a collection frame a second later. Collecting right after the save's
    // main-thread part puts the collection into the frame that is long anyway; the allocation budget starts over
    // and the rest of the save's garbage no longer tips it over on its own.
    //
    // Not part of the simulation: when memory is collected changes nothing the game computes.
    internal static class SaveCollect
    {
        // Swappable for the test harness.
        internal static Func<long> HeapBytes = () => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        internal static Action Collect = GC.Collect;

        private static bool _active;
        private static bool _collecting;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "SaveCollect" };
            feature.Patches.Add(new PatchSpec
            {
                // Inside every save, on the main thread, after the snapshot (and for a save the game writes itself,
                // after the JSON and compression too). BackgroundSave skips the original; postfixes still run.
                Name = "SaveWriter.WriteToSaveStream",
                Required = true,
                Target = () => Reflect.Method("Timberborn.SaveSystem.SaveWriter", "WriteToSaveStream"),
                Postfix = Reflect.Own(typeof(SaveCollect), nameof(WritePostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        internal static bool IsActive => _active;

        internal static void WritePostfix()
        {
            if (!_active || _collecting)
            {
                return;
            }
            _collecting = true;
            try
            {
                long before = HeapBytes();
                long stamp = Stopwatch.GetTimestamp();
                Collect();
                double ms = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
                long after = HeapBytes();
                Log.Info($"SaveCollect: memory clean-up right after the save took {ms:0} ms and freed " +
                         $"{Math.Max(0, before - after) / 1048576.0:0} MB ({after / 1048576.0:0} MB in use)");
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("SaveCollect failed and is off for this session: " + exception);
            }
            finally
            {
                _collecting = false;
            }
        }
    }
}
