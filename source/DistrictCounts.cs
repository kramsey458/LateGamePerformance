using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.ResourceCountingSystem;

namespace LateGamePerformance
{
    // Every tick each district adds up, for every good, what all its storage holds and how much room it has
    // (DistrictResourceCounter.Tick -> UpdateCounters): 1.07 ms per tick in the colony this was measured in. The
    // numbers feed the top bar, the stockpile tooltips and, inside the simulation, the automation resource counter,
    // so they have to be the game's numbers at the game's moment; counting later or less often is not an option.
    //
    // Most of the cost is the capacity question, which the game asks per inventory per allowed good with a linear
    // search inside (a warehouse that allows 30 goods does some 900 string comparisons to report one number).
    // Both questions are pure reads of the inventory, and the answer is a sum of whole numbers, which comes out the
    // same in any order. So the inventories are dealt out to worker threads, each worker asks the game's own
    // questions (Inventory.Stock, Gives, PublicInput, GetCapacity) and adds up into its own tables, and the main
    // thread adds the workers' tables into the game's tables. Then the two steps that go through interfaces a mod
    // may implement (goods inside workshops, goods being carried) run as the game's own code on the main thread.
    //
    // What may run on a worker is decided per inventory. GetCapacity asks the inventory's IGoodDisallower, which a
    // mod may implement; only the game's four implementations, which were read and are pure, go to workers. Any
    // other inventory is counted on the main thread with the same code. And if another mod has patched one of the
    // methods the workers call, everything stays on the main thread (the game's own code runs).
    //
    // A key the game would have stored as 0 may be absent here and the other way round; the game only ever reads
    // these tables with "value or 0", so that cannot be observed.
    internal static class DistrictCounts
    {
        // Below this many inventories the game's own loop is as fast as starting workers.
        private const int MinInventories = 96;

        private static readonly string[] PureDisallowers =
        {
            "Timberborn.InventorySystem.NullGoodDisallower",
            "Timberborn.InventorySystem.SingleGoodAllower",
            "Timberborn.Workshops.RecipeGoodDisallower",
            "Timberborn.Yielding.InRangeYielderGoodAllower"
        };

        internal sealed class Tally
        {
            public readonly Dictionary<string, int> OutputStock = new Dictionary<string, int>();
            public readonly Dictionary<string, int> InputOutputStock = new Dictionary<string, int>();
            public readonly Dictionary<string, int> OutputCapacity = new Dictionary<string, int>();
            public readonly Dictionary<string, int> InputOutputCapacity = new Dictionary<string, int>();
            private readonly List<GoodAmount> _capacity = new List<GoodAmount>();

            public void Clear()
            {
                OutputStock.Clear();
                InputOutputStock.Clear();
                OutputCapacity.Clear();
                InputOutputCapacity.Clear();
                _capacity.Clear();
            }

            // StockCounter.CountInventoryStock and CapacityCounter.CountInventoryCapacity, for one inventory.
            public void Count(Inventory inventory)
            {
                bool publicInput = inventory.PublicInput;
                foreach (GoodAmount good in inventory.Stock)
                {
                    if (inventory.Gives(good.GoodId))
                    {
                        Add(publicInput ? InputOutputStock : OutputStock, good.GoodId, good.Amount);
                    }
                }
                _capacity.Clear();
                inventory.GetCapacity(_capacity);
                foreach (GoodAmount good in _capacity)
                {
                    if (inventory.Gives(good.GoodId))
                    {
                        Add(publicInput ? InputOutputCapacity : OutputCapacity, good.GoodId, good.Amount);
                    }
                }
                _capacity.Clear();
            }

            public static void Add(Dictionary<string, int> table, string goodId, int amount)
            {
                table.TryGetValue(goodId, out int sum);
                table[goodId] = unchecked(sum + amount);
            }

            public static void AddAll(Dictionary<string, int> target, Dictionary<string, int> source)
            {
                foreach (KeyValuePair<string, int> pair in source)
                {
                    Add(target, pair.Key, pair.Value);
                }
            }
        }

