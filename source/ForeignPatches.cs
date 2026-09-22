using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LateGamePerformance
{
    // Other mods' patches on game methods whose code a feature's exactness argument was read from. Such a feature
    // stands down when another mod patches one of them (the patch could make the method depend on something that
    // was not read), unless that patch was read as well: listed by target, Harmony id and patch method together.
    // Used by DistrictCounts (0.4.15) and HaulCache (0.4.28); checked once, on the feature's first call in a game,
    // when every mod has patched.
    internal static class ForeignPatches
    {
        // Every patch on a method, as (Harmony id, "Namespace.Type.Method" of the patch); swapped by the tests,
        // where Harmony's patch registry cannot run.
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

        // The first patch by another mod on one of the methods that has not been read, as "Type.Method (owner, patch)",
        // or null. Reviewed ones are listed in `accepted` (may be null).
        internal static string First(IEnumerable<MethodBase> methods, (string Target, string Owner, string Patch)[] reviewed,
            List<string> accepted)
        {
            foreach (MethodBase method in methods)
            {
                IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
                if (patches == null)
                {
                    continue;
                }
                string target = $"{method.DeclaringType?.Name}.{method.Name}";
                foreach ((string owner, string patch) in patches)
                {
                    if (owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (Array.Exists(reviewed, known => known.Target == target && known.Owner == owner && known.Patch == patch))
                    {
                        accepted?.Add($"{target} ({owner}, {patch})");
                        continue;
                    }
                    return $"{target} ({owner}, {patch})";
                }
            }
            return null;
        }
    }
}
