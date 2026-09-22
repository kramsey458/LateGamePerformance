using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Timberborn.TickSystem;

namespace LateGamePerformance
{
    // Entities with nothing to tick are passed over in the tick loop.
    //
    // Every tree, crop, path, levee and platform is a tickable entity: it carries a tick component that is
    // switched off nearly all the time (the "demolition blocked" status only while marked for demolition, the
    // "unreachable" status only while selected). The game still visits every one of them 128 times a day: a
    // native call to ask whether the object is active, a try block, a walk over its components to find each
    // one disabled. About 10,000 of the 11,500 entity ticks in the logged 354-beaver colony were such visits.
    //
    // This keeps, per bucket, a second sorted list with exactly the game's keys, holding one flag per entity:
    // whether any of its tick components is enabled. The replacement of TickableEntityBucket.TickAll walks the
    // game's list by the game's own index and, for each position, reads that flag instead of asking Unity and
    // walking the components; only entities with a component switched on are ticked. A component can only be
    // switched through BaseComponent.EnableComponent and DisableComponent (the setter is private), so those two
    // are hooked and flip the flag; TickableEntityBucket.Add and Remove keep the keys in step, with the same
    // deferral of removals during a pass that the game has. Each pass first makes sure the keys still match the
    // game's list, rebuilding the mirror if not; the walk over the keys for that is skipped while the list's own
    // change counter has not moved since they last matched and no Add or Remove came through the hooks (every 128th
    // pass walks them anyway). An entity whose tick components are all disabled does nothing when ticked, and the
    // index is the game's, so everything the game would do, including its own quirk of ticking an entity again when
    // something is inserted before it mid-pass, happens exactly as before.
    // The game's own list is untouched; BeaverBuddies reads it for its per-tick hash.
    //
    // Ranged effect buildings with nothing to do (0.4.28). Roofs, lanterns and shrubs, about 110 entities in the logged
    // colony, were ticked for one switched-on component only, RangedEffectBuilding, whose whole Tick is
    //     if ((bool)_mechanicalBuilding) { ToggleActiveState(); _rangedEffectApplier.UpdateEfficiency(...); }
    // _mechanicalBuilding is private and written in one place, Awake, from GetComponent<MechanicalBuilding>(), and
    // BaseComponent's bool operator is false for null. So for a building without a mechanical part the tick does
    // nothing, however often it runs, and such a component counts here as if it were switched off, while its Enabled
    // stays what the game set: an entity whose switched-on tick parts are all of that kind is passed over, as one
    // with none switched on is. An entity with any other part switched on is ticked through the game's own Tick,
    // which ticks this component too, as before. The field is read whenever an entity's flag is worked out (an add, a
    // rebuild, a component switched on or off), never remembered: the flag can only go stale if the field changes
    // while the component stays switched on, and the one write, in Awake, is followed by Awake's own DisableComponent,
    // whose postfix works the flag out again. The rule is off, and these components count as switched on as before, if
    // the class's code is not the code that was read (RangedEffectHash, over the IL of every method it declares and of
    // BaseComponent's bool operator), and for the game scene if another mod patches one of the class's methods.
    //
    // Simulation feature: always on, the same on every computer. Self-disables on any bookkeeping exception and
    // hands the loop back to the game.
    internal static class IdleEntities
    {
        private const string BucketType = "Timberborn.TickSystem.TickableEntityBucket";

        private sealed class Record
        {
            public TickableEntity Entity;
            public Bucket Bucket;
            public BaseComponent[] Components;
            // A tick part is switched on that does something when ticked.
            public bool Awake;
            // Asleep, but with parts switched on that do nothing when ticked (ranged effect buildings; the stats line
            // counts these).
            public bool Quiet;
        }

        private sealed class Bucket
        {
            // The same keys as the game's _tickableEntities, so the same index means the same entity.
            public readonly SortedList<Guid, Record> Mirror = new SortedList<Guid, Record>();
            public bool Ticking;
            // The game's list and its change counter when its keys were last found equal to the mirror's (the list is
            // null once a postfix has changed the mirror's keys since), and the passes since the keys were last walked
            // (see Unchanged).
            public SortedList<Guid, TickableEntity> CheckedList;
            public int CheckedVersion;
            public int PassesSinceWalk;
        }

        // A bucket's keys are walked on at least every 128th of its passes, even with the list's counter unmoved.
        private const int WalkEvery = 128;

        private static Func<object, SortedList<Guid, TickableEntity>> _entities;
        private static Func<object, List<TickableEntity>> _toRemove;
        private static Action<object, bool> _setTicking;
        private static Func<object, object> _components;
        private static Func<object, object> _inner;
        // SortedList's own change counter; null when the runtime's list has none this knows, and then the keys are
        // walked on every pass.
        private static Func<object, int> _listVersion;

