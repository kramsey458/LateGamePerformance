using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Cutting;
using Timberborn.Forestry;
using Timberborn.Yielding;
using UnityEngine;

namespace LateGamePerformance
{
    // The grown trees of the tree cutting area, counted on a grid of the map, so that a lumberjack's search can tell
    // before its walk that no grown marked tree is inside its building's reach (YielderSearch, 0.4.31).
    //
    // A lumberjack's candidates are every unreserved marked tree on the map (TreeCuttingArea.YieldersInArea, through
    // the game's `!Reserved` filter and Timber Together's colony filter, both of which only leave trees out). The 0.4.28
    // session showed where the search's time went once the lookups were left out: the walk itself, about 1,100
    // candidates per search at some 390 ns each, and most searches ended with nothing to take because no grown tree
    // was in reach. The game's finder hands out an answer only for a plant that is yielding and reached; once its
    // "found something" flag is set, a plant that is not yielding cannot change anything. So if no marked tree that is
    // yielding lies inside the box of the building's terrain route map (TerrainReach: every tile the map can reach is
    // inside it), none of the rest of the walk can be reached and yielding, and the search may stop right after the
    // candidate that found something, exactly as the full-building stop does (0.4.28). The answer is then the game's
    // "nothing to take".
    //
    // What is kept:
    //   - the game's own dictionary, mirrored key by key (Index): TreeCuttingArea.AddYielder and RemoveYielder are the
    //     only writers of `_yieldersInArea`, and after each (a finalizer, so a throw is followed too) the mirror reads
    //     the game's entry for that key and follows it;
    //   - whether each tree is yielding: Yielder.IsYielding is `Enabled && _yield.Amount > 0`, `_yield` is written only
    //     by Yielder's own Initialize, ResetYield, DecreaseYield, SetYieldToZero and Load, and `Enabled` only through
    //     BaseComponent.EnableComponent and DisableComponent (the setter is private); each is hooked and asks the tree
    //     again;
    //   - where each tree is: the tile of its CenterPosition, as TerrainReach.MayReach reads it. The centre is set only
    //     by BlockObjectCenter.UpdateCenter (on placement), which is hooked too. Height is left out, which only ever
    //     counts a tree that is not in the box, never the other way round.
    // The count over a box is a two-dimensional Fenwick tree (a segment tree's lighter cousin): a few dozen additions
    // per question, whatever the size of the box or the forest.
    //
    // The stop is judged once per session, at the first search, and stands down (the walk goes on as in 0.4.30) if the
    // code it rests on is not the code that was read (a hash of it, game 1.1.2.4), or if another mod patches any of the
    // methods above. It is only used for a lumberjack's search (LumberjackFlagWorkplaceBehavior.FindCuttable, whose
    // candidates are the area's trees) of the area the mirror follows, with a box that MayReach trusts. If anything
    // throws, the index is off for the session: the search then walks every candidate, which is the game's result.
    internal static class GrownTrees
    {
        // The code the stop rests on, as read (game 1.1.2.4); see HashOf.
        internal const string ReadHash = "609cae555a603632";

        // One entry per key of the game's dictionary, and per tree its tile and whether it is yielding. Free of game
        // types so the tests can drive it.
        internal sealed class Index<TKey, TPlant> where TPlant : class
        {
            private sealed class Entry
            {
                public int X, Y, Keys;
                public bool Grown;
            }

            // Larger maps than this are counted in _outside, which every answer includes: never a wrong "none".
            internal const int MaxSide = 1024;

            private readonly Dictionary<TKey, TPlant> _byKey = new Dictionary<TKey, TPlant>();
            private readonly Dictionary<TPlant, Entry> _entries = new Dictionary<TPlant, Entry>(new ReferenceComparer<TPlant>());
            private int[] _tree = new int[0];
            private int _side;
            private int _outside;

            public int Keys => _byKey.Count;

            // Keys whose tree is yielding.
            public int Grown { get; private set; }

            public TPlant At(TKey key)
            {
                return _byKey.TryGetValue(key, out TPlant plant) ? plant : null;
            }

            public bool Contains(TPlant plant)
            {
                return _entries.ContainsKey(plant);
            }

