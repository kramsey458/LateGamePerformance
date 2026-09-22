using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace LateGamePerformance
{
    internal sealed class PatchSpec
    {
        public string Name;
        // A feature whose required patch fails is rolled back entirely, so it can never run half-hooked.
        public bool Required;
        public Func<MethodBase> Target;
        // A prefix that returns bool (or takes ref bool __runOriginal) can skip the target, and Harmony 2.4.1 then
        // skips every later prefix that could change the call: one that returns bool, or takes a ref or out argument,
        // or any argument of a reference type other than __instance, __originalMethod and __state. Each such prefix
        // carries [HarmonyPriority(Priority.Last)] (HarmonyMethod reads it), so other mods' prefixes on the same method
        // run before it whatever order the mods were loaded in; the tests check them all. That holds for this mod's own
        // prefixes too: Diagnostics' terrain A* timer, on the search TerrainSearch replaces, runs first and times it.
        public MethodInfo Prefix;
        public MethodInfo Postfix;
        // Runs whether the target returned or threw; the exception, if any, carries on unchanged.
        public MethodInfo Finalizer;
    }

    internal sealed class Feature
    {
        public string Name;
        public List<PatchSpec> Patches = new List<PatchSpec>();

        public bool Apply(string harmonyIdBase)
        {
            string harmonyId = harmonyIdBase + "." + Name;
            Harmony harmony = new Harmony(harmonyId);
            int optionalFailures = 0;
            foreach (PatchSpec patch in Patches)
            {
                try
                {
                    MethodBase target = patch.Target();
                    if (target == null)
                    {
                        throw new MissingMethodException("target not found");
                    }
                    harmony.Patch(target,
                        patch.Prefix != null ? new HarmonyMethod(patch.Prefix) : null,
                        patch.Postfix != null ? new HarmonyMethod(patch.Postfix) : null,
                        null,
                        patch.Finalizer != null ? new HarmonyMethod(patch.Finalizer) : null);
                }
                catch (Exception exception)
                {
                    if (patch.Required)
                    {
                        Log.Warning($"{Name}: required patch '{patch.Name}' failed ({exception.Message}). " +
                                    "Feature disabled; the game runs unmodified for this part.");
                        try
                        {
                            harmony.UnpatchAll(harmonyId);
                        }
                        catch (Exception unpatchException)
                        {
                            Log.Warning($"{Name}: rollback failed ({unpatchException.Message}).");
                        }
                        return false;
                    }
                    optionalFailures++;
                    Log.Warning($"{Name}: optional patch '{patch.Name}' failed ({exception.Message}).");
                }
            }
            Log.Info($"{Name}: enabled ({Patches.Count - optionalFailures}/{Patches.Count} patches).");
            return true;
        }
    }

    internal static class PatchValidator
    {
        // Dry run used by the test harness: resolves every target against the loaded game assemblies and
        // checks the patch methods only ask for parameters Harmony can supply. Returns problems found.
        public static List<string> Validate(Feature feature)
        {
            List<string> problems = new List<string>();
            foreach (PatchSpec patch in feature.Patches)
            {
                MethodBase target;
                try
                {
                    target = patch.Target();
                }
                catch (Exception exception)
                {
                    problems.Add($"{feature.Name}/{patch.Name}: {exception.Message}");
                    continue;
                }
                if (target == null)
                {
                    problems.Add($"{feature.Name}/{patch.Name}: target not found");
                    continue;
                }
                if (HasExceptionFilter(target))
                {
                    // Harmony cannot regenerate these under Mono ("Incorrect code generation for exception block"),
                    // and the failed attempt leaves an unfinished dynamic type that crashes the game later.
                    problems.Add($"{feature.Name}/{patch.Name}: target has an exception filter (catch ... when)");
                }
                CheckParameters(feature, patch, target, patch.Prefix, problems);
                CheckParameters(feature, patch, target, patch.Postfix, problems);
                CheckParameters(feature, patch, target, patch.Finalizer, problems);
                if (patch.Finalizer != null && patch.Finalizer.ReturnType != typeof(void))
                {
                    // A finalizer that returns an exception replaces the target's; none here may.
                    problems.Add($"{feature.Name}/{patch.Name}: the finalizer must return void");
                }
            }
            return problems;
        }

        public static bool HasExceptionFilter(MethodBase method)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null)
            {
                return false;
            }
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckParameters(Feature feature, PatchSpec patch, MethodBase target, MethodInfo patchMethod,
            List<string> problems)
        {
            if (patchMethod == null)
            {
                return;
            }
            ParameterInfo[] targetParameters = target.GetParameters();
            foreach (ParameterInfo parameter in patchMethod.GetParameters())
            {
                if (parameter.Name == "__instance")
                {
                    if (target.IsStatic)
                    {
                        problems.Add($"{feature.Name}/{patch.Name}: __instance on a static target");
                    }
                    else if (!parameter.ParameterType.IsAssignableFrom(target.DeclaringType))
                    {
                        problems.Add($"{feature.Name}/{patch.Name}: __instance type mismatch");
                    }
                    continue;
                }
                if (parameter.Name == "__state")
                {
                    continue;
                }
                if (parameter.Name == "__result")
                {
                    Type returned = (target as MethodInfo)?.ReturnType;
                    Type asked = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                    if (returned == null || returned == typeof(void) || !asked.IsAssignableFrom(returned))
                    {
                        problems.Add($"{feature.Name}/{patch.Name}: __result does not match the target's return type");
                    }
                    continue;
                }
                ParameterInfo match = Array.Find(targetParameters, candidate => candidate.Name == parameter.Name);
                if (match == null)
                {
                    problems.Add($"{feature.Name}/{patch.Name}: target has no parameter '{parameter.Name}'");
                }
                else if (parameter.ParameterType.IsByRef)
                {
                    // "ref" hands the patch the target's own argument slot: the types must be the same.
                    if (parameter.ParameterType.GetElementType() != match.ParameterType)
                    {
                        problems.Add($"{feature.Name}/{patch.Name}: ref parameter '{parameter.Name}' type mismatch");
                    }
                }
                else if (!parameter.ParameterType.IsAssignableFrom(match.ParameterType))
                {
                    problems.Add($"{feature.Name}/{patch.Name}: parameter '{parameter.Name}' type mismatch");
                }
            }
        }
    }

    internal static class Reflect
    {
        public static Type GameType(string fullName)
        {
            return AccessTools.TypeByName(fullName);
        }

        public static MethodInfo Method(string typeName, string methodName)
        {
            Type type = GameType(typeName);
            return type == null ? null : AccessTools.Method(type, methodName);
        }

        // One overload of a method, told apart by its parameter count; null unless exactly one matches.
        public static MethodInfo Overload(string typeName, string methodName, int parameterCount)
        {
            Type type = GameType(typeName);
            if (type == null)
            {
                return null;
            }
            MethodInfo found = null;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                {
                    if (found != null)
                    {
                        return null;
                    }
                    found = method;
                }
            }
            return found;
        }

        public static MethodInfo Setter(Type type, string propertyName)
        {
            return AccessTools.PropertySetter(type, propertyName);
        }

        public static ConstructorInfo FirstConstructor(string typeName)
        {
            Type type = GameType(typeName);
            if (type == null)
            {
                return null;
            }
            List<ConstructorInfo> constructors = AccessTools.GetDeclaredConstructors(type, false);
            return constructors.Count > 0 ? constructors[0] : null;
        }

        public static MethodInfo Own(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(type.Name, name);
            }
            return method;
        }

        // Compiled accessor for a private field on a type we cannot name at compile time.
        public static Func<object, TField> FieldGetter<TField>(Type declaringType, string fieldName)
        {
            FieldInfo field = AccessTools.Field(declaringType, fieldName);
            if (field == null)
            {
                throw new MissingFieldException(declaringType.Name, fieldName);
            }
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            Expression body = Expression.Convert(
                Expression.Field(Expression.Convert(instance, declaringType), field), typeof(TField));
            return Expression.Lambda<Func<object, TField>>(body, instance).Compile();
        }

        public static Action<object, TField> FieldSetter<TField>(Type declaringType, string fieldName)
        {
            FieldInfo field = AccessTools.Field(declaringType, fieldName);
            if (field == null)
            {
                throw new MissingFieldException(declaringType.Name, fieldName);
            }
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            ParameterExpression value = Expression.Parameter(typeof(TField), "value");
            Expression body = Expression.Assign(
                Expression.Field(Expression.Convert(instance, declaringType), field),
                Expression.Convert(value, field.FieldType));
            return Expression.Lambda<Action<object, TField>>(body, instance, value).Compile();
        }

        // Compiled call to an instance method on a type we cannot name at compile time. Arguments arrive as
        // objects (or exact value types) and are cast to the real parameter types.
        public static TDelegate InstanceCall<TDelegate>(MethodInfo method) where TDelegate : Delegate
        {
            if (method == null)
            {
                throw new MissingMethodException("method not found");
            }
            MethodInfo invoke = typeof(TDelegate).GetMethod("Invoke");
            ParameterInfo[] delegateParameters = invoke.GetParameters();
            ParameterInfo[] methodParameters = method.GetParameters();
            ParameterExpression[] parameters = new ParameterExpression[delegateParameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = Expression.Parameter(delegateParameters[i].ParameterType, "p" + i);
            }
            Expression[] arguments = new Expression[methodParameters.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                arguments[i] = Expression.Convert(parameters[i + 1], methodParameters[i].ParameterType);
            }
            Expression call = Expression.Call(Expression.Convert(parameters[0], method.DeclaringType), method, arguments);
            if (invoke.ReturnType != typeof(void))
            {
                call = Expression.Convert(call, invoke.ReturnType);
            }
            return Expression.Lambda<TDelegate>(call, parameters).Compile();
        }

        public static Func<object, TProperty> PropertyGetter<TProperty>(Type declaringType, string propertyName)
        {
            PropertyInfo property = AccessTools.Property(declaringType, propertyName);
            if (property == null)
            {
                throw new MissingMemberException(declaringType.Name, propertyName);
            }
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            Expression body = Expression.Convert(
                Expression.Property(Expression.Convert(instance, declaringType), property), typeof(TProperty));
            return Expression.Lambda<Func<object, TProperty>>(body, instance).Compile();
        }
    }
}
