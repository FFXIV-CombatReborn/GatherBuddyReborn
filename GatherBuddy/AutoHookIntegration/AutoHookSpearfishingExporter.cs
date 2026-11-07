using GatherBuddy.AutoHookIntegration.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GatherBuddy.AutoHookIntegration;

public class AutoHookSpearfishingExporter
{
    private const string ExportPrefixSf = "AHSF1_";

    public static string ExportPreset(AHAutoGigConfig preset)
    {
        var json = JsonConvert.SerializeObject(preset, Formatting.None, new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Ignore
        });

        var compressed = CompressString(json);
        return ExportPrefixSf + compressed;
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
