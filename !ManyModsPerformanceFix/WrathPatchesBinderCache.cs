using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
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
    private static int m_Dirty;
    private static int m_Hits;
    private static int m_Misses;
    private static int m_RestoredTypes;
    private static int m_SkippedTypes;
    private static int m_Reported;
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
                var measure = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(Measure));
                var report = AccessTools.Method(typeof(WrathPatchesBinderCache), nameof(Report));
                if (scanner == null || transpiler == null || measure == null || report == null) {
                    throw new MissingMemberException("The WrathPatches binder scanner is not supported.");
                }

                m_OwnerMvid = assembly.ManifestModule.ModuleVersionId;
                Main.HarmonyInstance.Patch(scanner,
                    prefix: new(measure) {
                        priority = Priority.First
                    },
                    postfix: new(report) {
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
        if (assembly == null || assembly.IsDynamic || m_Cache == null) {
            return assembly?.GetTypes() ?? Type.EmptyTypes;
        }

        Module module;
        string key;
        try {
            if (assembly.GetModules().Length != 1) {
                return assembly.GetTypes();
            }
            module = assembly.ManifestModule;
            key = AssemblyCacheKey.Create(assembly, module, m_OwnerMvid);
        } catch {
            return assembly.GetTypes();
        }

        CacheEntry entry;
        lock (m_Sync) {
            m_Cache.Entries.TryGetValue(key, out entry);
        }
        if (entry != null && TryRestore(assembly, module, entry, out var restored)) {
            Interlocked.Increment(ref m_Hits);
            Interlocked.Add(ref m_RestoredTypes, restored.Length);
            Interlocked.Add(ref m_SkippedTypes, entry.TotalTypes - restored.Length);
            return restored;
        }

        var allTypes = assembly.GetTypes();
        var typeIdTypes = new List<Type>();
        try {
            foreach (var type in allTypes) {
                if (HasTypeId(type)) {
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
            m_Dirty = 1;
        }
        Interlocked.Increment(ref m_Misses);
        return typeIdTypes.ToArray();
    }

    private static bool HasTypeId(Type type) {
        foreach (var attribute in CustomAttributeData.GetCustomAttributes(type)) {
            if (typeof(TypeIdAttribute).IsAssignableFrom(attribute.AttributeType)) {
                return true;
            }
        }
        return false;
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
            var cache = JsonConvert.DeserializeObject<CacheData>(File.ReadAllText(m_CachePath));
            if (cache?.Version == m_CacheVersion && cache.Entries != null) {
                return cache;
            }
        } catch (Exception ex) {
            Main.Log.Log($"Could not read the WrathPatches binder cache; rebuilding it.\n{ex}");
        }
        return new CacheData();
    }

    private static void SaveCache() {
        try {
            CacheData cache;
            lock (m_Sync) {
                if (m_Dirty == 0) {
                    return;
                }
                m_Dirty = 0;
                cache = m_Cache;
            }
            File.WriteAllText(m_CachePath, JsonConvert.SerializeObject(cache));
        } catch (Exception ex) {
            Interlocked.Exchange(ref m_Dirty, 1);
            Main.Log.Log($"Could not write the WrathPatches binder cache.\n{ex}");
        }
    }

    private static void Measure(out long __state) {
        __state = Stopwatch.GetTimestamp();
    }

    private static void Report(long __state) {
        if (Interlocked.Exchange(ref m_Reported, 1) != 0) {
            return;
        }
        SaveCache();
        if (m_Hits + m_Misses != 0) {
            var milliseconds = (Stopwatch.GetTimestamp() - __state) * 1000 / Stopwatch.Frequency;
            Main.Log.Log($"WrathPatches binder cache: {m_Hits} hit(s), {m_Misses} miss(es), restored {m_RestoredTypes} TypeId type(s), skipped {m_SkippedTypes} type check(s) in {milliseconds} ms.");
        }
    }
}
