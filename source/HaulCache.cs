using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // comparison as vanilla, so the result is the same list.
    //
    // Up to 0.4.11 the sorted district list was kept as well, until any building in it changed. In every session
    // measured, on two computers, that list was never served from cache once: a hauler that takes a job reserves
    // stock, which changes a building, so the next request always found the list out of date. It was code and
    // bookkeeping with no effect, and is gone. What does get reused is the per-building part, about a third of
    // the time.
    //
    // Everything is also dropped every tick, so an input this mod does not know about (for example one added by
    // another mod) can only be stale within a tick.
    internal static class HaulCache
    {
        private sealed class CandidateEntry
        {
            public readonly List<WeightedBehavior> Items = new List<WeightedBehavior>();
            public int Epoch = -1;
            public bool Dirty = true;
            public bool InputsRegistered;
        }

        private const string DistrictHaulCandidatesType = "Timberborn.Hauling.DistrictHaulCandidates";

        // Same comparison as DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered.
        private static readonly Comparison<WeightedBehavior> ByWeightDescending =
            (a, b) => b.Weight.CompareTo(a.Weight);

        private static readonly Dictionary<HaulCandidate, CandidateEntry> Candidates =
            new Dictionary<HaulCandidate, CandidateEntry>();

        // Inventories and flag components of a building -> that building's entry.
        private static readonly Dictionary<object, CandidateEntry> EntryByInput =
            new Dictionary<object, CandidateEntry>();

        // The list being assembled for the current request. Main thread only.
        private static readonly List<WeightedBehavior> Weighted = new List<WeightedBehavior>();
        private static readonly List<WorkplaceBehavior> Ordered = new List<WorkplaceBehavior>();

        private static readonly List<Inventory> InventoryScratch = new List<Inventory>();
        private static readonly List<WeightedBehavior> VerifyScratch = new List<WeightedBehavior>();
        private static readonly List<WeightedBehavior> VerifyItemScratch = new List<WeightedBehavior>();

        private static Func<object, HashSet<HaulCandidate>> _haulCandidatesOf;
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

        // Stats since the last report.
        private static long _requests;
        private static long _candidatesRecomputed;
        private static long _candidatesReused;
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
            // The periodic flush, at the start of every tick. Required: it is what bounds an input this mod does not
            // hook to one tick, so the cache does not run without it.
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableSingletonService.TickAll",
                Required = true,
                Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll"),
                Prefix = Reflect.Own(self, nameof(OnTickStarted))
            });

            // Building-local flags read by the vanilla haul behavior providers. Optional: the periodic flush
            // bounds the damage if one of these cannot be hooked.
            AddInputHook(feature, "Inventory.Enable", () => Reflect.Method("Timberborn.InventorySystem.Inventory", "Enable"));
            AddInputHook(feature, "Inventory.Disable", () => Reflect.Method("Timberborn.InventorySystem.Inventory", "Disable"));
            AddInputHook(feature, "BlockableObject.Block", () => Reflect.Method("Timberborn.BlockingSystem.BlockableObject", "Block"));
            AddInputHook(feature, "BlockableObject.Unblock", () => Reflect.Method("Timberborn.BlockingSystem.BlockableObject", "Unblock"));
            AddInputHook(feature, "Emptiable.IsMarkedForEmptying", () => Reflect.Setter(typeof(Emptiable), "IsMarkedForEmptying"));
            AddInputHook(feature, "GoodObtainer.IsObtaining", () => Reflect.Setter(typeof(GoodObtainer), "IsObtaining"));
            AddInputHook(feature, "GoodSupplier.IsSupplying", () => Reflect.Setter(typeof(GoodSupplier), "IsSupplying"));
            AddInputHook(feature, "Manufactory.CurrentRecipe", () => Reflect.Setter(typeof(Manufactory), "CurrentRecipe"));
            AddInputHook(feature, "HaulPrioritizable.Prioritized", () => Reflect.Setter(typeof(HaulPrioritizable), "Prioritized"));
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
                $"buildings reused {_candidatesReused}/{served}, recomputed {_candidatesRecomputed}" +
                (_verify
                    ? $"; verify mismatches {_verifyMismatches}; the game's own build alongside took " +
                      $"{_verifyStopwatchTicks * 1000.0 / Stopwatch.Frequency:0.0} ms " +
                      $"({(_requests > 0 ? _verifyStopwatchTicks * 1000.0 / Stopwatch.Frequency / _requests : 0):0.000} ms each)"
                    : "");
            _requests = _candidatesRecomputed = _candidatesReused = _rebuildStopwatchTicks = _verifyStopwatchTicks = 0;
            return line;
        }

        private static void AddInputHook(Feature feature, string name, Func<System.Reflection.MethodBase> target)
        {
            feature.Patches.Add(new PatchSpec
            {
                Name = name,
                Required = false,
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
                if (entry.Dirty || entry.Epoch != _epoch)
                {
                    if (!entry.InputsRegistered)
                    {
                        RegisterInputs(candidate, entry);
                    }
                    entry.Items.Clear();
                    candidate.GetWeightedBehaviors(entry.Items);
                    entry.Dirty = false;
                    entry.Epoch = _epoch;
                    _candidatesRecomputed++;
                }
                else
                {
                    _candidatesReused++;
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
            InventoryScratch.Clear();
            RegisterInput(candidate.GetComponent<BlockableObject>(), entry);
            RegisterInput(candidate.GetComponent<Emptiable>(), entry);
            RegisterInput(candidate.GetComponent<GoodObtainer>(), entry);
            RegisterInput(candidate.GetComponent<GoodSupplier>(), entry);
            RegisterInput(candidate.GetComponent<Manufactory>(), entry);
            RegisterInput(candidate.GetComponent<HaulPrioritizable>(), entry);
            entry.InputsRegistered = true;
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

        // Verify mode: the list about to be handed to the game against the game's own build of it. Counted and logged
        // only; the game is handed the cached list either way, because the setting is each player's own and in co-op
        // the one player who has it on must get the same list as the others (up to 0.4.26 a difference handed over
        // the vanilla list).
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