        private static readonly Dictionary<object, Bucket> Buckets = new Dictionary<object, Bucket>();
        private static readonly Dictionary<TickableEntity, Record> Records = new Dictionary<TickableEntity, Record>();
        private static readonly Dictionary<BaseComponent, Record> ByComponent = new Dictionary<BaseComponent, Record>();

        // Swappable for the test harness, where the game's Tick cannot run without Unity.
        internal static Action<TickableEntity> TickEntity = entity => entity.Tick();

        // Ranged effect buildings whose tick does nothing (see the top of the file). The type is null while the rule is
        // off for the session; _rangedEffectsJudged says whether this game scene's patches have been looked at, and
        // _rangedEffectsOff why the rule is off, for the stats line (null while it is on).
        private const string RangedEffectType = "Timberborn.RangedEffectSystem.RangedEffectBuilding";
        // dotnet run --project tests -c Release checks it against the installed game and prints the current one.
        internal const string RangedEffectHash = "3f3f75963182a077";
        private static Type _rangedEffect;
        private static Func<object, object> _mechanicalOf;
        private static bool _rangedEffectsJudged;
        private static bool _rangedEffectsOn;
        private static string _rangedEffectsOff;

        // Every patch on a method, as (Harmony id, "Namespace.Type.Method" of the patch); swapped by the tests, where
        // Harmony's patch registry cannot run.
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

        private static bool _active;
        private static long _passes;
        private static long _ticked;
        private static long _leftOut;
        private static long _quiet;
        private static long _resyncs;
        private static long _keyChecks;

