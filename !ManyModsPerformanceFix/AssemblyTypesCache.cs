using HarmonyLib;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ManyModsPerformanceFix;

internal static class AssemblyTypesCache {
    private static readonly ConditionalWeakTable<Assembly, Type[]> m_Cache = new();

    internal static void Enable() {
        try {
            var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
            var prefix = AccessTools.Method(typeof(AssemblyTypesCache), nameof(Prefix));
            var postfix = AccessTools.Method(typeof(AssemblyTypesCache), nameof(Postfix));
            if (getTypes == null || prefix == null || postfix == null) {
                throw new MissingMemberException("Could not resolve the assembly types cache methods.");
            }
            Main.HarmonyInstance.Patch(getTypes, new(prefix) {
                priority = Priority.First
            },
                new(postfix) {
                    priority = Priority.Last
                });
        } catch (Exception ex) {
            Main.Log.Log($"Could not enable the assembly types cache.\n{ex}");
        }
    }

    private static bool Prefix(Assembly __instance, ref Type[] __result, out bool __state) {
        __state = CanCache(__instance);
        if (!__state || !m_Cache.TryGetValue(__instance, out var types)) {
            return true;
        }

        __state = false;
        __result = (Type[])types.Clone();
        return false;
    }

    private static void Postfix(Assembly __instance, Type[] __result, bool __state) {
        if (__state && __result != null) {
            m_Cache.GetValue(__instance, _ => (Type[])__result.Clone());
        }
    }

    internal static bool CanCache(Assembly assembly) {
        return assembly != null && !assembly.IsDynamic && assembly.GetType().Assembly == typeof(Assembly).Assembly;
    }
}
