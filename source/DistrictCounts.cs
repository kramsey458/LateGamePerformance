using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.ResourceCountingSystem;

namespace LateGamePerformance
{
    // Every tick each district adds up, for every good, what all its storage holds and how much room it has
    // (DistrictResourceCounter.Tick -> UpdateCounters). The numbers feed the top bar, the stockpile tooltips and,
    // inside the simulation, the automation resource counter, so they have to be the game's numbers at the game's
    // moment; counting later or less often is not an option.
    //
    // Most of the cost is the room question. The game asks it per inventory per allowed good (Inventory.GetCapacity:
    // LimitedAmount = Min(the capacity rule's AllowedAmount, StorableGoodRegistry.GetAmount)), and GetAmount searches
    // the allowed goods from the start for the first entry with that id, so a warehouse that allows 25 goods does
    // some 300 string comparisons to report one number; CapacityCounter then asks Gives (a set lookup) per good.
    //
    // Here (0.4.28), one walk per inventory. An inventory's allowed goods are only ever added to, in
    // Inventory.Initialize: they live in a StorableGoodRegistry (the list, its input and output sets) that nothing
    // else can reach, and it has no way to remove or change an entry. So for each inventory the mod asks the game
    // once, in the list's order, for each allowed good's id, what GetAmount returns for it (the first entry with that
    // id) and whether the inventory Gives it, and keeps the answers until the inventory holds a different registry
    // or its registry a different number of goods (a second Initialize), or the inventory itself is a different
    // object. Each count then does what GetCapacity and CapacityCounter do, in their order, with those answers:
    // skip an inventory with ignorable capacity (read every time; the emptying registry switches it), then for every
    // allowed good call the real AllowedAmount of the inventory's current capacity rule (any other mod's patch on it
    // runs, as it would for the game), take the Min with the kept amount, and add it when it is above 0 and the good
    // is given. The stock part is the game's own loop. Both write straight into the game's tables, stock for every
    // inventory first and then room, in the district's set order, with the same additions: the tables end up equal
    // to the game's, down to which keys they hold and the order they were added in.
    //
    // It runs on the main thread, for every district however small. From 0.4.13 to 0.4.27 the inventories were dealt
    // out to worker threads (districts under 96 inventories were left to the game), each thread still doing the
    // game's search per good. Measured in 0.4.23 (236 inventories, 7 threads) that took 0.66-0.93 ms per count with
    // a 14.6 ms maximum, no better than the game's own loop: the threads had to be woken every tick, and
    // MixedStorage's allocation rule looks each storage up in a ConditionalWeakTable on every AllowedAmount call,
    // which takes a lock under Mono, so the threads queued on it. With one walk per inventory the work left is a few
    // thousand calls. The tests time it at the logged colony's size (236 inventories, 38 warehouses of 25 goods,
    // under .NET 8, several runs): the game's loop 95-160 us, the walk on the main thread 40-65 us; the same walk on
    // 7 worker threads 25-35 us only while they are still awake, 140-350 us once they have gone to sleep between
    // counts (as they do between ticks), and 115-140 us even awake once every warehouse's rule takes a shared lock.
    //
    // If another mod patches a method whose answers are kept or stood in for (GetCapacity, LimitedAmount, Gives,
    // the allowed and output goods, GetAmount) or the game's counting itself (StockCounter, CapacityCounter), the
    // feature stands down with one log line and the game's own count runs.
    internal static class DistrictCounts
    {
        // What the mod keeps of one inventory's allowed goods (see above).
        internal sealed class AllowedGoods
        {
            public StorableGoodRegistry Registry;
            public int Count;
            public string[] Ids = new string[0];
            public int[] Amounts = new int[0];
            public bool[] Gives = new bool[0];
            public long Seen;
        }

        // Kept lists not used by any count for this many counts are dropped (an inventory that was destroyed).
        private const int PruneEveryCounts = 4096;

