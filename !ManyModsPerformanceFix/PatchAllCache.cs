using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace ManyModsPerformanceFix;

internal static class PatchAllCache {
    private const int m_CacheVersion = 4;

    private class CacheData {
        public int Version = m_CacheVersion;
        public Dictionary<string, CacheEntry> Entries = [];
    }

    private class CacheEntry {
        public int TotalTypes;
        public PatchClassData[] PatchClasses;
    }

    private class PatchClassData {
        public int TypeToken;
        public HarmonyMethodData Container;
        public AuxiliaryMethodData[] AuxiliaryMethods;
        public AttributePatchData[] PatchMethods;
    }

    private class AuxiliaryMethodData {
        public int Kind;
        public MethodData Method;
    }

    private class AttributePatchData {
        public int? Type;
        public HarmonyMethodData Info;
    }

    private class HarmonyMethodData {
        public MethodData Method;
        public string Category;
        public TypeData DeclaringType;
        public string MethodName;
        public int? MethodType;
        public TypeData[] ArgumentTypes;
        public int Priority;
        public string[] Before;
        public string[] After;
        public int? ReversePatchType;
        public bool? Debug;
        public bool NonVirtualDelegate;
    }

    private class MethodData {
        public Guid Module;
        public int Token;
    }

    private class TypeData {
        public int Kind;
        public Guid Module;
        public int Token;
        public TypeData Element;
        public TypeData[] Arguments;
        public int Rank;
        public bool Vector;
    }

    private class ModuleResolver {
        private readonly Dictionary<Guid, Module> m_Modules = [];

        internal ModuleResolver() {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Module[] modules;
                try {
                    modules = assembly.GetModules();
                } catch {
                    continue;
                }
                foreach (var module in modules) {
                    m_Modules[module.ModuleVersionId] = module;
                }
            }
        }

