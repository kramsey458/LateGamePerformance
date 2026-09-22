using System;
using System.Diagnostics;
using System.IO;
using Unity.Profiling.Memory;
using UnityEngine.Profiling;

namespace LateGamePerformance
{
    // Two things a tester can do from the settings page to find out what holds the managed heap, which in the
    // logged colony was 3 GB and makes every collection expensive. Both are one-off actions behind a box that
    // unticks itself. Measurement only.
    internal static class MemoryTools
    {
        // Swappable for the test harness, which has no Unity.
        internal static Func<string> OutputFolder = () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Timberborn", "LateGamePerformance");

        // A full collection, then the live size. The one place in this mod that forces a collection: it freezes
        // the game for as long as a collection of a heap this size takes (about a second), on purpose and once.
        public static void MeasureLive()
        {
            try
            {
                long before = Profiler.GetMonoUsedSizeLong();
                Stopwatch watch = Stopwatch.StartNew();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                watch.Stop();
                long after = Profiler.GetMonoUsedSizeLong();
                long reserved = Profiler.GetMonoHeapSizeLong();
                long unityAllocated = Profiler.GetTotalAllocatedMemoryLong();
                long unityReserved = Profiler.GetTotalReservedMemoryLong();
                long workingSet = Process.GetCurrentProcess().WorkingSet64;
                Log.Info($"Memory: live managed heap {after >> 20} MB after a full collection ({before >> 20} MB before it, " +
                         $"{watch.ElapsedMilliseconds} ms); managed heap reserved {reserved >> 20} MB; Unity's own memory " +
                         $"{unityAllocated >> 20} MB in use of {unityReserved >> 20} MB reserved; process {workingSet >> 20} MB.");
            }
            catch (Exception exception)
            {
                Log.Warning("Memory: measuring failed: " + exception);
            }
        }

        // A memory snapshot file, if this build of the engine writes one outside the editor. It opens in the
        // Memory Profiler package of any Unity 6 editor and names every object by type.
        public static void WriteSnapshot()
        {
            try
            {
                string folder = OutputFolder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"heap-{DateTime.Now:yyyyMMdd-HHmmss}.snap");
                Log.Info($"Memory: writing a snapshot to {path}; this can take a while.");
                MemoryProfiler.TakeSnapshot(path, (written, success) => Log.Info(success
                        ? $"Memory: snapshot written to {written}. Open it with the Memory Profiler package in a Unity 6 editor."
                        : $"Memory: the engine did not write a snapshot to {written}; this build may not support it outside the editor."),
                    CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects | CaptureFlags.NativeAllocations);
            }
            catch (Exception exception)
            {
                Log.Warning("Memory: the snapshot could not be started: " + exception);
            }
        }
    }
}
