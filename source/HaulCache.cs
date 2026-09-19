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
    // Here: each building's weighted behaviors are kept until something that feeds them changes, and the
    // sorted district list is kept until any building in it changed. A rebuilt list is assembled in the
    // same candidate order and sorted with the same comparison as vanilla, so the result is the same list.
    //
    // Everything is also dropped every HaulCacheFlushEveryTicks ticks (default: every tick), so an input this
    // mod does not know about (for example one added by another mod) can only be stale within that window.
    internal static class HaulCache
    {
        private sealed class CandidateEntry
        {
            public readonly List<WeightedBehavior> Items = new List<WeightedBehavior>();
            public int Epoch = -1;
            public bool Dirty = true;
            public bool InputsRegistered;
        }

        private sealed class DistrictEntry
        {
            public readonly List<WeightedBehavior> Weighted = new List<WeightedBehavior>();
            public readonly List<WorkplaceBehavior> Ordered = new List<WorkplaceBehavior>();
            public int Epoch = -1;
            public long DirtyVersion = -1;
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

        private static readonly Dictionary<object, DistrictEntry> Districts = new Dictionary<object, DistrictEntry>();

        private static readonly List<Inventory> InventoryScratch = new List<Inventory>();
        private static readonly List<WeightedBehavior> VerifyScratch = new List<WeightedBehavior>();
        private static readonly List<WeightedBehavior> VerifyItemScratch = new List<WeightedBehavior>();

        private static Func<object, HashSet<HaulCandidate>> _haulCandidatesOf;
        private static bool _active;
        private static bool _verify;
        private static int _flushEveryTicks;
        private static int _epoch;
        private static long _dirtyVersion;
        private static long _ticks;

        // Stats since the last report.
        private static long _requests;
        private static long _rebuilds;
        private static long _candidatesRecomputed;
        private static long _candidatesReused;
        private static long _rebuildStopwatchTicks;
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
            Districts.Clear();
            _epoch++;
            _dirtyVersion++;
            _ticks = 0;
        }

        public static void OnTickStarted()
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
            double rebuildMs = _rebuildStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double perRebuildMs = _rebuilds > 0 ? rebuildMs / _rebuilds : 0;
            long served = _candidatesRecomputed + _candidatesReused;
            string line =
                $"HaulCache: {_requests} hauler list requests, {_requests - _rebuilds} served from cache, " +
                $"{_rebuilds} rebuilt in {rebuildMs:0.0} ms ({perRebuildMs:0.000} ms each); " +
                $"buildings recomputed {_candidatesRecomputed}/{served}" +
                (_verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _requests = _rebuilds = _candidatesRecomputed = _candidatesReused = _rebuildStopwatchTicks = 0;
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
        private static bool GetWorkplaceBehaviorsOrderedPrefix(object __instance, IList<WorkplaceBehavior> workplaceBehaviors)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                _requests++;
                if (!Districts.TryGetValue(__instance, out DistrictEntry district))
                {
                    district = new DistrictEntry();
                    Districts[__instance] = district;
                }
                if (district.Epoch != _epoch || district.DirtyVersion != _dirtyVersion)
                {
                    Rebuild(__instance, district);
                }
                if (_verify)
                {
                    Verify(__instance, district);
                }
                List<WorkplaceBehavior> ordered = district.Ordered;
                for (int i = 0; i < ordered.Count; i++)
                {
                    workplaceBehaviors.Add(ordered[i]);
                }
                return false;
            }
            catch (Exception exception)
            {
                // Nothing was added to workplaceBehaviors before this point, so vanilla can take over cleanly.
                _active = false;
                Log.Warning("HaulCache failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        private static void Rebuild(object districtHaulCandidates, DistrictEntry district)
        {
            long started = Stopwatch.GetTimestamp();
            long dirtyVersion = _dirtyVersion;
            List<WeightedBehavior> weighted = district.Weighted;
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
            district.Ordered.Clear();
            for (int i = 0; i < weighted.Count; i++)
            {
                district.Ordered.Add(weighted[i].WorkplaceBehavior);
            }
            district.Epoch = _epoch;
            district.DirtyVersion = dirtyVersion;
            _rebuilds++;
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
                _dirtyVersion++;
            }
        }

        private static void CandidateSetChangedPostfix()
        {
            if (_active)
            {
                // Rare (a building finished or was removed). Dropping everything also releases destroyed objects.
                Candidates.Clear();
                EntryByInput.Clear();
                Districts.Clear();
                _epoch++;
                _dirtyVersion++;
            }
        }

        private static void Verify(object districtHaulCandidates, DistrictEntry district)
        {
            VerifyScratch.Clear();
            foreach (HaulCandidate candidate in _haulCandidatesOf(districtHaulCandidates))
            {
                VerifyItemScratch.Clear();
                candidate.GetWeightedBehaviors(VerifyItemScratch);
                VerifyScratch.AddRange(VerifyItemScratch);
            }
            VerifyScratch.Sort(ByWeightDescending);
            bool same = VerifyScratch.Count == district.Ordered.Count;
            for (int i = 0; same && i < VerifyScratch.Count; i++)
            {
                same = ReferenceEquals(VerifyScratch[i].WorkplaceBehavior, district.Ordered[i]);
            }
            if (!same)
            {
                _verifyMismatches++;
                if (_verifyMismatches <= 10)
                {
                    Log.Warning($"HaulCache verify: cached list differs from vanilla (cached {district.Ordered.Count}, " +
                                $"vanilla {VerifyScratch.Count}). Using the vanilla list.");
                }
                district.Ordered.Clear();
                for (int i = 0; i < VerifyScratch.Count; i++)
                {
                    district.Ordered.Add(VerifyScratch[i].WorkplaceBehavior);
                }
            }
            VerifyScratch.Clear();
            VerifyItemScratch.Clear();
        }
    }
}
