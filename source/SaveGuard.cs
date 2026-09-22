using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Timberborn.Persistence;

namespace LateGamePerformance
{
    // What else could run on a snapshot worker besides the code this mod has read: another mod's Harmony patch.
    // The IL hash of a component's Save (SaveSnapshot.Allowed) tells a game or fork update from the code that
    // was read, but a Harmony patch leaves that IL untouched and runs inside the call all the same, on whichever
    // thread makes it. So before a type is used on a worker, the patches on its Save, on every method the Save reaches
    // through the game's own code (three calls deep, implementations of the interface and virtual methods it
    // calls included) and on every value serializer it loads from a field are read from Harmony's registry, and
    // the shared saving helpers every type goes through (the entity and object savers, the serialized objects
    // they write into, the keys, the common serializers) are checked once per session, for patches and for their
    // IL. A patch by this mod, or one that was read and listed as safe (ReviewedPatches), is fine; any other
    // keeps that type on the main thread, or, on a helper, leaves the whole save to the game, with one log line
    // each. The registry is read again whenever the number of patches changes, so a mod that patches late is
    // seen at the next save. Main thread only.
    //
    // Since 0.4.28 the list of every patched method is taken from Harmony once per judging pass (a pass starts
    // with RegistryChanged: at every save, and at the first tick of a game scene, when SaveSnapshot judges every
    // listed type ahead of the first save) and indexed by name. Before, it was asked for again for every interface
    // or virtual method a Save reached: a few hundred copies of the list of every patched method in the game (262
    // over the listed types with the harness's stand-in registry), each walked in full, at the first save of a
    // session. Only methods with the reached method's name can implement or override it, and the index
    // keeps Harmony's order within a name, so the first match, and with it every verdict and its message, is the
    // same as walking the whole list.
    internal static class SaveGuard
    {
        // The helpers, with the assembly each lives in.
        private static readonly (string Type, string Assembly)[] HelperTypeNames =
        {
            ("Timberborn.WorldPersistence.EntitySaver", "Timberborn.WorldPersistence"),
            ("Timberborn.WorldPersistence.ComponentKey", "Timberborn.WorldPersistence"),
            ("Timberborn.Persistence.ObjectSaver", "Timberborn.Persistence"),
            ("Timberborn.Persistence.ValueSaver", "Timberborn.Persistence"),
            ("Timberborn.Persistence.PropertyKey`1", "Timberborn.Persistence"),
            ("Timberborn.Persistence.ListKey`1", "Timberborn.Persistence"),
            ("Timberborn.Persistence.SaveConversions", "Timberborn.Persistence"),
            ("Timberborn.Persistence.CommonNumberSerializer", "Timberborn.Persistence"),
            ("Timberborn.Persistence.InvariantDateTimeSerializer", "Timberborn.Persistence"),
            ("Timberborn.SerializationSystem.SerializedObject", "Timberborn.SerializationSystem"),
            ("Timberborn.SerializationSystem.PrimitiveTypeSerialization", "Timberborn.SerializationSystem"),
            ("Timberborn.WorldSerialization.SerializedEntity", "Timberborn.WorldSerialization"),
            ("Timberborn.Goods.GoodAmountSerializer", "Timberborn.Goods"),
            ("Timberborn.Goods.GoodRegistryValueSerializer", "Timberborn.Goods"),
            ("Timberborn.Goods.SerializedGoodValueSerializer", "Timberborn.Goods")
        };

        // One hash over the IL of every method of the helpers above, as read (game 1.1.2.4);
        // `dotnet run --project tests -c Release -- --hashes` prints the current one.
        internal const string HelpersHash = "2e1734a92e17be06";

        // Patches by other mods on a listed Save, on something it calls, or on a helper, that were read and are
        // safe on a worker: (target as "Type.Method", Harmony id of the owner, "Namespace.Type.Method" of the patch).
        internal static readonly List<(string Target, string Owner, string Patch)> ReviewedPatches =
            new List<(string Target, string Owner, string Patch)>
            {
                // MixedStorage (kyler.mixedstorage, read at 1.0.0; the patch is the same since 0.5.8): after the
                // game's SingleGoodAllower.Save, writes the storage's allocation into the same entity. It looks the
                // storage's state up in a ConditionalWeakTable (reads are thread-safe), turns its share table into
                // a string and sets one property; it reads nothing shared and writes only into its own entity.
                ("SingleGoodAllower.Save", "kyler.mixedstorage", "MixedStorage.SavePatch.Postfix")
            };

