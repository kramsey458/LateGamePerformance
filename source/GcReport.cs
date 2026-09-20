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
        private const string BootConfigKey = BootConfig.Key;

        // Replaced by the test harness, where UnityEngine.Application is not callable.
        public static Func<string> BootConfigPath = () => Path.Combine(Application.dataPath, "boot.config");

        // Called when the player ticks or unticks the setting. Never throws.
        public static void ApplyIncremental(bool incremental)
        {
            try
            {
                string path = BootConfigPath();
                BootConfig.Outcome outcome = BootConfig.Apply(path, incremental, out string error);
                switch (outcome)
                {
                    case BootConfig.Outcome.Changed:
                        Log.Info(incremental
                            ? $"GC: added '{BootConfig.Line}' to {path} (backup: boot.config{BootConfig.BackupSuffix}). " +
                              "Restart the game; the startup line should then say incremental=True."
                            : $"GC: removed '{BootConfig.Key}' from {path}. Takes effect after a restart.");
                        break;
                    case BootConfig.Outcome.AlreadyAsWanted:
                        Log.Info($"GC: {path} already " + (incremental ? "has" : "lacks") + $" '{BootConfig.Key}'; nothing changed.");
                        break;
                    case BootConfig.Outcome.FileMissing:
                        Log.Warning($"GC: {path} was not found, so incremental collection could not be changed.");
                        break;
                    default:
                        Log.Warning($"GC: could not change {path} ({error}). To do it by hand, close the game and " +
                                    (incremental ? $"add the line '{BootConfig.Line}'." : $"remove the '{BootConfig.Key}' line."));
                        break;
                }
            }
            catch (Exception exception)
            {
                Log.Warning("GC: changing boot.config failed: " + exception.Message);
            }
        }

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
                string bootConfig = BootConfigPath();
                Log.Info(BootConfig.FileHasKey(bootConfig)
                    ? $"GC: {BootConfigKey} is in boot.config but incremental GC is off. It takes effect the next " +
                      "time the game starts; if the game has been restarted since, this Unity build ignores it."
                    : "GC: every collection stops the whole game until it is done, and in a large colony that is " +
                      "most of a second, about once a minute. Tick 'Incremental garbage collection' in this mod's " +
                      "settings (Mods > Late Game Performance) and restart the game, or add the line " +
                      $"'{BootConfig.Line}' to {bootConfig} yourself (Steam 'verify files' reverts it).");
            }
            catch (Exception exception)
            {
                Log.Warning("GC report failed: " + exception.Message);
            }
        }
    }
}
