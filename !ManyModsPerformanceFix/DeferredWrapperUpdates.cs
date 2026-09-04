using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using Kingmaker.Localization;
using Kingmaker.Modding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace ManyModsPerformanceFix;

internal static class DeferredWrapperUpdates {
    private class DeferredMethod {
        internal readonly MethodBase Original;
        internal MethodInfo ActiveReplacement;
        internal bool Dirty;
        internal int SkippedUpdates;

        internal DeferredMethod(MethodBase original) {
            Original = original;
        }
    }

    private static readonly Dictionary<MethodBase, DeferredMethod> m_Methods = [];
    private static MethodInfo m_UpdateWrapper;
    private static MethodInfo m_GetPatchInfo;
    private static MethodInfo m_UpdatePatchInfo;
    private static object m_HarmonyLock;
    private static bool m_Enabled;
    private static bool m_Flushing;

    internal static void Enable() {
        try {
            var blueprintInit = AccessTools.Method(typeof(BlueprintsCache), nameof(BlueprintsCache.Init));
            var loadPackToc = AccessTools.Method(typeof(StartGameLoader), nameof(StartGameLoader.LoadPackTOC));
            var localeChanged = AccessTools.Method(typeof(LocalizationManager), "OnLocaleChanged", Type.EmptyTypes);
            var modsLoaded = AccessTools.Method(typeof(OwlcatModificationsManager), nameof(OwlcatModificationsManager.Start));
            var patchFunctions = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions", true);
            var sharedState = typeof(Harmony).Assembly.GetType("HarmonyLib.HarmonySharedState", true);

            m_UpdateWrapper = AccessTools.Method(patchFunctions, "UpdateWrapper");
            m_GetPatchInfo = AccessTools.Method(sharedState, "GetPatchInfo");
            m_UpdatePatchInfo = AccessTools.Method(sharedState, "UpdatePatchInfo");
            m_HarmonyLock = AccessTools.Field(typeof(PatchProcessor), "locker").GetValue(null);

            if (blueprintInit == null || loadPackToc == null || localeChanged == null || modsLoaded == null
                || m_UpdateWrapper == null || m_GetPatchInfo == null || m_UpdatePatchInfo == null || m_HarmonyLock == null) {
                throw new MissingMemberException("Could not resolve the Harmony wrapper batching methods.");
            }

            m_Methods.Add(blueprintInit, new(blueprintInit));
            m_Methods.Add(loadPackToc, new(loadPackToc));
            m_Methods.Add(localeChanged, new(localeChanged));

            var flush = AccessTools.Method(typeof(DeferredWrapperUpdates), nameof(Flush));
            Main.HarmonyInstance.Patch(modsLoaded, postfix: new(flush) {
                priority = Priority.Last
            });

            var prefix = AccessTools.Method(typeof(DeferredWrapperUpdates), nameof(UpdateWrapperPrefix));
            var postfix = AccessTools.Method(typeof(DeferredWrapperUpdates), nameof(UpdateWrapperPostfix));
            Main.HarmonyInstance.Patch(m_UpdateWrapper, new(prefix), new(postfix));

            m_Enabled = true;
        } catch (Exception ex) {
            m_Enabled = false;
            Main.Log.Log($"Could not enable deferred Harmony wrapper updates.\n{ex}");
        }
    }

    private static bool UpdateWrapperPrefix(MethodBase original, ref MethodInfo __result) {
        if (!m_Enabled || m_Flushing || !m_Methods.TryGetValue(original, out var method) || method.ActiveReplacement == null) {
            return true;
        }

        method.Dirty = true;
        method.SkippedUpdates++;
        __result = method.ActiveReplacement;
        return false;
    }

    private static void UpdateWrapperPostfix(MethodBase original, MethodInfo __result) {
        if (m_Enabled && m_Methods.TryGetValue(original, out var method) && method.ActiveReplacement == null) {
            method.ActiveReplacement = __result;
        }
    }

    private static void Flush() {
        if (!m_Enabled) {
            return;
        }

        lock (m_HarmonyLock) {
            if (!m_Enabled) {
                return;
            }

            var timer = Stopwatch.StartNew();
            var compiled = 0;
            var skipped = 0;
            m_Flushing = true;
            try {
                foreach (var method in m_Methods.Values) {
                    skipped += method.SkippedUpdates;
                    if (!method.Dirty) {
                        continue;
                    }

                    var patchInfo = m_GetPatchInfo.Invoke(null, [method.Original]);
                    var replacement = (MethodInfo)m_UpdateWrapper.Invoke(null, [method.Original, patchInfo]);
                    m_UpdatePatchInfo.Invoke(null, [method.Original, replacement, patchInfo]);
                    method.ActiveReplacement = replacement;
                    method.Dirty = false;
                    compiled++;
                }

                m_Enabled = false;
                if (skipped > 0) {
                    Main.Log.Log($"Deferred {skipped} Harmony wrapper updates and compiled {compiled} final wrapper(s) in {timer.ElapsedMilliseconds} ms.");
                }
            } catch (Exception ex) {
                m_Enabled = false;
                Main.Log.Log($"Failed to compile deferred Harmony wrappers.\n{ex}");
                throw;
            } finally {
                m_Flushing = false;
            }
        }
    }
}
