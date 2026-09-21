using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Timberborn.WaterObjects;

namespace LateGamePerformance
{
    // Reads that may run on any thread, results that are used in the original order on the calling thread.
    // Free of game types so the tests can drive it.
    internal static class OrderedParallel
    {
        // results[i] = read(items[i]) for every i below count. Which worker reads which item is fixed (strided),
        // and no worker touches anything but its own result slots. Returns the first exception a worker hit, or
        // null; it never throws.
        public static Exception Read<T>(IList<T> items, int count, int[] results, Func<T, int> read, int workers)
        {
            workers = Math.Max(1, Math.Min(workers, count));
            Exception failure = null;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
            {
                try
                {
                    for (int i = worker; i < count; i += workers)
                    {
                        results[i] = read(items[i]);
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
            });
            return failure;
        }
    }

    // Every tick the game asks, for every plant and every other object that cares about flooding (about 8000 in a
    // late game colony), how high the water stands at its tile (WaterObjectService.Tick -> WaterObject
    // .UpdateWaterAboveBase), one after another on the main thread: 1.3 ms per tick, 8% of all tick time, in the
    // colony this was measured in. Almost none of them change from one tick to the next.
    //
    // The question is a pure read: the object's fixed tile, looked up in the water map the game keeps for readers
    // on other threads (ThreadSafeWaterMap, only rewritten in its own tick, never during this one). So the reads
    // are spread over worker threads, and then the main thread goes through the results in the game's own order
    // and, for each object whose level changed, does exactly what the game does: store the new level and raise
    // WaterAboveBaseChanged. Those handlers (a plant starting to drown, a building flooding) run on the main
    // thread, in the same order, with the same values as without the mod.
    //
    // Why reading first gives the same values: a handler cannot change the water map (only its tick does) or an
    // object's tile, and it cannot add or remove an object from the list, because the game walks that list with
    // foreach and would throw if one did. So what object N reads does not depend on what happened for objects
    // before it. The result is the same on every computer whatever the thread count.
    //
    // PlantWaterVerify reads everything again on the main thread and compares. If anything throws, the feature
    // switches itself off and the game's own loop runs; nothing has been changed at that point, or what was
    // changed is what the game's loop would have changed first, so it simply carries on.
    internal static class PlantWater
    {
        // Below this many objects the game's own loop is as fast as starting workers.
        private const int MinObjects = 512;

        private static Func<object, List<WaterObject>> _objectsOf;
        private static Func<WaterObject, int> _currentLevel;
        private static Action<WaterObject, int> _setLevel;

        private static int[] _levels = new int[0];
        private static bool _active;
        private static bool _verify;
        private static int _workers;

        private static long _passes;
        private static long _objects;
        private static long _changes;
        private static long _readStopwatchTicks;
        private static long _applyStopwatchTicks;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.PlantWaterVerify;
            _workers = config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
            Feature feature = new Feature { Name = "PlantWater" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterObjectService.Tick",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return HarmonyLib.AccessTools.Method(typeof(WaterObjectService), "Tick");
                },
                Prefix = Reflect.Own(typeof(PlantWater), nameof(TickPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            Type service = typeof(WaterObjectService);
            Type waterObject = typeof(WaterObject);
            _objectsOf = Reflect.FieldGetter<List<WaterObject>>(service, "_waterObjects");

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo baseCoordinates = waterObject.GetField("_baseCoordinates", any);
            MethodInfo currentLevel = null, setLevel = null;
            foreach (MethodInfo method in waterObject.GetMethods(any))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "CurrentWaterAboveBase" && parameters.Length == 1 && method.ReturnType == typeof(int))
                {
                    currentLevel = method;
                }
                else if (method.Name == "UpdateWaterAboveBase" && parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    setLevel = method;
                }
            }
            if (baseCoordinates == null || currentLevel == null || setLevel == null ||
                currentLevel.GetParameters()[0].ParameterType != baseCoordinates.FieldType ||
                waterObject.GetProperty("WaterAboveBase") == null)
            {
                throw new MissingMemberException("WaterObject", "_baseCoordinates / CurrentWaterAboveBase / UpdateWaterAboveBase");
            }

            // o => o.CurrentWaterAboveBase(o._baseCoordinates): the game's own question, asked the game's own way.
            ParameterExpression o = Expression.Parameter(waterObject, "o");
            _currentLevel = Expression.Lambda<Func<WaterObject, int>>(
                Expression.Call(o, currentLevel, Expression.Field(o, baseCoordinates)), o).Compile();
            // (o, level) => o.UpdateWaterAboveBase(level): stores the level and raises the event.
            ParameterExpression level = Expression.Parameter(typeof(int), "level");
            _setLevel = Expression.Lambda<Action<WaterObject, int>>(Expression.Call(o, setLevel, level), o, level).Compile();
        }

        public static string TakeStatsLine()
        {
            if (!_active && _passes == 0)
            {
                return null;
            }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "PlantWater: {0} passes over {1} objects on {2} workers; reading {3:0.0} ms ({4:0.000} ms per pass), " +
                "{5} level changes applied in {6:0.0} ms{7}",
                _passes, _passes > 0 ? _objects / _passes : 0, _workers, _readStopwatchTicks * msPerTick,
                _passes > 0 ? _readStopwatchTicks * msPerTick / _passes : 0, _changes, _applyStopwatchTicks * msPerTick,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _passes = _objects = _changes = _readStopwatchTicks = _applyStopwatchTicks = 0;
            return line;
        }

        // ReSharper disable once InconsistentNaming
        internal static bool TickPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                List<WaterObject> objects = _objectsOf(__instance);
                int count = objects.Count;
                if (count < MinObjects)
                {
                    return true;
                }
                if (_levels.Length < count)
                {
                    _levels = new int[count + count / 4];
                }
                long started = Stopwatch.GetTimestamp();
                Exception failure = OrderedParallel.Read(objects, count, _levels, _currentLevel, _workers);
                if (failure != null)
                {
                    throw failure;
                }
                long read = Stopwatch.GetTimestamp();
                if (_verify)
                {
                    Verify(objects, count);
                }
                for (int i = 0; i < count && i < objects.Count; i++)
                {
                    WaterObject waterObject = objects[i];
                    if (_levels[i] != waterObject.WaterAboveBase)
                    {
                        _setLevel(waterObject, _levels[i]);
                        _changes++;
                    }
                }
                _passes++;
                _objects += count;
                _readStopwatchTicks += read - started;
                _applyStopwatchTicks += Stopwatch.GetTimestamp() - read;
                return false;
            }
            catch (Exception exception)
            {
                // The game's loop now runs over the same list. Objects this pass already updated compare equal
                // there and are passed over, so every change is still applied once, in order.
                _active = false;
                Log.Warning("PlantWater failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        private static void Verify(List<WaterObject> objects, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int level = _currentLevel(objects[i]);
                if (level != _levels[i])
                {
                    _verifyMismatches++;
                    if (_verifyMismatches <= 10)
                    {
                        Log.Warning($"PlantWater verify: object {i} read {_levels[i]} on a worker and {level} on the main " +
                                    "thread. Using the main thread's.");
                    }
                    _levels[i] = level;
                }
            }
        }
    }
}
