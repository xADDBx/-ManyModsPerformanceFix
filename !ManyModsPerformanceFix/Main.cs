using HarmonyLib;
using UnityModManagerNet;
using Core.Cheats;
using Kingmaker.Visual.Particles;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection.Emit;

namespace ManyModsPerformanceFix;

public static class Main {
    public static UnityModManager.ModEntry.ModLogger Log;
    public static Harmony HarmonyInstance;
    public static bool Load(UnityModManager.ModEntry modEntry) {
        Log = modEntry.Logger;
        HarmonyInstance = new(modEntry.Info.Id);
        Apply_ManyModsLoad_PerformanceFix();
        Apply_SnapMapBase_UpdateRuntimeData_PerformanceFix();
        return true;
    }
    private static void Apply_ManyModsLoad_PerformanceFix() {
        _ = CheatsManagerHolder.Instance;
    }
    private static void Apply_SnapMapBase_UpdateRuntimeData_PerformanceFix() {
        var target = AccessTools.Method(typeof(SnapMapBase), nameof(SnapMapBase.UpdateRuntimeData));
        var patch = AccessTools.Method(typeof(Main), nameof(Main.SnapMapBase_UpdateRuntimeData_Transpiler));
        HarmonyInstance.Patch(target, transpiler: new(patch));
    }
    private static IEnumerable<CodeInstruction> SnapMapBase_UpdateRuntimeData_Transpiler(IEnumerable<CodeInstruction> instructions) {
        var m = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.localToWorldMatrix));
        bool skip = false;
        foreach (var inst in instructions) {
            if (skip) {
                skip = false;
                continue;
            }
            if (inst.Calls(m)) {
                yield return new(OpCodes.Pop);
                yield return new(OpCodes.Pop);
                skip = true;
            } else {
                yield return inst;
            }
        }
    }
}
