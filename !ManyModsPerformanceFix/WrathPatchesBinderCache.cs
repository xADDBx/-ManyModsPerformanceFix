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

internal static class WrathPatchesBinderCache {
    private const int m_CacheVersion = 1;
    private const string m_ScannerAssemblyName = "WrathPatches";
    private const string m_ScannerTypeName = "WrathPatches.UmmModsToGuidClassBinder";

    private class CacheData {
        public int Version = m_CacheVersion;
        public Dictionary<string, CacheEntry> Entries = [];
    }

    private class CacheEntry {
        public int TotalTypes;
        public int[] TypeTokens;
    }

    private static readonly object m_Sync = new();
    private static CacheData m_Cache;
    private static string m_CachePath;
    private static Guid m_OwnerMvid;
    private static bool m_Dirty;
    private static bool m_ScannerPatched;
    private static bool m_TranspilerApplied;

    internal static void Enable() {
        try {
            m_CachePath = Path.Combine(Main.ModEntry.Path, "WrathPatchesBinderCache.json");
            m_Cache = LoadCache();

            var load = AccessTools.Method(typeof(UnityModManager.ModEntry), nameof(UnityModManager.ModEntry.Load), Type.EmptyTypes);
            var loaded = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(ModLoaded));
            if (load == null || loaded == null) {
                throw new MissingMemberException("Could not resolve the WrathPatches binder cache loader hook.");
            }
            Main.HarmonyInstance.Patch(load, postfix: new(loaded) {
                priority = Priority.Last
            });

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                TryPatchScanner(assembly);
            }
        } catch (Exception ex) {
            m_Cache = null;
            Main.Log.Log($"Could not enable the WrathPatches binder cache.\n{ex}");
        }
    }

    private static void ModLoaded(UnityModManager.ModEntry __instance) {
        TryPatchScanner(__instance?.Assembly);
    }

    private static void TryPatchScanner(Assembly assembly) {
        if (assembly == null || m_ScannerPatched || !string.Equals(assembly.GetName().Name, m_ScannerAssemblyName, StringComparison.Ordinal)) {
            return;
        }

        lock (m_Sync) {
            if (m_ScannerPatched || m_Cache == null) {
                return;
            }

            try {
                var type = assembly.GetType(m_ScannerTypeName, false);
                var scanner = AccessTools.Method(type, "Prefix", Type.EmptyTypes);
                var transpiler = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(ScannerTranspiler));
                var save = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(SaveCache));
                if (scanner == null || transpiler == null || save == null) {
                    throw new MissingMemberException("The WrathPatches binder scanner is not supported.");
                }

                m_OwnerMvid = assembly.ManifestModule.ModuleVersionId;
                Main.HarmonyInstance.Patch(scanner,
                    postfix: new(save) {
                        priority = Priority.Last
                    },
                    transpiler: new(transpiler) {
                        priority = Priority.First
                    });
                if (!m_TranspilerApplied) {
                    throw new InvalidOperationException("Could not find the WrathPatches type scan.");
                }
                m_ScannerPatched = true;
            } catch (Exception ex) {
                m_Cache = null;
                Main.Log.Log($"Could not enable the WrathPatches binder cache.\n{ex}");
            }
        }
    }

    private static IEnumerable<CodeInstruction> ScannerTranspiler(IEnumerable<CodeInstruction> instructions) {
        var result = instructions.ToList();
        var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(GetTypeIdTypes));
        var matches = result.Where(instruction => instruction.Calls(getTypes)).ToArray();
        if (matches.Length != 1 || replacement == null) {
            return result;
        }

        matches[0].opcode = OpCodes.Call;
        matches[0].operand = replacement;
        m_TranspilerApplied = true;
        return result;
    }

    private static Type[] GetTypeIdTypes(Assembly assembly) {
        if (m_Cache == null || !AssemblyCacheKey.TryCreate(assembly, m_OwnerMvid, out var module, out var key)) {
            return assembly?.GetTypes() ?? Type.EmptyTypes;
        }

        CacheEntry entry;
        lock (m_Sync) {
            m_Cache.Entries.TryGetValue(key, out entry);
        }
        if (entry != null && TryRestore(assembly, module, entry, out var restored)) {
            return restored;
        }

        var allTypes = assembly.GetTypes();
        var typeIdTypes = new List<Type>();
        try {
            foreach (var type in allTypes) {
                if (type.IsDefined(typeof(TypeIdAttribute), false)) {
                    _ = type.MetadataToken;
                    typeIdTypes.Add(type);
                }
            }
        } catch {
            return allTypes;
        }

        lock (m_Sync) {
            m_Cache.Entries[key] = new CacheEntry {
                TotalTypes = allTypes.Length,
                TypeTokens = typeIdTypes.Select(type => type.MetadataToken).ToArray()
            };
            m_Dirty = true;
        }

        return typeIdTypes.ToArray();
    }

    private static bool TryRestore(Assembly assembly, Module module, CacheEntry entry, out Type[] types) {
        types = null;
        if (entry.TypeTokens == null || entry.TotalTypes < entry.TypeTokens.Length) {
            return false;
        }

        try {
            var restored = new Type[entry.TypeTokens.Length];
            for (int i = 0; i < restored.Length; i++) {
                if (entry.TypeTokens[i] <= 0) {
                    return false;
                }
                var type = module.ResolveType(entry.TypeTokens[i]);
                if (type == null || type.Assembly != assembly || !type.IsDefined(typeof(TypeIdAttribute), false)) {
                    return false;
                }
                restored[i] = type;
            }
            types = restored;
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
            Main.Log.Log($"Could not read the WrathPatches binder cache; rebuilding it.\n{ex}");
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
                Main.Log.Log($"Could not write the WrathPatches binder cache.\n{ex}");
            }
        }
    }
}
