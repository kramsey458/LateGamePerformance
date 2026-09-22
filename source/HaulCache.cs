using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Timberborn.BlockingSystem;
using Timberborn.Emptying;
using Timberborn.Hauling;
using Timberborn.InventorySystem;
using Timberborn.StockpilePrioritySystem;
using Timberborn.WorkSystem;
using Timberborn.Workshops;

namespace LateGamePerformance
{
    // Vanilla: every hauler decision asks every haul candidate (building with an inventory) in the district
    // for its weighted behaviors, which scans the building's inventories, then sorts the whole list.
    //
    // Here: each building's weighted behaviors are kept until something that feeds them changes. The district's
    // list is assembled from them on every request, in the same candidate order and sorted with the same
    // comparison as vanilla, so the result is the same list. What is kept is the game's own answer
    // (HaulCandidate.GetWeightedBehaviors), asked again whenever it could have changed.
    //
    // Up to 0.4.11 the sorted district list was kept as well, until any building in it changed. In every session
    // measured, on two computers, that list was never served from cache once: a hauler that takes a job reserves
    // stock, which changes a building, so the next request always found the list out of date. It was code and
    // bookkeeping with no effect, and is gone.
    //
    // Up to 0.4.27 everything was also dropped at the start of every tick, so that an input this mod did not know
    // about could only be stale within a tick. With under one request per tick that left only about a third of the
    // buildings reused. From 0.4.28 an answer is kept across ticks, which rests on every input of the game's eight
    // haul providers being covered. Read provider by provider in Timberborn 1.1.2.4:
    //
    //   input (read by)                              changes only in                         covered by
    //   HaulPrioritizable.Prioritized                its public auto setter (entity panel,   value compared on every
    //     (HaulCandidate.PrioritizeAndValidate)      batch control, DuplicateFrom, Load,     request
    //                                                BeaverBuddies' replayed event)
    //   Emptiable.IsMarkedForEmptying (Emptiable)    private auto setter: MarkForEmptying,   value compared
    //                                                UnmarkForEmptying, Load
    //   GoodObtainer.IsObtaining (ObtainGood)        Enable/DisableObtaining, Load           value compared
    //   GoodSupplier.IsSupplying (SupplyGood)        Enable/DisableSupplying, Load           value compared
    //   Manufactory.HasCurrentRecipe (Manufactory)   SetRecipe, Load                         CurrentRecipe compared
    //   BlockableObject.IsUnblocked (all but         Block, Unblock                          postfixes
    //     Emptiable and UnwantedStock)
    //   Inventory.Enabled, and Inventories'          Inventory.Enable/Disable (every caller  postfixes
    //     enabled list and its order (SimpleOutput,  in the game goes through them; the list
    //     FillInput, Manufactory, UnwantedStock)     follows their events)
    //   Inventory.HasUnwantedStock (UnwantedStock)   Enable; OnDisallowedGoodsChanged and    the Enable postfix and
    //                                                TakeInternal, each followed by the      the changed-event postfix
    //                                                inventory's changed event
    //   the stock (every fill percentage)            Give*/Take*, all through the changed    InvokeInventoryChangedEvent
    //                                                event; Load replaces it (a new scene)   postfix
    //   the allowed goods, their amounts, the        Inventory.Initialize only               nothing to cover
    //     input and output sets, IsInput, IsOutput,
    //     Capacity (fill percentages)
    //   (bool) of BreedingPod and Manufactory        the building is destroyed only after    the candidate-set drop
    //                                                it has left the district
    //   the capacity rule's AllowedAmount, inside    see below
    //     LimitedAmount (fill percentages)
    //   reservations                                 read by no provider (they raise the changed event, so they
    //                                                mark the building anyway)
    //
    // The capacity rules: NullGoodDisallower answers a constant. SingleGoodAllower reads its allowed good (Allow and
    // Disallow announce every change through DisallowedGoodsChanged, which the inventory turns into its changed
    // event) and the inventory's own stock; with MixedStorage (LimitPatch, read at 0.5.8 and 1.0.0) it answers from
    // the player's allocation instead, which MixedStorage announces for every good the storage takes, through the same
    // event, whenever one is applied or copied (and in 1.0.0 dropped), and from Inventory.Capacity. It is set from a
    // save only while one loads. RecipeGoodDisallower reads limits set only in
    // UpdateAllowedAmounts, which announces every good. InRangeYielderGoodAllower (the lumberjack and gatherer flags'
    // output) reads its allowed yields (announced), the stock, and two things nothing announces: the number of
    // assigned workers and the goods on their way in (dropped in its own Tick). Both only matter for a good with
    // nothing in stock (a good in stock is always allowed), and the in-stock output fill that SimpleOutput reads
    // skips goods with nothing in stock. So a building with such an inventory is kept only while all its providers
    // are SimpleOutput, Emptiable and UnwantedStock, which read no limit of a good with nothing in stock; with any
    // other provider it is asked again on every request.
    //
    // Anything not in the table is not kept: a building with a haul provider or a capacity rule of any other type
    // (another mod's, or a subclass) is asked again on every request. If another mod patches one of the methods the
    // table was read from (other than the reviewed patch below), or the game's code of those methods is not the code
    // that was read (an IL hash, as SaveSnapshot uses), no building is kept at all and every request asks every
    // building, the game's own way. Buildings joining or leaving a district still drop everything.
    //
    // The five flags are compared when read rather than hooked because their setters are 8-byte methods, and Mono
    // compiles a method under 20 bytes of IL into its callers, where a hook would not run. Every input hook is on a
    // method of 20 bytes or more (the tests check). (In practice a method patched at startup seems to stay patched for
    // callers compiled later: TickableSingletonService.TickAll is 19 bytes, and the stats lines that hang on it have
    // appeared in every session. Nothing here depends on that.)
    internal static class HaulCache
    {
        private sealed class CandidateEntry
        {
            public readonly List<WeightedBehavior> Items = new List<WeightedBehavior>();
            public int Epoch = -1;
            public bool Dirty = true;
            public bool InputsRegistered;
            // False: asked again on every request (see the table above).
            public bool Keepable;
            // The tick Items was computed in, for the stats line.
            public long Tick;
            // The flags compared on every request, and their values when Items was computed.
            public HaulPrioritizable Prioritizable;
            public Emptiable Emptiable;
            public GoodObtainer Obtainer;
            public GoodSupplier Supplier;
            public Manufactory Manufactory;
            public bool Prioritized;
            public bool MarkedForEmptying;
            public bool Obtaining;
            public bool Supplying;
            public RecipeSpec Recipe;

