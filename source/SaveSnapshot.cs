using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Timberborn.EntitySystem;
using Timberborn.SerializationSystem;
using Timberborn.Persistence;
using Timberborn.TemplateSystem;
using Timberborn.Versioning;
using Timberborn.WorldPersistence;
using Timberborn.WorldSerialization;

namespace LateGamePerformance
{
    // The snapshot a save starts with, taken on worker threads where that is safe.
    //
    // Every save begins with SerializedWorldFactory.Create: for each of the colony's entities (11,000 in the
    // logged colony) every persistent component writes its state into a tree of dictionaries. That is the part
    // of a save still on the main thread since 0.4.13 moved the JSON, compression and file write off it: 200 to
    // 285 ms per autosave in the logged colony. Nearly all of those entities are trees, crops, paths, levees and
    // platforms, whose persistent components only read their own managed fields (growth progress, coordinates,
    // a yield, an inventory's goods) and write into their own entity's dictionaries; nothing they touch is
    // shared or belongs to Unity. Those entities are snapshotted on worker threads. Everything else (beavers,
    // whose Character reads a transform; workplaces, whose Worker uses the reference serializer's cache; any
    // component this mod has not read, including other mods') stays on the main thread, which does its share
    // while the workers do theirs. The entities are then assembled in the game's own order, and the singletons
    // are saved by the game's own code afterwards, so the save is the one the game would have written.
    //
    // The list of components allowed on workers is exactly the ones whose Save this mod has read (see
    // TECHNICAL.md). An entity is eligible only if every persistent component on it is on that list. If a worker
    // throws for any reason, everything is discarded and the game's own Create runs; the feature is then off for
    // the session. SaveSnapshotVerify also runs the game's own snapshot and compares every entity, and uses the
    // game's. Not part of the simulation: what is saved does not change, only which thread writes it down.
    internal static class SaveSnapshot
    {
        private const string FactoryType = "Timberborn.WorldPersistence.SerializedWorldFactory";

