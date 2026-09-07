using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityModManagerNet;

namespace ManyModsPerformanceFix;

internal static class UmmReloadCache {
    private const int m_CacheVersion = 1;

    private class CacheData {
        public int Version = m_CacheVersion;
        public Dictionary<string, CacheEntry> Entries = [];
    }

    private class CacheEntry {
        public int TotalTypes;
        public int ReloadableTypeToken;
    }

    private static readonly object m_Sync = new();
    private static readonly Guid m_UmmMvid = typeof(UnityModManager).Module.ModuleVersionId;
    private static CacheData m_Cache;
    private static string m_CachePath;
    private static bool m_Dirty;
    private static bool m_Patched;

    internal static void Enable() {
        try {
            m_CachePath = Path.Combine(Main.ModEntry.Path, "UmmReloadCache.json");
            m_Cache = LoadCache();

            var load = AccessTools.Method(typeof(UnityModManager.ModEntry), nameof(UnityModManager.ModEntry.Load), Type.EmptyTypes);
            var transpiler = AccessTools.Method(typeof(UmmReloadCache), nameof(LoadTranspiler));
            var loadPackToc = AccessTools.Method(typeof(StartGameLoader), nameof(StartGameLoader.LoadPackTOC));
            var save = AccessTools.Method(typeof(UmmReloadCache), nameof(SaveCache));
            if (load == null || transpiler == null || loadPackToc == null || save == null) {
                throw new MissingMemberException("Could not resolve the UMM reload cache methods.");
            }

            Main.HarmonyInstance.Patch(load, transpiler: new(transpiler) {
                priority = Priority.First
            });
            if (!m_Patched) {
                throw new InvalidOperationException("Could not find UMM's reloadability scan.");
            }
            Main.HarmonyInstance.Patch(loadPackToc, prefix: new(save) {
                priority = Priority.Last
            });
        } catch (Exception ex) {
            m_Cache = null;
            Main.Log.Log($"Could not enable the UMM reload cache.\n{ex}");
        }
    }

    private static IEnumerable<CodeInstruction> LoadTranspiler(IEnumerable<CodeInstruction> instructions) {
        var result = instructions.ToList();
        var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(UmmReloadCache), nameof(GetReloadableTypes));
        var matches = result.Where(instruction => instruction.Calls(getTypes)).ToArray();
        if (matches.Length != 1 || replacement == null) {
            return result;
        }

        matches[0].opcode = OpCodes.Call;
        matches[0].operand = replacement;
        m_Patched = true;
        return result;
    }

    private static Type[] GetReloadableTypes(Assembly assembly) {
        if (m_Cache == null || !AssemblyCacheKey.TryCreate(assembly, m_UmmMvid, out var module, out var key)) {
            return assembly?.GetTypes() ?? Type.EmptyTypes;
        }

        CacheEntry entry;
        lock (m_Sync) {
            m_Cache.Entries.TryGetValue(key, out entry);
        }
        if (entry != null && TryRestore(assembly, module, entry, out var types)) {
            return types;
        }

        var allTypes = assembly.GetTypes();
        Type reloadableType = null;
        foreach (var type in allTypes) {
            if (type.IsDefined(typeof(EnableReloadingAttribute), true)) {
                reloadableType = type;
                break;
            }
        }

        var token = 0;
        try {
            token = reloadableType?.MetadataToken ?? 0;
        } catch {
        }
        if (reloadableType == null || token != 0) {
            lock (m_Sync) {
                m_Cache.Entries[key] = new CacheEntry {
                    TotalTypes = allTypes.Length,
                    ReloadableTypeToken = token
                };
                m_Dirty = true;
            }
        }

        return reloadableType == null ? Type.EmptyTypes : [reloadableType];
    }

    private static bool TryRestore(Assembly assembly, Module module, CacheEntry entry, out Type[] types) {
        types = null;
        if (entry.TotalTypes < 0 || entry.ReloadableTypeToken < 0) {
            return false;
        }
        if (entry.ReloadableTypeToken == 0) {
            types = Type.EmptyTypes;
            return true;
        }

        try {
            var type = module.ResolveType(entry.ReloadableTypeToken);
            if (type == null || type.Assembly != assembly || !type.IsDefined(typeof(EnableReloadingAttribute), true)) {
                return false;
            }
            types = [type];
            return true;
        } catch {
            return false;
        }
    }

    private static CacheData LoadCache() {
        try {
            if (!File.Exists(m_CachePath)) {
                return new CacheData();
            }
            var cache = CacheFile.Read<CacheData>(m_CachePath);
            if (cache?.Version == m_CacheVersion && cache.Entries != null) {
                return cache;
            }
        } catch (Exception ex) {
            Main.Log.Log($"Could not read the UMM reload cache; rebuilding it.\n{ex}");
        }
        return new CacheData();
    }

    private static void SaveCache() {
        lock (m_Sync) {
            if (!m_Dirty) {
                return;
            }
            try {
                CacheFile.Write(m_CachePath, m_Cache);
                m_Dirty = false;
            } catch (Exception ex) {
                Main.Log.Log($"Could not write the UMM reload cache.\n{ex}");
            }
        }
    }
}
