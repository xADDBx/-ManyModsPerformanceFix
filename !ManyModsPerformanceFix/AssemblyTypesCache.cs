using HarmonyLib;
using Kingmaker.Modding;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ManyModsPerformanceFix;

internal static class AssemblyTypesCache {
    private static readonly ConditionalWeakTable<Assembly, Type[]> m_Cache = new();
    private static int m_Hits;
    private static int m_Misses;
    private static int m_ReusedTypes;

    internal static void Enable() {
        try {
            var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
            var start = AccessTools.Method(typeof(OwlcatModificationsManager), nameof(OwlcatModificationsManager.Start));
            var prefix = AccessTools.Method(typeof(AssemblyTypesCache), nameof(Prefix));
            var postfix = AccessTools.Method(typeof(AssemblyTypesCache), nameof(Postfix));
            var report = AccessTools.Method(typeof(AssemblyTypesCache), nameof(Report));
            if (getTypes == null || start == null || prefix == null || postfix == null || report == null) {
                throw new MissingMemberException("Could not resolve the assembly types cache methods.");
            }
            Main.HarmonyInstance.Patch(getTypes, new(prefix) {
                priority = Priority.First
            },
                new(postfix) {
                    priority = Priority.Last
                });
            Main.HarmonyInstance.Patch(start, prefix: new(report) {
                priority = Priority.First
            });
        } catch (Exception ex) {
            Main.Log.Log($"Could not enable the assembly types cache.\n{ex}");
        }
    }

    private static bool Prefix(Assembly __instance, ref Type[] __result, out bool __state) {
        __state = false;
        if (!CanCache(__instance) || !m_Cache.TryGetValue(__instance, out var types)) {
            Interlocked.Increment(ref m_Misses);
            __state = true;
            return true;
        }

        Interlocked.Increment(ref m_Hits);
        Interlocked.Add(ref m_ReusedTypes, types.Length);
        __result = (Type[])types.Clone();
        return false;
    }

    private static void Postfix(Assembly __instance, Type[] __result, bool __state) {
        if (__state && CanCache(__instance) && __result != null) {
            m_Cache.GetValue(__instance, _ => (Type[])__result.Clone());
        }
    }

    private static bool CanCache(Assembly assembly) {
        return assembly != null && !assembly.IsDynamic && assembly.GetType().Assembly == typeof(Assembly).Assembly;
    }

    private static void Report() {
        if (m_Hits + m_Misses != 0) {
            Main.Log.Log($"Assembly types cache: {m_Hits} hit(s), {m_Misses} miss(es), reused {m_ReusedTypes} type result(s).");
        }
    }
}
