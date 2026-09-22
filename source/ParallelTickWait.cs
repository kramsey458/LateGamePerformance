using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace LateGamePerformance
{
    // A save's snapshot starts only once the game's parallel tick has finished.
    //
    // Every tick, TickableSingletonService.TickAll ends by starting the parallel tick: the water simulation, soil
    // moisture and contamination (and the water renderer's data) run as tasks on the game's own worker threads
    // (Timberborn.Multithreading.Parallelizer) and write their maps until the next TickAll waits for them
    // (FinishParallelTick). The game's own save finishes the tick first: GameSaver.Save -> Ticker.FinishFullTick ->
    // TickableBucketService.FinishFullTick -> TickableSingletonService.ForceFinishParallelTick, which waits for those
    // tasks. BeaverBuddies moves every save to a tick boundary and then skips TickableBucketService.FinishFullTick
    // while it saves (DeterminismService.cs, TickableBucketService_FinishFullTick_Patch), so that wait never happens,
    // and its TickOnlyArrayFix switches off the game's guard (TickOnlyArrayService.AllowEdit) that would refuse the
    // snapshot's reads of those maps while the tasks may still run. The snapshot (SerializedWorldFactory.Create) could
    // then read the water, moisture and contamination maps while a task still writes them: a torn map in the save.
    //
    // So at the start of Create (a prefix at Harmony's first priority: before SaveTiming's stage timer, before
    // SaveSnapshot's replacing prefix, and whether SaveSnapshot is on or not) this waits until the game's
    // Parallelizer has no task pending. In the game's own save that is already so, and the wait is one field read.
    //
    // It is the loop of the game's Parallelizer.Wait (spin while the pending count is above zero), not Wait itself:
    // when a task has failed, Wait closes the game's worker threads, empties the queue of failures and throws. Caught
    // here, that would turn the game's report of the failure at its next tick into an endless wait there, since a
    // task that failed never counts itself done. So this only watches the pending count and the failure queue. It
    // stops waiting as soon as a task has failed (the game's next FinishParallelTick reports it, as it would have) or
    // after a few seconds, and the save goes on either way, as it did before. FinishParallelTick is not called
    // (BeaverBuddies' postfix on it ticks its LateTickableBuffer, which would change the simulation on this computer
    // alone), nor ForceFinishParallelTick (its event makes the water and soil maps and the terrain column map apply
    // their pending modifications, which is simulation).
    //
    // Waiting changes nothing the tasks compute and nothing the game's next FinishParallelTick does (it finds nothing
    // pending and goes on; LastParallelTickDuration comes from the tasks' own timestamps, ParalleTicklIsFinished is set
    // there as before), so this is not part of the simulation and may differ between players. Always on: not a
    // setting, since there is nothing to gain by skipping it. The Parallelizer is the one the game's
    // TickableSingletonService uses, taken from its field when it loads and held weakly (a scene that is gone is not
    // kept alive by it).
    internal static class ParallelTickWait
    {
        private const string ServiceType = "Timberborn.TickSystem.TickableSingletonService";

        // A parallel tick takes a few milliseconds; one still running after this long is not going to finish, and the
        // game's own next tick would wait for it anyway.
        internal static double GiveUpAfterSeconds = 5;

        private static Func<object, object> _parallelizerOf;
        private static WeakReference _parallelizer = new WeakReference(null);
        private static Type _boundType;
        private static Func<object, int> _pending;
        private static Func<object, bool> _noFailures;
        private static Func<object, bool> _closed;

        private static bool _active;
        private static long _snapshots;
        private static long _waited;
        private static long _waitStopwatchTicks;
        private static long _longestStopwatchTicks;
        private static long _failed;
        private static long _gaveUp;
        private static long _without;

        public static Feature CreateFeature()
        {
            Type self = typeof(ParallelTickWait);
            Feature feature = new Feature { Name = "ParallelTickWait" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableSingletonService.Load",
                Required = true,
                Target = () =>
                {
                    Type service = Reflect.GameType(ServiceType);
                    _parallelizerOf = Reflect.FieldGetter<object>(service, "_parallelizer");
                    return AccessTools.Method(service, "Load");
                },
                Postfix = Reflect.Own(self, nameof(LoadPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SerializedWorldFactory.Create",
                Required = true,
                Target = () =>
                {
                    // The overload without parameters: the save's snapshot. The other one only saves chosen singletons
                    // when a map is loaded.
                    Type factory = Reflect.GameType("Timberborn.WorldPersistence.SerializedWorldFactory");
                    return factory == null ? null : AccessTools.Method(factory, "Create", Type.EmptyTypes);
                },
                Prefix = Reflect.Own(self, nameof(CreatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        internal static bool IsActive => _active;

        // ReSharper disable InconsistentNaming
        internal static void LoadPostfix(object __instance)
        {
            try
            {
                object parallelizer = _parallelizerOf(__instance);
                if (parallelizer != null && parallelizer.GetType() != _boundType)
                {
                    Bind(parallelizer.GetType());
                }
                _parallelizer = new WeakReference(parallelizer);
            }
            catch (Exception exception)
            {
                _parallelizer = new WeakReference(null);
                _boundType = null;
                Log.Warning("ParallelTickWait: the game's parallelizer could not be read (" + exception.Message + "); saves in " +
                            "this game scene do not wait for the parallel tick, as before 0.4.28.");
            }
        }

        // First, so the snapshot's own prefixes, measuring or replacing, run after the wait.
        [HarmonyPriority(Priority.First)]
        internal static void CreatePrefix()
        {
            if (!_active)
            {
                return;
            }
            try
            {
                Wait();
            }
            catch (Exception exception)
            {
                // Nothing of the game's was changed; the save goes on as it did before this existed.
                _active = false;
                Log.Warning("ParallelTickWait failed and is off for this session; saves no longer wait for the game's " +
                            "parallel tick: " + exception);
            }
        }
        // ReSharper restore InconsistentNaming

        // The fields Parallelizer.Wait reads: the pending count and whether a task has failed; and whether its threads
        // were closed (its scene unloaded, so it is not the one ticking now).
        private static void Bind(Type type)
        {
            FieldInfo pending = AccessTools.Field(type, "_pendingTasks");
            FieldInfo failures = AccessTools.Field(type, "_exceptions");
            FieldInfo closed = AccessTools.Field(type, "_closed");
            PropertyInfo isEmpty = failures?.FieldType.GetProperty("IsEmpty", BindingFlags.Instance | BindingFlags.Public);
            if (pending == null || pending.FieldType != typeof(int) || isEmpty == null || isEmpty.PropertyType != typeof(bool) ||
                closed == null || closed.FieldType != typeof(bool))
            {
                throw new MissingFieldException(type.FullName, "_pendingTasks / _exceptions.IsEmpty / _closed");
            }
            ParameterExpression instance = Expression.Parameter(typeof(object), "parallelizer");
            Expression typed = Expression.Convert(instance, type);
            _pending = Expression.Lambda<Func<object, int>>(Expression.Field(typed, pending), instance).Compile();
            _noFailures = Expression.Lambda<Func<object, bool>>(Expression.Property(Expression.Field(typed, failures), isEmpty), instance).Compile();
            _closed = Expression.Lambda<Func<object, bool>>(Expression.Field(typed, closed), instance).Compile();
            _boundType = type;
        }

        // Main thread, at the start of every snapshot.
        internal static void Wait()
        {
            _snapshots++;
            object parallelizer = _parallelizer.Target;
            if (parallelizer == null || parallelizer.GetType() != _boundType || _closed(parallelizer))
            {
                // No scene has loaded one yet, or the one held belongs to a scene that has unloaded (the save comes
                // from a scene without a tick service of its own): nothing of it is ticking in parallel.
                _without++;
                return;
            }
            if (_pending(parallelizer) <= 0)
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            long giveUp = started + (long)(GiveUpAfterSeconds * Stopwatch.Frequency);
            SpinWait spin = default(SpinWait);
            bool failed = false, gaveUp = false;
            while (_pending(parallelizer) > 0)
            {
                if (!_noFailures(parallelizer))
                {
                    failed = true;
                    break;
                }
                if (Stopwatch.GetTimestamp() > giveUp)
                {
                    gaveUp = true;
                    break;
                }
                spin.SpinOnce();
            }
            // What the tasks wrote before they counted themselves done is seen by what the snapshot reads next.
            Thread.MemoryBarrier();
            long waited = Stopwatch.GetTimestamp() - started;
            _waited++;
            _waitStopwatchTicks += waited;
            _longestStopwatchTicks = Math.Max(_longestStopwatchTicks, waited);
            if (failed)
            {
                _failed++;
                Log.Warning("ParallelTickWait: one of the game's parallel tasks has failed; this save goes on without waiting " +
                            "for the rest, and the game reports the failure at its next tick.");
            }
            else if (gaveUp)
            {
                _gaveUp++;
                Log.Warning(string.Format(CultureInfo.InvariantCulture,
                    "ParallelTickWait: the game's parallel tick was still running after {0:0} s; this save goes on without " +
                    "waiting for it.", GiveUpAfterSeconds));
            }
        }

        public static string TakeStatsLine()
        {
            if (_snapshots == 0)
            {
                return null;
            }
            double ms = 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "ParallelTickWait: {0} snapshot(s); the game's parallel tick was still running at the start of {1} of them, " +
                "waited {2:0.0} ms in all (longest {3:0.0} ms)", _snapshots, _waited, _waitStopwatchTicks * ms,
                _longestStopwatchTicks * ms);
            if (_failed > 0 || _gaveUp > 0)
            {
                line += $"; stopped waiting {_failed} time(s) because a task had failed and {_gaveUp} time(s) after {GiveUpAfterSeconds:0} s";
            }
            if (_without > 0)
            {
                line += $"; {_without} without the game's parallelizer at hand (nothing to wait for)";
            }
            _snapshots = _waited = _waitStopwatchTicks = _longestStopwatchTicks = _failed = _gaveUp = _without = 0;
            return line;
        }

        // For the tests.
        internal static void ResetForTests()
        {
            _active = false;
            _parallelizer = new WeakReference(null);
            _boundType = null;
            _snapshots = _waited = _waitStopwatchTicks = _longestStopwatchTicks = _failed = _gaveUp = _without = 0;
            GiveUpAfterSeconds = 5;
        }
    }
}
