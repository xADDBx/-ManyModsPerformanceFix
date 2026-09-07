using HarmonyLib;
using Owlcat.Runtime.Core.Updatables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine.Rendering;

namespace ManyModsPerformanceFix;

internal static class RuntimeTypeDiscoveryCache {
    private const int m_CacheVersion = 2;

    private class CacheData {
        public int Version = m_CacheVersion;
        public Dictionary<string, CacheEntry> Updates = [];
        public Dictionary<string, CacheEntry> Volumes = [];
    }

    private class CacheEntry {
        public int[] Updatable;
        public int[] LateUpdatable;
        public int[] Volumes;
    }

    private class RuntimeEntry {
        public Type[] Updatable;
        public Type[] LateUpdatable;
        public Type[] Volumes;
    }

    private static readonly object m_Sync = new();
    private static readonly Dictionary<Assembly, RuntimeEntry> m_Runtime = [];
    private static CacheData m_Cache;
    private static string m_CachePath;
    private static bool m_Dirty;

    internal static void Enable() {
        try {
            m_CachePath = Path.Combine(Main.ModEntry.Path, "RuntimeTypeDiscoveryCache.json");
            m_Cache = LoadCache();

            var updateCaller = AccessTools.Method(typeof(UpdateCaller), "OnAfterFirstSceneLoaded");
            var reloadBaseTypes = AccessTools.Method(typeof(VolumeManager), "ReloadBaseTypes");
            var updateTranspiler = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(UpdateTranspiler));
            var volumeTranspiler = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(VolumeTranspiler));
            var save = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(Save));
            if (updateCaller == null || reloadBaseTypes == null || updateTranspiler == null
                || volumeTranspiler == null || save == null) {
                throw new MissingMemberException("Could not resolve the runtime type discovery methods.");
            }

            Main.HarmonyInstance.Patch(updateCaller, postfix: new(save), transpiler: new(updateTranspiler));
            Main.HarmonyInstance.Patch(reloadBaseTypes, postfix: new(save), transpiler: new(volumeTranspiler));
        } catch (Exception ex) {
            m_Cache = null;
            Main.Log.Log($"Could not enable the runtime type discovery cache.\n{ex}");
        }
    }

    private static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
        var result = instructions.ToList();
        var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
        var getUpdatable = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(GetUpdatableTypes));
        var getLateUpdatable = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(GetLateUpdatableTypes));
        var calls = result.Where(instruction => instruction.Calls(getTypes)).ToList();
        if (calls.Count != 2 || getUpdatable == null || getLateUpdatable == null) {
            Main.Log.Log("Runtime type discovery cache: UpdateCaller layout is not supported.");
            return result;
        }

        calls[0].opcode = OpCodes.Call;
        calls[0].operand = getUpdatable;
        calls[1].opcode = OpCodes.Call;
        calls[1].operand = getLateUpdatable;
        return result;
    }

    private static IEnumerable<CodeInstruction> VolumeTranspiler(IEnumerable<CodeInstruction> instructions) {
        var result = instructions.ToList();
        var replacement = AccessTools.Method(typeof(RuntimeTypeDiscoveryCache), nameof(GetVolumeTypes));
        var calls = result.Where(instruction => IsVolumeTypeQuery(instruction.operand as MethodInfo)).ToList();
        if (calls.Count != 1 || replacement == null) {
            Main.Log.Log("Runtime type discovery cache: VolumeManager layout is not supported.");
            return result;
        }

        calls[0].opcode = OpCodes.Call;
        calls[0].operand = replacement;
        return result;
    }

    private static bool IsVolumeTypeQuery(MethodInfo method) {
        if (method == null || method.DeclaringType != typeof(CoreUtils)
            || method.Name != nameof(CoreUtils.GetAllTypesDerivedFrom) || !method.IsGenericMethod) {
            return false;
        }
        var arguments = method.GetGenericArguments();
        return arguments.Length == 1 && arguments[0] == typeof(VolumeComponent);
    }

    private static Type[] GetUpdatableTypes(Assembly assembly) {
        return GetUpdateEntry(assembly).Updatable;
    }

    private static Type[] GetLateUpdatableTypes(Assembly assembly) {
        return GetUpdateEntry(assembly).LateUpdatable;
    }

    private static RuntimeEntry GetUpdateEntry(Assembly assembly) {
        if (!AssemblyTypesCache.CanCache(assembly)) {
            return FindUpdateTypes(assembly.GetTypes());
        }
        lock (m_Sync) {
            if (m_Runtime.TryGetValue(assembly, out var runtime) && runtime.Updatable != null) {
                return runtime;
            }

            runtime ??= new RuntimeEntry();
            if (TryRestoreUpdate(assembly, runtime)) {
                m_Runtime[assembly] = runtime;
                return runtime;
            }

            var types = assembly.GetTypes();
            runtime = FindUpdateTypes(types);
            m_Runtime[assembly] = runtime;
            StoreUpdate(assembly, runtime);
            StoreVolumes(assembly, runtime.Volumes);
            return runtime;
        }
    }

    private static RuntimeEntry FindUpdateTypes(Type[] types) {
        var updatable = new List<Type>();
        var lateUpdatable = new List<Type>();
        var volumes = new List<Type>();
        foreach (var type in types) {
            if (IsUpdatable(type)) {
                updatable.Add(type);
            }
            if (IsLateUpdatable(type)) {
                lateUpdatable.Add(type);
            }
            if (IsVolume(type)) {
                volumes.Add(type);
            }
        }
        return new RuntimeEntry {
            Updatable = updatable.ToArray(),
            LateUpdatable = lateUpdatable.ToArray(),
            Volumes = volumes.ToArray()
        };
    }

    private static IEnumerable<Type> GetVolumeTypes() {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            Type[] types;
            try {
                types = GetVolumeEntry(assembly);
            } catch {
                continue;
            }
            foreach (var type in types) {
                yield return type;
            }
        }
    }

    private static Type[] GetVolumeEntry(Assembly assembly) {
        if (!AssemblyTypesCache.CanCache(assembly)) {
            return assembly.GetTypes().Where(IsVolume).ToArray();
        }
        lock (m_Sync) {
            if (m_Runtime.TryGetValue(assembly, out var runtime) && runtime.Volumes != null) {
                return runtime.Volumes;
            }

            runtime ??= new RuntimeEntry();
            if (TryRestoreVolumes(assembly, runtime)) {
                m_Runtime[assembly] = runtime;
                return runtime.Volumes;
            }

            var types = assembly.GetTypes();
            runtime.Volumes = types.Where(IsVolume).ToArray();
            m_Runtime[assembly] = runtime;
            StoreVolumes(assembly, runtime.Volumes);
            return runtime.Volumes;
        }
    }

    private static bool TryRestoreUpdate(Assembly assembly, RuntimeEntry runtime) {
        if (!TryGetCacheEntry(assembly, typeof(UpdateCaller).Module.ModuleVersionId, m_Cache?.Updates, out var entry, out var module)
            || !TryResolveTypes(module, entry.Updatable, IsUpdatable, out runtime.Updatable)
            || !TryResolveTypes(module, entry.LateUpdatable, IsLateUpdatable, out runtime.LateUpdatable)) {
            runtime.Updatable = null;
            runtime.LateUpdatable = null;
            return false;
        }

        return true;
    }

    private static bool TryRestoreVolumes(Assembly assembly, RuntimeEntry runtime) {
        if (!TryGetCacheEntry(assembly, typeof(VolumeComponent).Module.ModuleVersionId, m_Cache?.Volumes, out var entry, out var module)
            || !TryResolveTypes(module, entry.Volumes, IsVolume, out runtime.Volumes)) {
            runtime.Volumes = null;
            return false;
        }

        return true;
    }

    private static bool TryGetCacheEntry(Assembly assembly, Guid ownerMvid, Dictionary<string, CacheEntry> entries,
        out CacheEntry entry, out Module module) {
        entry = null;
        module = null;
        if (entries == null || !AssemblyCacheKey.TryCreate(assembly, ownerMvid, out module, out var key)) {
            return false;
        }
        return entries.TryGetValue(key, out entry) && entry != null;
    }

    private static bool TryResolveTypes(Module module, int[] tokens, Func<Type, bool> predicate, out Type[] types) {
        types = null;
        if (tokens == null) {
            return false;
        }

        var result = new Type[tokens.Length];
        try {
            for (var i = 0; i < tokens.Length; i++) {
                var type = module.ResolveType(tokens[i]);
                if (type.Module != module || !predicate(type)) {
                    return false;
                }
                result[i] = type;
            }
        } catch {
            return false;
        }

        types = result;
        return true;
    }

    private static bool IsUpdatable(Type type) {
        return type != typeof(Owlcat.Runtime.Core.Updatables.IUpdatable)
            && typeof(Owlcat.Runtime.Core.Updatables.IUpdatable).IsAssignableFrom(type);
    }

    private static bool IsLateUpdatable(Type type) {
        return type != typeof(ILateUpdatable) && typeof(ILateUpdatable).IsAssignableFrom(type);
    }

    private static bool IsVolume(Type type) {
        return type.IsSubclassOf(typeof(VolumeComponent));
    }

    private static void StoreUpdate(Assembly assembly, RuntimeEntry runtime) {
        if (m_Cache == null || !AssemblyCacheKey.TryCreate(assembly, typeof(UpdateCaller).Module.ModuleVersionId, out _, out var key)) {
            return;
        }
        m_Cache.Updates[key] = new CacheEntry {
            Updatable = runtime.Updatable.Select(type => type.MetadataToken).ToArray(),
            LateUpdatable = runtime.LateUpdatable.Select(type => type.MetadataToken).ToArray()
        };
        m_Dirty = true;
    }

    private static void StoreVolumes(Assembly assembly, Type[] types) {
        if (m_Cache == null || !AssemblyCacheKey.TryCreate(assembly, typeof(VolumeComponent).Module.ModuleVersionId, out _, out var key)) {
            return;
        }
        m_Cache.Volumes[key] = new CacheEntry {
            Volumes = types.Select(type => type.MetadataToken).ToArray()
        };
        m_Dirty = true;
    }

    private static CacheData LoadCache() {
        try {
            if (!File.Exists(m_CachePath)) {
                return new CacheData();
            }
            var cache = CacheFile.Read<CacheData>(m_CachePath);
            if (cache?.Version == m_CacheVersion && cache.Updates != null && cache.Volumes != null) {
                return cache;
            }
        } catch (Exception ex) {
            Main.Log.Log($"Could not read the runtime type discovery cache.\n{ex}");
        }
        return new CacheData();
    }

    private static void Save() {
        lock (m_Sync) {
            if (!m_Dirty) {
                return;
            }
            try {
                CacheFile.Write(m_CachePath, m_Cache);
                m_Dirty = false;
            } catch (Exception ex) {
                Main.Log.Log($"Could not write the runtime type discovery cache.\n{ex}");
            }
        }
    }
}