        // Persistent components whose Save reads only managed state of their own entity or of stateless
        // serializers, each with a hash of its Save method as compiled (the IL bytes). A type is used only while
        // its Save is still the one that was read: a game or mod update that changes it leaves the type on the main
        // thread, with a log line, until it is read again and its hash renewed (`dotnet run --project tests --
        // --hashes` prints the current hashes). Read in the game's 1.1.2.4 source and BeaverBuddies MultiColony
        // 1.4.0-beta2; a type not here keeps its entity on the main thread.
        internal static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Timberborn.BlockSystem.BlockObject", "16fee102cd264960" },
            { "Timberborn.BlockSystem.BlockObjectState", "fe1e48f91b4945a2" },
            { "Timberborn.Yielding.Yielder", "188abd75358c76fd" },
            { "Timberborn.Growing.Growable", "41d21bf8ab506e2d" },
            { "Timberborn.NaturalResourcesLifecycle.LivingNaturalResource", "6acc69bb460d86ad" },
            { "Timberborn.NaturalResources.CoordinatesOffsetter", "afe4bcf012b3e385" },
            { "Timberborn.NaturalResourcesMoisture.LivingWaterNaturalResource", "f4c308943aad89f4" },
            { "Timberborn.NaturalResourcesMoisture.WateredNaturalResource", "2f26b09fcd339bc0" },
            { "Timberborn.NaturalResourcesMoisture.AridNaturalResource", "fbd582828ac157b8" },
            { "Timberborn.NaturalResourcesContamination.ContaminatedNaturalResource", "62a9eaf3e8a58848" },
            { "Timberborn.Gathering.GatherableYieldGrower", "a236fc31b91fb613" },
            { "Timberborn.Cutting.DeadCuttableYieldRemover", "93b081fd6795c1e3" },
            { "Timberborn.ConstructionSites.ConstructionSite", "70e77bc05bb87d69" },
            { "Timberborn.Demolishing.Demolishable", "dad4313d6b8c5a17" },
            { "Timberborn.BuilderPrioritySystem.BuilderPrioritizable", "b7f2fa7a811d4396" },
            { "Timberborn.Pollination.Pollinatee", "ddf765d374bf42b8" },
            { "Timberborn.BlockObstacles.LayeredBlockObstacle", "89fd45a8599be30c" },
            { "Timberborn.DecalSystem.FlippableDecal", "1520d55c1837ce67" },
            { "Timberborn.DecalSystem.DecalSupplier", "b3462680948b9ab4" },
            { "Timberborn.Hauling.HaulPrioritizable", "323b795681906661" },
            { "Timberborn.Buildings.PausableBuilding", "85dc1d3112a91861" },
            { "Timberborn.Emptying.Emptiable", "e3f8e685a26b3249" },
            { "Timberborn.ActivatorSystem.TimedComponentActivator", "36a625d5091538d5" },
            { "Timberborn.EntityNaming.NamedEntity", "ee684b4cbe8b217f" },
            { "Timberborn.InventorySystem.Inventory", "8fb3a05c6583d60d" },
            { "Timberborn.Ruins.RuinModels", "4f18efb4a97a2636" },
            { "Timberborn.MapEditorPlacementRandomizing.BlockObjectPlacementRandomizer", "af63a74c8601927d" },
            { "Timberborn.WorkSystem.Workplace", "1e27218d146bc29b" },
            { "Timberborn.WorkSystem.WorkplacePriority", "8afe37125960913c" },
            { "Timberborn.WorkSystem.WorkplaceWorkerType", "cac86c6201c9d914" },
            { "Timberborn.WorkSystem.DistrictDefaultWorkerType", "a0387aa6ac00c1db" },
            { "Timberborn.Workshops.Manufactory", "388e43956809eba3" },
            { "Timberborn.Workshops.ProductionResetter", "7f5de7d869e98fa6" },
            { "Timberborn.Workshops.WorkshopProductivityCounter", "aba9d7066b0db445" },
            { "Timberborn.GoodConsumingBuildingSystem.GoodConsumingBuilding", "eb68afca78fa562c" },
            { "Timberborn.Fields.FarmHouse", "1f54f9c879fabb22" },
            { "Timberborn.Forestry.Forester", "219b20100ebc4ea1" },
            { "Timberborn.Planting.PlantablePrioritizer", "39e37a50e81d4287" },
            { "Timberborn.WaterBuildings.Floodgate", "a894b361ae89aa63" },
            { "Timberborn.WaterBuildings.WaterInput", "d319584ea641c234" },
            { "Timberborn.WaterBuildings.WaterMover", "45575a4d68bfe789" },
            { "Timberborn.WaterBuildings.WaterInputPipeCoordinates", "b9a877c3fc3625bd" },
            { "Timberborn.WaterBuildings.FillValve", "1502f0693d7538d2" },
            { "Timberborn.WaterBuildings.ThrottlingValve", "530ea69ee5e79f87" },
            { "Timberborn.WaterBuildings.StreamGauge", "4f2bae3d8974d9cd" },
            { "Timberborn.WaterSourceSystem.WaterSource", "352937426e27a37f" },
            { "Timberborn.WaterSourceSystem.WaterDepthStrengthModifier", "496b705beaca9d90" },
            { "Timberborn.WaterSourceSystem.WaterSourceRegulator", "d3cd339503eee900" },
            { "Timberborn.Illumination.CustomizableIlluminator", "1f0acb23ef0bcdf9" },
            { "Timberborn.Buildings.BuildingSoundController", "783026e46c065330" },
            { "Timberborn.DeteriorationSystem.Deteriorable", "854d352adff7f469" },
            { "Timberborn.ScienceSystem.ScienceNeedingBuilding", "140482a6f4f1e5c1" },
            { "Timberborn.AutomationBuildings.Gate", "db671c8220269a36" },
            { "Timberborn.AutomationBuildings.GateNavMeshBlocker", "5d35f9385ea901b9" },
            { "Timberborn.AutomationBuildings.DepthSensor", "b0d8310fee58cf83" },
            { "Timberborn.AutomationBuildings.Chronometer", "5591d59d2fd3a8a7" },
            { "Timberborn.AutomationBuildings.ContaminationSensor", "d66a63bf03c89ad6" },
            { "Timberborn.AutomationBuildings.FlowSensor", "5eedec85d12b2fa7" },
            { "Timberborn.AutomationBuildings.Detonator", "f8ce033c192d791c" },
            { "Timberborn.AutomationBuildings.Indicator", "cc2b3368603860bd" },
            { "Timberborn.AutomationBuildings.Lever", "d3fe62ab867752b6" },
            { "Timberborn.AutomationBuildings.PopulationCounter", "dcdc50e9fee472c9" },
            { "Timberborn.AutomationBuildings.PowerMeter", "5af08fdbc102e0d8" },
            { "Timberborn.AutomationBuildings.ResourceCounter", "b9f9fcd8fdb2100f" },
            { "Timberborn.AutomationBuildings.ScienceCounter", "957d4a3ce9da7b26" },
            { "Timberborn.AutomationBuildings.Speaker", "53739f49bc97c2ee" },
            { "Timberborn.AutomationBuildings.WeatherStation", "bc8f6a193cf3a63f" },
            { "Timberborn.Automation.Automator", "a6136a8b45c586ab" },
            { "Timberborn.Explosions.Dynamite", "a7d63be6d520d3e6" },
            { "Timberborn.Explosions.UnstableCore", "b024707ff3f321ab" },
            { "Timberborn.Wonders.Wonder", "a75134dffdfb5482" },
            { "Timberborn.Wonders.WonderDeactivationTimer", "2d00cb66e4b9125f" },
            { "Timberborn.Pollination.Hive", "daae4d53cb8df348" },
            { "Timberborn.InventorySystem.SingleGoodAllower", "7912d9bfc4413d58" },
            { "Timberborn.Stockpiles.FixedStockpile", "c0986b14e567a5d7" },
            { "Timberborn.StockpileVisualization.StockpileVisualizers", "ebb3441fa04c9208" },
            { "Timberborn.StockpilePrioritySystem.GoodObtainer", "8b24e0f527d56a81" },
            { "Timberborn.StockpilePrioritySystem.GoodSupplier", "714440aa6dcbaffd" },
            { "Timberborn.DistributionSystem.DistrictDistributionSetting", "4091226ac5704596" },
            { "Timberborn.Attractions.AttractionAttender", "87cde2e5d37e0189" },
            { "Timberborn.Attractions.AttractionLoadRate", "63fb7fb276f27161" },
            { "Timberborn.GameDistrictsMigration.PopulationDistributor", "685e289f78ad6bb7" },
            { "Timberborn.ResourceCountingSystem.DistrictGoodsBalance", "b48ad36e66baadbd" },
            { "Timberborn.PopulationStatisticsSampling.DistrictPopulationBalance", "8115f73709591873" },
            { "Timberborn.PowerManagement.Clutch", "9b1bebd95b16267f" },
            { "Timberborn.Reproduction.BreedingPod", "5011f0060936edae" },
            { "Timberborn.NeedApplication.AreaNeedApplier", "9ddd8168deb416aa" },
            { "Timberborn.NeedApplication.DemolisherNeedApplier", "b3750869c2b361b8" },
            { "Timberborn.NeedApplication.YieldRemoverNeedApplier", "53c8d9a9aedee3be" },
            { "Timberborn.FireworkSystem.FireworkLauncher", "3845e4dd4e324f72" },
            { "BeaverBuddies.Colonies.ColonyStamp", "ae23955dc80c940f" },
        };

