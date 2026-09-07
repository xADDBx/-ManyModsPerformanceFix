using Core.Cheats;
using HarmonyLib;
using Kingmaker.Visual.Particles;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using UnityModManagerNet;

namespace ManyModsPerformanceFix;

public static class Main {
    public static UnityModManager.ModEntry.ModLogger Log;
    public static Harmony HarmonyInstance;
    public static UnityModManager.ModEntry ModEntry;
    public static bool Load(UnityModManager.ModEntry modEntry) {
        Log = modEntry.Logger;
        HarmonyInstance = new(modEntry.Info.Id);
        ModEntry = modEntry;
        // Reuse assembly type lists.
        AssemblyTypesCache.Enable();
        // Batch startup Harmony wrapper updates.
        DeferredWrapperUpdates.Enable();
        // Cache Harmony patch discovery.
        PatchAllCache.Enable();
        // Cache UMM reload checks.
        UmmReloadCache.Enable();
        // Cache TypeId discovery.
        WrathPatchesBinderCache.Enable();
        // Cache update, volume and graph types.
        RuntimeTypeDiscoveryCache.Enable();
        // Reuse localization tags and packs.
        LocalizationTagCache.Enable();
        Apply_ManyModsLoad_PerformanceFix();
        Apply_SnapMapBase_UpdateRuntimeData_PerformanceFix();
        // Install the load-order helper.
        FirstStartInstaller.Install();
        return true;
    }
    private static void Remove_NoOp_From_CheatsManagerInit() {
        var target = AccessTools.Constructor(typeof(CheatsManager), []);
        var patch = AccessTools.Method(typeof(Main), nameof(Main.CheatsManagerInit_Remvoe_NoOp_Transpiler));
        HarmonyInstance.Patch(target, transpiler: new(patch));
    }
    private static IEnumerable<CodeInstruction> CheatsManagerInit_Remvoe_NoOp_Transpiler(IEnumerable<CodeInstruction> instructions) {
        var m = AccessTools.Method(typeof(CheatsManager), nameof(CheatsManager.CollectAll));
        bool skip = false;
        foreach (var inst in instructions) {
            if (skip) {
                skip = false;
                continue;
            }
            if (inst.Calls(m)) {
                skip = true;
                continue;
            } else {
                yield return inst;
            }
        }
    }
    private static void Apply_ManyModsLoad_PerformanceFix() {
        try {
            _ = CheatsCache.Instance;
        } catch (Exception ex) {
            Log.Log($"Encountered issue while using CheatsCache, falling back to normal init.\n{ex}");
            Remove_NoOp_From_CheatsManagerInit();
            _ = CheatsManagerHolder.Instance;
        }
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
