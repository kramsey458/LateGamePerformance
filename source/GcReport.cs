using System;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace LateGamePerformance
{
    // Incremental garbage collection is fixed by boot.config before any mod loads, so a mod cannot switch it on.
    // This only reports the state and how to change it.
    internal static class GcReport
    {
        private const string BootConfigKey = "gc-max-time-slice";

        public static void Write()
        {
            try
            {
                bool incremental = GarbageCollector.isIncremental;
                long heapMb = Profiler.GetMonoHeapSizeLong() / (1024 * 1024);
                Log.Info($"GC: incremental={incremental}, mode={GarbageCollector.GCMode}, managed heap={heapMb} MB.");
                if (incremental)
                {
                    return;
                }
                string bootConfig = Path.Combine(Application.dataPath, "boot.config");
                bool hasKey = File.Exists(bootConfig) && File.ReadAllText(bootConfig).Contains(BootConfigKey);
                Log.Info(hasKey
                    ? $"GC: {BootConfigKey} is present in boot.config but incremental GC is still off."
                    : $"GC: collections stop the whole game while they run. To try incremental GC, close the game " +
                      $"and add the line '{BootConfigKey}=3' to {bootConfig} (Steam 'verify files' reverts it).");
            }
            catch (Exception exception)
            {
                Log.Warning("GC report failed: " + exception.Message);
            }
        }
    }
}
