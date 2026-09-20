using System;
using System.Globalization;

namespace LateGamePerformance
{
    // How much managed memory this mod's own work allocates, so its share of the garbage can be read from the
    // stats lines instead of guessed. Collections get more frequent with every byte allocated, on any thread.
    //
    // Uses the runtime's per-thread allocation counter, which is exact for the calling thread and is not
    // disturbed by what other threads do or by a collection in between. Not every runtime keeps that counter
    // (it may be missing, or always read zero), so it is tried once at startup; without it no figure is given,
    // because the alternative (watching the heap grow around a call) would also count every other thread.
    internal static class Allocations
    {
        // Replaced by the tests.
        public static Func<long> ThreadCounter = () => GC.GetAllocatedBytesForCurrentThread();

        public static bool Available { get; private set; }

        // Returns a line for the log.
        public static string Probe()
        {
            Available = false;
            try
            {
                long before = ThreadCounter();
                byte[] block = new byte[64 * 1024];
                block[block.Length - 1] = 1;
                long counted = ThreadCounter() - before;
                GC.KeepAlive(block);
                Available = counted >= block.Length / 2;
                return Available
                    ? "Allocations: this mod's own allocations are counted per feature and shown in the stats lines."
                    : "Allocations: this runtime does not count allocated bytes per thread (the counter did not move), " +
                      "so the stats lines give no allocation figures.";
            }
            catch (Exception exception)
            {
                return "Allocations: this runtime has no per-thread allocation counter (" + exception.GetType().Name +
                       "), so the stats lines give no allocation figures.";
            }
        }

        // -1 when unavailable, so End costs nothing.
        public static long Begin()
        {
            return Available ? ThreadCounter() : -1;
        }

        public static long End(long begin)
        {
            return begin < 0 ? 0 : Math.Max(0, ThreadCounter() - begin);
        }

        // ", allocating 123 KB" or "" when nothing can be said.
        public static string Describe(long bytes)
        {
            return Available
                ? string.Format(CultureInfo.InvariantCulture, ", allocating {0:0} KB", bytes / 1024.0)
                : "";
        }
    }
}