        internal bool TryGet(Guid module, out Module result) {
            return m_Modules.TryGetValue(module, out result);
        }
    }

    private static readonly object m_Sync = new();
    private static readonly Guid m_HarmonyMvid = typeof(Harmony).Module.ModuleVersionId;
    private static readonly Type[] m_AuxiliaryTypes = [
        typeof(HarmonyPrepare),
        typeof(HarmonyCleanup),
        typeof(HarmonyTargetMethod),
        typeof(HarmonyTargetMethods)
    ];

    private static Type m_AttributePatchType;
    private static FieldInfo m_ProcessorInstance;
    private static FieldInfo m_ProcessorContainerType;
    private static FieldInfo m_ProcessorContainerAttributes;
    private static FieldInfo m_ProcessorAuxiliaryMethods;
    private static FieldInfo m_ProcessorPatchMethods;
    private static PropertyInfo m_ProcessorCategory;
    private static FieldInfo m_AttributePatchInfo;
    private static FieldInfo m_AttributePatchKind;
    private static CacheData m_Cache;
    private static string m_CachePath;
    private static int m_Dirty;
    private static int m_Hits;
    private static int m_Misses;
    private static int m_RestoredClasses;
    private static int m_ReflectedClasses;
    private static int m_SkippedTypes;
    private static int m_Reported;

    internal static void Enable() {
        try {
            ResolveHarmonyInternals();
            m_CachePath = Path.Combine(Main.ModEntry.Path, "PatchAllCache.json");
            m_Cache = LoadCache();

            var patchAll = AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), [typeof(Assembly)]);
            var prefix = AccessTools.Method(typeof(PatchAllCache), nameof(PatchAllPrefix));
            var loadPackToc = AccessTools.Method(typeof(StartGameLoader), nameof(StartGameLoader.LoadPackTOC));
            var report = AccessTools.Method(typeof(PatchAllCache), nameof(Report));
            if (patchAll == null || prefix == null || loadPackToc == null || report == null) {
                throw new MissingMemberException("Could not resolve the Harmony PatchAll cache methods.");
            }

            Main.HarmonyInstance.Patch(loadPackToc, prefix: new(report) {
                priority = Priority.Last
            });
            Main.HarmonyInstance.Patch(patchAll, prefix: new(prefix) {
                priority = Priority.First
            });
        } catch (Exception ex) {
            m_Cache = null;
            Main.Log.Log($"Could not enable the Harmony PatchAll cache.\n{ex}");
        }
    }

    private static void ResolveHarmonyInternals() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var processorType = typeof(PatchClassProcessor);
        m_AttributePatchType = typeof(Harmony).Assembly.GetType("HarmonyLib.AttributePatch", true);
        m_ProcessorInstance = processorType.GetField("instance", flags);
        m_ProcessorContainerType = processorType.GetField("containerType", flags);
        m_ProcessorContainerAttributes = processorType.GetField("containerAttributes", flags);
        m_ProcessorAuxiliaryMethods = processorType.GetField("auxilaryMethods", flags);
        m_ProcessorPatchMethods = processorType.GetField("patchMethods", flags);
        m_ProcessorCategory = processorType.GetProperty("Category", flags);
        m_AttributePatchInfo = m_AttributePatchType.GetField("info", flags);
        m_AttributePatchKind = m_AttributePatchType.GetField("type", flags);
        if (m_ProcessorInstance == null || m_ProcessorContainerType == null || m_ProcessorContainerAttributes == null
            || m_ProcessorAuxiliaryMethods == null || m_ProcessorPatchMethods == null || m_ProcessorCategory == null
            || m_AttributePatchInfo == null || m_AttributePatchKind == null) {
            throw new MissingMemberException("Harmony's patch class processor layout is not supported.");
        }
    }

    private static bool PatchAllPrefix(Harmony __instance, Assembly assembly) {
        if (assembly == null || assembly.IsDynamic || m_Cache == null) {
            return true;
        }

        Module module;
        string key;
        try {
            if (assembly.GetModules().Length != 1) {
                return true;
            }
            module = assembly.ManifestModule;
            key = AssemblyCacheKey.Create(assembly, module, m_HarmonyMvid);
        } catch {
            return true;
        }
        CacheEntry entry;
        lock (m_Sync) {
            m_Cache.Entries.TryGetValue(key, out entry);
        }

        if (entry != null && TryRestoreProcessors(__instance, assembly, module, entry, out var processors, out var restored)) {
            Interlocked.Increment(ref m_Hits);
            Interlocked.Add(ref m_RestoredClasses, restored);
            Interlocked.Add(ref m_ReflectedClasses, processors.Length - restored);
            Interlocked.Add(ref m_SkippedTypes, entry.TotalTypes - processors.Length);
            foreach (var processor in processors) {
                processor.Patch();
            }
            return false;
        }

        Type[] types;
        try {
            types = AccessTools.GetTypesFromAssembly(assembly).ToArray();
        } catch {
            return true;
        }

        var patchClasses = new List<PatchClassData>();
        foreach (var type in types) {
            if (HarmonyMethodExtensions.GetFromType(type).Count == 0) {
                continue;
            }

            var processor = __instance.CreateClassProcessor(type);
            processor.Patch();

            PatchClassData data = null;
            try {
                data = CaptureProcessor(type, processor);
            } catch {
            }
            patchClasses.Add(data ?? new PatchClassData { TypeToken = type.MetadataToken });
        }

        Interlocked.Increment(ref m_Misses);
        Interlocked.Add(ref m_ReflectedClasses, patchClasses.Count);
        lock (m_Sync) {
            m_Cache.Entries[key] = new CacheEntry {
                TotalTypes = types.Length,
                PatchClasses = patchClasses.ToArray()
            };
            m_Dirty = 1;
        }
        return false;
    }

    private static PatchClassData CaptureProcessor(Type containerType, PatchClassProcessor processor) {
        if (HasCustomHarmonyAttributes(containerType, processor)) {
            return null;
        }

        var container = (HarmonyMethod)m_ProcessorContainerAttributes.GetValue(processor);
        var auxiliary = (IDictionary)m_ProcessorAuxiliaryMethods.GetValue(processor);
        var patchMethods = (IEnumerable)m_ProcessorPatchMethods.GetValue(processor);
        if (container == null || auxiliary == null || patchMethods == null) {
            return null;
        }

        var auxiliaryData = new List<AuxiliaryMethodData>();
        foreach (DictionaryEntry item in auxiliary) {
            var kind = Array.IndexOf(m_AuxiliaryTypes, (Type)item.Key);
            var method = CaptureMethod((MethodInfo)item.Value);
            if (kind < 0 || method == null) {
                return null;
            }
            auxiliaryData.Add(new AuxiliaryMethodData { Kind = kind, Method = method });
        }

        var patchData = new List<AttributePatchData>();
        foreach (var patch in patchMethods) {
            var info = (HarmonyMethod)m_AttributePatchInfo.GetValue(patch);
            var type = (HarmonyPatchType?)m_AttributePatchKind.GetValue(patch);
            var capturedInfo = CaptureHarmonyMethod(info);
            if (capturedInfo == null) {
                return null;
            }
            patchData.Add(new AttributePatchData {
                Type = type.HasValue ? (int?)type.Value : null,
                Info = capturedInfo
            });
        }

        var capturedContainer = CaptureHarmonyMethod(container);
        if (capturedContainer == null) {
            return null;
        }
        return new PatchClassData {
            TypeToken = containerType.MetadataToken,
            Container = capturedContainer,
            AuxiliaryMethods = auxiliaryData.ToArray(),
            PatchMethods = patchData.ToArray()
        };
    }

    private static bool HasCustomHarmonyAttributes(Type containerType, PatchClassProcessor processor) {
        for (var type = containerType; type != null; type = type.BaseType) {
            if (HasCustomHarmonyAttributes(CustomAttributeData.GetCustomAttributes(type))) {
                return true;
            }
        }

        var patchMethods = (IEnumerable)m_ProcessorPatchMethods.GetValue(processor);
        foreach (var patch in patchMethods) {
            var info = (HarmonyMethod)m_AttributePatchInfo.GetValue(patch);
            if (info?.method != null && HasCustomHarmonyAttributes(CustomAttributeData.GetCustomAttributes(info.method))) {
                return true;
            }
        }
        return false;
    }

    private static bool HasCustomHarmonyAttributes(IList<CustomAttributeData> attributes) {
        foreach (var attribute in attributes) {
            var type = attribute.AttributeType;
            if (typeof(HarmonyAttribute).IsAssignableFrom(type) && type.Assembly != typeof(Harmony).Assembly) {
                return true;
            }
        }
        return false;
    }

    private static bool TryRestoreProcessors(Harmony harmony, Assembly assembly, Module module, CacheEntry entry,
        out PatchClassProcessor[] processors, out int restored) {
        processors = null;
        restored = 0;
        if (entry.PatchClasses == null || entry.TotalTypes < entry.PatchClasses.Length) {
            return false;
        }

        var resolver = new ModuleResolver();
        var result = new PatchClassProcessor[entry.PatchClasses.Length];
        try {
            for (int i = 0; i < result.Length; i++) {
                var data = entry.PatchClasses[i];
                var containerType = module.ResolveType(data.TypeToken);
                if (containerType == null || containerType.Assembly != assembly) {
                    return false;
                }

                if (data.Container == null) {
                    result[i] = harmony.CreateClassProcessor(containerType);
                } else if (TryRestoreProcessor(harmony, containerType, data, resolver, out result[i])) {
                    restored++;
                } else {
                    return false;
                }
            }
        } catch {
            return false;
        }

        processors = result;
        return true;
    }

    private static bool TryRestoreProcessor(Harmony harmony, Type containerType, PatchClassData data,
        ModuleResolver resolver, out PatchClassProcessor processor) {
        processor = null;
        if (data.AuxiliaryMethods == null || data.PatchMethods == null
            || !TryRestoreHarmonyMethod(data.Container, resolver, out var container)) {
            return false;
        }

        var auxiliary = new Dictionary<Type, MethodInfo>();
        foreach (var item in data.AuxiliaryMethods) {
            if (item.Kind < 0 || item.Kind >= m_AuxiliaryTypes.Length
            || !TryResolveMethod(item.Method, resolver, out var method)) {
                return false;
            }
            auxiliary[m_AuxiliaryTypes[item.Kind]] = method;
        }

        var listType = typeof(List<>).MakeGenericType(m_AttributePatchType);
        var patchMethods = (IList)Activator.CreateInstance(listType);
        foreach (var item in data.PatchMethods) {
            if (item.Type.HasValue && !Enum.IsDefined(typeof(HarmonyPatchType), item.Type.Value)) {
                return false;
            }
            if (!TryRestoreHarmonyMethod(item.Info, resolver, out var info)) {
                return false;
            }
            var patch = FormatterServices.GetUninitializedObject(m_AttributePatchType);
            m_AttributePatchInfo.SetValue(patch, info);
            m_AttributePatchKind.SetValue(patch, item.Type.HasValue ? (HarmonyPatchType?)item.Type.Value : null);
            patchMethods.Add(patch);
        }

        var restored = (PatchClassProcessor)FormatterServices.GetUninitializedObject(typeof(PatchClassProcessor));
        m_ProcessorInstance.SetValue(restored, harmony);
        m_ProcessorContainerType.SetValue(restored, containerType);
        m_ProcessorContainerAttributes.SetValue(restored, container);
        m_ProcessorAuxiliaryMethods.SetValue(restored, auxiliary);
        m_ProcessorPatchMethods.SetValue(restored, patchMethods);
        m_ProcessorCategory.SetValue(restored, container.category);
        processor = restored;
        return true;
    }

    private static HarmonyMethodData CaptureHarmonyMethod(HarmonyMethod method) {
        if (method == null) {
            return null;
        }

        TypeData[] argumentTypes = null;
        if (method.argumentTypes != null) {
            argumentTypes = new TypeData[method.argumentTypes.Length];
            for (int i = 0; i < argumentTypes.Length; i++) {
                argumentTypes[i] = CaptureType(method.argumentTypes[i]);
                if (argumentTypes[i] == null) {
                    return null;
                }
            }
        }

        var declaringType = CaptureType(method.declaringType);
        var patchMethod = CaptureMethod(method.method);
        if (method.declaringType != null && declaringType == null || method.method != null && patchMethod == null) {
            return null;
        }
        return new HarmonyMethodData {
            Method = patchMethod,
            Category = method.category,
            DeclaringType = declaringType,
            MethodName = method.methodName,
            MethodType = method.methodType.HasValue ? (int?)method.methodType.Value : null,
            ArgumentTypes = argumentTypes,
            Priority = method.priority,
            Before = method.before,
            After = method.after,
            ReversePatchType = method.reversePatchType.HasValue ? (int?)method.reversePatchType.Value : null,
            Debug = method.debug,
            NonVirtualDelegate = method.nonVirtualDelegate
        };
    }

    private static bool TryRestoreHarmonyMethod(HarmonyMethodData data, ModuleResolver resolver, out HarmonyMethod method) {
        method = null;
        if (data == null
            || data.MethodType.HasValue && !Enum.IsDefined(typeof(MethodType), data.MethodType.Value)
            || data.ReversePatchType.HasValue && !Enum.IsDefined(typeof(HarmonyReversePatchType), data.ReversePatchType.Value)) {
            return false;
        }

        MethodInfo patchMethod = null;
        Type declaringType = null;
        if (data.Method != null && !TryResolveMethod(data.Method, resolver, out patchMethod)
            || data.DeclaringType != null && !TryResolveType(data.DeclaringType, resolver, out declaringType)) {
            return false;
        }

        Type[] argumentTypes = null;
        if (data.ArgumentTypes != null) {
            argumentTypes = new Type[data.ArgumentTypes.Length];
            for (int i = 0; i < argumentTypes.Length; i++) {
                if (!TryResolveType(data.ArgumentTypes[i], resolver, out argumentTypes[i])) {
                    return false;
                }
            }
        }

        method = new HarmonyMethod {
            method = patchMethod,
            category = data.Category,
            declaringType = declaringType,
            methodName = data.MethodName,
            methodType = data.MethodType.HasValue ? (MethodType?)data.MethodType.Value : null,
            argumentTypes = argumentTypes,
            priority = data.Priority,
            before = data.Before,
            after = data.After,
            reversePatchType = data.ReversePatchType.HasValue ? (HarmonyReversePatchType?)data.ReversePatchType.Value : null,
            debug = data.Debug,
            nonVirtualDelegate = data.NonVirtualDelegate
        };
        return true;
    }

    private static MethodData CaptureMethod(MethodInfo method) {
        if (method == null) {
            return null;
        }
        if (method.IsGenericMethod && !method.IsGenericMethodDefinition) {
            return null;
        }
        try {
            return new MethodData {
                Module = method.Module.ModuleVersionId,
                Token = method.MetadataToken
            };
        } catch {
            return null;
        }
    }

    private static bool TryResolveMethod(MethodData data, ModuleResolver resolver, out MethodInfo method) {
        method = null;
        if (data == null || !resolver.TryGet(data.Module, out var module)) {
            return false;
        }
        try {
            method = module.ResolveMethod(data.Token) as MethodInfo;
            return method != null;
        } catch {
            return false;
        }
    }

    private static TypeData CaptureType(Type type) {
        if (type == null) {
            return null;
        }
        if (type.IsGenericParameter) {
            return null;
        }
        if (type.IsArray) {
            var element = CaptureType(type.GetElementType());
            if (element == null) {
                return null;
            }
            return new TypeData {
                Kind = 2,
                Element = element,
                Rank = type.GetArrayRank(),
                Vector = type.GetArrayRank() == 1 && type.Name.EndsWith("[]", StringComparison.Ordinal)
            };
        }
        if (type.IsByRef || type.IsPointer) {
            var element = CaptureType(type.GetElementType());
            if (element == null) {
                return null;
            }
            return new TypeData {
                Kind = type.IsByRef ? 3 : 4,
                Element = element
            };
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition) {
            var arguments = type.GetGenericArguments();
            var capturedArguments = new TypeData[arguments.Length];
            for (int i = 0; i < arguments.Length; i++) {
                capturedArguments[i] = CaptureType(arguments[i]);
                if (capturedArguments[i] == null) {
                    return null;
                }
            }
            var definition = CaptureType(type.GetGenericTypeDefinition());
            if (definition == null) {
                return null;
            }
            return new TypeData { Kind = 1, Element = definition, Arguments = capturedArguments };
        }

        try {
            return new TypeData {
                Module = type.Module.ModuleVersionId,
                Token = type.MetadataToken
            };
        } catch {
            return null;
        }
    }

    private static bool TryResolveType(TypeData data, ModuleResolver resolver, out Type type) {
        type = null;
        if (data == null) {
            return false;
        }
        if (data.Kind != 0) {
            if (!TryResolveType(data.Element, resolver, out var element)) {
                return false;
            }
            try {
                if (data.Kind == 1) {
                    if (data.Arguments == null) {
                        return false;
                    }
                    var arguments = new Type[data.Arguments.Length];
                    for (int i = 0; i < arguments.Length; i++) {
                        if (!TryResolveType(data.Arguments[i], resolver, out arguments[i])) {
                            return false;
                        }
                    }
                    type = element.MakeGenericType(arguments);
                } else if (data.Kind == 2 && data.Rank > 0) {
                    type = data.Vector ? element.MakeArrayType() : element.MakeArrayType(data.Rank);
                } else if (data.Kind == 3) {
                    type = element.MakeByRefType();
                } else if (data.Kind == 4) {
                    type = element.MakePointerType();
                } else {
                    return false;
                }
                return true;
            } catch {
                return false;
            }
        }
        if (!resolver.TryGet(data.Module, out var module)) {
            return false;
        }
        try {
            type = module.ResolveType(data.Token);
            return type != null;
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
            Main.Log.Log($"Could not read the Harmony PatchAll cache; rebuilding it.\n{ex}");
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
            Main.Log.Log($"Could not write the Harmony PatchAll cache.\n{ex}");
        }
    }

    private static void Report() {
        if (Interlocked.Exchange(ref m_Reported, 1) != 0) {
            return;
        }
        SaveCache();
        if (m_Hits + m_Misses != 0) {
            Main.Log.Log($"PatchAll cache: {m_Hits} hit(s), {m_Misses} miss(es), restored {m_RestoredClasses} patch class(es), reflected {m_ReflectedClasses}, skipped {m_SkippedTypes} non-patch type scans.");
        }
    }
}
