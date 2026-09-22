using System;
using System.Globalization;
using System.Linq;
using LateGamePerformance;
using Timberborn.Common;
using Vector3 = UnityEngine.Vector3;
using Vector3Int = UnityEngine.Vector3Int;

// The pieces added in 0.4.25 that need no game scene: the mod's own worker threads, the deferred behaviour log
// against the game's real ring buffer, the animator distance policy and the terrain reach box policy.
internal static class FeatureTests
{
    public static void Run(Action<bool, string> check)
    {
        RunTickWorkers(check);
        RunBehaviorLog(check);
        RunAnimatorLod(check);
        RunTerrainReachPolicy(check);
    }

    private static void RunTickWorkers(Action<bool, string> check)
    {
        TickWorkers.Configure(4);
        long[] sums = new long[4];
        int shared = 0;
        Exception failure = TickWorkers.Run(4, (worker, workers) =>
        {
            if (worker == 0) shared = workers;
            long sum = 0;
            for (int i = worker; i < 100000; i += workers) sum += i;
            sums[worker] = sum;
        });
        check(failure == null && shared == 4 && sums.Sum() == 4999950000L,
            "tick workers: four workers share a strided range and the caller waits for all of them");
        long[] again = new long[4];
        failure = TickWorkers.Run(4, (worker, workers) => { again[worker] = worker + 1; });
        check(failure == null && again.Sum() == 10, "tick workers: a second job runs on the same threads");
        failure = TickWorkers.Run(3, (worker, workers) => { if (worker == 2) throw new InvalidOperationException("boom"); });
        check(failure is InvalidOperationException, "tick workers: a worker's exception comes back as a result, nothing is thrown");
        int innerWorkers = -1;
        failure = TickWorkers.Run(2, (worker, workers) =>
        {
            if (worker == 0) TickWorkers.Run(4, (w, ws) => { if (w == 0) innerWorkers = ws; });
        });
        check(failure == null && innerWorkers == 1, "tick workers: a job started from inside a job runs alone on the calling thread");
        int capped = 0, single = 0;
        TickWorkers.Run(64, (worker, workers) => { if (worker == 0) capped = workers; });
        TickWorkers.Run(1, (worker, workers) => { single = workers; });
        check(capped == 4 && single == 1, "tick workers: the count is capped at the configured maximum, and one worker means no threads");
        string stats = TickWorkers.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats.StartsWith("TickWorkers: 5 jobs shared with 3 worker threads") && stats.Contains("2 jobs ran on the calling thread alone"),
            "tick workers: the stats line counts shared jobs, threads and jobs run alone");
        TickWorkers.Configure(0);
        int atLeastOne = 0;
        TickWorkers.Run(3, (worker, workers) => { atLeastOne = workers; });
        check(atLeastOne == 1 && TickWorkers.Maximum == 1, "tick workers: a maximum below one means one");
        TickWorkers.Configure(7);
    }

    private static void RunBehaviorLog(Action<bool, string> check)
    {
        string[] names = { "Eat", "Sleep", "Work", "Drink", "Wander", "Idle" };
        Random random = new Random(5);
        // The game's ring fed directly, and the game's ring fed through the mod's ring flushed every seven changes.
        CyclicBuffer<string> games = new CyclicBuffer<string>(BehaviorLog.Capacity);
        CyclicBuffer<string> mods = new CyclicBuffer<string>(BehaviorLog.Capacity);
        BehaviorLog.Ring ring = new BehaviorLog.Ring();
        bool same = true;
        int written = 0;
        for (int i = 0; i < 37; i++)
        {
            string name = names[random.Next(names.Length)];
            float day = (float)(random.NextDouble() * 300);
            games.Add($"{name} {day:0.00}");   // the game's own line, as its compiler wrote it
            ring.Push(name, day);
            if (i % 7 == 6 || i == 36)
            {
                written += ring.Flush(mods);
                same &= games.Values.SequenceEqual(mods.Values);
            }
        }
        check(same && written == 37 && mods.Values.Count() == BehaviorLog.Capacity,
            "behaviour log: flushed every seven changes, the game's ring holds exactly the lines it would have, in order");
        // Flushed once after 25 changes: only the last ten are left to write, and the game's ring ends the same.
        games = new CyclicBuffer<string>(BehaviorLog.Capacity);
        mods = new CyclicBuffer<string>(BehaviorLog.Capacity);
        ring = new BehaviorLog.Ring();
        for (int i = 0; i < 25; i++)
        {
            string name = names[random.Next(names.Length)];
            float day = (float)(random.NextDouble() * 300);
            games.Add($"{name} {day:0.00}");
            ring.Push(name, day);
        }
        written = ring.Flush(mods);
        check(written == BehaviorLog.Capacity && games.Values.SequenceEqual(mods.Values) && ring.Count == 0,
            "behaviour log: flushed after 25 changes, the ten the game's ring would keep are written and the rest were dropped");
        check(ring.Flush(mods) == 0 && games.Values.SequenceEqual(mods.Values), "behaviour log: a second flush writes nothing");
        // The format is the game's for awkward values too: many digits, negative, rounding at the half.
        bool formats = true;
        foreach (float day in new[] { 0f, 0.005f, 1.995f, 123456.789f, -3.14159f, 1e-7f, 999999.5f })
        {
            formats &= BehaviorLog.Format("Walk", day) == $"Walk {day:0.00}";
        }
        check(formats, "behaviour log: the deferred format gives the game's own string for awkward day numbers");
        check(BehaviorLog.Format("Eat", 12.345f) == string.Format(CultureInfo.CurrentCulture, "{0} {1:0.00}", "Eat", 12.345f),
            "behaviour log: formatted with the current culture, as the game's interpolated string is");
    }

    private static void RunAnimatorLod(Action<bool, string> check)
    {
        check(AnimatorCulling.PoseInterval(0, 80) == 1 && AnimatorCulling.PoseInterval(79.9f, 80) == 1 &&
              AnimatorCulling.PoseInterval(80, 80) == 2 && AnimatorCulling.PoseInterval(159.9f, 80) == 2 &&
              AnimatorCulling.PoseInterval(160, 80) == 4 && AnimatorCulling.PoseInterval(5000, 80) == 4,
            "animator distance: every frame up to the distance, every second frame beyond it, every fourth beyond twice it");
        // Over any four consecutive frames an object at interval 4 is due exactly once and one at interval 2 exactly
        // twice, whatever its hash, and interval 1 is every frame.
        Random random = new Random(3);
        bool spread = true;
        int[] firstDue = new int[4];
        for (int k = 0; k < 2000; k++)
        {
            int hash = random.Next(int.MinValue, int.MaxValue);
            int frame = random.Next(0, 1 << 20);
            int due4 = 0, due2 = 0, due1 = 0;
            for (int f = frame; f < frame + 4; f++)
            {
                if (AnimatorCulling.PoseDue(f, hash, 4)) { due4++; if (f == frame) firstDue[0]++; }
                if (AnimatorCulling.PoseDue(f, hash, 2)) due2++;
                if (AnimatorCulling.PoseDue(f, hash, 1)) due1++;
            }
            spread &= due4 == 1 && due2 == 2 && due1 == 4;
        }
        check(spread, "animator distance: an object is due exactly once per interval, whatever its hash");
        check(firstDue[0] > 350 && firstDue[0] < 650, $"animator distance: objects are spread over the frames by their hash ({firstDue[0]} of 2000 due on a given frame)");
    }

    private static void RunTerrainReachPolicy(Action<bool, string> check)
    {
        object field = new object();
        bool filled = true;
        TerrainReach.SetFilledForTests(f => ReferenceEquals(f, field) && filled, world => Vector3Int.FloorToInt(world));
        TerrainReach.Box box = TerrainReach.BoxForTests(field, new Vector3Int(3, 4, 0), new Vector3Int(10, 12, 2));
        check(TerrainReach.MayReach(box, new Vector3(3.5f, 4.5f, 0.5f)) && TerrainReach.MayReach(box, new Vector3(10.9f, 12.9f, 2.9f)) &&
              TerrainReach.MayReach(box, new Vector3(7f, 8f, 1f)),
            "terrain reach: a tile inside the box may be in the map");
        check(!TerrainReach.MayReach(box, new Vector3(2.9f, 5f, 1f)) && !TerrainReach.MayReach(box, new Vector3(11f, 5f, 1f)) &&
              !TerrainReach.MayReach(box, new Vector3(5f, 3.9f, 1f)) && !TerrainReach.MayReach(box, new Vector3(5f, 13f, 1f)) &&
              !TerrainReach.MayReach(box, new Vector3(5f, 5f, -0.1f)) && !TerrainReach.MayReach(box, new Vector3(5f, 5f, 3f)),
            "terrain reach: a tile outside the box on any axis is certainly not in the map");
        filled = false;
        check(TerrainReach.MayReach(box, new Vector3(50f, 50f, 50f)), "terrain reach: a map cleared since its box was made answers maybe");
        filled = true;
        check(TerrainReach.MayReach(null, new Vector3(50f, 50f, 50f)) && ReferenceEquals(TerrainReach.BoxOf(field), box) &&
              TerrainReach.BoxOf(new object()) == null,
            "terrain reach: no box means maybe, and a box is found by its map");
        TerrainReach.Box empty = TerrainReach.BoxForTests(new object(), new Vector3Int(1, 1, 1), new Vector3Int(0, 0, 0));
        bool emptyFilled = true;
        TerrainReach.SetFilledForTests(f => ReferenceEquals(f, field) && filled || ReferenceEquals(f, empty.Field) && emptyFilled,
            world => Vector3Int.FloorToInt(world));
        check(!TerrainReach.MayReach(empty, new Vector3(0.5f, 0.5f, 0.5f)) && !TerrainReach.MayReach(empty, new Vector3(1.5f, 1.5f, 1.5f)),
            "terrain reach: the box of a map that reaches nothing contains nothing");
    }
}
