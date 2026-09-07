using Newtonsoft.Json;
using System;
using System.IO;

namespace ManyModsPerformanceFix;

internal static class CacheFile {
    internal static T Read<T>(string path) {
        using var file = File.OpenText(path);
        using var reader = new JsonTextReader(file);
        return new JsonSerializer().Deserialize<T>(reader);
    }

    internal static void Write(string path, object cache) {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = File.CreateText(temporary)) {
                using var writer = new JsonTextWriter(file);
                new JsonSerializer().Serialize(writer, cache);
            }
            if (File.Exists(path)) {
                File.Replace(temporary, path, null);
            } else {
                File.Move(temporary, path);
            }
        } finally {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }
        }
    }
}