            public bool FlagsAsTaken()
            {
                return (Prioritizable == null || Prioritizable.Prioritized == Prioritized) &&
                       (Emptiable == null || Emptiable.IsMarkedForEmptying == MarkedForEmptying) &&
                       (Obtainer == null || Obtainer.IsObtaining == Obtaining) &&
                       (Supplier == null || Supplier.IsSupplying == Supplying) &&
                       (Manufactory == null || ReferenceEquals(Manufactory.CurrentRecipe, Recipe));
            }

            public void TakeFlags()
            {
                Prioritized = Prioritizable != null && Prioritizable.Prioritized;
                MarkedForEmptying = Emptiable != null && Emptiable.IsMarkedForEmptying;
                Obtaining = Obtainer != null && Obtainer.IsObtaining;
                Supplying = Supplier != null && Supplier.IsSupplying;
                Recipe = Manufactory != null ? Manufactory.CurrentRecipe : null;
            }
        }

        private const string DistrictHaulCandidatesType = "Timberborn.Hauling.DistrictHaulCandidates";

        // The game's haul providers, and whether one reads a capacity limit of a good with nothing in stock (the
        // input and output fill percentages go over every allowed good).
        private static readonly (string Type, bool ReadsLimits)[] Providers =
        {
            ("Timberborn.Emptying.EmptiableHaulBehaviorProvider", false),
            ("Timberborn.Emptying.UnwantedStockHaulBehaviorProvider", false),
            ("Timberborn.SimpleOutputBuildings.SimpleOutputInventoryHaulBehaviorProvider", false),
            ("Timberborn.Reproduction.BringNutrientHaulBehaviorProvider", true),
            ("Timberborn.StockpilePrioritySystem.ObtainGoodHaulBehaviorProvider", true),
            ("Timberborn.StockpilePrioritySystem.SupplyGoodHaulBehaviorProvider", true),
            ("Timberborn.Workshops.FillInputHaulBehaviorProvider", true),
            ("Timberborn.Workshops.ManufactoryHaulBehaviorProvider", true)
        };

