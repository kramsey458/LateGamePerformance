using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.BaseComponentSystem;
using Timberborn.Forestry;
using Timberborn.Goods;
using Timberborn.Yielding;

// The grown tree count (0.4.31): the index against a count over every tree under random changes, and what it rests on
// in the game's real classes (who writes a yield, who writes the cutting area's trees, what IsYielding reads), the
// guard that stands it down, and Mono's inlining limit for every hooked method.
internal static class GrownTreesTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static void Run(Action<bool, string> check)
    {
        RandomChanges(check);
        GameCode(check);
        RealYielder(check);
        Guard(check);
        RealArea(check);
        Inlining(check);
    }

    private sealed class Tree
    {
    }

    // Puts, drops, the same tree under two keys, yield changes and moves, on and off the grid (negative tiles and tiles
    // past its largest size), against a count over every tree, for random boxes.
    private static void RandomChanges(Action<bool, string> check)
    {
        Random random = new Random(3141);
        var index = new GrownTrees.Index<int, Tree>();
        var byKey = new Dictionary<int, Tree>();
        var grown = new Dictionary<Tree, bool>();
        var tile = new Dictionary<Tree, (int X, int Y)>();
        List<Tree> trees = Enumerable.Range(0, 400).Select(_ => new Tree()).ToList();
        int wrong = 0, questions = 0, nonZero = 0, zero = 0;
        (int, int) RandomTile()
        {
            int roll = random.Next(400);
            if (roll == 0) return (-1 - random.Next(5), random.Next(300));
            if (roll == 1) return (random.Next(300), GrownTrees.Index<int, Tree>.MaxSide + random.Next(50));
            return (random.Next(roll < 200 ? 60 : 300), random.Next(roll < 200 ? 60 : 300));
        }
        foreach (Tree tree in trees)
        {
            grown[tree] = random.Next(4) == 0;
            tile[tree] = RandomTile();
        }
        for (int step = 0; step < 20000; step++)
        {
            Tree tree = trees[random.Next(trees.Count)];
            switch (random.Next(6))
            {
                case 0:
                case 1:
                {
                    int key = random.Next(600);
                    byKey[key] = tree;
                    index.Put(key, tree, tile[tree].X, tile[tree].Y, grown[tree]);
                    break;
                }
                case 2:
                {
                    int key = random.Next(600);
                    byKey.Remove(key);
                    index.Drop(key);
                    break;
                }
                case 3:
                case 4:
                    grown[tree] = !grown[tree];
                    index.SetGrown(tree, grown[tree]);
                    break;
                default:
                    tile[tree] = RandomTile();
                    index.Move(tree, tile[tree].X, tile[tree].Y);
                    break;
            }
            if (step % 20 == 0)
            {
                // Mostly boxes the size of a flag's reach, some of them the whole map.
                int size = random.Next(3) == 0 ? 400 : random.Next(-2, 25);
                int minX = random.Next(-10, 320), minY = random.Next(-10, 320);
                int maxX = minX + size, maxY = minY + random.Next(-2, 25);
                int expected = 0, total = 0;
                foreach (KeyValuePair<int, Tree> pair in byKey)
                {
                    if (!grown[pair.Value]) continue;
                    total++;
                    (int x, int y) = tile[pair.Value];
                    bool onGrid = x >= 0 && y >= 0 && x < GrownTrees.Index<int, Tree>.MaxSide && y < GrownTrees.Index<int, Tree>.MaxSide;
                    if (!onGrid || x >= minX && x <= maxX && y >= minY && y <= maxY) expected++;
                }
                int answer = index.GrownIn(minX, minY, maxX, maxY);
                questions++;
                if (answer != expected || index.GrownInByWalking(minX, minY, maxX, maxY) != expected || index.Grown != total ||
                    index.Keys != byKey.Count)
                {
                    wrong++;
                }
                if (expected == 0) zero++;
                else nonZero++;
            }
        }
        check(wrong == 0 && zero > 50 && nonZero > 50,
            $"grown trees: the index answers {questions} random boxes like a count over every tree, through 20000 random puts, " +
            $"drops, yield changes and moves ({wrong} wrong; {zero} boxes with none, {nonZero} with some)");

        // What leaving height out and filing trees off the grid amount to: counting more, never fewer.
        var edge = new GrownTrees.Index<int, Tree>();
        edge.Put(1, new Tree(), -3, 5, true);
        check(edge.GrownIn(10, 10, 20, 20) == 1 && edge.GrownIn(30, 30, 20, 20) == 1,
            "grown trees: a grown tree off the grid is counted in every box, so it can never make a wrong \"none\"");
    }

    // One instruction's operand, when it is a field or a method.
    private static IEnumerable<(OpCode Code, MemberInfo Member)> Read(MethodBase method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) yield break;
        Dictionary<short, OpCode> table = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(code => code.Value);
        Type[] typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE) value = (short)(0xFE00 | il[i++]);
            OpCode code = table[value];
            int size;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(il, i); break;
                default: size = 4; break;
            }
            MemberInfo member = null;
            if (code.OperandType == OperandType.InlineField || code.OperandType == OperandType.InlineMethod)
            {
                member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArguments, methodArguments);
            }
            i += size;
            yield return (code, member);
        }
    }

    private static IEnumerable<MethodBase> Declared(Type type)
    {
        return type.GetMethods(Any).Cast<MethodBase>().Concat(type.GetConstructors(Any));
    }

    // The game's code, read for what the count rests on.
    private static void GameCode(Action<bool, string> check)
    {
        // Who writes a tree's yield: exactly the hooked methods.
        FieldInfo yield = typeof(Yielder).GetField("_yield", Any);
        string[] writers = Declared(typeof(Yielder))
            .Where(method => Read(method).Any(instruction => instruction.Code == OpCodes.Stfld && instruction.Member == yield))
            .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] hooked = GrownTrees.YieldWriters.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        check(yield != null && writers.SequenceEqual(hooked),
            $"grown trees: the game's Yielder writes its yield only in the hooked methods ({string.Join(", ", writers)})");

        // Enabled can only be written by BaseComponent's two switches.
        MethodInfo setter = typeof(BaseComponent).GetProperty("Enabled")?.GetSetMethod(true);
        string[] enabledWriters = Declared(typeof(BaseComponent))
            .Where(method => Read(method).Any(instruction => instruction.Member == setter))
            .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        check(setter != null && setter.IsPrivate && enabledWriters.SequenceEqual(new[] { "DisableComponent", "EnableComponent" }),
            $"grown trees: a component is switched on and off only through EnableComponent and DisableComponent " +
            $"(the Enabled setter is private and called from {string.Join(", ", enabledWriters)})");

        // The cutting area's trees change only in AddYielder and RemoveYielder.
        FieldInfo trees = typeof(TreeCuttingArea).GetField("_yieldersInArea", Any);
        string[] changers = Declared(typeof(TreeCuttingArea))
            .Where(method =>
            {
                List<(OpCode Code, MemberInfo Member)> code = Read(method).ToList();
                return code.Any(instruction => instruction.Member == trees) &&
                       code.Any(instruction => instruction.Member is MethodInfo called && called.DeclaringType == trees.FieldType &&
                                               (called.Name == "set_Item" || called.Name == "Add" || called.Name == "Remove" ||
                                                called.Name == "Clear" || called.Name == "TryAdd"));
            })
            .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        bool notHandedOut = Declared(typeof(TreeCuttingArea)).All(method =>
            !Read(method).Any(instruction => instruction.Member == trees) ||
            new[] { "AddYielder", "RemoveYielder", "get_YieldersInArea", "get_AnyYielderSelected", "HasYielder", ".ctor" }.Contains(method.Name));
        check(trees != null && changers.SequenceEqual(new[] { "AddYielder", "RemoveYielder" }) && notHandedOut,
            $"grown trees: the game's cutting area changes its trees only in AddYielder and RemoveYielder ({string.Join(", ", changers)}), " +
            "and hands them out only as its values");

        // A lumberjack's candidates are the area's trees through a filter that only leaves trees out.
        MethodBase findCuttable = Reflect.Method("Timberborn.Forestry.LumberjackFlagWorkplaceBehavior", "FindCuttable");
        List<MemberInfo> calls = Read(findCuttable).Select(instruction => instruction.Member).Where(member => member != null).ToList();
        check(calls.Any(member => member.Name == "get_YieldersInArea") && calls.Any(member => member.Name == "Where") &&
              calls.Any(member => member.Name == "FindLivingYielderWithoutAccessible"),
            "grown trees: the lumberjack's search hands the finder the cutting area's trees through Where");

        // The tile the index files a tree under is the one TerrainReach.MayReach reads: the same position, which only
        // UpdateCenter sets.
        MethodInfo grounded = typeof(Timberborn.BlockSystem.BlockObjectCenter).GetProperty("WorldCenterGrounded")?.GetSetMethod(true);
        string[] placers = Declared(typeof(Timberborn.BlockSystem.BlockObjectCenter))
            .Where(method => Read(method).Any(instruction => instruction.Member == grounded)).Select(method => method.Name).ToArray();
        check(Read(typeof(Yielder).GetProperty("CenterPosition").GetGetMethod()).Any(instruction => instruction.Member?.Name == "get_WorldCenterGrounded") &&
              grounded != null && grounded.IsPrivate && placers.SequenceEqual(new[] { "UpdateCenter" }),
            $"grown trees: a tree's CenterPosition is its BlockObjectCenter's WorldCenterGrounded, set only in UpdateCenter (hooked; set in {string.Join(", ", placers)})");
    }

    // The game's own Yielder: IsYielding is on and yielding, whichever way either changes, through the game's methods.
    private static void RealYielder(Action<bool, string> check)
    {
        FieldInfo yieldField = typeof(Yielder).GetField("_yield", Any);
        MethodInfo enabled = typeof(BaseComponent).GetProperty("Enabled").GetSetMethod(true);
        int wrong = 0, cases = 0;
        foreach (bool on in new[] { true, false })
        {
            foreach (int amount in new[] { 0, 1, 3 })
            {
                Yielder yielder = (Yielder)RuntimeHelpers.GetUninitializedObject(typeof(Yielder));
                yieldField.SetValue(yielder, new GoodAmount("Log", amount));
                enabled.Invoke(yielder, new object[] { on });
                cases++;
                if (yielder.IsYielding != (on && amount > 0)) wrong++;
                // DecreaseYield: the game's method, as a lumberjack takes the logs.
                yielder.DecreaseYield(new GoodAmount("Log", amount));
                cases++;
                if (yielder.IsYielding) wrong++;
            }
        }
        check(wrong == 0, $"grown trees: the game's IsYielding is \"switched on and yield above 0\" ({cases} cases, {wrong} wrong)");
    }

    // A game Yielder of the given yield, switched on, standing at the tile.
    private static Yielder MakeTree(int amount, int x, int y)
    {
        Timberborn.BlockSystem.BlockObjectCenter center =
            (Timberborn.BlockSystem.BlockObjectCenter)RuntimeHelpers.GetUninitializedObject(typeof(Timberborn.BlockSystem.BlockObjectCenter));
        typeof(Timberborn.BlockSystem.BlockObjectCenter).GetProperty("WorldCenterGrounded").GetSetMethod(true)
            .Invoke(center, new object[] { new UnityEngine.Vector3(x + 0.5f, y + 0.5f, 0.5f) });
        Yielder yielder = (Yielder)RuntimeHelpers.GetUninitializedObject(typeof(Yielder));
        typeof(Yielder).GetField("_blockObjectCenter", Any).SetValue(yielder, center);
        typeof(Yielder).GetField("_yield", Any).SetValue(yielder, new GoodAmount("Log", amount));
        typeof(BaseComponent).GetProperty("Enabled").GetSetMethod(true).Invoke(yielder, new object[] { true });
        return yielder;
    }

    // The glue with the game's own TreeCuttingArea, Yielder and lumberjack behaviour: the first search follows the area,
    // the hooks keep the count, the verify mode sees a yield changed behind the hooks' back, and an area that is not
    // the one followed, or several in turn, never get a "none".
    private static void RealArea(Action<bool, string> check)
    {
        Func<MethodBase, IEnumerable<(string Owner, string Patch)>> previous = YielderSearch.PatchesOn;
        bool wasVerifying = YielderSearch.VerifyEnabled;
        try
        {
            YielderSearch.PatchesOn = _ => null;
            GrownTrees.CreateFeature().Patches[0].Target();   // binds the field readers
            GrownTrees.Activate();
            object field = new object();
            TerrainReach.SetFilledForTests(f => ReferenceEquals(f, field), world => UnityEngine.Vector3Int.FloorToInt(world));
            TerrainReach.Box reach = TerrainReach.BoxForTests(field, new UnityEngine.Vector3Int(0, 0, 0), new UnityEngine.Vector3Int(9, 9, 5));

            TreeCuttingArea MakeArea(Dictionary<UnityEngine.Vector3Int, Yielder> trees)
            {
                TreeCuttingArea made = (TreeCuttingArea)RuntimeHelpers.GetUninitializedObject(typeof(TreeCuttingArea));
                typeof(TreeCuttingArea).GetField("_yieldersInArea", Any).SetValue(made, trees);
                return made;
            }
            object Flag(TreeCuttingArea of)
            {
                Type type = Reflect.GameType("Timberborn.Forestry.LumberjackFlagWorkplaceBehavior");
                object flag = RuntimeHelpers.GetUninitializedObject(type);
                type.GetField("_treeCuttingArea", Any).SetValue(flag, of);
                return flag;
            }
            bool Ask(object flag)
            {
                GrownTrees.CuttingPrefix(flag);
                try
                {
                    return GrownTrees.NoneGrownIn(reach);
                }
                finally
                {
                    GrownTrees.CuttingFinalizer();
                }
            }

            var marked = new Dictionary<UnityEngine.Vector3Int, Yielder>();
            Yielder inReach = MakeTree(0, 4, 4);
            Yielder farAway = MakeTree(2, 40, 40);
            marked[new UnityEngine.Vector3Int(4, 4, 0)] = inReach;
            marked[new UnityEngine.Vector3Int(40, 40, 0)] = farAway;
            TreeCuttingArea area = MakeArea(marked);
            object lumberjack = Flag(area);
            bool noneAtFirst = Ask(lumberjack);
            // The tree in reach grows: the game's ResetYield would write the yield; here the yield is written and the
            // hook the game's method carries is called, as Harmony would.
            typeof(Yielder).GetField("_yield", Any).SetValue(inReach, new GoodAmount("Log", 3));
            GrownTrees.YieldChangedFinalizer(inReach);
            bool someOnceGrown = !Ask(lumberjack);
            // Cut: the game's own DecreaseYield, then the hook.
            inReach.DecreaseYield(new GoodAmount("Log", 3));
            GrownTrees.YieldChangedFinalizer(inReach);
            bool noneOnceCut = Ask(lumberjack);
            // Switched off while grown: the game's own switch needs a component cache, so the setter and the hook.
            typeof(Yielder).GetField("_yield", Any).SetValue(inReach, new GoodAmount("Log", 3));
            GrownTrees.YieldChangedFinalizer(inReach);
            typeof(BaseComponent).GetProperty("Enabled").GetSetMethod(true).Invoke(inReach, new object[] { false });
            GrownTrees.SwitchedPostfix(inReach);
            bool noneWhenOff = Ask(lumberjack);
            check(noneAtFirst && someOnceGrown && noneOnceCut && noneWhenOff,
                "grown trees: with the game's own area and trees, the first search follows the area, and growing, cutting and " +
                $"switching a tree off change the answer as the game's IsYielding does ({noneAtFirst}, {someOnceGrown}, {noneOnceCut}, {noneWhenOff})");

            // Verify: a tree switched on where no hook runs is found and counted, and changes nothing that is handed out.
            YielderSearch.VerifyEnabled = true;
            typeof(BaseComponent).GetProperty("Enabled").GetSetMethod(true).Invoke(inReach, new object[] { true });
            GrownTrees.CuttingPrefix(lumberjack);
            GrownTrees.Check(reach);
            GrownTrees.CuttingFinalizer();
            string line = GrownTrees.TakeStatsLine();
            Console.WriteLine("     " + line);
            check(line != null && line.Contains("verify checks 1, mismatches 1"),
                "grown trees verify: a tree switched on behind the hooks' back is counted as a mismatch");
            GrownTrees.SwitchedPostfix(inReach);

            // A search of another area than the one followed follows that one instead; several in turn stand the count down.
            TreeCuttingArea other = MakeArea(new Dictionary<UnityEngine.Vector3Int, Yielder> { [new UnityEngine.Vector3Int(5, 5, 0)] = MakeTree(1, 5, 5) });
            bool otherSeen = !Ask(Flag(other));
            bool backAgain = !Ask(lumberjack);
            bool lastAnswer = true;
            for (int i = 0; i < 6; i++)
            {
                lastAnswer = Ask(Flag(MakeArea(new Dictionary<UnityEngine.Vector3Int, Yielder>())));
            }
            string offLine = GrownTrees.TakeStatsLine();
            Console.WriteLine("     " + offLine);
            check(otherSeen && backAgain && !lastAnswer && !GrownTrees.IsActive && offLine != null &&
                  offLine.Contains("more than one tree cutting area"),
                "grown trees: another area's search follows that area, and areas followed in turn stand the count down " +
                "instead of answering \"none\" from an emptied index");
        }
        finally
        {
            YielderSearch.PatchesOn = previous;
            YielderSearch.VerifyEnabled = wasVerifying;
            GrownTrees.ResetForTests();
            GrownTrees.SceneCreated();
        }
    }

    // On with the code that was read and no other mod's patches; off, with a reason, otherwise.
    private static void Guard(Action<bool, string> check)
    {
        Func<MethodBase, IEnumerable<(string Owner, string Patch)>> previous = YielderSearch.PatchesOn;
        try
        {
            string hash = GrownTrees.HashNow();
            Console.WriteLine($"     GrownTrees hash: {hash}");
            YielderSearch.PatchesOn = _ => null;
            check(GrownTrees.Blocker() == null,
                $"grown trees: the game's code is the code that was read (hash {hash}, read {GrownTrees.ReadHash})");
            List<MethodBase> methods = GrownTrees.Methods();
            YielderSearch.PatchesOn = _ => new[] { (Plugin.HarmonyId + ".IdleEntities", "LateGamePerformance.IdleEntities.EnabledPostfix") };
            bool ours = GrownTrees.Blocker() == null;
            int blocked = 0;
            foreach (MethodBase method in methods)
            {
                YielderSearch.PatchesOn = m => m == method ? new[] { ("some.other.mod", "Other.Mod.Patch.Postfix") } : null;
                string blocker = GrownTrees.Blocker();
                if (blocker != null && blocker.Contains("some.other.mod")) blocked++;
            }
            check(methods.All(method => method != null) && methods.Count > 30 && ours && blocked == methods.Count,
                $"grown trees: another mod's patch on any of the {methods.Count} methods the count rests on stands it down, " +
                $"this mod's own do not (stood down for {blocked})");
        }
        finally
        {
            YielderSearch.PatchesOn = previous;
            GrownTrees.ResetForTests();
        }
    }

    // Mono compiles a method of under 20 bytes of IL into its callers, where a hook on it would not run.
    private static void Inlining(Action<bool, string> check)
    {
        List<string> small = new List<string>();
        Feature feature = GrownTrees.CreateFeature();
        foreach (PatchSpec patch in feature.Patches)
        {
            int size = patch.Target().GetMethodBody().GetILAsByteArray().Length;
            if (size < 20)
            {
                small.Add($"{patch.Name} ({size} bytes)");
            }
        }
        check(feature.Patches.Count == 11 && small.Count == 0,
            $"grown trees: all {feature.Patches.Count} hooked methods are at least 20 bytes of IL, Mono's inlining limit" +
            (small.Count > 0 ? "; too small: " + string.Join(", ", small) : ""));
    }
}
