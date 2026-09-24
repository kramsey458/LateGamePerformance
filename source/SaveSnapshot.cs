using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Timberborn.EntitySystem;
using Timberborn.SerializationSystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
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
    // are saved afterwards on the main thread by the game's own loop (written out here since 0.4.28 so each one
    // can be timed; SaveSingletons), so the save is the one the game would have written.
    //
    // The list of components allowed on workers is exactly the ones whose Save this mod has read (see
    // TECHNICAL.md). An entity is eligible only if every persistent component on it is on that list. If a worker
    // throws for any reason, everything is discarded and the game's own Create runs; the feature is then off for
    // the session. SaveSnapshotVerify also runs the game's own snapshot and compares every entity; the mod's is
    // saved either way. Not part of the simulation: what is saved does not change, only which thread writes it down.
    //
    // 0.4.28: every finished-pausable building, dwelling, floodgate and gate carries the game's Automatable, which
    // was not on the list, so the building entries added in 0.4.25 kept almost none of those buildings off the main
    // thread. It is on the list now for the buildings whose automation input is not connected (see Conditional),
    // with WorkshopRandomNeedApplier (workshops, zipline stations). The stats line splits the main thread's time into
    // its steps and names the slowest singletons, and SaveGuard judges every listed type at the first tick of a game
    // scene instead of at the first save.
    internal static class SaveSnapshot
    {
        private const string FactoryType = "Timberborn.WorldPersistence.SerializedWorldFactory";

        // Persistent components whose Save reads only managed state of their own entity or of stateless
        // serializers, each with a hash of its Save method as compiled (the IL bytes). A type is used only while
        // its Save is still the one that was read: a game or mod update that changes it leaves the type on the main
        // thread, with a log line, until it is read again and its hash renewed (`dotnet run --project tests --
        // --hashes` prints the current hashes). Read in the game's 1.1.2.4 source and Timber Together
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
            // 0.4.28, only while its input is not connected: see Conditional.
            { "Timberborn.Automation.Automatable", "9a5dc0c754e5e8f2" },
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
            // 0.4.28: the progress of its own time trigger (ITimeTrigger.Progress, which reads the day-night cycle's
            // time), set into its own entity; the same Save as AreaNeedApplier's and the same read as Growable's.
            { "Timberborn.NeedApplication.WorkshopRandomNeedApplier", "a6fa1368d111df5c" },
            { "Timberborn.FireworkSystem.FireworkLauncher", "3845e4dd4e324f72" },
            // Several reviewed versions may be listed, separated by '|'. Timber Together 1.4.0-beta2 to -beta5:
            // `if (slot >= 0) Set(...)`; -beta12 adds `ColonyModeService.IsSeparateColonies &&`, a static bool read
            // (`separateNow`), still nothing but its own field and a plain static read (reviewed 2026-09-22).
            { "BeaverBuddies.Colonies.ColonyStamp", "ae23955dc80c940f|cb81466cba22c084" },
        };

        // One entity's share of the snapshot: what the game does per entity in SaveEntities/SaveEntity.
        internal sealed class Item
        {
            public Guid Id;
            public string Template;
            public IPersistentEntity[] Parts;
            public bool OnWorkers;
        }

        // Listed types whose Save is safe on a worker only in some states: the state that keeps an entity on the main
        // thread (as the once-per-session line names it) and how to read it, built from the type when it is judged.
        //
        // Automatable. Its Save (read in 1.1.2.4, pinned by its hash above) is
        //     if (_inputConnection.IsConnected)
        //         entitySaver.GetComponent(AutomatableKey).Set(InputKey, Input, _referenceSerializer.Of<Automator>());
        // Connected, it goes through the reference serializer, whose Of<T> adds to a plain Dictionary on first use:
        // not safe on a worker, so such an entity stays on the main thread. Not connected, it reads one field and one
        // property (AutomatorConnection.IsConnected is `Transmitter != null`, a reference compare; BaseComponent has
        // no == operator) and writes nothing. Which branch the Save will take is read here, in Collect, on the main
        // thread, by the very expression the Save branches on: the same private field and the same property getter,
        // compiled, so a getter changed by an update is changed for both. Nothing can connect the input between
        // Collect and the worker's Save: connections change only on the main thread (a player's action, BeaverBuddies'
        // replayed event, an entity deleted), and the main thread is inside Create until the workers have joined. A
        // patch by another mod on the getter or on anything else the Save reaches keeps the type on the main thread
        // (SaveGuard), and a field or property that is not there any more refuses the type with a log line.
        internal static readonly Dictionary<string, (string State, Func<Type, Func<object, bool>> Build)> Conditional =
            new Dictionary<string, (string State, Func<Type, Func<object, bool>> Build)>(StringComparer.Ordinal)
            {
                { "Timberborn.Automation.Automatable", ("automation input connected", type => Getter(type, "_inputConnection", "IsConnected")) }
            };

        // What was decided about a component type: whether it may go to a worker, and for a conditional one when it
        // may not after all.
        private sealed class Verdict
        {
            public static readonly Verdict Refused = new Verdict();
            public static readonly Verdict Plain = new Verdict { Allowed = true };

            public bool Allowed;
            public Func<object, bool> MainOnlyWhen;
            public string State;
        }

        private static Func<object, TemplateNameRetriever> _retriever;
        private static Func<object, EntityRegistry> _registry;
        private static Action<object, SerializedWorld> _saveSingletons;
        private static Action<object, SerializedWorld> _saveEntities;
        private static Func<object, ISingletonRepository> _singletonRepository;
        private static Func<SerializedWorld, ISingletonSaver> _newSingletonSaver;
        private static string _singletonLoopBindFailure;

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
        private static long _collectStopwatchTicks;
        private static long _mainShareStopwatchTicks;
        private static long _waitStopwatchTicks;
        private static long _assembleStopwatchTicks;
        private static long _singletonsStopwatchTicks;
        private static long _takenByMain;
        private static long _verifyMismatches;
        private static readonly Dictionary<string, int> Unlisted = new Dictionary<string, int>();
        private static bool _unlistedLogged;
        private static readonly Dictionary<Type, Verdict> Verdicts = new Dictionary<Type, Verdict>();
        private static long _leftToGame;
        private static bool _parkedLogged;
        private static bool _judgeAheadPending;
        // Each singleton's Save time over the snapshots of this stats interval, by type name; and this snapshot's.
        private static readonly Dictionary<string, long> SingletonStopwatchTicks = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly List<KeyValuePair<string, long>> ThisSnapshotsSingletons = new List<KeyValuePair<string, long>>();
        private static string _singletonSaving;
        private static bool _singletonLoopJudged;
        private static string _singletonLoopRefusal;

        // Whether this component type may be snapshotted on a worker: on the list, its Save unchanged since it
        // was read, and nothing of another mod's patched into its Save or what it calls (SaveGuard). Decided once
        // per type per session, and again whenever the patched methods change. A conditional type (Conditional) is
        // allowed here and then checked per entity in Collect.
        internal static bool IsAllowed(Type type)
        {
            return VerdictOf(type).Allowed;
        }

        private static Verdict VerdictOf(Type type)
        {
            if (Verdicts.TryGetValue(type, out Verdict verdict))
            {
                return verdict;
            }
            verdict = Judge(type);
            Verdicts[type] = verdict;
            return verdict;
        }

        private static Verdict Judge(Type type)
        {
            if (!Allowed.TryGetValue(type.FullName ?? "", out string expected))
            {
                return Verdict.Refused;
            }
            string actual = SaveHash(type);
            if (Array.IndexOf(expected.Split('|'), actual) < 0)
            {
                Log.Warning($"SaveSnapshot: the saving code of {type.FullName} is not the one that was read (it hashes to {actual}, " +
                            $"the one read to {expected}); its entities stay on the main thread until it is read again.");
                return Verdict.Refused;
            }
            string refusal = SaveGuard.Refusal(type);
            if (refusal != null)
            {
                Log.Info($"SaveSnapshot: the entities of {type.FullName} stay on the main thread: {refusal}.");
                return Verdict.Refused;
            }
            if (!Conditional.TryGetValue(type.FullName, out (string State, Func<Type, Func<object, bool>> Build) condition))
            {
                return Verdict.Plain;
            }
            Func<object, bool> check;
            try
            {
                check = condition.Build(type);
            }
            catch (Exception exception)
            {
                Log.Info($"SaveSnapshot: the entities of {type.FullName} stay on the main thread: the check of when its Save " +
                         $"may run on a worker could not be built ({exception.Message}).");
                return Verdict.Refused;
            }
            return new Verdict { Allowed = true, MainOnlyWhen = check, State = condition.State };
        }

        // For a conditional type: reads component.field.property, compiled, as the Save itself does.
        internal static Func<object, bool> Getter(Type type, string fieldName, string propertyName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                throw new MissingFieldException(type.Name, fieldName);
            }
            PropertyInfo property = field.FieldType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || property.PropertyType != typeof(bool) || property.GetGetMethod(true) == null)
            {
                throw new MissingMemberException(field.FieldType.Name, propertyName);
            }
            ParameterExpression component = Expression.Parameter(typeof(object), "component");
            Expression read = Expression.Property(Expression.Field(Expression.Convert(component, type), field), property);
            return Expression.Lambda<Func<object, bool>>(read, component).Compile();
        }

        // A hash (FNV-1a, 64 bits) of the IL bytes of the type's Save(IEntitySaver): what the method does, as compiled.
        internal static string SaveHash(Type type)
        {
            MethodInfo save = type.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(IEntitySaver) }, null);
            return IlHash(save);
        }

        // The same hash over the IL of several methods one after another; "none" if one of them is missing.
        internal static string IlHash(params MethodBase[] methods)
        {
            ulong hash = 14695981039346656037UL;
            foreach (MethodBase method in methods)
            {
                byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                {
                    return "none";
                }
                foreach (byte b in il)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash.ToString("x16");
        }

        internal static void ForgetVerdictsForTests()
        {
            Verdicts.Clear();
        }

        // Off, with nothing counted, judged or noted.
        internal static void ResetForTests()
        {
            _active = false;
            _saves = _onWorkers = _onMain = _mainStopwatchTicks = _workerStopwatchTicks = _verifyMismatches = _leftToGame = 0;
            _collectStopwatchTicks = _mainShareStopwatchTicks = _waitStopwatchTicks = _assembleStopwatchTicks = _singletonsStopwatchTicks = 0;
            _takenByMain = 0;
            SingletonStopwatchTicks.Clear();
            ThisSnapshotsSingletons.Clear();
            Verdicts.Clear();
            Unlisted.Clear();
            _unlistedLogged = false;
            _parkedLogged = false;
            _judgeAheadPending = false;
            _singletonLoopJudged = false;
            _singletonLoopRefusal = null;
            _singletonSaving = null;
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
                    BindSingletonLoop(factory);
                    return Reflect.Overload(FactoryType, "Create", 0);
                },
                Prefix = Reflect.Own(typeof(SaveSnapshot), nameof(CreatePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                // The first tick of a game scene judges every listed type ahead of the first save (JudgeAhead).
                // Without it the first save judges them, as before 0.4.28.
                Name = "TickableSingletonService.TickAll",
                Required = false,
                Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll"),
                Prefix = Reflect.Own(typeof(SaveSnapshot), nameof(TickAllPrefix))
            });
            return feature;
        }

        // What SaveSingletons needs to be written out here (SaveSingletons below). Anything missing leaves the
        // singletons to the game's own method, timed as a whole.
        private static void BindSingletonLoop(Type factory)
        {
            try
            {
                _singletonRepository = Reflect.FieldGetter<ISingletonRepository>(factory, "_singletonRepository");
                Type saverType = Reflect.GameType("Timberborn.WorldPersistence.SingletonSaver");
                ConstructorInfo constructor = saverType?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(SerializedWorld) }, null);
                if (constructor == null || !typeof(ISingletonSaver).IsAssignableFrom(saverType))
                {
                    throw new MissingMethodException("SingletonSaver(SerializedWorld)");
                }
                ParameterExpression world = Expression.Parameter(typeof(SerializedWorld), "world");
                _newSingletonSaver = Expression.Lambda<Func<SerializedWorld, ISingletonSaver>>(
                    Expression.Convert(Expression.New(constructor, world), typeof(ISingletonSaver)), world).Compile();
                _singletonLoopBindFailure = null;
            }
            catch (Exception exception)
            {
                _singletonRepository = null;
                _newSingletonSaver = null;
                _singletonLoopBindFailure = exception.Message;
            }
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

        // A new game scene: its first tick judges every listed type (JudgeAhead).
        internal static void SceneCreated()
        {
            _judgeAheadPending = true;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _saves == 0)
            {
                return null;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            double ms = 1000.0 / Stopwatch.Frequency / _saves;
            string line = string.Format(c,
                "SaveSnapshot: {0} snapshot(s); per snapshot {1} entities on worker threads and {2} on the main thread; the main " +
                "thread spent {3:0} ms per snapshot: collecting {4:0}, its share of the entities {5:0} (it took {6} of the " +
                "workers' as well), waiting for the workers {7:0}, assembling {8:0}, singletons {9:0} ms (workers {10:0} ms alongside)",
                _saves, _onWorkers / _saves, _onMain / _saves, _mainStopwatchTicks * ms, _collectStopwatchTicks * ms,
                _mainShareStopwatchTicks * ms, _takenByMain / _saves, _waitStopwatchTicks * ms, _assembleStopwatchTicks * ms,
                _singletonsStopwatchTicks * ms, _workerStopwatchTicks * ms);
            if (SingletonStopwatchTicks.Count > 0)
            {
                List<KeyValuePair<string, long>> slowest = new List<KeyValuePair<string, long>>(SingletonStopwatchTicks);
                slowest.Sort((x, y) => y.Value.CompareTo(x.Value));
                List<string> parts = new List<string>();
                for (int i = 0; i < Math.Min(5, slowest.Count); i++)
                {
                    parts.Add(string.Format(c, "{0} {1:0.0}", slowest[i].Key, slowest[i].Value * ms));
                }
                line += "; slowest singletons per snapshot: " + string.Join(", ", parts) + " ms";
            }
            line += (_leftToGame > 0 ? $"; {_leftToGame} save(s) left entirely to the game because of another mod's patch or a changed helper (see the log)" : "") +
                    (_verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _saves = _onWorkers = _onMain = _mainStopwatchTicks = _workerStopwatchTicks = _verifyMismatches = _leftToGame = 0;
            _collectStopwatchTicks = _mainShareStopwatchTicks = _waitStopwatchTicks = _assembleStopwatchTicks = _singletonsStopwatchTicks = 0;
            _takenByMain = 0;
            SingletonStopwatchTicks.Clear();
            return line;
        }

        // Other mods' patches are read again whenever their number changed (SaveGuard); every verdict is then
        // taken again, and so is whether SaveSingletons may be written out here.
        private static void CheckRegistry()
        {
            if (SaveGuard.RegistryChanged())
            {
                Verdicts.Clear();
                _parkedLogged = false;
                _singletonLoopJudged = false;
            }
        }

        // SaveGuard's judging of every listed type (each Save's IL and what it reaches, Harmony's registry for all of
        // them, the helpers' IL) took an estimated 0.2 to 0.5 s at the first save of a session, on top of that save's
        // own freeze. It is pure reflection and reads no game state, so any moment on the main thread will do; the
        // first tick of a game scene comes after every mod has patched what it patches while the scene loads, so the
        // first save normally finds the same number of patches and judges nothing. If the number changed after all,
        // that save judges again, as it always has. Which types are allowed does not depend on when they are judged.
        internal static void JudgeAhead()
        {
            if (!_active)
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                CheckRegistry();
                if (SaveGuard.ParkedReason != null)
                {
                    // Every save is left to the game until the patches change; the save says so.
                    return;
                }
                int judged = 0;
                Dictionary<string, Assembly> loaded = LoadedAssemblies();
                foreach (string name in Allowed.Keys)
                {
                    Type type = FindListedType(name, loaded);
                    if (type != null && !Verdicts.ContainsKey(type))
                    {
                        VerdictOf(type);
                        judged++;
                    }
                }
                JudgeSingletonLoop();
                if (judged > 0)
                {
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        "SaveSnapshot: {0} listed components judged ahead of the first save in {1:0} ms.", judged,
                        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency));
                }
            }
            catch (Exception exception)
            {
                // The same judging would throw inside the first save's snapshot and turn the feature off there.
                Fail(exception);
            }
        }

        // A listed type by its full name among the loaded assemblies: first the one named like its namespace (the
        // game's rule), then all of them. Nothing is loaded that is not loaded already.
        internal static Type FindListedType(string fullName, Dictionary<string, Assembly> loaded = null)
        {
            loaded = loaded ?? LoadedAssemblies();
            int dot = fullName.LastIndexOf('.');
            if (dot > 0 && loaded.TryGetValue(fullName.Substring(0, dot), out Assembly named))
            {
                Type type = TypeIn(named, fullName);
                if (type != null)
                {
                    return type;
                }
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = TypeIn(assembly, fullName);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        // The loaded assemblies by their simple name (the first of two with the same name; FindListedType looks
        // through all of them when that one does not hold the type).
        private static Dictionary<string, Assembly> LoadedAssemblies()
        {
            Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try
                {
                    name = assembly.GetName().Name;
                }
                catch (Exception)
                {
                    continue;
                }
                if (name != null && !loaded.ContainsKey(name))
                {
                    loaded[name] = assembly;
                }
            }
            return loaded;
        }

        private static Type TypeIn(Assembly assembly, string fullName)
        {
            try
            {
                return assembly.GetType(fullName, false);
            }
            catch (Exception)
            {
                // An assembly whose references cannot be resolved; nothing on the list lives there.
                return null;
            }
        }

        // ReSharper disable InconsistentNaming
        private static void TickAllPrefix()
        {
            if (_judgeAheadPending)
            {
                _judgeAheadPending = false;
                JudgeAhead();
            }
        }

        // Last, so that measuring prefixes on Create (SaveTiming's stage timer) still run before it, and so does
        // ParallelTickWait's wait for the game's parallel tick (first).
        [HarmonyPriority(Priority.Last)]
        internal static bool CreatePrefix(object __instance, ref SerializedWorld __result)
        {
            if (!_active)
            {
                return true;
            }
            // Other mods' patches are read again whenever their number changed (SaveGuard); a patch on one of the
            // helpers every entity goes through leaves this save to the game.
            CheckRegistry();
            string parked = SaveGuard.ParkedReason;
            if (parked != null)
            {
                if (!_parkedLogged)
                {
                    Log.Info("SaveSnapshot: every entity stays on the main thread and the game takes its own snapshot until the " +
                             "patched methods change: " + parked + ".");
                    _parkedLogged = true;
                }
                _leftToGame++;
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
            long collected = Stopwatch.GetTimestamp();
            SerializedEntity[] entities = Build(items, Workers(), out Exception failure, out BuildTimes times);
            if (failure != null)
            {
                Fail(failure);
                return true;
            }
            long built = Stopwatch.GetTimestamp();
            SerializedWorld world;
            long assembled;
            try
            {
                world = Assemble(items, entities);
                assembled = Stopwatch.GetTimestamp();
                SaveSingletons(__instance, world);
            }
            catch (Exception exception)
            {
                // The game's own Create runs next and saves every singleton once more from the start, so those saved
                // before one threw here have saved twice (the first world is thrown away). Read for 0.4.28: every
                // singleton Save in the game (51) and in the installed mods (Timber Together's 8, Optimized
                // Local Housing's 1) only reads its own state and writes into the world being built. The one exception,
                // DateSalter, draws two random numbers for the save's salt: BeaverBuddies keeps that draw off the game's
                // random sequence in co-op (its DateSalterPatcher marks it as not part of the game), and in a game
                // alone it only picks another salt. A singleton whose Save throws here throws in the game's Create as
                // well, and the save fails as it would have; if the failure was this mod's, the game's Create saves.
                string singleton = _singletonSaving;
                _singletonSaving = null;
                Fail(exception, singleton);
                return true;
            }
            long finished = Stopwatch.GetTimestamp();
            _mainStopwatchTicks += finished - stamp;
            _collectStopwatchTicks += collected - stamp;
            _mainShareStopwatchTicks += times.MainShare;
            _waitStopwatchTicks += times.Waiting;
            _assembleStopwatchTicks += assembled - built;
            _singletonsStopwatchTicks += finished - assembled;
            _takenByMain += times.TakenByMain;
            _workerStopwatchTicks += times.Workers;
            foreach (KeyValuePair<string, long> singleton in ThisSnapshotsSingletons)
            {
                SingletonStopwatchTicks.TryGetValue(singleton.Key, out long sum);
                SingletonStopwatchTicks[singleton.Key] = sum + singleton.Value;
            }
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

        // SerializedWorldFactory.SaveSingletons, the game's two overloads written out, so that each singleton's Save
        // can be timed:
        //     SaveSingletons(world) => SaveSingletons(world, _singletonRepository.GetSingletons<ISaveableSingleton>());
        //     SaveSingletons(world, singletons) { var saver = new SingletonSaver(world); foreach (s in singletons) s.Save(saver); }
        // The same calls in the same order, with a clock read around each Save. Used only while both overloads still
        // hash to what was read (SingletonLoopHash) and nothing patches either of them (this copy would pass such a
        // patch by); otherwise the game's own method runs and the singletons are timed as a whole. Nothing is moved
        // to another thread.
        internal static void SaveSingletons(object factory, SerializedWorld world)
        {
            ThisSnapshotsSingletons.Clear();
            if (!_singletonLoopJudged)
            {
                JudgeSingletonLoop();
            }
            if (_singletonLoopRefusal != null)
            {
                _saveSingletons(factory, world);
                return;
            }
            IEnumerable<ISaveableSingleton> singletons = _singletonRepository(factory).GetSingletons<ISaveableSingleton>();
            ISingletonSaver singletonSaver = _newSingletonSaver(world);
            foreach (ISaveableSingleton singleton in singletons)
            {
                string name = singleton.GetType().Name;
                _singletonSaving = name;
                long started = Stopwatch.GetTimestamp();
                singleton.Save(singletonSaver);
                ThisSnapshotsSingletons.Add(new KeyValuePair<string, long>(name, Stopwatch.GetTimestamp() - started));
            }
            _singletonSaving = null;
        }

        // Once per judging pass: whether SaveSingletons may be written out here, with one log line when not.
        private static void JudgeSingletonLoop()
        {
            string refusal;
            try
            {
                refusal = SingletonLoopRefusal();
            }
            catch (Exception exception)
            {
                refusal = "it could not be checked (" + exception.Message + ")";
            }
            if (refusal != null && refusal != _singletonLoopRefusal)
            {
                Log.Info("SaveSnapshot: the singletons are saved by the game's own SaveSingletons and timed as a whole: " + refusal + ".");
            }
            _singletonLoopRefusal = refusal;
            _singletonLoopJudged = true;
        }

        private static string SingletonLoopRefusal()
        {
            if (_singletonLoopBindFailure != null || _singletonRepository == null || _newSingletonSaver == null)
            {
                return "what it uses was not found (" + (_singletonLoopBindFailure ?? "not bound") + ")";
            }
            MethodInfo one = Reflect.Overload(FactoryType, "SaveSingletons", 1);
            MethodInfo two = Reflect.Overload(FactoryType, "SaveSingletons", 2);
            string actual = IlHash(one, two);
            if (actual != SingletonLoopHash)
            {
                return $"it is not the one that was read (it hashes to {actual}, the one read to {SingletonLoopHash})";
            }
            foreach (MethodInfo method in new[] { one, two })
            {
                IEnumerable<(string Owner, string Patch)> patches = SaveGuard.PatchesOn(method);
                if (patches == null)
                {
                    continue;
                }
                foreach ((string owner, string patch) in patches)
                {
                    return $"it is patched ({owner}, {patch})";
                }
            }
            return null;
        }

        // The IL of SerializedWorldFactory.SaveSingletons(SerializedWorld) and then (SerializedWorld,
        // IEnumerable<ISaveableSingleton>), as read in 1.1.2.4; `dotnet run --project tests -c Release -- --hashes`
        // prints the current one.
        internal const string SingletonLoopHash = "b7a5849f791be966";

        internal static string SingletonLoopHashNow()
        {
            return IlHash(Reflect.Overload(FactoryType, "SaveSingletons", 1), Reflect.Overload(FactoryType, "SaveSingletons", 2));
        }

        // The main-thread pass the game makes anyway, minus the writing: template names (a Unity name lookup,
        // main thread only), the persistent components, and whether every one of them is on the list; for a
        // conditional one (Conditional), whether this entity's is in the state its Save may run on a worker in.
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
                        if (!MayGoToWorker(component))
                        {
                            onWorkers = false;
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

        // Where the time of Build went, in stopwatch ticks.
        internal struct BuildTimes
        {
            // The workers' thread time, summed over them.
            public long Workers;
            // The main thread starting the workers, snapshotting the entities kept on it, then taking eligible ones.
            public long MainShare;
            // The main thread waiting for the last worker to finish.
            public long Waiting;
            // Eligible entities the main thread snapshotted itself.
            public int TakenByMain;
        }

        // Whether this persistent part lets its entity go to a worker: its type is allowed and, for a conditional
        // type, this instance is not in the state that keeps it on the main thread. A part that does not is noted.
        internal static bool MayGoToWorker(object component)
        {
            Verdict verdict = VerdictOf(component.GetType());
            if (!verdict.Allowed)
            {
                Note(component.GetType().FullName);
                return false;
            }
            if (verdict.MainOnlyWhen != null && verdict.MainOnlyWhen(component))
            {
                Note(component.GetType().FullName + " (" + verdict.State + ")");
                return false;
            }
            return true;
        }

        // Every item's SerializedEntity (null where nothing was written). Dedicated threads (the thread pool may
        // be busy with this mod's route maps) take eligible items from a shared counter while the main thread
        // snapshots the rest, then takes eligible items too until none are left, then joins. Which thread did an
        // item does not matter: every result goes to its own slot. Never throws: a failure comes back instead.
        internal static SerializedEntity[] Build(List<Item> items, int workers, out Exception failure, out BuildTimes times)
        {
            long begun = Stopwatch.GetTimestamp();
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
            int TakeEligible()
            {
                int taken = 0;
                while (true)
                {
                    int k = Interlocked.Increment(ref next);
                    if (k >= eligible.Count)
                    {
                        return taken;
                    }
                    int index = eligible[k];
                    results[index] = Snapshot(items[index]);
                    taken++;
                }
            }
            int takenByMain = 0;
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
                takenByMain = TakeEligible();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref firstFailure, exception, null);
            }
            long shareDone = Stopwatch.GetTimestamp();
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
            times = new BuildTimes
            {
                Workers = Interlocked.Read(ref ticks), MainShare = shareDone - begun, Waiting = Stopwatch.GetTimestamp() - shareDone,
                TakenByMain = takenByMain
            };
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

        // The game's own entity snapshot as well, compared entity by entity. The singletons are saved once, in the
        // snapshot that is used (DateSalter's Save draws random numbers); the entities are pure reads, so taking them
        // twice is safe.
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

        internal static bool DeepEquals(SerializedObject a, SerializedObject b)
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

        private static void Fail(Exception exception, string singleton = null)
        {
            _active = false;
            Log.Warning("SaveSnapshot failed" + (singleton != null ? " while the singleton " + singleton + " saved its state" : "") +
                        " and is off for this session; the game takes its own snapshot: " + exception);
        }
    }
}
