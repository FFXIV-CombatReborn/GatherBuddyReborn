using GatherBuddy.AutoHookIntegration.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GatherBuddy.AutoHookIntegration;

public static class AutoHookExporter
{
    private const string ExportPrefix = "AH4_";

    public static string ExportPreset(AHCustomPresetConfig preset)
    {
        var json = JsonConvert.SerializeObject(preset,
            new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Include,
                NullValueHandling = NullValueHandling.Ignore
            });

        if (preset.AutoCastsCfg?.CastPatience != null)
        {
            GatherBuddy.Log.Debug($"[AutoHook Export] Before serialization - Patience: Enabled={preset.AutoCastsCfg.CastPatience.Enabled}, Id={preset.AutoCastsCfg.CastPatience.Id}, GP: {preset.AutoCastsCfg.CastPatience.GpThreshold} (Above={preset.AutoCastsCfg.CastPatience.GpThresholdAbove})");
        }

        var autoCastsIndex = json.IndexOf("\"AutoCastsCfg\"");
        if (autoCastsIndex >= 0)
        {
            var snippet = json.Substring(autoCastsIndex, Math.Min(500, json.Length - autoCastsIndex));
            GatherBuddy.Log.Debug($"[AutoHook Export] AutoCastsCfg section:\n{snippet}");
        }

        var compressed = CompressString(json);
        return ExportPrefix + compressed;
    }

    private static string CompressString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var gs = new GZipStream(ms, CompressionMode.Compress))
        {
            gs.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(ms.ToArray());
    }
}
