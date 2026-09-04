using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ManyModsPerformanceFix;

internal static class AssemblyCacheKey {
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
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!item.IsDynamic) {
                loaded[item.FullName] = item;
            }
        }

        foreach (var reference in assembly.GetReferencedAssemblies().OrderBy(item => item.FullName, StringComparer.Ordinal)) {
            key.Append('|').Append(reference.FullName).Append('=');
            if (!loaded.TryGetValue(reference.FullName, out var dependency)) {
                key.Append("unloaded");
                continue;
            }
            try {
                foreach (var dependencyModule in dependency.GetModules().OrderBy(item => item.Name, StringComparer.Ordinal)) {
                    key.Append(dependencyModule.ModuleVersionId.ToString("N")).Append(',');
                }
            } catch {
                key.Append("unknown");
            }
        }
    }
}