            // The game has `plant` under `key` now.
            public void Put(TKey key, TPlant plant, int x, int y, bool grown)
            {
                if (_byKey.TryGetValue(key, out TPlant old))
                {
                    if (ReferenceEquals(old, plant))
                    {
                        return;
                    }
                    Detach(old);
                }
                _byKey[key] = plant;
                if (_entries.TryGetValue(plant, out Entry entry))
                {
                    // The same tree under a second key: the game's list hands it out twice, so it counts twice.
                    entry.Keys++;
                    if (entry.Grown)
                    {
                        Weigh(entry.X, entry.Y, 1);
                    }
                    return;
                }
                Fit(x, y);
                entry = new Entry { X = x, Y = y, Keys = 1, Grown = grown };
                _entries[plant] = entry;
                if (grown)
                {
                    Weigh(x, y, 1);
                }
            }

            // The game has nothing under `key` now.
            public void Drop(TKey key)
            {
                if (_byKey.TryGetValue(key, out TPlant old))
                {
                    _byKey.Remove(key);
                    Detach(old);
                }
            }

            public void SetGrown(TPlant plant, bool grown)
            {
                if (_entries.TryGetValue(plant, out Entry entry) && entry.Grown != grown)
                {
                    entry.Grown = grown;
                    Weigh(entry.X, entry.Y, grown ? entry.Keys : -entry.Keys);
                }
            }

            public void Move(TPlant plant, int x, int y)
            {
                if (_entries.TryGetValue(plant, out Entry entry) && (entry.X != x || entry.Y != y))
                {
                    Fit(x, y);
                    if (entry.Grown)
                    {
                        Weigh(entry.X, entry.Y, -entry.Keys);
                        Weigh(x, y, entry.Keys);
                    }
                    entry.X = x;
                    entry.Y = y;
                }
            }

            // Yielding trees (counted per key) on tiles with minX <= x <= maxX and minY <= y <= maxY, plus any filed
            // outside the grid.
            public int GrownIn(int minX, int minY, int maxX, int maxY)
            {
                int total = _outside;
                if (minX < 0) minX = 0;
                if (minY < 0) minY = 0;
                if (maxX > _side - 1) maxX = _side - 1;
                if (maxY > _side - 1) maxY = _side - 1;
                if (minX > maxX || minY > maxY)
                {
                    return total;
                }
                return total + Prefix(maxX, maxY) - Prefix(minX - 1, maxY) - Prefix(maxX, minY - 1) + Prefix(minX - 1, minY - 1);
            }

            // The same question answered by going through every tree, for the tests and the verify mode.
            public int GrownInByWalking(int minX, int minY, int maxX, int maxY)
            {
                int total = 0;
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Grown && (!OnGrid(entry.X, entry.Y) ||
                                        entry.X >= minX && entry.X <= maxX && entry.Y >= minY && entry.Y <= maxY))
                    {
                        total += entry.Keys;
                    }
                }
                return total;
            }

            public IEnumerable<KeyValuePair<TKey, TPlant>> Pairs => _byKey;

            public void Clear()
            {
                _byKey.Clear();
                _entries.Clear();
                _tree = new int[0];
                _side = 0;
                _outside = 0;
                Grown = 0;
            }

            private void Detach(TPlant plant)
            {
                Entry entry = _entries[plant];
                entry.Keys--;
                if (entry.Grown)
                {
                    Weigh(entry.X, entry.Y, -1);
                }
                if (entry.Keys == 0)
                {
                    _entries.Remove(plant);
                }
            }

            private static bool OnGrid(int x, int y)
            {
                return x >= 0 && y >= 0 && x < MaxSide && y < MaxSide;
            }

