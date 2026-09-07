using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ManyModsPerformanceFix;

internal static class AssemblyCacheKey {
    private static readonly object m_Sync = new();
    private static readonly ConditionalWeakTable<Assembly, string[]> m_References = new();
    private static readonly Dictionary<Assembly, string> m_Signatures = [];
    private static Dictionary<string, Assembly> m_Loaded;
    private static Dictionary<Guid, Module> m_Modules;
    private static volatile bool m_AssembliesChanged = true;

    static AssemblyCacheKey() {
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => m_AssembliesChanged = true;
    }

    internal static bool TryCreate(Assembly assembly, Guid ownerMvid, out Module module, out string key) {
        module = null;
        key = null;
        if (assembly == null || assembly.IsDynamic) {
            return false;
        }
        try {
            var modules = assembly.GetModules();
            if (modules.Length != 1) {
                return false;
            }
            module = modules[0];
            key = Create(assembly, module, ownerMvid);
            return true;
        } catch {
            return false;
        }
    }

    internal static string Create(Assembly assembly, Module module, Guid ownerMvid) {
        var length = 0L;
        var modified = 0L;
        try {
            var file = new FileInfo(assembly.Location);
            if (file.Exists) {
                length = file.Length;
                modified = file.LastWriteTimeUtc.Ticks;
            }
        } catch { }

        var key = new StringBuilder();
        key.Append(assembly.FullName).Append('|').Append(module.ModuleVersionId.ToString("N"))
            .Append('|').Append(length).Append('|').Append(modified).Append('|').Append(ownerMvid.ToString("N"));
        AppendReferencedAssemblies(key, assembly);
        return key.ToString();
    }

    private static void AppendReferencedAssemblies(StringBuilder key, Assembly assembly) {
        lock (m_Sync) {
            RefreshAssemblies();
            if (!m_Signatures.TryGetValue(assembly, out var signature)) {
                var references = new StringBuilder();
                var names = m_References.GetValue(assembly, item => item.GetReferencedAssemblies()
                    .Select(reference => reference.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
                foreach (var name in names) {
                    references.Append('|').Append(name).Append('=');
                    if (!m_Loaded.TryGetValue(name, out var dependency)) {
                        references.Append("unloaded");
                        continue;
                    }
                    try {
                        foreach (var module in dependency.GetModules().OrderBy(item => item.Name, StringComparer.Ordinal)) {
                            references.Append(module.ModuleVersionId.ToString("N")).Append(',');
                        }
                    } catch {
                        references.Append("unknown");
                    }
                }
                signature = references.ToString();
                m_Signatures[assembly] = signature;
            }
            key.Append(signature);
        }
    }

    internal static Dictionary<Guid, Module> GetModules() {
        lock (m_Sync) {
            RefreshAssemblies();
            if (m_Modules == null) {
                var modules = new Dictionary<Guid, Module>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    try {
                        foreach (var module in assembly.GetModules()) {
                            modules[module.ModuleVersionId] = module;
                        }
                    } catch {
                    }
                }
                m_Modules = modules;
            }
            return m_Modules;
        }
    }

    private static void RefreshAssemblies() {
        if (!m_AssembliesChanged) {
            return;
        }
        // Loads during the scan must trigger another refresh.
        m_AssembliesChanged = false;
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic) {
                loaded[assembly.FullName] = assembly;
            }
        }
        m_Loaded = loaded;
        m_Modules = null;
        m_Signatures.Clear();
    }
}