        // The game's capacity rules, and whether everything their answer depends on is announced (false: only for
        // goods in stock).
        private static readonly (string Type, bool Announced)[] Rules =
        {
            ("Timberborn.InventorySystem.NullGoodDisallower", true),
            ("Timberborn.InventorySystem.SingleGoodAllower", true),
            ("Timberborn.Workshops.RecipeGoodDisallower", true),
            ("Timberborn.Yielding.InRangeYielderGoodAllower", false)
        };

        // Another mod's patch on a method in ReadMethods that was read: target, Harmony id, patch method. MixedStorage
        // (read at 0.5.8, the version the logged colony runs, and 1.0.0): LimitPatch.Prefix answers
        // SingleGoodAllower.AllowedAmount for its multi-good warehouses and piles from the allocation (announced, see
        // the top) and Inventory.Capacity (set once, in Initialize).
        internal static readonly (string Target, string Owner, string Patch)[] ReviewedPatches =
        {
            ("SingleGoodAllower.AllowedAmount", "kyler.mixedstorage", "MixedStorage.LimitPatch.Prefix")
        };

        // The IL of ReadMethods() in Timberborn 1.1.2.4, hashed (FNV-1a, 64 bits); the tests print the current one.
        internal const string ReadHash = "5635d1eb1caa8844";

        // Same comparison as DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered.
        private static readonly Comparison<WeightedBehavior> ByWeightDescending =
            (a, b) => b.Weight.CompareTo(a.Weight);

        private static readonly Dictionary<HaulCandidate, CandidateEntry> Candidates =
            new Dictionary<HaulCandidate, CandidateEntry>();

        // Inventories and blockable objects of a building -> that building's entry.
        private static readonly Dictionary<object, CandidateEntry> EntryByInput =
            new Dictionary<object, CandidateEntry>();

        // The list being assembled for the current request. Main thread only.
        private static readonly List<WeightedBehavior> Weighted = new List<WeightedBehavior>();
        private static readonly List<WorkplaceBehavior> Ordered = new List<WorkplaceBehavior>();

        private static readonly List<Inventory> InventoryScratch = new List<Inventory>();
        private static readonly List<WeightedBehavior> VerifyScratch = new List<WeightedBehavior>();
        private static readonly List<WeightedBehavior> VerifyItemScratch = new List<WeightedBehavior>();

        private static Func<object, HashSet<HaulCandidate>> _haulCandidatesOf;
        private static Func<object, List<IHaulBehaviorProvider>> _providersOf;
        private static Func<object, IGoodDisallower> _disallowerOf;
        private static bool _active;
        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        private static bool _verify;
        private static int _flushEveryTicks;
        private static int _epoch;
        private static long _ticks;
        // Whether anything is kept between requests at all; decided on the first request (see the top).
        private static List<MethodBase> _read;
        private static string _readProblem;
        private static bool _checked;
        private static bool _keep = true;
        private static string _notKept;