            // Makes the grid large enough for the tile before anything is changed, so that a rebuild sees a
            // consistent state.
            private void Fit(int x, int y)
            {
                if (!OnGrid(x, y) || x < _side && y < _side)
                {
                    return;
                }
                int side = Math.Max(64, _side);
                while (side <= x || side <= y)
                {
                    side *= 2;
                }
                _side = side;
                _tree = new int[(side + 1) * (side + 1)];
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Grown && OnGrid(entry.X, entry.Y))
                    {
                        Add(entry.X, entry.Y, entry.Keys);
                    }
                }
            }

            private void Weigh(int x, int y, int weight)
            {
                Grown += weight;
                if (OnGrid(x, y))
                {
                    Add(x, y, weight);
                }
                else
                {
                    _outside += weight;
                }
            }

            private void Add(int x, int y, int weight)
            {
                int row = _side + 1;
                for (int i = x + 1; i <= _side; i += i & -i)
                {
                    for (int j = y + 1; j <= _side; j += j & -j)
                    {
                        _tree[i * row + j] += weight;
                    }
                }
            }

            // The weight on tiles 0..x by 0..y; 0 when either is -1.
            private int Prefix(int x, int y)
            {
                int row = _side + 1;
                int sum = 0;
                for (int i = x + 1; i > 0; i -= i & -i)
                {
                    for (int j = y + 1; j > 0; j -= j & -j)
                    {
                        sum += _tree[i * row + j];
                    }
                }
                return sum;
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T item) => RuntimeHelpers.GetHashCode(item);
        }

        private const string LumberjackBehaviorType = "Timberborn.Forestry.LumberjackFlagWorkplaceBehavior";

        private static readonly Index<Vector3Int, Yielder> Trees = new Index<Vector3Int, Yielder>();
        // The tree of each centre, for UpdateCenter; weak, so a cut tree is let go of with its entity.
        private static ConditionalWeakTable<BlockObjectCenter, Yielder> _centers = new ConditionalWeakTable<BlockObjectCenter, Yielder>();
        private static Func<object, object> _areaOf;
        private static Func<object, Dictionary<Vector3Int, Yielder>> _yieldersOf;
        // The centre a tree's CenterPosition reads (Yielder._blockObjectCenter).
        private static Func<object, BlockObjectCenter> _centerOf;
        // The area the mirror follows, and the area of the lumberjack search under way (main thread only).
        private static TreeCuttingArea _area;
        private static TreeCuttingArea _searching;
        private static bool _active;
        // 0 until the first search judges it, then 1 (on) or -1 (off) for the session; _off says why not.
        private static int _judged;
        private static string _off;
        private static long _checks;
        private static long _mismatches;
        // Areas followed in this scene. The game has one; should a mod make several, following them in turn would rebuild
        // the index on every change, so the count stands down instead.
        private static int _follows;
        private const int MostFollows = 4;

        public static Feature CreateFeature()
        {
            Type self = typeof(GrownTrees);
            Feature feature = new Feature { Name = "GrownTrees" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TreeCuttingArea.AddYielder",
                Required = true,
                Target = () =>
                {
                    _yieldersOf = Reflect.FieldGetter<Dictionary<Vector3Int, Yielder>>(typeof(TreeCuttingArea), "_yieldersInArea");
                    _areaOf = Reflect.FieldGetter<object>(Reflect.GameType(LumberjackBehaviorType), "_treeCuttingArea");
                    _centerOf = Reflect.FieldGetter<BlockObjectCenter>(typeof(Yielder), "_blockObjectCenter");
                    return AccessTools.Method(typeof(TreeCuttingArea), "AddYielder");
                },
                Finalizer = Reflect.Own(self, nameof(AreaChangedFinalizer))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TreeCuttingArea.RemoveYielder",
                Required = true,
                Target = () => AccessTools.Method(typeof(TreeCuttingArea), "RemoveYielder"),
                Finalizer = Reflect.Own(self, nameof(AreaChangedFinalizer))
            });
            foreach (string method in YieldWriters)
            {
                string name = method;
                feature.Patches.Add(new PatchSpec
                {
                    Name = "Yielder." + name,
                    Required = true,
                    // Declared only: BaseComponent has an Initialize of its own.
                    Target = () => typeof(Yielder).GetMethod(name, BindingFlags.Instance | BindingFlags.Public |
                                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                    Finalizer = Reflect.Own(self, nameof(YieldChangedFinalizer))
                });
            }
            foreach (string method in new[] { "EnableComponent", "DisableComponent" })
            {
                string name = method;
                feature.Patches.Add(new PatchSpec
                {
                    Name = "BaseComponent." + name,
                    Required = true,
                    Target = () => AccessTools.Method(typeof(BaseComponent), name),
                    Postfix = Reflect.Own(self, nameof(SwitchedPostfix))
                });
            }
            feature.Patches.Add(new PatchSpec
            {
                Name = "BlockObjectCenter.UpdateCenter",
                Required = true,
                Target = () => AccessTools.Method(typeof(BlockObjectCenter), "UpdateCenter"),
                Postfix = Reflect.Own(self, nameof(CenterPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "LumberjackFlagWorkplaceBehavior.FindCuttable",
                Required = true,
                Target = () => Reflect.Method(LumberjackBehaviorType, "FindCuttable"),
                Prefix = Reflect.Own(self, nameof(CuttingPrefix)),
                Finalizer = Reflect.Own(self, nameof(CuttingFinalizer))
            });
            return feature;
        }

        // The methods of Yielder that write `_yield` (the tests read Yielder's code to hold this list to it).
        internal static readonly string[] YieldWriters = { "Initialize", "ResetYield", "DecreaseYield", "SetYieldToZero", "Load" };

        public static void Activate()
        {
            SceneCreated();
            _judged = 0;
            _off = null;
            _active = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            Trees.Clear();
            _centers = new ConditionalWeakTable<BlockObjectCenter, Yielder>();
            _area = null;
            _searching = null;
            _follows = 0;
        }

        // For the lumberjack search under way: true when no marked tree that is yielding can be inside the box, so
        // the walk may stop once something is found. False whenever that cannot be told.
        internal static bool NoneGrownIn(TerrainReach.Box box)
        {
            TreeCuttingArea area = _searching;
            if (!_active || area == null || !TerrainReach.Trusted(box) || !Allowed())
            {
                return false;
            }
            try
            {
                if (!ReferenceEquals(area, _area))
                {
                    Follow(area);
                    // Following may have stood the count down, and an emptied index would answer "none".
                    if (!_active || !ReferenceEquals(area, _area))
                    {
                        return false;
                    }
                }
                return Trees.GrownIn(box.MinX, box.MinY, box.MaxX, box.MaxY) == 0;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return false;
            }
        }

        // Verify mode: the index against the game's own dictionary, for the search under way. Counts and logs a
        // difference; nothing the game is handed depends on it (the stop is judged before and either way).
        internal static void Check(TerrainReach.Box box)
        {
            TreeCuttingArea area = _searching;
            if (!_active || area == null || !ReferenceEquals(area, _area) || !TerrainReach.Trusted(box))
            {
                return;
            }
            Dictionary<Vector3Int, Yielder> game = _yieldersOf(area);
            int grown = 0;
            foreach (Yielder yielder in game.Values)
            {
                if (yielder != null && yielder.IsYielding)
                {
                    Vector3Int tile = TerrainReach.GridOf(yielder.CenterPosition);
                    if (tile.x >= box.MinX && tile.x <= box.MaxX && tile.y >= box.MinY && tile.y <= box.MaxY)
                    {
                        grown++;
                    }
                }
            }
            int kept = Trees.GrownIn(box.MinX, box.MinY, box.MaxX, box.MaxY);
            _checks++;
            if (kept != grown || Trees.Keys != game.Count)
            {
                _mismatches++;
                if (_mismatches <= 10)
                {
                    Log.Warning($"GrownTrees verify: the index has {kept} grown trees in the box ({Trees.Keys} marked), " +
                                $"the game's area {grown} ({game.Count} marked).");
                }
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active && _off == null)
            {
                return null;
            }
            string line = string.Format(CultureInfo.InvariantCulture,
                "GrownTrees: {0} marked trees, {1} grown{2}{3}", Trees.Keys, Trees.Grown,
                _checks > 0 ? $"; verify checks {_checks}, mismatches {_mismatches}" : "",
                _off != null ? $" (lumberjack searches walk every candidate: {_off})" : "");
            _checks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static void AreaChangedFinalizer(TreeCuttingArea __instance, TreeComponent treeComponent)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (!ReferenceEquals(__instance, _area))
                {
                    Follow(__instance);
                    return;
                }
                Sync(_yieldersOf(__instance), treeComponent.GetComponent<BlockObject>().Coordinates);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void YieldChangedFinalizer(Yielder __instance)
        {
            if (!_active || Trees.Keys == 0)
            {
                return;
            }
            try
            {
                Trees.SetGrown(__instance, __instance.IsYielding);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void SwitchedPostfix(BaseComponent __instance)
        {
            if (!_active || Trees.Keys == 0 || !(__instance is Yielder yielder))
            {
                return;
            }
            try
            {
                Trees.SetGrown(yielder, yielder.IsYielding);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void CenterPostfix(BlockObjectCenter __instance)
        {
            if (!_active || Trees.Keys == 0 || !_centers.TryGetValue(__instance, out Yielder yielder) || !Trees.Contains(yielder))
            {
                return;
            }
            try
            {
                Vector3Int tile = TerrainReach.GridOf(yielder.CenterPosition);
                Trees.Move(yielder, tile.x, tile.y);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void CuttingPrefix(object __instance)
        {
            _searching = _active ? _areaOf(__instance) as TreeCuttingArea : null;
        }

        internal static void CuttingFinalizer()
        {
            _searching = null;
        }
        // ReSharper restore InconsistentNaming

        // Starts following an area: everything the game has in it now, then each change.
        private static void Follow(TreeCuttingArea area)
        {
            if (++_follows > MostFollows)
            {
                _active = false;
                Trees.Clear();
                _area = null;
                _off = "a game scene had more than one tree cutting area";
                Log.Info("GrownTrees: lumberjack searches walk every candidate as before: " + _off + ".");
                return;
            }
            Trees.Clear();
            _centers = new ConditionalWeakTable<BlockObjectCenter, Yielder>();
            _area = area;
            foreach (KeyValuePair<Vector3Int, Yielder> pair in _yieldersOf(area))
            {
                Sync(_yieldersOf(area), pair.Key);
            }
        }

        // The mirror's entry for one key made the game's.
        private static void Sync(Dictionary<Vector3Int, Yielder> game, Vector3Int key)
        {
            Yielder now = game.TryGetValue(key, out Yielder yielder) ? yielder : null;
            if (ReferenceEquals(Trees.At(key), now))
            {
                return;
            }
            if (now == null)
            {
                Trees.Drop(key);
                return;
            }
            Vector3Int tile = TerrainReach.GridOf(now.CenterPosition);
            Trees.Put(key, now, tile.x, tile.y, now.IsYielding);
            BlockObjectCenter center = _centerOf(now);
            if (center != null)
            {
                _centers.Remove(center);
                _centers.Add(center, now);
            }
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            Trees.Clear();
            _area = null;
            Log.Warning("GrownTrees failed and is off for this session; lumberjack searches walk every candidate as before: " + exception);
        }

        // Judged at the first search of the session, when every mod has patched what it patches.
        private static bool Allowed()
        {
            if (_judged == 0)
            {
                string off;
                try
                {
                    off = Blocker();
                }
                catch (Exception exception)
                {
                    off = "the code could not be read (" + exception.Message + ")";
                }
                _judged = off == null ? 1 : -1;
                _off = off;
                if (off != null)
                {
                    Log.Info("GrownTrees: lumberjack searches walk every candidate as before: " + off + ".");
                }
            }
            return _judged > 0;
        }

        // Why the stop may not be used, or null.
        internal static string Blocker()
        {
            string hash = HashNow();
            if (hash != ReadHash)
            {
                return $"the game's code is not the code that was read (it hashes to {hash}, the code read to {ReadHash})";
            }
            foreach (MethodBase method in Methods())
            {
                IEnumerable<(string Owner, string Patch)> patches = YielderSearch.PatchesOn(method);
                if (patches == null)
                {
                    continue;
                }
                foreach ((string owner, string patch) in patches)
                {
                    if (!owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                    {
                        return $"another mod patches {method.DeclaringType?.Name}.{method.Name} ({owner}, {patch})";
                    }
                }
            }
            return null;
        }

        // Every method the stop rests on: all of Yielder, TreeCuttingArea and BlockObjectCenter, the lumberjack's
        // search, and the component switches.
        internal static List<MethodBase> Methods()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            List<MethodBase> methods = new List<MethodBase>();
            foreach (Type type in new[] { typeof(Yielder), typeof(TreeCuttingArea), typeof(BlockObjectCenter) })
            {
                List<MethodBase> declared = new List<MethodBase>(type.GetMethods(any));
                declared.AddRange(type.GetConstructors(any));
                declared.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                methods.AddRange(declared);
            }
            methods.Add(Reflect.Method(LumberjackBehaviorType, "FindCuttable"));
            methods.Add(AccessTools.Method(typeof(BaseComponent), "EnableComponent"));
            methods.Add(AccessTools.Method(typeof(BaseComponent), "DisableComponent"));
            methods.Add(AccessTools.PropertyGetter(typeof(BaseComponent), "Enabled"));
            return methods;
        }

        // FNV-1a (64 bits) over the IL of Methods(), in that order.
        internal static string HashNow()
        {
            ulong hash = 14695981039346656037UL;
            foreach (MethodBase method in Methods())
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

        internal static void ResetForTests()
        {
            _judged = 0;
            _off = null;
        }
    }
}
