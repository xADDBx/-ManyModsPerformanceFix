using Core.Cheats;
using Kingmaker;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;

namespace ManyModsPerformanceFix;
public struct BetterKnownObjectsInfo {
    public Dictionary<string, CheatMethodInfo> Methods;
    public Dictionary<string, CheatPropertyInfo> Variables;
    public string[] Externals;
}
public class CheatsCache {
    public BetterKnownObjectsInfo Content;
    public string CachedGameVersion;
    public static CheatsCache Instance {
        get {
            if (field == null) {
                field = Load();
                field ??= CreateInstance();
                field.Inject();
            }
            return field;
        }
        private set;
    }
    private static string GetPath() => Path.Combine(Main.ModEntry.Path, "Cache.json");
    private CheatsCache() {
    }
    private static CheatsCache CreateInstance() {
        var instance = new CheatsCache();
        instance.Content = CreateCache();
        instance.CachedGameVersion = GameVersion.GetVersion();

        var json = JsonConvert.SerializeObject(instance);
        using var stream = File.OpenWrite(GetPath());
        using var writer = new StreamWriter(stream);
        writer.Write(json);
        return instance;
    }
    private void Inject() {
        CheatsManager manager = (CheatsManager)FormatterServices.GetUninitializedObject(typeof(CheatsManager));
        manager._externalCommands = Content.Externals;
        var t = typeof(CheatsManager);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        t.GetField("_commandsByName", flags).SetValue(manager, Content.Methods);
        t.GetField("_propertiesByName", flags).SetValue(manager, Content.Variables);
        t.GetField("<Version>k__BackingField", flags).SetValue(manager, Guid.NewGuid().ToString());
        t.GetField("_externalDelegate", flags).SetValue(manager, new CheatsManager.ExecuteExternalDelegate(manager.ExecuteExternalWithDefaultLogging));
        t.GetField("_commandDelegate", flags).SetValue(manager, new CheatsManager.ExecuteCommandDelegate(manager.ExecuteCommandWithDefaultLogging));
        t.GetField("_getVarDelegate", flags).SetValue(manager, new CheatsManager.ExecuteGetVariableDelegate(manager.ExecuteGetVariableWithDefaultLogging));
        t.GetField("_setVarDelegate", flags).SetValue(manager, new CheatsManager.ExecuteSetVariableDelegate(manager.ExecuteSetVariableWithDefaultLogging));

        CheatsManagerHolder._instance = manager;
    }
    private static CheatsCache Load() {
        var path = GetPath();
        if (File.Exists(path)) {
            try {
                using var stream = File.OpenRead(path);
                using var reader = new StreamReader(stream);
                var deserialized = JsonConvert.DeserializeObject<CheatsCache>(reader.ReadToEnd());
                var currentVersion = GameVersion.GetVersion();
                if (currentVersion == deserialized.CachedGameVersion) {
                    return deserialized;
                }
            } catch (Exception ex) {
                Main.Log.Log($"[Warn] Encountered error while trying to load existing cache:\n{ex}");
                File.Delete(path);
            }
        }
        return null;
    }
    public static BetterKnownObjectsInfo CreateCache() {
        var internals = CheatsManager.GetInternals();
        return new BetterKnownObjectsInfo {
            Externals = [],
            Methods = internals.commands as Dictionary<string, CheatMethodInfo>,
            Variables = internals.variables as Dictionary<string, CheatPropertyInfo>
        };
    }
}