        // Harmony's registry: the patches on a method as (Harmony id, "Namespace.Type.Method" of the patch), every
        // patched method, and the number of patches in all; swapped by the tests, where the Workshop Harmony
        // cannot run.
        internal static Func<MethodBase, IEnumerable<(string Owner, string Patch)>> PatchesOn = HarmonyPatchesOn;
        internal static Func<IEnumerable<MethodBase>> PatchedMethods = Harmony.GetAllPatchedMethods;
        internal static Func<int> PatchCount = HarmonyPatchCount;

        // How far into the game's code a Save is followed from its own calls.
        private const int MaxDepth = 3;
        private const int MaxMethods = 600;

        private static readonly Dictionary<short, OpCode> OpCodeTable = BuildOpCodeTable();
        private static int _lastPatchedCount = -1;
        private static bool _helpersJudged;
        private static string _parkedReason;
        // Every patched method by name, for this judging pass; null until a pass needs it.
        private static Dictionary<string, List<MethodBase>> _patchedByName;

        // For the tests: ask Harmony for the whole list at every reached virtual method, as before 0.4.28, so the
        // verdicts of both ways can be compared.
        internal static bool FetchPatchedMethodsEveryTimeForTests;

        // Called at every save (and before judging ahead of the first one): when the number of patches changed,
        // every verdict is taken again. Either way a new judging pass starts, with a fresh list of patched methods
        // when one is needed.
        internal static bool RegistryChanged()
        {
            _patchedByName = null;
            int count;
            try
            {
                count = PatchCount();
            }
            catch (Exception)
            {
                count = -2;
            }
            if (count == _lastPatchedCount)
            {
                return false;
            }
            _lastPatchedCount = count;
            _helpersJudged = false;
            return true;
        }

        // Why the whole save has to be left to the game, or null: a helper is not the one that was read, or another
        // mod patches one.
        internal static string ParkedReason
        {
            get
            {
                if (!_helpersJudged)
                {
                    try
                    {
                        _parkedReason = JudgeHelpers();
                    }
                    catch (Exception exception)
                    {
                        _parkedReason = "the shared saving helpers could not be checked: " + exception.Message;
                    }
                    _helpersJudged = true;
                }
                return _parkedReason;
            }
        }

        // Why this type has to stay on the main thread although its Save is the one that was read, or null. The
        // Save's calls are followed through the game's code (and the type's own assembly) up to MaxDepth deep; a
        // patched method that implements or overrides an interface or virtual method reached on the way counts too.
        // The shared helpers are judged separately (ParkedReason) and are not followed here.
        internal static string Refusal(Type type)
        {
            MethodInfo save = SaveMethod(type);
            if (save == null)
            {
                return "it has no Save(IEntitySaver)";
            }
            string foreign = ForeignPatchOn(save);
            if (foreign != null)
            {
                return "another mod patches its Save: " + foreign;
            }
            Assembly own = type.Assembly;
            HashSet<MethodBase> seen = new HashSet<MethodBase> { save };
            Queue<(MethodBase Method, int Depth)> queue = new Queue<(MethodBase, int)>();
            string failure = Follow(save, 1, own, seen, queue);
            if (failure != null)
            {
                return failure;
            }
            while (queue.Count > 0)
            {
                (MethodBase method, int depth) = queue.Dequeue();
                foreign = ForeignPatchOn(method);
                if (foreign != null)
                {
                    return $"another mod patches {Describe(method)}, which its Save reaches: {foreign}";
                }
                foreign = ForeignPatchOnImplementations(method, out MethodBase implementation);
                if (foreign != null)
                {
                    return $"another mod patches {Describe(implementation)}, an implementation of {Describe(method)}, which its Save reaches: {foreign}";
                }
                if (depth < MaxDepth && seen.Count < MaxMethods && Followed(method, own))
                {
                    failure = Follow(method, depth + 1, own, seen, queue);
                    if (failure != null)
                    {
                        return failure;
                    }
                }
            }
            return null;
        }

        // Reads what the method calls and queues the calls not seen before; a read that fails is a refusal.
        private static string Follow(MethodBase method, int depth, Assembly own, HashSet<MethodBase> seen,
            Queue<(MethodBase Method, int Depth)> queue)
        {
            List<MethodBase> callees;
            List<Type> serializers;
            try
            {
                Read(method, out callees, out serializers);
            }
            catch (Exception exception)
            {
                return $"the calls of {Describe(method)}, which its Save reaches, could not be read: {exception.Message}";
            }
            foreach (MethodBase callee in callees)
            {
                if (seen.Add(callee))
                {
                    queue.Enqueue((callee, depth));
                }
            }
            foreach (Type serializer in serializers)
            {
                foreach (MethodBase serializerMethod in Methods(serializer))
                {
                    if (seen.Add(serializerMethod))
                    {
                        queue.Enqueue((serializerMethod, depth));
                    }
                }
            }
            return null;
        }

