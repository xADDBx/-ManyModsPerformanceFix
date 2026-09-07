using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityModManagerNet;

namespace ManyModsPerformanceFix;

internal static class FirstStartInstaller {
    internal static void Install() {
        try {
            const string id = "!!!FirstStart";
            const string version = "1.0.0";
            const string entryMethod = "FirstStart.Main.Load";
            var path = Path.Combine(UnityModManager.modsPath, id);
            var infoPath = Path.Combine(path, "Info.json");
            var dllPath = Path.Combine(path, "FirstStart.dll");
            var loadAfter = new[] { Main.ModEntry.Info.Id };
            if (File.Exists(infoPath)) {
                var info = JsonConvert.DeserializeObject<UnityModManager.ModInfo>(File.ReadAllText(infoPath));
                if (info?.Id != id || info.EntryMethod != entryMethod) {
                    Main.Log.Log("FirstStart folder is already in use.");
                    return;
                }
                if (info.Version == version && info.LoadAfter?.SequenceEqual(loadAfter) == true && File.Exists(dllPath)) {
                    return;
                }
            }

            using var resource = typeof(FirstStartInstaller).Assembly.GetManifestResourceStream("FirstStart.dll");
            Directory.CreateDirectory(path);
            using (var file = File.Create(dllPath)) {
                resource.CopyTo(file);
            }
            var manifest = new {
                Id = id,
                DisplayName = "Performance Fixes Load Order",
                Author = "ADDB",
                Version = version,
                ManagerVersion = "0.27.11",
                AssemblyName = "FirstStart.dll",
                EntryMethod = entryMethod,
                LoadAfter = loadAfter
            };
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented).Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(infoPath, json, new UTF8Encoding(false));
        } catch (Exception ex) {
            Main.Log.Log($"Could not install FirstStart: {ex.Message}");
        }
    }
}
