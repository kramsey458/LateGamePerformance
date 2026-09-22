using System;
using System.Threading;

namespace LateGamePerformance
{
    // A few threads of this mod's own for parallel work the tick has to wait for: the district counts, the plant
    // water levels when they have to be read inside the tick, and terrain and road route maps built as a batch.
    // Parallel.For does the same on the thread pool, whose threads sleep between ticks; waking and joining them
    // cost about half a millisecond per use in the logged colony, more than the work itself. These threads spin
    // briefly after a job in case the next one follows at once, then block on a signal of their own, and one
    // signal each wakes them.
    //
    // The calling thread takes part as worker 0, and Run is never re-entered: a call from inside a job, or from
    // another thread while a job runs, gets the whole job on the calling thread. A job is told how many workers
    // share it and must leave its results in slots no two workers share, which every caller here does (strided
    // indices, one tally or generator per worker). How work is split is never simulation state.
    internal static class TickWorkers
    {
        public delegate void Job(int worker, int workers);

        // Spinning rounds before a worker goes to sleep: about 100 to 200 microseconds on a desktop core.
        private const int SpinRounds = 4000;

        private sealed class Worker
        {
            public int Index;
            public readonly ManualResetEventSlim Go = new ManualResetEventSlim(false, 0);
        }

        private static readonly object Lock = new object();
        private static readonly ManualResetEventSlim Done = new ManualResetEventSlim(false, 2000);
        private static Worker[] _workers = new Worker[0];
        private static int _maximum = Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
        private static Job _job;
        private static int _sharing;
        private static int _pending;
        private static Exception _failure;
        private static int _busy;
        private static long _runs;
        private static long _alone;
        private static long _spinWakes;
        private static long _sleepWakes;

        public static int Maximum => _maximum;

        public static void Configure(int maximum)
        {
            _maximum = Math.Max(1, Math.Min(maximum, 32));
        }

        // Runs job(worker, workers) for every worker in [0, workers): worker 0 on the calling thread, the others on
        // this pool's threads, and waits for all of them. Returns the first exception any of them threw, or null;
        // never throws itself.
        public static Exception Run(int requested, Job job)
        {
            int workers = Math.Min(requested, _maximum);
            if (workers <= 1 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                _alone++;
                return RunAlone(job);
            }
            try
            {
                try
                {
                    EnsureStarted(workers - 1);
                }
                catch (Exception)
                {
                    // A thread could not be created: this job runs here, and so will the next ones.
                    _maximum = 1;
                    _alone++;
                    return RunAlone(job);
                }
                _job = job;
                _sharing = workers;
                _failure = null;
                Volatile.Write(ref _pending, workers - 1);
                Done.Reset();
                for (int i = 0; i < workers - 1; i++)
                {
                    _workers[i].Go.Set();
                }
                try
                {
                    job(0, workers);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _failure, exception, null);
                }
                Done.Wait();
                _runs++;
                return _failure;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        public static string TakeStatsLine()
        {
            if (_runs + _alone == 0)
            {
                return null;
            }
            string line = $"TickWorkers: {_runs} jobs shared with {_workers.Length} worker threads (woken while still spinning " +
                          $"{Interlocked.Read(ref _spinWakes)} times, from sleep {Interlocked.Read(ref _sleepWakes)} times); " +
                          $"{_alone} jobs ran on the calling thread alone";
            _runs = _alone = 0;
            Interlocked.Exchange(ref _spinWakes, 0);
            Interlocked.Exchange(ref _sleepWakes, 0);
            return line;
        }

        private static Exception RunAlone(Job job)
        {
            try
            {
                job(0, 1);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static void EnsureStarted(int count)
        {
            if (_workers.Length >= count)
            {
                return;
            }
            lock (Lock)
            {
                if (_workers.Length >= count)
                {
                    return;
                }
                Worker[] workers = new Worker[count];
                Array.Copy(_workers, workers, _workers.Length);
                for (int i = _workers.Length; i < count; i++)
                {
                    Worker worker = new Worker { Index = i };
                    Thread thread = new Thread(() => Loop(worker))
                    {
                        IsBackground = true,
                        Name = "LateGamePerformance worker " + (i + 1)
                    };
                    workers[i] = worker;
                    thread.Start();
                }
                _workers = workers;
            }
        }

        private static void Loop(Worker worker)
        {
            while (true)
            {
                bool woke = false;
                for (int round = 0; round < SpinRounds && !woke; round++)
                {
                    if (worker.Go.IsSet)
                    {
                        woke = true;
                    }
                    else
                    {
                        Thread.SpinWait(20);
                    }
                }
                if (woke)
                {
                    Interlocked.Increment(ref _spinWakes);
                }
                else
                {
                    worker.Go.Wait();
                    Interlocked.Increment(ref _sleepWakes);
                }
                worker.Go.Reset();
                Job job = _job;
                int sharing = _sharing;
                try
                {
                    job(worker.Index + 1, sharing);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _failure, exception, null);
                }
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    Done.Set();
                }
            }
        }
    }
}