        public static Feature CreateFeature(Config config)
        {
            Type self = typeof(IdleEntities);
            Feature feature = new Feature { Name = "IdleEntities" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.TickAll",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Method(BucketType, "TickAll");
                },
                Prefix = Reflect.Own(self, nameof(TickAllPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.Add",
                Required = true,
                Target = () => Reflect.Method(BucketType, "Add"),
                Postfix = Reflect.Own(self, nameof(AddPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.Remove",
                Required = true,
                Target = () => Reflect.Method(BucketType, "Remove"),
                Postfix = Reflect.Own(self, nameof(RemovePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BaseComponent.EnableComponent",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(BaseComponent), "EnableComponent"),
                Postfix = Reflect.Own(self, nameof(EnabledPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BaseComponent.DisableComponent",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(BaseComponent), "DisableComponent"),
                Postfix = Reflect.Own(self, nameof(DisabledPostfix))
            });
            return feature;
        }

        internal static void Bind()
        {
            if (_entities != null)
            {
                return;
            }
            Type bucket = Reflect.GameType(BucketType);
            _entities = Reflect.FieldGetter<SortedList<Guid, TickableEntity>>(bucket, "_tickableEntities");
            _toRemove = Reflect.FieldGetter<List<TickableEntity>>(bucket, "_entitiesToRemove");
            _setTicking = Reflect.FieldSetter<bool>(bucket, "_isTicking");
            _components = Reflect.FieldGetter<object>(typeof(TickableEntity), "_tickableComponents");
            _inner = Reflect.FieldGetter<object>(typeof(MeteredTickableComponent), "_tickableComponent");
            _listVersion = ChangeCounter(typeof(SortedList<Guid, TickableEntity>));
            BindRangedEffects();
        }

        // The ranged effect rule for the session: on only if the class is there and its code is the code that was read.
        // Whatever goes wrong here leaves the rule off and the rest of the feature as it was.
        private static void BindRangedEffects()
        {
            _rangedEffect = null;
            _mechanicalOf = null;
            try
            {
                Type type = RangedEffectClass();
                if (type == null)
                {
                    _rangedEffectsOff = "RangedEffectBuilding was not found in this game version";
                }
                else if (RangedEffectHashOf(type) != RangedEffectHash)
                {
                    _rangedEffectsOff = $"the game's RangedEffectBuilding is not the code that was read (it hashes to " +
                                        $"{RangedEffectHashOf(type)}, the code read to {RangedEffectHash})";
                }
                else
                {
                    _mechanicalOf = Reflect.FieldGetter<object>(type, "_mechanicalBuilding");
                    _rangedEffect = type;
                    _rangedEffectsOff = null;
                }
            }
            catch (Exception exception)
            {
                _rangedEffectsOff = "RangedEffectBuilding could not be read: " + exception.Message;
            }
            if (_rangedEffectsOff != null)
            {
                Log.Info("IdleEntities: ranged effect buildings are ticked as before: " + _rangedEffectsOff + ".");
            }
        }

        // FNV-1a (64 bits) over the IL of every method and constructor RangedEffectBuilding declares, in metadata order,
        // and of BaseComponent's bool operator: the code the rule rests on, as read (game 1.1.2.4).
        internal static string RangedEffectHashOf(Type type)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            List<MethodBase> methods = new List<MethodBase>();
            methods.AddRange(type.GetMethods(any));
            methods.AddRange(type.GetConstructors(any));
            methods.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            methods.Add(typeof(BaseComponent).GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(BaseComponent) }, null));
            ulong hash = 14695981039346656037UL;
            foreach (MethodBase method in methods)
            {
                byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                {
                    hash ^= 0xff;
                    hash *= 1099511628211UL;
                    continue;
                }
                foreach (byte b in il)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash.ToString("x16");
        }

        // The current hash, for the harness.
        internal static string RangedEffectHashNow()
        {
            Type type = RangedEffectClass();
            return type == null ? "none" : RangedEffectHashOf(type);
        }

        // By assembly and name, which loads the assembly if nothing has yet (the harness), then among loaded ones.
        private static Type RangedEffectClass()
        {
            return Type.GetType(RangedEffectType + ", Timberborn.RangedEffectSystem", false) ?? Reflect.GameType(RangedEffectType);
        }

        // Whether the rule holds in this game scene: judged at its first use in the scene, when every mod has patched
        // what it patches. A patch by another mod on any of the class's methods may do something where the game's code
        // does nothing, so then the rule stands down.
        private static bool RangedEffectsOn()
        {
            if (!_rangedEffectsJudged)
            {
                _rangedEffectsJudged = true;
                _rangedEffectsOn = false;
                string patched = null;
                try
                {
                    const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                             BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    List<MethodBase> methods = new List<MethodBase>(_rangedEffect.GetMethods(any));
                    methods.AddRange(_rangedEffect.GetConstructors(any));
                    foreach (MethodBase method in methods)
                    {
                        IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
                        if (patches == null)
                        {
                            continue;
                        }
                        foreach ((string owner, string patch) in patches)
                        {
                            if (patched == null && !owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                            {
                                patched = $"another mod patches RangedEffectBuilding.{method.Name} ({owner}, {patch})";
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    patched = "Harmony's patch list could not be read (" + exception.Message + ")";
                }
                _rangedEffectsOn = patched == null;
                if (patched != null && patched != _rangedEffectsOff)
                {
                    Log.Info("IdleEntities: ranged effect buildings are ticked as before in this game: " + patched + ".");
                }
                _rangedEffectsOff = patched;
            }
            return _rangedEffectsOn;
        }

        // A switched-on tick part that does nothing when ticked: a RangedEffectBuilding (the class itself, not one
        // derived from it) without a mechanical part, while the rule is on.
        private static bool DoesNothing(BaseComponent component)
        {
            return _rangedEffect != null && component.GetType() == _rangedEffect && RangedEffectsOn() &&
                   _mechanicalOf(component) == null;
        }

        // The list's change counter, which its every insert, removal and clear moves. It is 'version' in the game's Mono
        // and in .NET 8; '_version', the name List<T> uses, is tried in case a runtime renames it. Null if neither is
        // there, and then the keys are walked on every pass, as before.
        internal static Func<object, int> ChangeCounter(Type listType)
        {
            foreach (string name in new[] { "version", "_version" })
            {
                FieldInfo field = AccessTools.Field(listType, name);
                if (field != null && !field.IsStatic && field.FieldType == typeof(int))
                {
                    try
                    {
                        return Reflect.FieldGetter<int>(listType, name);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        public static void Activate()
        {
            SceneCreated();
            _active = true;
        }

        internal static bool IsActive => _active;

        // A new game or map: the previous scene's buckets and entities are gone.
        public static void SceneCreated()
        {
            Buckets.Clear();
            Records.Clear();
            ByComponent.Clear();
            _rangedEffectsJudged = false;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _passes == 0)
            {
                return null;
            }
            double ticks = _passes / 128.0;
            string line = $"IdleEntities: per tick {_ticked / ticks:0} entities ticked and {_leftOut / ticks:0} passed over because " +
                          "none of their tick parts was switched on" +
                          (_rangedEffectsOff == null
                              ? $" or would do anything ({_quiet / ticks:0} of them only had ranged effect buildings with no " +
                                "mechanical part switched on)"
                              : $" (ranged effect buildings are ticked as before: {_rangedEffectsOff})") +
                          $"; keys walked against the game's list on {_keyChecks} of {_passes} passes" +
                          (_listVersion == null ? " (the list's change counter was not found)" : "") +
                          (_resyncs > 0 ? $"; the mirror had to be rebuilt {_resyncs} time(s), which should not happen" : "");
            _passes = _ticked = _leftOut = _quiet = _resyncs = _keyChecks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        // Last, so that another mod's prefix on the same method (BeaverBuddies hashes the game's list before the
        // pass) still runs before the pass, and one that skips the pass skips this too.
        [HarmonyPriority(Priority.Last)]
        internal static bool TickAllPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            Bucket bucket;
            SortedList<Guid, TickableEntity> all;
            try
            {
                all = _entities(__instance);
                if (!Buckets.TryGetValue(__instance, out bucket))
                {
                    if (all.Count == 0)
                    {
                        return true;
                    }
                    bucket = new Bucket();
                    Buckets[__instance] = bucket;
                }
                int version = _listVersion != null ? _listVersion(all) : 0;
                if (bucket.Mirror.Count != all.Count || !(Unchanged(bucket, all, version) || SameKeys(bucket, all)))
                {
                    Resync(bucket, all);
                }
                // The keys match now; until the counter moves or a postfix changes the mirror they still will.
                bucket.CheckedList = _listVersion != null ? all : null;
                bucket.CheckedVersion = version;
                _setTicking(__instance, true);
                bucket.Ticking = true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
            SortedList<Guid, Record> mirror = bucket.Mirror;
            int total = all.Count;
            int ticked = 0;
            int quiet = 0;
            // The game's loop, index for index; the flag replaces the visit. No try/finally: if an entity's Tick
            // throws, the game leaves the bucket mid-pass (_isTicking set, removals pending) and so does this.
            // Should the mirror stop being trusted mid-pass (a bookkeeping failure while an entity was added from
            // inside a tick), every remaining entity is ticked, which is exactly the game's loop.
            for (int i = 0; i < all.Count; i++)
            {
                Record record = null;
                if (!_active || mirror.Count != all.Count || (record = mirror.Values[i]).Awake)
                {
                    ticked++;
                    TickEntity(all.Values[i]);
                }
                else if (record.Quiet)
                {
                    quiet++;
                }
            }
            // What the game does at the end of its own pass, on both lists.
            bucket.Ticking = false;
            try
            {
                _setTicking(__instance, false);
                List<TickableEntity> toRemove = _toRemove(__instance);
                for (int i = 0; i < toRemove.Count; i++)
                {
                    Guid id = toRemove[i].EntityId;
                    all.Remove(id);
                    mirror.Remove(id);
                }
                toRemove.Clear();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            _passes++;
            _ticked += ticked;
            _leftOut += total - ticked;
            _quiet += quiet;
            return false;
        }

        internal static void AddPostfix(object __instance, TickableEntity tickableEntity)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (!Buckets.TryGetValue(__instance, out Bucket bucket))
                {
                    bucket = new Bucket();
                    Buckets[__instance] = bucket;
                }
                Record record = Register(bucket, tickableEntity);
                // The game's Add inserts at once, even during a pass; so does the mirror, at the same index.
                bucket.Mirror[tickableEntity.EntityId] = record;
                // The next pass walks the keys (see Unchanged), even should another mod have skipped the game's Add.
                bucket.CheckedList = null;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void RemovePostfix(object __instance, TickableEntity tickableEntity)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (!Buckets.TryGetValue(__instance, out Bucket bucket))
                {
                    return;
                }
                if (bucket.Ticking && Records.TryGetValue(tickableEntity, out Record pending))
                {
                    // The game still ticks it for the rest of this pass, but once forgotten no part switched on
                    // from here on would wake it. Awake, it goes through the game's own Tick, which checks every part
                    // as the game's loop does; with nothing switched on that does nothing.
                    pending.Awake = true;
                }
                Forget(tickableEntity);
                if (!bucket.Ticking)
                {
                    bucket.Mirror.Remove(tickableEntity.EntityId);
                    // As in Add: the next pass walks the keys, even should another mod have skipped the game's Remove.
                    bucket.CheckedList = null;
                }
                // During a pass the game defers the removal to the end of TickAll (_entitiesToRemove); the mirror
                // removes the same keys there.
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void EnabledPostfix(BaseComponent __instance)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (ByComponent.TryGetValue(__instance, out Record record))
                {
                    if (DoesNothing(__instance))
                    {
                        // Switched on, but its tick does nothing: the entity stays as it was.
                        record.Quiet = !record.Awake;
                    }
                    else
                    {
                        record.Awake = true;
                        record.Quiet = false;
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void DisabledPostfix(BaseComponent __instance)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                // With neither flag set nothing of the entity's is switched on, and one part switched off changes that not.
                if (ByComponent.TryGetValue(__instance, out Record record) && (record.Awake || record.Quiet))
                {
                    Classify(record);
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
        // ReSharper restore InconsistentNaming

        private static Record Register(Bucket bucket, TickableEntity entity)
        {
            if (Records.TryGetValue(entity, out Record existing))
            {
                return existing;
            }
            Record record = new Record { Entity = entity, Bucket = bucket, Components = Collect(entity) };
            Classify(record);
            Records[entity] = record;
            foreach (BaseComponent component in record.Components)
            {
                ByComponent[component] = record;
            }
            return record;
        }

        private static void Forget(TickableEntity entity)
        {
            if (Records.TryGetValue(entity, out Record record))
            {
                Records.Remove(entity);
                foreach (BaseComponent component in record.Components)
                {
                    ByComponent.Remove(component);
                }
            }
        }

        // Whether the game's list is the one whose keys last matched the mirror's, with its change counter where it stood
        // then and the mirror untouched by the postfixes since; if so, the walk over the keys is skipped. Every change to a
        // SortedList's keys (an insert, a removal, a clear) moves its counter, so the game's keys are as they were. The
        // mirror's keys change in Add's postfix and in Remove's outside a pass, which both clear CheckedList so that the
        // next pass walks whatever the game's list did (another mod's prefix may have skipped the game's Add or Remove
        // while these postfixes still ran), and in the end-of-pass removals, where each key goes from both lists (moving
        // the counter) or is in neither. So the keys still match, the walk would have found them equal, and skipping it
        // changes nothing. The walk runs anyway on every 128th pass of a bucket, which catches a change that bypasses the
        // counter (only a write to the list's private fields could) or a counter that went all the way round.
        private static bool Unchanged(Bucket bucket, SortedList<Guid, TickableEntity> all, int version)
        {
            return _listVersion != null && ReferenceEquals(bucket.CheckedList, all) && version == bucket.CheckedVersion &&
                   ++bucket.PassesSinceWalk < WalkEvery;
        }

        private static bool SameKeys(Bucket bucket, SortedList<Guid, TickableEntity> all)
        {
            _keyChecks++;
            bucket.PassesSinceWalk = 0;
            IList<Guid> mine = bucket.Mirror.Keys;
            IList<Guid> theirs = all.Keys;
            for (int i = 0; i < theirs.Count; i++)
            {
                if (mine[i] != theirs[i])
                {
                    return false;
                }
            }
            return true;
        }

        // Should never be needed: the mirror follows every Add and Remove. If it ever is, it is rebuilt from the
        // game's list and counted, so the stats line shows it.
        private static void Resync(Bucket bucket, SortedList<Guid, TickableEntity> all)
        {
            // Records of entities this bucket no longer holds go too.
            List<TickableEntity> stale = new List<TickableEntity>();
            foreach (KeyValuePair<TickableEntity, Record> pair in Records)
            {
                if (pair.Value.Bucket == bucket && !all.ContainsKey(pair.Key.EntityId))
                {
                    stale.Add(pair.Key);
                }
            }
            foreach (TickableEntity entity in stale)
            {
                Forget(entity);
            }
            bucket.Mirror.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                TickableEntity entity = all.Values[i];
                Record record = Register(bucket, entity);
                Classify(record);
                bucket.Mirror.Add(entity.EntityId, record);
            }
            bucket.PassesSinceWalk = 0;
            _resyncs++;
        }

        private static BaseComponent[] Collect(TickableEntity entity)
        {
            List<BaseComponent> components = new List<BaseComponent>();
            foreach (object metered in (IEnumerable)_components(entity))
            {
                components.Add((BaseComponent)_inner(metered));
            }
            return components.ToArray();
        }

        // Works out an entity's flags from its components as they are now: awake if any switched-on tick part does
        // something when ticked; quiet if asleep with parts switched on that do nothing (DoesNothing).
        private static void Classify(Record record)
        {
            bool awake = false, quiet = false;
            BaseComponent[] components = record.Components;
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i].Enabled)
                {
                    if (!DoesNothing(components[i]))
                    {
                        awake = true;
                        break;
                    }
                    quiet = true;
                }
            }
            record.Awake = awake;
            record.Quiet = quiet && !awake;
        }

        private static void Fail(object reason)
        {
            _active = false;
            TurnedOff.Report("IdleEntities",
                "IdleEntities failed and turned itself off for this session; the game's own tick loop runs: " + reason);
        }
    }
}