        // Whether a reached method's own calls are followed: game code and the judged type's own assembly, but not
        // the runtime, Unity, or the shared helpers, which are judged on their own.
        private static bool Followed(MethodBase method, Assembly own)
        {
            Type type = method.DeclaringType;
            if (type == null)
            {
                return false;
            }
            string assembly = type.Assembly.GetName().Name ?? "";
            if (type.Assembly != own && !assembly.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                return false;
            }
            string name = (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName;
            foreach ((string helper, _) in HelperTypeNames)
            {
                if (helper == name)
                {
                    return false;
                }
            }
            return true;
        }

        // A reached interface or virtual method: any patched method that implements or overrides it counts as
        // patched too, since the call lands there.
        private static string ForeignPatchOnImplementations(MethodBase method, out MethodBase implementation)
        {
            implementation = null;
            Type declaring = method.DeclaringType;
            if (declaring == null || !(declaring.IsInterface || method.IsVirtual))
            {
                return null;
            }
            ParameterInfo[] parameters = method.GetParameters();
            foreach (MethodBase patched in PatchedNamed(method.Name))
            {
                Type owner = patched.DeclaringType;
                if (owner == null || owner == declaring || patched.Name != method.Name || !declaring.IsAssignableFrom(owner))
                {
                    continue;
                }
                ParameterInfo[] theirs = patched.GetParameters();
                if (theirs.Length != parameters.Length)
                {
                    continue;
                }
                bool same = true;
                for (int i = 0; same && i < parameters.Length; i++)
                {
                    same = theirs[i].ParameterType == parameters[i].ParameterType ||
                           theirs[i].ParameterType.IsGenericParameter || parameters[i].ParameterType.IsGenericParameter;
                }
                if (!same)
                {
                    continue;
                }
                string foreign = ForeignPatchOn(patched);
                if (foreign != null)
                {
                    implementation = patched;
                    return foreign;
                }
            }
            return null;
        }

        // The patched methods with this name, in Harmony's order: from this pass's index, built on first use.
        private static IEnumerable<MethodBase> PatchedNamed(string name)
        {
            if (FetchPatchedMethodsEveryTimeForTests)
            {
                return PatchedMethods() ?? Enumerable.Empty<MethodBase>();
            }
            if (_patchedByName == null)
            {
                Dictionary<string, List<MethodBase>> index = new Dictionary<string, List<MethodBase>>(StringComparer.Ordinal);
                foreach (MethodBase patched in PatchedMethods() ?? Enumerable.Empty<MethodBase>())
                {
                    if (!index.TryGetValue(patched.Name, out List<MethodBase> named))
                    {
                        named = new List<MethodBase>();
                        index[patched.Name] = named;
                    }
                    named.Add(patched);
                }
                _patchedByName = index;
            }
            return _patchedByName.TryGetValue(name, out List<MethodBase> found) ? found : (IEnumerable<MethodBase>)Array.Empty<MethodBase>();
        }

        internal static MethodInfo SaveMethod(Type type)
        {
            return type.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Timberborn.WorldPersistence.IEntitySaver) }, null);
        }

        // The current hash of the helpers, for the harness.
        internal static string HelpersHashNow()
        {
            return HashOf(HelperTypes());
        }

        internal static void ResetForTests()
        {
            _lastPatchedCount = -1;
            _helpersJudged = false;
            _parkedReason = null;
            _patchedByName = null;
        }

        // For the tests: what a Save calls and which serializers it loads.
        internal static void ReadForTests(MethodBase method, out List<MethodBase> callees, out List<Type> serializers)
        {
            Read(method, out callees, out serializers);
        }

        private static string JudgeHelpers()
        {
            List<Type> types = HelperTypes();
            string actual = HashOf(types);
            if (actual != HelpersHash)
            {
                return $"the shared saving helpers are not the ones that were read (they hash to {actual}, the ones read to {HelpersHash})";
            }
            foreach (Type type in types)
            {
                foreach (MethodBase method in Methods(type))
                {
                    string foreign = ForeignPatchOn(method);
                    if (foreign != null)
                    {
                        return $"another mod patches {Describe(method)}, which every snapshot goes through: {foreign}";
                    }
                }
            }
            return null;
        }

        private static List<Type> HelperTypes()
        {
            List<Type> types = new List<Type>();
            foreach ((string name, string assembly) in HelperTypeNames)
            {
                Type type = Type.GetType(name + ", " + assembly, false) ?? Reflect.GameType(name);
                if (type == null)
                {
                    throw new TypeLoadException("the shared saving helper " + name + " was not found");
                }
                types.Add(type);
            }
            return types;
        }

        // Every method and constructor the type declares.
        private static IEnumerable<MethodBase> Methods(Type type)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(any))
            {
                yield return method;
            }
            foreach (ConstructorInfo constructor in type.GetConstructors(any))
            {
                yield return constructor;
            }
        }

        // FNV-1a (64 bits) over the names and IL bytes of every method of the types, in a fixed order.
        private static string HashOf(IEnumerable<Type> types)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(byte[] bytes)
            {
                foreach (byte b in bytes)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            foreach (Type type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                Mix(Encoding.UTF8.GetBytes(type.FullName ?? ""));
                foreach (MethodBase method in Methods(type).OrderBy(method => method.ToString(), StringComparer.Ordinal))
                {
                    Mix(Encoding.UTF8.GetBytes(method.ToString() ?? ""));
                    byte[] il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il != null)
                    {
                        Mix(il);
                    }
                }
            }
            return hash.ToString("x16");
        }

        // The first patch on the method by another mod that is not on the reviewed list, as "(owner, patch)", or null.
        private static string ForeignPatchOn(MethodBase method)
        {
            IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
            if (patches == null && method is MethodInfo info && info.IsGenericMethod && !info.IsGenericMethodDefinition)
            {
                patches = PatchesOn(info.GetGenericMethodDefinition());
            }
            if (patches == null)
            {
                return null;
            }
            string target = Describe(method);
            foreach ((string owner, string patch) in patches)
            {
                if (owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (ReviewedPatches.Exists(reviewed => reviewed.Target == target && reviewed.Owner == owner && reviewed.Patch == patch))
                {
                    continue;
                }
                return $"({owner}, {patch})";
            }
            return null;
        }

        private static string Describe(MethodBase method)
        {
            return $"{method.DeclaringType?.Name}.{method.Name}";
        }

        // Every prefix, postfix, transpiler and finalizer in the process, so that a second patch on an already
        // patched method is a change too.
        private static int HarmonyPatchCount()
        {
            int count = 0;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info != null)
                {
                    count += info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count;
                }
            }
            return count;
        }

        private static IEnumerable<(string Owner, string Patch)> HarmonyPatchesOn(MethodBase method)
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
        }

        // The methods a method calls directly (call, callvirt, newobj, ldftn, ldvirtftn) and the concrete value
        // serializer types it loads from fields, read from its IL.
        private static void Read(MethodBase method, out List<MethodBase> callees, out List<Type> serializers)
        {
            callees = new List<MethodBase>();
            serializers = new List<Type>();
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return;
            }
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments()
                : null;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i++];
                if (value == 0xFE)
                {
                    if (i >= il.Length)
                    {
                        throw new BadImageFormatException("truncated two-byte opcode");
                    }
                    value = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeTable.TryGetValue(value, out OpCode opCode))
                {
                    throw new BadImageFormatException($"unknown opcode {value:x4} at {i - 1}");
                }
                int operandSize;
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        operandSize = 0;
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        operandSize = 1;
                        break;
                    case OperandType.InlineVar:
                        operandSize = 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        operandSize = 8;
                        break;
                    case OperandType.InlineSwitch:
                        operandSize = 4 + 4 * BitConverter.ToInt32(il, i);
                        break;
                    default:
                        operandSize = 4;
                        break;
                }
                if (i + operandSize > il.Length)
                {
                    throw new BadImageFormatException("truncated operand");
                }
                if (opCode.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = module.ResolveMethod(BitConverter.ToInt32(il, i), typeArguments, methodArguments);
                    if (callee != null && !callees.Contains(callee))
                    {
                        callees.Add(callee);
                    }
                }
                else if (opCode.OperandType == OperandType.InlineField)
                {
                    NoteSerializer(module.ResolveField(BitConverter.ToInt32(il, i), typeArguments, methodArguments), serializers);
                }
                else if (opCode.OperandType == OperandType.InlineTok)
                {
                    MemberInfo member = module.ResolveMember(BitConverter.ToInt32(il, i), typeArguments, methodArguments);
                    if (member is MethodBase tokenMethod && !callees.Contains(tokenMethod))
                    {
                        callees.Add(tokenMethod);
                    }
                    else if (member is FieldInfo field)
                    {
                        NoteSerializer(field, serializers);
                    }
                }
                i += operandSize;
            }
        }

        private static void NoteSerializer(FieldInfo field, List<Type> serializers)
        {
            Type type = field?.FieldType;
            if (type == null || type.IsInterface || serializers.Contains(type))
            {
                return;
            }
            foreach (Type implemented in type.GetInterfaces())
            {
                if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IValueSerializer<>))
                {
                    serializers.Add(type);
                    return;
                }
            }
        }

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            Dictionary<short, OpCode> table = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(OpCode))
                {
                    OpCode opCode = (OpCode)field.GetValue(null);
                    table[opCode.Value] = opCode;
                }
            }
            return table;
        }
    }
}
