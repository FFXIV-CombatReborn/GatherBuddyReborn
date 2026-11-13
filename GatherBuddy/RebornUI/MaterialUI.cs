using Dalamud.Interface;
using Dalamud.Plugin;
using RebornMaterial;
using System;
using System.Numerics;

namespace GatherBuddy.RebornUI;

internal static class MaterialUI
{
    public const string ProductName = "GatherBuddy Reborn";

    private const string LogoResource = "GatherBuddy.RebornUI.Images.Logo.png";

    private const float LogoCornerFraction = 0.047f;

    public static readonly Vector4 DefaultAccent = M3ColorMath.FromRgb(0x117C1E);

    public static readonly M3ColorChoice[] AccentPresets =
    [
        new("Green (default)", DefaultAccent),
        new("Ocean", M3ColorMath.FromRgb(0x2F6DB5)),
        new("Teal", M3ColorMath.FromRgb(0x00897B)),
        new("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        new("Amber", M3ColorMath.FromRgb(0xC98A00)),
        new("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        new("Crimson", M3.DefaultSeed),
    ];

    private static bool _frameFailed;

    public static void Initialize(IDalamudPluginInterface pluginInterface, IM3Settings settings)
    {
        M3.ElementBaseline = 0.75f;
        M3.Initialize(pluginInterface, settings);

        M3Brand.Name = ProductName;
        M3Brand.Version = GatherBuddy.Version;
        M3Brand.FallbackIcon = FontAwesomeIcon.Seedling;
        M3Brand.LogoCornerFraction = LogoCornerFraction;
        M3Brand.SetLogoResource(typeof(MaterialUI).Assembly, LogoResource);
    }

    public static void BeginFrame()
    {
        if (_frameFailed || !M3.IsInitialized)
        {
            return;
        }

        try
        {
            M3Window.DefaultDensity = GatherBuddy.Config.UiCompactLayout ? M3Density.Tight : M3Density.Compact;
            M3.BeginFrame();
        }
        catch (Exception ex)
        {
            _frameFailed = true;
            GatherBuddy.Log.Error($"[RebornUI] Per-frame update failed; it stays off until the plugin reloads.\n{ex}");
        }
    }

    public static void Dispose()
    {
        if (!M3.IsInitialized)
        {
            return;
        }

        try
        {
            M3.Dispose();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RebornUI] Cleanup failed.\n{ex}");
        }
    }
}