        // Stats since the last report.
        private static long _requests;
        private static long _candidatesRecomputed;
        private static long _candidatesReused;
        private static long _reusedFromEarlierTick;
        private static long _recomputedAlways;
        private static long _rebuildStopwatchTicks;
        private static long _verifyStopwatchTicks;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.HaulCacheVerify;
            _flushEveryTicks = config.HaulCacheFlushEveryTicks;
            Type self = typeof(HaulCache);
            Feature feature = new Feature { Name = "HaulCache" };

            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered",
                Required = true,
                Target = () =>
                {
                    Type type = Reflect.GameType(DistrictHaulCandidatesType);
                    _haulCandidatesOf = Reflect.FieldGetter<HashSet<HaulCandidate>>(type, "_haulCandidates");
                    _providersOf = Reflect.FieldGetter<List<IHaulBehaviorProvider>>(typeof(HaulCandidate), "_providers");
                    _disallowerOf = Reflect.FieldGetter<IGoodDisallower>(typeof(Inventory), "_goodDisallower");
                    BindRead();
                    return Reflect.Method(DistrictHaulCandidatesType, "GetWorkplaceBehaviorsOrdered");
                },
                Prefix = Reflect.Own(self, nameof(GetWorkplaceBehaviorsOrderedPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "Inventory.InvokeInventoryChangedEvent",
                Required = true,
                Target = () => Reflect.Method("Timberborn.InventorySystem.Inventory", "InvokeInventoryChangedEvent"),
                Postfix = Reflect.Own(self, nameof(InputChangedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictHaulCandidates.OnFinishedBuildingRegistered",
                Required = true,
                Target = () => Reflect.Method(DistrictHaulCandidatesType, "OnFinishedBuildingRegistered"),
                Postfix = Reflect.Own(self, nameof(CandidateSetChangedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictHaulCandidates.OnFinishedBuildingUnregistered",
                Required = true,
                Target = () => Reflect.Method(DistrictHaulCandidatesType, "OnFinishedBuildingUnregistered"),
                Postfix = Reflect.Own(self, nameof(CandidateSetChangedPostfix))
            });
            // The start of every tick: counts ticks for the stats line, and would drop everything every
            // HaulCacheFlushEveryTicks ticks if that fixed value were above 0 (it is 0 from 0.4.28). The cache's own
            // required hook, so it never depends on the optional one that writes the stats lines.
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableSingletonService.TickAll",
                Required = true,
                Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll"),
                Prefix = Reflect.Own(self, nameof(OnTickStarted))
            });

            // Building-local inputs read by the game's haul providers (the table at the top). Required since 0.4.28:
            // with nothing dropped on a timer, a missing one would leave a building's answer out of date for good.
            AddInputHook(feature, "Inventory.Enable", () => Reflect.Method("Timberborn.InventorySystem.Inventory", "Enable"));
            AddInputHook(feature, "Inventory.Disable", () => Reflect.Method("Timberborn.InventorySystem.Inventory", "Disable"));
            AddInputHook(feature, "BlockableObject.Block", () => Reflect.Method("Timberborn.BlockingSystem.BlockableObject", "Block"));
            AddInputHook(feature, "BlockableObject.Unblock", () => Reflect.Method("Timberborn.BlockingSystem.BlockableObject", "Unblock"));
            return feature;
        }

        public static void Activate()
        {
            Reset();
            _active = true;
        }

        public static void Reset()
        {
            Candidates.Clear();
            EntryByInput.Clear();
            _epoch++;
            _ticks = 0;
        }

        internal static void ResetForTests()
        {
            Reset();
            _active = false;
            _checked = false;
            _keep = true;
            _notKept = null;
            _requests = _candidatesRecomputed = _candidatesReused = _reusedFromEarlierTick = _recomputedAlways = 0;
            _rebuildStopwatchTicks = _verifyStopwatchTicks = _verifyMismatches = 0;
        }

        // Prefix on TickableSingletonService.TickAll.
        private static void OnTickStarted()
        {
            if (!_active)
            {
                return;
            }
            _ticks++;
            if (_flushEveryTicks > 0 && _ticks % _flushEveryTicks == 0)
            {
                _epoch++;
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active)
            {
                return null;
            }
            double buildMs = _rebuildStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double perRequestMs = _requests > 0 ? buildMs / _requests : 0;
            long served = _candidatesRecomputed + _candidatesReused;
            string line =
                $"HaulCache: {_requests} hauler list requests built in {buildMs:0.0} ms ({perRequestMs:0.000} ms each); " +
                $"buildings reused {_candidatesReused}/{served} ({_reusedFromEarlierTick} from an earlier tick), " +
                $"recomputed {_candidatesRecomputed}" +
                (_recomputedAlways > 0 && _keep
                    ? $" ({_recomputedAlways} of them on every request: a haul provider or capacity rule the mod has not read)"
                    : "") +
                (_keep ? "" : "; nothing kept between requests: " + _notKept) +
                (_verify
                    ? $"; verify mismatches {_verifyMismatches}; the game's own build alongside took " +
                      $"{_verifyStopwatchTicks * 1000.0 / Stopwatch.Frequency:0.0} ms " +
                      $"({(_requests > 0 ? _verifyStopwatchTicks * 1000.0 / Stopwatch.Frequency / _requests : 0):0.000} ms each)"
                    : "");
            _requests = _candidatesRecomputed = _candidatesReused = _reusedFromEarlierTick = _recomputedAlways = 0;
            _rebuildStopwatchTicks = _verifyStopwatchTicks = 0;
            return line;
        }

        private static void AddInputHook(Feature feature, string name, Func<MethodBase> target)
        {
            feature.Patches.Add(new PatchSpec
            {
                Name = name,
                Required = true,
                Target = target,
                Postfix = Reflect.Own(typeof(HaulCache), nameof(InputChangedPostfix))
            });
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        private static bool GetWorkplaceBehaviorsOrderedPrefix(object __instance, IList<WorkplaceBehavior> workplaceBehaviors)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (!_checked)
                {
                    _checked = true;
                    CheckWhatWasRead();
                }
                _requests++;
                Build(__instance);
                if (_verify)
                {
                    // The game's own build alongside, timed, so the two can be compared from the stats line. It only
                    // compares: Ordered, the cached list, is what the game is handed either way (the tests drive
                    // Compare, not this prefix, so keep any substitution out of here too).
                    long verifying = Stopwatch.GetTimestamp();
                    Verify(__instance);
                    _verifyStopwatchTicks += Stopwatch.GetTimestamp() - verifying;
                }
                for (int i = 0; i < Ordered.Count; i++)
                {
                    workplaceBehaviors.Add(Ordered[i]);
                }
                return false;
            }
            catch (Exception exception)
            {
                // Nothing was added to workplaceBehaviors before this point, so vanilla can take over cleanly.
                _active = false;
                TurnedOff.Report("HaulCache", "HaulCache failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        // Finds the methods the table at the top was read from and hashes their IL; at startup, so the first request
        // in a game does not pay for the reflection.
        private static void BindRead()
        {
            try
            {
                _read = ReadMethods();
                string hash = Hash(_read);
                _readProblem = hash == ReadHash
                    ? null
                    : $"the game's haul providers are not the code that was read (hash {hash}, read {ReadHash})";
            }
            catch (Exception exception)
            {
                _read = null;
                _readProblem = "the game's haul providers could not be checked (" + exception.Message + ")";
            }
        }

        // Whether the code the table at the top was read from is what runs: the game's own IL unchanged, and no unread
        // patch of another mod on it (checked on the first request, when every mod has patched). If not, nothing is
        // kept between requests (still the game's list every time).
        private static void CheckWhatWasRead()
        {
            if (_read == null)
            {
                // Not found at startup (or not bound, in the tests): once more, now that the game is loaded.
                BindRead();
            }
            _notKept = _readProblem;
            if (_notKept == null)
            {
                try
                {
                    List<string> accepted = new List<string>();
                    string patched = ForeignPatches.First(_read, ReviewedPatches, accepted);
                    foreach (string reviewed in accepted)
                    {
                        Log.Info($"HaulCache: another mod patches {reviewed}; that patch was read and changes nothing the " +
                                 "kept hauling jobs depend on without saying so.");
                    }
                    if (patched != null)
                    {
                        _notKept = "another mod patches " + patched;
                    }
                }
                catch (Exception exception)
                {
                    _notKept = "other mods' patches could not be checked (" + exception.Message + ")";
                }
            }
            if (_notKept != null)
            {
                _keep = false;
                Log.Info("HaulCache: nothing is kept between requests, every building is asked on every request as in " +
                         "the game: " + _notKept + ".");
            }
        }

        // What the kept answers depend on: the providers, what they read, and the capacity rules (the table at the top).
        internal static List<MethodBase> ReadMethods()
        {
            List<MethodBase> methods = new List<MethodBase>
            {
                AccessTools.Method(typeof(HaulCandidate), "GetWeightedBehaviors"),
                AccessTools.Method(typeof(HaulCandidate), "PrioritizeAndValidate"),
                AccessTools.Method(typeof(InventoryFillCalculator), "GetInputFillPercentage"),
                AccessTools.Method(typeof(InventoryFillCalculator), "GetOutputFillPercentage"),
                AccessTools.Method(typeof(InventoryFillCalculator), "GetInStockOutputFillPercentage"),
                AccessTools.Method(typeof(InventoryFillCalculator), "GetInventoryFillPercentage"),
                AccessTools.Method(typeof(Inventory), "LimitedAmount"),
                AccessTools.Method(typeof(Inventory), "AmountInStock", new[] { typeof(string) }),
                AccessTools.PropertyGetter(typeof(Inventory), "HasUnwantedStock"),
                AccessTools.PropertyGetter(typeof(Inventories), "EnabledInventories"),
                AccessTools.PropertyGetter(typeof(BlockableObject), "IsUnblocked"),
                AccessTools.PropertyGetter(typeof(Emptiable), "IsMarkedForEmptying"),
                AccessTools.PropertyGetter(typeof(GoodObtainer), "IsObtaining"),
                AccessTools.PropertyGetter(typeof(GoodSupplier), "IsSupplying"),
                AccessTools.PropertyGetter(typeof(Manufactory), "HasCurrentRecipe"),
                AccessTools.PropertyGetter(typeof(HaulPrioritizable), "Prioritized")
            };
            foreach ((string type, bool _) in Providers)
            {
                methods.Add(AccessTools.Method(Reflect.GameType(type), "GetWeightedBehaviors"));
            }
            foreach ((string type, bool _) in Rules)
            {
                methods.Add(AccessTools.Method(Reflect.GameType(type), "AllowedAmount"));
            }
            methods.Add(AccessTools.Method(Reflect.GameType("Timberborn.InventorySystem.SingleGoodAllower"), "HasOtherGoods"));
            methods.Add(AccessTools.Method(Reflect.GameType("Timberborn.Yielding.InRangeYielderGoodAllower"), "AllowsGood"));
            if (methods.Contains(null))
            {
                throw new MissingMethodException("a method the kept hauling jobs depend on was not found");
            }
            return methods;
        }

        internal static string ReadHashNow()
        {
            return Hash(ReadMethods());
        }

        private static string Hash(List<MethodBase> methods)
        {
            ulong hash = 14695981039346656037UL;
            foreach (MethodBase method in methods)
            {
                byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? new byte[0];
                foreach (byte b in il)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
                // A separator, so that moving bytes from one method to the next changes the hash.
                hash ^= 0xff;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        private static void Build(object districtHaulCandidates)
        {
            long started = Stopwatch.GetTimestamp();
            List<WeightedBehavior> weighted = Weighted;
            weighted.Clear();
            foreach (HaulCandidate candidate in _haulCandidatesOf(districtHaulCandidates))
            {
                if (!Candidates.TryGetValue(candidate, out CandidateEntry entry))
                {
                    entry = new CandidateEntry();
                    Candidates[candidate] = entry;
                }
                // A new entry is dirty, so its inputs are registered (and Keepable decided) before it is ever reused.
                if (_keep && entry.Keepable && !entry.Dirty && entry.Epoch == _epoch && entry.FlagsAsTaken())
                {
                    _candidatesReused++;
                    if (entry.Tick != _ticks)
                    {
                        _reusedFromEarlierTick++;
                    }
                }
                else
                {
                    if (!entry.InputsRegistered)
                    {
                        RegisterInputs(candidate, entry);
                    }
                    entry.Items.Clear();
                    entry.TakeFlags();
                    candidate.GetWeightedBehaviors(entry.Items);
                    entry.Dirty = false;
                    entry.Epoch = _epoch;
                    entry.Tick = _ticks;
                    _candidatesRecomputed++;
                    if (!entry.Keepable)
                    {
                        _recomputedAlways++;
                    }
                }
                weighted.AddRange(entry.Items);
            }
            weighted.Sort(ByWeightDescending);
            Ordered.Clear();
            for (int i = 0; i < weighted.Count; i++)
            {
                Ordered.Add(weighted[i].WorkplaceBehavior);
            }
            _rebuildStopwatchTicks += Stopwatch.GetTimestamp() - started;
        }

        private static void RegisterInputs(HaulCandidate candidate, CandidateEntry entry)
        {
            InventoryScratch.Clear();
            candidate.GetComponents(InventoryScratch);
            for (int i = 0; i < InventoryScratch.Count; i++)
            {
                EntryByInput[InventoryScratch[i]] = entry;
            }
            entry.Keepable = IsKeepable(_providersOf(candidate), InventoryScratch);
            InventoryScratch.Clear();
            RegisterInput(candidate.GetComponent<BlockableObject>(), entry);
            entry.Prioritizable = candidate.GetComponent<HaulPrioritizable>();
            entry.Emptiable = candidate.GetComponent<Emptiable>();
            entry.Obtainer = candidate.GetComponent<GoodObtainer>();
            entry.Supplier = candidate.GetComponent<GoodSupplier>();
            entry.Manufactory = candidate.GetComponent<Manufactory>();
            entry.InputsRegistered = true;
        }

        // Whether a building's answer may be kept (the table at the top): only the game's own providers and capacity
        // rules, and a rule that does not announce everything only beside providers that do not need it to.
        internal static bool IsKeepable(List<IHaulBehaviorProvider> providers, List<Inventory> inventories)
        {
            bool readsLimits = false;
            foreach (IHaulBehaviorProvider provider in providers)
            {
                int index = IndexOf(Providers, provider?.GetType().FullName);
                if (index < 0)
                {
                    return false;
                }
                readsLimits |= Providers[index].ReadsLimits;
            }
            foreach (Inventory inventory in inventories)
            {
                IGoodDisallower rule = _disallowerOf(inventory);
                int index = IndexOf(Rules, rule?.GetType().FullName);
                if (index < 0 || !Rules[index].Announced && readsLimits)
                {
                    return false;
                }
            }
            return true;
        }

        private static int IndexOf((string Type, bool Flag)[] types, string fullName)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].Type == fullName)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void RegisterInput(object component, CandidateEntry entry)
        {
            if (component != null)
            {
                EntryByInput[component] = entry;
            }
        }

        // ReSharper disable once InconsistentNaming
        private static void InputChangedPostfix(object __instance)
        {
            if (_active && EntryByInput.TryGetValue(__instance, out CandidateEntry entry))
            {
                entry.Dirty = true;
            }
        }

        private static void CandidateSetChangedPostfix()
        {
            if (_active)
            {
                // Rare (a building finished or was removed). Dropping everything also releases destroyed objects.
                Candidates.Clear();
                EntryByInput.Clear();
                _epoch++;
            }
        }

        private static void Verify(object districtHaulCandidates)
        {
            VerifyScratch.Clear();
            foreach (HaulCandidate candidate in _haulCandidatesOf(districtHaulCandidates))
            {
                VerifyItemScratch.Clear();
                candidate.GetWeightedBehaviors(VerifyItemScratch);
                VerifyScratch.AddRange(VerifyItemScratch);
            }
            VerifyScratch.Sort(ByWeightDescending);
            Compare(Ordered, VerifyScratch);
            VerifyScratch.Clear();
            VerifyItemScratch.Clear();
        }

        // Verify mode: the list about to be handed to the game against the game's own build of it, whether the
        // buildings' answers were computed in this tick or kept from an earlier one. Counted and logged only; the game
        // is handed the cached list either way, because the setting is each player's own and in co-op the one player
        // who has it on must get the same list as the others (up to 0.4.26 a difference handed over the vanilla list).
        internal static void Compare(List<WorkplaceBehavior> handed, List<WeightedBehavior> vanilla)
        {
            bool same = vanilla.Count == handed.Count;
            for (int i = 0; same && i < vanilla.Count; i++)
            {
                same = ReferenceEquals(vanilla[i].WorkplaceBehavior, handed[i]);
            }
            if (!same)
            {
                _verifyMismatches++;
                if (_verifyMismatches <= 10)
                {
                    Log.Warning($"HaulCache verify: cached list differs from vanilla (cached {handed.Count}, " +
                                $"vanilla {vanilla.Count}).");
                }
            }
        }
    }
}