        private static Func<object, DistrictInventoryRegistry> _registryOf;
        private static Func<object, object> _stockCounterOf;
        private static Func<object, object> _capacityCounterOf;
        private static Func<object, object> _processedCounterOf;
        private static Func<object, Dictionary<string, int>> _outputStockOf;
        private static Func<object, Dictionary<string, int>> _inputOutputStockOf;
        private static Func<object, Dictionary<string, int>> _outputCapacityOf;
        private static Func<object, Dictionary<string, int>> _inputOutputCapacityOf;
        private static Func<object, IGoodDisallower> _disallowerOf;
        private static Func<object, StorableGoodRegistry> _allowedGoodsOf;
        private static Func<object, bool> _ignorableCapacityOf;
        private static Action<object> _updateProcessed;
        private static Action<object> _updateCarried;
        private static Action<object, ReadOnlyHashSet<Inventory>> _gameUpdateStock;
        private static Action<object, ReadOnlyHashSet<Inventory>> _gameUpdateCapacity;
        private static MethodBase[] _standInFor = new MethodBase[0];

        private static readonly Dictionary<Inventory, AllowedGoods> Allowed = new Dictionary<Inventory, AllowedGoods>();
        private static readonly List<Inventory> PruneScratch = new List<Inventory>();

        private static bool _active;
        private static bool _checkedForeignPatches;
        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        private static bool _verify;
        private static long _stamp;

        private static long _passes;
        private static long _inventories;
        private static long _listsRead;
        private static long _stopwatchTicks;
        private static long _verifyStopwatchTicks;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.DistrictCountsVerify;
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
            Allowed.Clear();
            _passes = _inventories = _listsRead = _stopwatchTicks = _verifyStopwatchTicks = _verifyMismatches = 0;
        }