        // One entity's share of the snapshot: what the game does per entity in SaveEntities/SaveEntity.
        internal sealed class Item
        {
            public Guid Id;
            public string Template;
            public IPersistentEntity[] Parts;
            public bool OnWorkers;
        }

        private static Func<object, TemplateNameRetriever> _retriever;
        private static Func<object, EntityRegistry> _registry;
        private static Action<object, SerializedWorld> _saveSingletons;
        private static Action<object, SerializedWorld> _saveEntities;

        // Swappable for the test harness (the game's version property reads Unity).
        internal static Func<int> Workers = () => Math.Max(1, RouteMaps.WorkerCount);
        internal static Func<Timberborn.Versioning.Version> CurrentVersion = () => GameVersions.CurrentVersion;

        private static bool _active;
        private static bool _verify;
        private static long _saves;
        private static long _onWorkers;
        private static long _onMain;
        private static long _mainStopwatchTicks;
        private static long _workerStopwatchTicks;
        private static long _verifyMismatches;
        private static readonly Dictionary<string, int> Unlisted = new Dictionary<string, int>();
        private static bool _unlistedLogged;
        private static readonly Dictionary<Type, bool> Verdicts = new Dictionary<Type, bool>();

        // Whether this component type may be snapshotted on a worker: on the list, and its Save unchanged since
        // it was read. Decided once per type per session.
        internal static bool IsAllowed(Type type)
        {
            if (Verdicts.TryGetValue(type, out bool allowed))
            {
                return allowed;
            }
            allowed = Judge(type);
            Verdicts[type] = allowed;
            return allowed;
        }

