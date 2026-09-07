using System;
using System.IO;
using UnityModManagerNet;

namespace FirstStart;

public static class Main {
    public static bool Load(UnityModManager.ModEntry modEntry) {
        if (UnityModManager.FindMod("!ManyModsPerformanceFix") != null) {
            return true;
        }

        try {
            File.Delete(Path.Combine(modEntry.Path, "Info.json"));
            foreach (var file in Directory.GetFiles(modEntry.Path, "FirstStart.dll*")) {
                File.Delete(file);
            }
            // Other mods still scan this directory during startup.
        } catch (Exception ex) {
            modEntry.Logger.Log($"Could not remove FirstStart files: {ex.Message}");
        }
        return true;
    }
}