        // A new game scene: the previous scene's inventories must not be kept alive by this cache.
        public static void SceneCreated()
        {
            Allowed.Clear();
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
            _allowedGoodsOf = Reflect.FieldGetter<StorableGoodRegistry>(typeof(Inventory), "_allowedGoods");
            _ignorableCapacityOf = Reflect.FieldGetter<bool>(typeof(Inventory), "_ignorableCapacity");
            _updateProcessed = Reflect.InstanceCall<Action<object>>(Parameterless(processed, "UpdateStock"));
            _updateCarried = Reflect.InstanceCall<Action<object>>(Parameterless(counter, "UpdateCarriedGoods"));
            _gameUpdateStock = Reflect.InstanceCall<Action<object, ReadOnlyHashSet<Inventory>>>(AccessTools.Method(stock, "UpdateStock"));
            _gameUpdateCapacity = Reflect.InstanceCall<Action<object, ReadOnlyHashSet<Inventory>>>(AccessTools.Method(capacity, "UpdateCapacity"));

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

            List<MethodBase> standInFor = new List<MethodBase>
            {
                AccessTools.Method(typeof(Inventory), "GetCapacity"),
                AccessTools.Method(typeof(Inventory), "LimitedAmount"),
                AccessTools.Method(typeof(Inventory), "Gives"),
                AccessTools.PropertyGetter(typeof(Inventory), "AllowedGoods"),
                AccessTools.PropertyGetter(typeof(Inventory), "OutputGoods"),
                AccessTools.Method(typeof(StorableGoodRegistry), "GetAmount"),
                AccessTools.PropertyGetter(typeof(StorableGoodRegistry), "Goods"),
                AccessTools.PropertyGetter(typeof(StorableGoodRegistry), "OutputGoods")
            };
            standInFor.AddRange(AccessTools.GetDeclaredMethods(stock));
            standInFor.AddRange(AccessTools.GetDeclaredMethods(capacity));
            foreach (MethodBase method in standInFor)
            {
                if (method == null)
                {
                    throw new MissingMethodException("a method this feature stands in for was not found");
                }
            }
            _standInFor = standInFor.ToArray();
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

        // "ms each" is the stock and room count, the part the mod does instead of the game; the verify part times the
        // game's own count of the same (StockCounter.UpdateStock + CapacityCounter.UpdateCapacity), so the two compare.
        public static string TakeStatsLine()
        {
            if (!_active && _passes == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double gameMs = _verifyStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "DistrictCounts: {0} counts of {1} inventories on the main thread in {2:0.0} ms ({3:0.000} ms each); " +
                "allowed goods read anew for {4} inventories{5}",
                _passes, _passes > 0 ? _inventories / _passes : 0, ms, _passes > 0 ? ms / _passes : 0, _listsRead,
                _verify
                    ? string.Format(CultureInfo.InvariantCulture,
                        "; verify mismatches {0}; the game's own count of the same took {1:0.0} ms ({2:0.000} ms each)",
                        _verifyMismatches, gameMs, _passes > 0 ? gameMs / _passes : 0)
                    : "");
            _passes = _inventories = _listsRead = _stopwatchTicks = _verifyStopwatchTicks = 0;
            return line;
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool TickPrefix(object __instance)
        {
            return Update(__instance);
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool UpdatePrefix(object __instance)
        {
            return Update(__instance);
        }

        private static bool Update(object __instance)
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
                    string patched = ForeignPatch(null);
                    if (patched != null)
                    {
                        _active = false;
                        Log.Info($"DistrictCounts: another mod patches {patched}, which this feature stands in for. " +
                                 "The game's own counting runs instead. The numbers are the same.");
                        return true;
                    }
                }
                DistrictInventoryRegistry registry = _registryOf(__instance);
                if (registry == null)
                {
                    return true;
                }
                ReadOnlyHashSet<Inventory> inventories = registry.Inventories;
                _stamp++;
                long started = Stopwatch.GetTimestamp();
                Count(__instance, inventories);
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                if (_verify)
                {
                    Verify(__instance, inventories);
                }
                _updateProcessed(_processedCounterOf(__instance));
                _updateCarried(__instance);
                _passes++;
                _inventories += inventories.Count;
                if (_stamp % PruneEveryCounts == 0)
                {
                    Prune();
                }
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

        // Another mod's patch that was read and may stay: target, Harmony id, patch method. None since 0.4.28: the
        // count now calls the capacity rules on the main thread exactly as the game does, so a patch on
        // AllowedAmount runs for the mod as it would for the game. (MixedStorage's LimitPatch on
        // SingleGoodAllower.AllowedAmount was listed here from 0.4.15 as safe on worker threads.)
        internal static readonly (string Target, string Owner, string Patch)[] ReviewedPatches =
            new (string Target, string Owner, string Patch)[0];

        // Every patch on a method; swapped by the tests. Shared with HaulCache (ForeignPatches).
        internal static Func<MethodBase, IEnumerable<(string Owner, string Patch)>> PatchesOn
        {
            get => ForeignPatches.PatchesOn;
            set => ForeignPatches.PatchesOn = value;
        }

        // The first patch by another mod on a method this feature stands in for that has not been read, or null.
        // Reviewed ones are listed in `accepted`.
        internal static string ForeignPatch(List<string> accepted)
        {
            return ForeignPatches.First(_standInFor, ReviewedPatches, accepted);
        }

        // StockCounter.UpdateStock and then CapacityCounter.UpdateCapacity, with the kept allowed goods.
        private static void Count(object counter, ReadOnlyHashSet<Inventory> inventories)
        {
            object stockCounter = _stockCounterOf(counter);
            object capacityCounter = _capacityCounterOf(counter);
            Dictionary<string, int> outputStock = _outputStockOf(stockCounter);
            Dictionary<string, int> inputOutputStock = _inputOutputStockOf(stockCounter);
            Dictionary<string, int> outputCapacity = _outputCapacityOf(capacityCounter);
            Dictionary<string, int> inputOutputCapacity = _inputOutputCapacityOf(capacityCounter);
            outputStock.Clear();
            inputOutputStock.Clear();
            foreach (Inventory inventory in inventories)
            {
                CountStock(inventory, outputStock, inputOutputStock);
            }
            outputCapacity.Clear();
            inputOutputCapacity.Clear();
            foreach (Inventory inventory in inventories)
            {
                CountCapacity(inventory, AllowedOf(inventory), outputCapacity, inputOutputCapacity);
            }
        }

        // StockCounter.CountInventoryStock, for one inventory.
        internal static void CountStock(Inventory inventory, Dictionary<string, int> output, Dictionary<string, int> inputOutput)
        {
            foreach (GoodAmount good in inventory.Stock)
            {
                if (inventory.Gives(good.GoodId))
                {
                    Add(inventory.PublicInput ? inputOutput : output, good.GoodId, good.Amount);
                }
            }
        }

        // Inventory.GetCapacity and CapacityCounter.CountInventoryCapacity, for one inventory, with its kept list.
        internal static void CountCapacity(Inventory inventory, AllowedGoods allowed, Dictionary<string, int> output,
            Dictionary<string, int> inputOutput)
        {
            if (_ignorableCapacityOf(inventory))
            {
                return;
            }
            IGoodDisallower disallower = _disallowerOf(inventory);
            string[] ids = allowed.Ids;
            int[] amounts = allowed.Amounts;
            bool[] gives = allowed.Gives;
            for (int i = 0; i < allowed.Count; i++)
            {
                int amount = Math.Min(disallower.AllowedAmount(ids[i]), amounts[i]);
                if (amount > 0 && gives[i])
                {
                    Add(inventory.PublicInput ? inputOutput : output, ids[i], amount);
                }
            }
        }

        // The kept list of an inventory, read from the game again if its allowed goods are not the ones it was read
        // from. Main thread only.
        internal static AllowedGoods AllowedOf(Inventory inventory)
        {
            if (!Allowed.TryGetValue(inventory, out AllowedGoods allowed))
            {
                allowed = new AllowedGoods();
                Allowed[inventory] = allowed;
            }
            StorableGoodRegistry registry = _allowedGoodsOf(inventory);
            if (!ReferenceEquals(allowed.Registry, registry) || allowed.Count != registry.Goods.Count)
            {
                ReadAllowed(inventory, registry, allowed);
            }
            allowed.Seen = _stamp;
            return allowed;
        }

        private static void ReadAllowed(Inventory inventory, StorableGoodRegistry registry, AllowedGoods allowed)
        {
            ReadOnlyList<StorableGoodAmount> goods = registry.Goods;
            int count = goods.Count;
            if (allowed.Ids.Length < count)
            {
                allowed.Ids = new string[count];
                allowed.Amounts = new int[count];
                allowed.Gives = new bool[count];
            }
            for (int i = 0; i < count; i++)
            {
                string goodId = goods[i].StorableGood.GoodId;
                allowed.Ids[i] = goodId;
                allowed.Amounts[i] = registry.GetAmount(goodId);
                allowed.Gives[i] = inventory.Gives(goodId);
            }
            allowed.Registry = registry;
            allowed.Count = count;
            _listsRead++;
        }

        private static void Prune()
        {
            PruneScratch.Clear();
            foreach (KeyValuePair<Inventory, AllowedGoods> pair in Allowed)
            {
                if (pair.Value.Seen < _stamp - PruneEveryCounts)
                {
                    PruneScratch.Add(pair.Key);
                }
            }
            foreach (Inventory inventory in PruneScratch)
            {
                Allowed.Remove(inventory);
            }
            PruneScratch.Clear();
        }

        internal static void Add(Dictionary<string, int> table, string goodId, int amount)
        {
            table.TryGetValue(goodId, out int sum);
            table[goodId] = unchecked(sum + amount);
        }

        // Lets the game count into its own tables (timed), compares with what the mod had just written there, and
        // puts the mod's numbers back: the setting is each player's own, so in co-op the one player who has it on must
        // read the same numbers as the others (up to 0.4.26 a difference left the game's in the tables). Refilled in
        // the order they were copied out, so the tables end up as they are with the setting off.
        private static void Verify(object counter, ReadOnlyHashSet<Inventory> inventories)
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
            long started = Stopwatch.GetTimestamp();
            _gameUpdateStock(stockCounter, inventories);
            _gameUpdateCapacity(capacityCounter, inventories);
            _verifyStopwatchTicks += Stopwatch.GetTimestamp() - started;
            for (int i = 0; i < 4; i++)
            {
                Dictionary<string, int> games = tables[i](i < 2 ? stockCounter : capacityCounter);
                string difference = Difference(mine[i], games);
                if (difference != null)
                {
                    _verifyMismatches++;
                    if (_verifyMismatches <= 10)
                    {
                        Log.Warning($"DistrictCounts verify: {names[i]} of {difference}.");
                    }
                }
                games.Clear();
                foreach (KeyValuePair<string, int> pair in mine[i])
                {
                    games.Add(pair.Key, pair.Value);
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