        private static Func<object, DistrictInventoryRegistry> _registryOf;
        private static Func<object, object> _stockCounterOf;
        private static Func<object, object> _capacityCounterOf;
        private static Func<object, object> _processedCounterOf;
        private static Func<object, Dictionary<string, int>> _outputStockOf;
        private static Func<object, Dictionary<string, int>> _inputOutputStockOf;
        private static Func<object, Dictionary<string, int>> _outputCapacityOf;
        private static Func<object, Dictionary<string, int>> _inputOutputCapacityOf;
        private static Func<object, IGoodDisallower> _disallowerOf;
        private static Action<object> _updateProcessed;
        private static Action<object> _updateCarried;
        private static MethodInfo _gameUpdateStock;
        private static MethodInfo _gameUpdateCapacity;
        private static MethodBase[] _workerMethods = new MethodBase[0];

        private static readonly Dictionary<Type, bool> PureByType = new Dictionary<Type, bool>();
        private static Inventory[] _parallel = new Inventory[0];
        private static Inventory[] _direct = new Inventory[0];
        private static Tally[] _tallies = new Tally[0];
        private static readonly Tally MainTally = new Tally();

        private static bool _active;
        private static bool _checkedForeignPatches;
        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        private static bool _verify;
        private static int _workers;

        private static long _passes;
        private static long _smallPasses;
        private static long _inventories;
        private static long _directInventories;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.DistrictCountsVerify;
            _workers = config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1));
            Feature feature = new Feature { Name = "DistrictCounts" };
            // Tick calls UpdateCounters and nothing else; both are patched so that it does not matter whether the
            // runtime has inlined the one into the other.
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictResourceCounter.Tick",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return AccessTools.Method(typeof(DistrictResourceCounter), "Tick");
                },
                Prefix = Reflect.Own(typeof(DistrictCounts), nameof(TickPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictResourceCounter.UpdateCounters",
                Required = true,
                Target = () => AccessTools.Method(typeof(DistrictResourceCounter), "UpdateCounters"),
                Prefix = Reflect.Own(typeof(DistrictCounts), nameof(UpdatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        internal static void ResetForTests()
        {
            _active = false;
            _checkedForeignPatches = false;
            _passes = _smallPasses = _inventories = _directInventories = _stopwatchTicks = _verifyMismatches = 0;
        }

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            Type counter = typeof(DistrictResourceCounter);
            Type stock = FieldType(counter, "_stockCounter");
            Type capacity = FieldType(counter, "_capacityCounter");
            Type processed = FieldType(counter, "_processedGoodCounter");
            _registryOf = Reflect.FieldGetter<DistrictInventoryRegistry>(counter, "_districtInventoryRegistry");
            _stockCounterOf = Reflect.FieldGetter<object>(counter, "_stockCounter");
            _capacityCounterOf = Reflect.FieldGetter<object>(counter, "_capacityCounter");
            _processedCounterOf = Reflect.FieldGetter<object>(counter, "_processedGoodCounter");
            _outputStockOf = Reflect.FieldGetter<Dictionary<string, int>>(stock, "_outputStock");
            _inputOutputStockOf = Reflect.FieldGetter<Dictionary<string, int>>(stock, "_inputOutputStock");
            _outputCapacityOf = Reflect.FieldGetter<Dictionary<string, int>>(capacity, "_outputCapacity");
            _inputOutputCapacityOf = Reflect.FieldGetter<Dictionary<string, int>>(capacity, "_inputOutputCapacity");
            _disallowerOf = Reflect.FieldGetter<IGoodDisallower>(typeof(Inventory), "_goodDisallower");
            _updateProcessed = Reflect.InstanceCall<Action<object>>(Parameterless(processed, "UpdateStock"));
            _updateCarried = Reflect.InstanceCall<Action<object>>(Parameterless(counter, "UpdateCarriedGoods"));
            _gameUpdateStock = AccessTools.Method(stock, "UpdateStock");
            _gameUpdateCapacity = AccessTools.Method(capacity, "UpdateCapacity");
            if (_gameUpdateStock == null || _gameUpdateCapacity == null)
            {
                throw new MissingMethodException("StockCounter.UpdateStock / CapacityCounter.UpdateCapacity");
            }

            // The shape of the game's counting this feature stands in for. If a game update adds a step to
            // UpdateCounters, these fields change and the feature does not start.
            int fields = 0;
            foreach (FieldInfo unused in counter.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                                                           BindingFlags.DeclaredOnly))
            {
                fields++;
            }
            if (fields != 7)
            {
                throw new MissingMemberException($"DistrictResourceCounter has {fields} fields where 7 were expected");
            }

            List<MethodBase> workerMethods = new List<MethodBase>
            {
                AccessTools.Method(typeof(Inventory), "GetCapacity"),
                AccessTools.Method(typeof(Inventory), "LimitedAmount"),
                AccessTools.Method(typeof(Inventory), "Gives"),
                AccessTools.Method(typeof(Inventory), "AmountInStock", new[] { typeof(string) }),
                AccessTools.PropertyGetter(typeof(Inventory), "Stock"),
                AccessTools.PropertyGetter(typeof(Inventory), "PublicInput")
            };
            foreach (string name in PureDisallowers)
            {
                Type type = Reflect.GameType(name);
                workerMethods.Add(AccessTools.Method(type, "AllowedAmount"));
            }
            foreach (MethodBase method in workerMethods)
            {
                if (method == null)
                {
                    throw new MissingMethodException("a method the workers call was not found");
                }
            }
            _workerMethods = workerMethods.ToArray();
        }

        private static Type FieldType(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null)
            {
                throw new MissingFieldException(type.Name, name);
            }
            return field.FieldType;
        }

        private static MethodInfo Parameterless(Type type, string name)
        {
            MethodInfo method = AccessTools.Method(type, name, Type.EmptyTypes);
            if (method == null)
            {
                throw new MissingMethodException(type.Name, name);
            }
            return method;
        }

        public static string TakeStatsLine()
        {
            if (!_active && _passes == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "DistrictCounts: {0} counts of {1} inventories on {2} workers in {3:0.0} ms ({4:0.000} ms each); " +
                "{5} inventories per count on the main thread; {6} counts of small districts left to the game{7}",
                _passes, _passes > 0 ? _inventories / _passes : 0, _workers, ms, _passes > 0 ? ms / _passes : 0,
                _passes > 0 ? _directInventories / _passes : 0, _smallPasses,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _passes = _smallPasses = _inventories = _directInventories = _stopwatchTicks = 0;
            return line;
        }

        // ReSharper disable once InconsistentNaming
        internal static bool TickPrefix(object __instance)
        {
            return Update(__instance, false);
        }

        // ReSharper disable once InconsistentNaming
        internal static bool UpdatePrefix(object __instance)
        {
            return Update(__instance, true);
        }

        private static bool Update(object __instance, bool countSmall)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (!_checkedForeignPatches)
                {
                    _checkedForeignPatches = true;
                    List<string> accepted = new List<string>();
                    string patched = ForeignPatch(accepted);
                    foreach (string reviewed in accepted)
                    {
                        Log.Info($"DistrictCounts: another mod patches {reviewed}; that patch was read and is safe to " +
                                 "run on worker threads, so counting stays on them.");
                    }
                    if (patched != null)
                    {
                        _active = false;
                        Log.Info($"DistrictCounts: another mod patches {patched}, which this feature would call on " +
                                 "worker threads. The game's own counting runs instead. The numbers are the same.");
                        return true;
                    }
                }
                DistrictInventoryRegistry registry = _registryOf(__instance);
                if (registry == null)
                {
                    return true;
                }
                var inventories = registry.Inventories;
                if (inventories.Count < MinInventories)
                {
                    if (countSmall)
                    {
                        _smallPasses++;
                    }
                    return true;
                }
                long started = Stopwatch.GetTimestamp();
                Count(__instance, inventories.Count, inventories.GetEnumerator());
                if (_verify)
                {
                    Verify(__instance, inventories);
                }
                _updateProcessed(_processedCounterOf(__instance));
                _updateCarried(__instance);
                _passes++;
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                return false;
            }
            catch (Exception exception)
            {
                // The game's own count starts by clearing every table, so whatever was written is thrown away.
                _active = false;
                TurnedOff.Report("DistrictCounts",
                    "DistrictCounts failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        // Another mod's patch that was read and may run on worker threads: target, Harmony id, patch method.
        //
        // MixedStorage (kyler.mixedstorage, read at 0.5.7): LimitPatch.Prefix answers AllowedAmount for its multi-good
        // warehouses and piles from the allocation the player set and Inventory.Capacity. It only reads, except for
        // its own per-storage cache of limits, which it rebuilds from those same two values; each storage belongs to
        // one inventory, and each inventory is counted by exactly one worker, so no two threads touch one cache. The
        // allocation only changes on the main thread (the panel, loading), never during a count.
        internal static readonly (string Target, string Owner, string Patch)[] ReviewedPatches =
        {
            ("SingleGoodAllower.AllowedAmount", "kyler.mixedstorage", "MixedStorage.LimitPatch.Prefix")
        };

        // Every patch on a method, as (Harmony id, "Namespace.Type.Method" of the patch); swapped by the tests,
        // where Harmony's patch registry cannot run.
        internal static Func<MethodBase, IEnumerable<(string Owner, string Patch)>> PatchesOn = method =>
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return null;
            }
            List<(string, string)> all = new List<(string, string)>();
            foreach (IEnumerable<Patch> kind in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
            {
                foreach (Patch patch in kind)
                {
                    all.Add((patch.owner, patch.PatchMethod.DeclaringType?.FullName + "." + patch.PatchMethod.Name));
                }
            }
            return all;
        };

        // The first patch by another mod that has not been read, or null. Reviewed ones are listed in `accepted`.
        internal static string ForeignPatch(List<string> accepted)
        {
            foreach (MethodBase method in _workerMethods)
            {
                IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
                if (patches == null)
                {
                    continue;
                }
                string target = $"{method.DeclaringType?.Name}.{method.Name}";
                foreach ((string owner, string patch) in patches)
                {
                    if (owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (Array.Exists(ReviewedPatches, reviewed =>
                            reviewed.Target == target && reviewed.Owner == owner && reviewed.Patch == patch))
                    {
                        accepted?.Add($"{target} ({owner}, {patch})");
                        continue;
                    }
                    return $"{target} ({owner}, {patch})";
                }
            }
            return null;
        }

        private static void Count(object counter, int count, HashSet<Inventory>.Enumerator enumerator)
        {
            if (_parallel.Length < count)
            {
                _parallel = new Inventory[count + count / 4];
                _direct = new Inventory[count + count / 4];
            }
            int parallel = 0, direct = 0;
            while (enumerator.MoveNext())
            {
                Inventory inventory = enumerator.Current;
                if (IsPure(_disallowerOf(inventory)))
                {
                    _parallel[parallel++] = inventory;
                }
                else
                {
                    _direct[direct++] = inventory;
                }
            }

            int workers = Math.Max(1, Math.Min(_workers, parallel));
            Exception failure = CountParallel(_parallel, parallel, workers, out int used);
            if (failure != null)
            {
                throw failure;
            }
            MainTally.Clear();
            for (int i = 0; i < direct; i++)
            {
                MainTally.Count(_direct[i]);
            }
            Array.Clear(_parallel, 0, parallel);
            Array.Clear(_direct, 0, direct);

            object stockCounter = _stockCounterOf(counter);
            object capacityCounter = _capacityCounterOf(counter);
            Dictionary<string, int> outputStock = _outputStockOf(stockCounter);
            Dictionary<string, int> inputOutputStock = _inputOutputStockOf(stockCounter);
            Dictionary<string, int> outputCapacity = _outputCapacityOf(capacityCounter);
            Dictionary<string, int> inputOutputCapacity = _inputOutputCapacityOf(capacityCounter);
            outputStock.Clear();
            inputOutputStock.Clear();
            outputCapacity.Clear();
            inputOutputCapacity.Clear();
            for (int worker = -1; worker < used; worker++)
            {
                Tally tally = worker < 0 ? MainTally : _tallies[worker];
                Tally.AddAll(outputStock, tally.OutputStock);
                Tally.AddAll(inputOutputStock, tally.InputOutputStock);
                Tally.AddAll(outputCapacity, tally.OutputCapacity);
                Tally.AddAll(inputOutputCapacity, tally.InputOutputCapacity);
            }
            _inventories += count;
            _directInventories += direct;
        }

        // Worker w counts items w, w + workers, w + 2 * workers... into its own tally, on this mod's own worker
        // threads (TickWorkers). `used` is how many workers shared the count. Returns the first exception a worker
        // hit, or null; it never throws.
        internal static Exception CountParallel(Inventory[] items, int count, int workers, out int used)
        {
            if (_tallies.Length < workers)
            {
                Tally[] tallies = new Tally[workers];
                for (int i = 0; i < workers; i++)
                {
                    tallies[i] = i < _tallies.Length ? _tallies[i] : new Tally();
                }
                _tallies = tallies;
            }
            Tally[] current = _tallies;
            int shared = 1;
            Exception failure = TickWorkers.Run(workers, (worker, sharing) =>
            {
                if (worker == 0)
                {
                    shared = sharing;
                }
                Tally tally = current[worker];
                tally.Clear();
                for (int i = worker; i < count; i += sharing)
                {
                    tally.Count(items[i]);
                }
            });
            used = shared;
            return failure;
        }

        private static bool IsPure(IGoodDisallower disallower)
        {
            if (disallower == null)
            {
                return false;
            }
            Type type = disallower.GetType();
            if (!PureByType.TryGetValue(type, out bool pure))
            {
                pure = Array.IndexOf(PureDisallowers, type.FullName) >= 0;
                PureByType[type] = pure;
            }
            return pure;
        }

        // Lets the game count into its own tables and compares with what was just written there.
        private static void Verify(object counter, object inventories)
        {
            object stockCounter = _stockCounterOf(counter);
            object capacityCounter = _capacityCounterOf(counter);
            Func<object, Dictionary<string, int>>[] tables =
                { _outputStockOf, _inputOutputStockOf, _outputCapacityOf, _inputOutputCapacityOf };
            string[] names = { "output stock", "input/output stock", "output capacity", "input/output capacity" };
            var mine = new Dictionary<string, int>[4];
            for (int i = 0; i < 4; i++)
            {
                mine[i] = new Dictionary<string, int>(tables[i](i < 2 ? stockCounter : capacityCounter));
            }
            _gameUpdateStock.Invoke(stockCounter, new[] { inventories });
            _gameUpdateCapacity.Invoke(capacityCounter, new[] { inventories });
            for (int i = 0; i < 4; i++)
            {
                string difference = Difference(mine[i], tables[i](i < 2 ? stockCounter : capacityCounter));
                if (difference != null)
                {
                    _verifyMismatches++;
                    if (_verifyMismatches <= 10)
                    {
                        Log.Warning($"DistrictCounts verify: {names[i]} of {difference}. Using the game's.");
                    }
                }
            }
        }

        // Null when both tables read the same through "value or 0".
        internal static string Difference(Dictionary<string, int> mine, Dictionary<string, int> games)
        {
            foreach (KeyValuePair<string, int> pair in games)
            {
                mine.TryGetValue(pair.Key, out int value);
                if (value != pair.Value)
                {
                    return $"{pair.Key}: the mod counted {value}, the game {pair.Value}";
                }
            }
            foreach (KeyValuePair<string, int> pair in mine)
            {
                if (pair.Value != 0 && !games.ContainsKey(pair.Key))
                {
                    return $"{pair.Key}: the mod counted {pair.Value}, the game 0";
                }
            }
            return null;
        }
    }
}
