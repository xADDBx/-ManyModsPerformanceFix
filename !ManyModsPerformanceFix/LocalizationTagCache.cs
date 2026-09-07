using HarmonyLib;
using Kingmaker;
using Kingmaker.Localization;
using Kingmaker.Localization.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ManyModsPerformanceFix;

internal static class LocalizationTagCache {
    private const int m_CacheVersion = 1;

    private class CacheData {
        public int Version = m_CacheVersion;
        public Dictionary<string, Dictionary<string, string>> Entries = [];
    }

    private static readonly object m_Sync = new();
    private static readonly Dictionary<MethodBase, Dictionary<string, string>> m_Cache = [];
    private static readonly Dictionary<MethodBase, string> m_Keys = [];
    private static readonly HashSet<MethodBase> m_Patched = [];
    private static readonly ConditionalWeakTable<object, Dictionary<Locale, LocalizationPack>> m_Packs = new();
    private static CacheData m_Data;
    private static string m_CachePath;
    private static bool m_Dirty;

    internal static void Enable() {
        try {
            m_CachePath = Path.Combine(Main.ModEntry.Path, "LocalizationTagCache.json");
            m_Data = Load();
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Patch(assembly);
            }

            var mainMenuAwake = AccessTools.Method(typeof(MainMenu), "Awake");
            var save = AccessTools.Method(typeof(LocalizationTagCache), nameof(Save));
            Main.HarmonyInstance.Patch(mainMenuAwake, postfix: new(save));
        } catch (Exception ex) {
            Main.Log.Log($"Could not enable the localization cache.\n{ex}");
        }
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) {
        try {
            Patch(args.LoadedAssembly);
        } catch (Exception ex) {
            Main.Log.Log($"Could not cache localization for {args.LoadedAssembly.GetName().Name}.\n{ex}");
        }
    }

    private static void Patch(Assembly assembly) {
        Patch(assembly.GetType("BlueprintCore.Utils.EncyclopediaTool", false));
        Patch(assembly.GetType("TabletopTweaks.Core.Utilities.DescriptionTools", false));
        PatchPack(assembly.GetType("BlueprintCore.Utils.Localization.MultiLocalizationPack", false));
    }

    private static void Patch(Type type) {
        if (type == null) {
            return;
        }

        var method = AccessTools.Method(type, "TagEncyclopediaEntries", [typeof(string)]);
        if (method == null || !method.IsStatic || method.ReturnType != typeof(string)) {
            return;
        }

        var key = AssemblyCacheKey.Create(method.DeclaringType.Assembly, method.Module,
            typeof(LocalizationTagCache).Module.ModuleVersionId) + '|' + method.MetadataToken;
        lock (m_Sync) {
            if (!m_Patched.Add(method)) {
                return;
            }
            if (!m_Data.Entries.TryGetValue(key, out var entries) || entries == null) {
                entries = new(StringComparer.Ordinal);
            }
            m_Cache.Add(method, entries);
            m_Keys.Add(method, key);
        }

        var prefix = AccessTools.Method(typeof(LocalizationTagCache), nameof(Prefix));
        var postfix = AccessTools.Method(typeof(LocalizationTagCache), nameof(Postfix));
        Main.HarmonyInstance.Patch(method, prefix: new(prefix), postfix: new(postfix));
    }

    private static void PatchPack(Type type) {
        if (type == null) {
            return;
        }
        var getPack = AccessTools.Method(type, "GetCurrentPack", Type.EmptyTypes);
        var addStrings = AccessTools.Method(type, "AddStrings");
        if (getPack == null || getPack.IsStatic || getPack.ReturnType != typeof(LocalizationPack)
            || addStrings == null || addStrings.IsStatic) {
            return;
        }
        lock (m_Sync) {
            if (!m_Patched.Add(getPack)) {
                return;
            }
        }

        Main.HarmonyInstance.Patch(addStrings, prefix: new(AccessTools.Method(typeof(LocalizationTagCache), nameof(InvalidatePack))));
        Main.HarmonyInstance.Patch(getPack,
            prefix: new(AccessTools.Method(typeof(LocalizationTagCache), nameof(PackPrefix))),
            postfix: new(AccessTools.Method(typeof(LocalizationTagCache), nameof(PackPostfix))));
    }

    private static bool PackPrefix(object __instance, ref LocalizationPack __result,
        out Dictionary<Locale, LocalizationPack> __state) {
        lock (m_Sync) {
            var packs = m_Packs.GetOrCreateValue(__instance);
            if (packs.TryGetValue(LocalizationManager.CurrentPack.Locale, out __result)) {
                __state = null;
                return false;
            }
            __state = packs;
            return true;
        }
    }

    private static void PackPostfix(LocalizationPack __result, Dictionary<Locale, LocalizationPack> __state) {
        if (__state == null || __result == null) {
            return;
        }
        lock (m_Sync) {
            __state[__result.Locale] = __result;
        }
    }

    private static void InvalidatePack(object __instance) {
        lock (m_Sync) {
            m_Packs.Remove(__instance);
        }
    }

    private static bool Prefix(MethodBase __originalMethod, string __0, ref string __result, out bool __state) {
        lock (m_Sync) {
            if (__0 != null && m_Cache[__originalMethod].TryGetValue(__0, out __result)) {
                __state = false;
                return false;
            }
        }

        __state = true;
        return true;
    }

    private static void Postfix(MethodBase __originalMethod, string __0, string __result, bool __state) {
        if (!__state || __0 == null) {
            return;
        }

        lock (m_Sync) {
            m_Cache[__originalMethod][__0] = __result;
            m_Dirty = true;
        }
    }

    private static CacheData Load() {
        try {
            if (File.Exists(m_CachePath)) {
                var cache = CacheFile.Read<CacheData>(m_CachePath);
                if (cache?.Version == m_CacheVersion && cache.Entries != null) {
                    return cache;
                }
            }
        } catch (Exception ex) {
            Main.Log.Log($"Could not read the localization cache.\n{ex}");
        }
        return new CacheData();
    }

    private static void Save() {
        lock (m_Sync) {
            if (!m_Dirty) {
                return;
            }
            try {
                m_Data.Entries = m_Cache.ToDictionary(pair => m_Keys[pair.Key], pair => pair.Value);
                CacheFile.Write(m_CachePath, m_Data);
                m_Dirty = false;
            } catch (Exception ex) {
                Main.Log.Log($"Could not write the localization cache.\n{ex}");
            }
        }
    }
}