        private static bool Judge(Type type)
        {
            if (!Allowed.TryGetValue(type.FullName ?? "", out string expected))
            {
                return false;
            }
            string actual = SaveHash(type);
            if (actual == expected)
            {
                return true;
            }
            Log.Warning($"SaveSnapshot: the saving code of {type.FullName} is not the one that was read (it hashes to {actual}, " +
                        $"the one read to {expected}); its entities stay on the main thread until it is read again.");
            return false;
        }

        // A hash (FNV-1a, 64 bits) of the IL bytes of the type's Save(IEntitySaver): what the method does, as compiled.
        internal static string SaveHash(Type type)
        {
            MethodInfo save = type.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(IEntitySaver) }, null);
            byte[] il = save?.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return "none";
            }
            ulong hash = 14695981039346656037UL;
            foreach (byte b in il)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        internal static void ForgetVerdictsForTests()
        {
            Verdicts.Clear();
        }

        public static Feature CreateFeature(Config config)
        {
            _verify = config.SaveSnapshotVerify;
            Feature feature = new Feature { Name = "SaveSnapshot" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "SerializedWorldFactory.Create",
                Required = true,
                Target = () =>
                {
                    Type factory = Reflect.GameType(FactoryType);
                    _retriever = Reflect.FieldGetter<TemplateNameRetriever>(factory, "_templateNameRetriever");
                    _registry = Reflect.FieldGetter<EntityRegistry>(factory, "_entityRegistry");
                    _saveSingletons = Reflect.InstanceCall<Action<object, SerializedWorld>>(Reflect.Overload(FactoryType, "SaveSingletons", 1));
                    _saveEntities = Reflect.InstanceCall<Action<object, SerializedWorld>>(Reflect.Overload(FactoryType, "SaveEntities", 1));
                    return Reflect.Overload(FactoryType, "Create", 0);
                },
                Prefix = Reflect.Own(typeof(SaveSnapshot), nameof(CreatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        internal static bool IsActive => _active;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _saves == 0)
            {
                return null;
            }
            double mainMs = _mainStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double workerMs = _workerStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"SaveSnapshot: {_saves} snapshot(s); per snapshot {_onWorkers / _saves} entities on worker threads and " +
                          $"{_onMain / _saves} on the main thread; the main thread spent {mainMs / _saves:0} ms per snapshot " +
                          $"(workers {workerMs / _saves:0} ms alongside)" +
                          (_verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _saves = _onWorkers = _onMain = _mainStopwatchTicks = _workerStopwatchTicks = _verifyMismatches = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        // Last, so that measuring prefixes on Create (SaveTiming's stage timer) still run before it.
        [HarmonyPriority(Priority.Last)]
        internal static bool CreatePrefix(object __instance, ref SerializedWorld __result)
        {
            if (!_active)
            {
                return true;
            }
            long stamp = Stopwatch.GetTimestamp();
            List<Item> items;
            try
            {
                items = Collect(_retriever(__instance), _registry(__instance));
            }
            catch (Exception exception)
            {
                // Including the game's own ArgumentException for an entity with an ISaveableSingleton: the
                // original throws it too.
                Fail(exception);
                return true;
            }
            SerializedEntity[] entities = Build(items, Workers(), out Exception failure, out long workerTicks);
            if (failure != null)
            {
                Fail(failure);
                return true;
            }
            SerializedWorld world;
            try
            {
                world = Assemble(items, entities);
                _saveSingletons(__instance, world);
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
            _mainStopwatchTicks += Stopwatch.GetTimestamp() - stamp;
            _workerStopwatchTicks += workerTicks;
            _saves++;
            foreach (Item item in items)
            {
                if (item.OnWorkers) _onWorkers++; else _onMain++;
            }
            if (_verify)
            {
                Verify(__instance, world);
            }
            __result = world;
            return false;
        }
        // ReSharper restore InconsistentNaming

        // The main-thread pass the game makes anyway, minus the writing: template names (a Unity name lookup,
        // main thread only), the persistent components, and whether every one of them is on the list.
        private static List<Item> Collect(TemplateNameRetriever retriever, EntityRegistry registry)
        {
            List<Item> items = new List<Item>(registry.Entities.Count);
            List<IPersistentEntity> parts = new List<IPersistentEntity>();
            if (!_unlistedLogged)
            {
                Unlisted.Clear();
            }
            foreach (EntityComponent entity in registry.Entities)
            {
                string template = retriever.GetTemplateName(entity);
                parts.Clear();
                bool onWorkers = true;
                foreach (object component in entity.AllComponents)
                {
                    if (component is ISaveableSingleton)
                    {
                        throw new ArgumentException("An entity must not implement ISaveableSingleton: " + component.GetType().Name);
                    }
                    if (component is IPersistentEntity persistent)
                    {
                        parts.Add(persistent);
                        // Every part that is not on the list is noted, so the once-per-session line names all of them.
                        if (!IsAllowed(component.GetType()))
                        {
                            onWorkers = false;
                            Note(component.GetType().FullName);
                        }
                    }
                }
                items.Add(new Item
                {
                    Id = entity.GetComponent<EntityComponent>().EntityId, Template = template, Parts = parts.ToArray(),
                    OnWorkers = onWorkers && parts.Count > 0
                });
            }
            return items;
        }

        // Every item's SerializedEntity (null where nothing was written). Dedicated threads (the thread pool may
        // be busy with this mod's route maps) take eligible items from a shared counter while the main thread
        // snapshots the rest, then takes eligible items too until none are left, then joins. Which thread did an
        // item does not matter: every result goes to its own slot. Never throws: a failure comes back instead.
        internal static SerializedEntity[] Build(List<Item> items, int workers, out Exception failure, out long workerTicks)
        {
            SerializedEntity[] results = new SerializedEntity[items.Count];
            List<int> eligible = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].OnWorkers)
                {
                    eligible.Add(i);
                }
            }
            Exception firstFailure = null;
            long ticks = 0;
            int next = -1;
            void TakeEligible()
            {
                while (true)
                {
                    int k = Interlocked.Increment(ref next);
                    if (k >= eligible.Count)
                    {
                        return;
                    }
                    int index = eligible[k];
                    results[index] = Snapshot(items[index]);
                }
            }
            int count = Math.Min(Math.Max(1, workers), eligible.Count);
            Thread[] threads = new Thread[count];
            for (int w = 0; w < count; w++)
            {
                threads[w] = new Thread(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        TakeEligible();
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, exception, null);
                    }
                    Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - started);
                }) { IsBackground = true, Name = "LateGamePerformance save snapshot " + w };
                threads[w].Start();
            }
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (!items[i].OnWorkers)
                    {
                        results[i] = Snapshot(items[i]);
                    }
                }
                TakeEligible();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref firstFailure, exception, null);
            }
            for (int w = 0; w < count; w++)
            {
                try
                {
                    threads[w].Join();
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref firstFailure, exception, null);
                }
            }
            failure = firstFailure;
            workerTicks = ticks;
            return failure == null ? results : null;
        }

        // SerializedWorldFactory.SaveEntity, for one entity.
        private static SerializedEntity Snapshot(Item item)
        {
            SerializedEntity entity = new SerializedEntity(item.Id, item.Template);
            IPersistentEntity[] parts = item.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].Save(new EntitySaver(entity));
            }
            return entity.HasComponents() ? entity : null;
        }

        internal static SerializedWorld Assemble(List<Item> items, SerializedEntity[] entities)
        {
            SerializedWorld world = new SerializedWorld(CurrentVersion());
            for (int i = 0; i < items.Count; i++)
            {
                if (entities[i] != null)
                {
                    world.AddEntity(entities[i]);
                }
            }
            return world;
        }

        // The game's own entity snapshot as well, compared entity by entity. The singletons are saved once, by the
        // game's code, in the snapshot that is used (a singleton's Save may have side effects); the entities are
        // pure reads, so taking them twice is safe.
        private static void Verify(object factory, SerializedWorld ours)
        {
            try
            {
                SerializedWorld theirs = new SerializedWorld(CurrentVersion());
                _saveEntities(factory, theirs);
                List<SerializedEntity> a = new List<SerializedEntity>(ours.Entities());
                List<SerializedEntity> b = new List<SerializedEntity>(theirs.Entities());
                long mismatches = 0;
                if (a.Count != b.Count)
                {
                    mismatches++;
                    Log.Warning($"SaveSnapshot verify: {a.Count} entities against the game's {b.Count}");
                }
                for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                {
                    if (!DeepEquals(a[i], b[i]))
                    {
                        if (mismatches++ < 5)
                        {
                            Log.Warning($"SaveSnapshot verify: entity {a[i].TemplateName} {a[i].Id} differs from the game's snapshot");
                        }
                    }
                }
                _verifyMismatches += mismatches;
            }
            catch (Exception exception)
            {
                _verify = false;
                Log.Warning("SaveSnapshot verify failed and is off for this session; the snapshot itself is unaffected: " + exception);
            }
        }

        // The game's SerializedObject.Equals compares list properties by reference, so two snapshots that hold
        // the same values never compare equal through it; this compares values all the way down.
        internal static bool DeepEquals(SerializedEntity a, SerializedEntity b)
        {
            if (a.Id != b.Id || a.TemplateName != b.TemplateName)
            {
                return false;
            }
            List<string> names = new List<string>(a.Components());
            List<string> otherNames = new List<string>(b.Components());
            if (names.Count != otherNames.Count)
            {
                return false;
            }
            foreach (string name in names)
            {
                if (!b.HasComponent(name) || !DeepEquals(a.GetComponent(name).Value, b.GetComponent(name).Value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DeepEquals(SerializedObject a, SerializedObject b)
        {
            List<string> names = new List<string>(a.Properties());
            List<string> otherNames = new List<string>(b.Properties());
            if (names.Count != otherNames.Count)
            {
                return false;
            }
            foreach (string name in names)
            {
                if (!b.Has(name) || !DeepEquals(a.GetSerialized(name), b.GetSerialized(name)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DeepEquals(object a, object b)
        {
            if (a is SerializedObject objectA)
            {
                return b is SerializedObject objectB && DeepEquals(objectA, objectB);
            }
            if (a is object[] arrayA)
            {
                if (!(b is object[] arrayB) || arrayA.Length != arrayB.Length)
                {
                    return false;
                }
                for (int i = 0; i < arrayA.Length; i++)
                {
                    if (!DeepEquals(arrayA[i], arrayB[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return Equals(a, b);
        }

        private static void Note(string typeName)
        {
            Unlisted.TryGetValue(typeName, out int count);
            Unlisted[typeName] = count + 1;
        }

        // Once per session, after the first snapshot: which components kept entities on the main thread, so a
        // later version can read them and extend the list.
        internal static string TakeUnlistedLine()
        {
            if (!_active || _unlistedLogged || Unlisted.Count == 0)
            {
                return null;
            }
            _unlistedLogged = true;
            List<KeyValuePair<string, int>> top = new List<KeyValuePair<string, int>>(Unlisted);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            List<string> parts = new List<string>();
            for (int i = 0; i < Math.Min(12, top.Count); i++)
            {
                parts.Add($"{top[i].Key} ({top[i].Value})");
            }
            return "SaveSnapshot: components that keep an entity on the main thread, by how many entities carry them: " +
                   string.Join(", ", parts);
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            Log.Warning("SaveSnapshot failed and is off for this session; the game takes its own snapshot: " + exception);
        }
    }
}
