using Dalamud.Interface.Windowing;
using RebornMaterial;
using System;
using System.Collections;
using System.Reflection;

namespace GatherBuddy.RebornUI;

internal static class WindowPresetReset
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? HostsField = typeof(WindowSystem).GetField("windowHosts", InstanceFlags);
    private static readonly Type? HostType = typeof(WindowSystem).Assembly.GetType("Dalamud.Interface.Windowing.WindowHost");
    private static readonly PropertyInfo? HostWindowProperty = HostType?.GetProperty("Window", InstanceFlags);
    private static readonly FieldInfo? AlphaField = HostType?.GetField("internalAlpha", InstanceFlags);
    private static readonly FieldInfo? BlurField = HostType?.GetField("internalBlurFactorOverride", InstanceFlags);
    private static readonly FieldInfo? DirtyField = HostType?.GetField("presetDirty", InstanceFlags);

    private static bool _failed;

    public static void Apply(WindowSystem windowSystem)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            if (HostsField?.GetValue(windowSystem) is not IList hosts)
            {
                ResetWindows(windowSystem);
                return;
            }

            for (var i = 0; i < hosts.Count; i++)
            {
                var host = hosts[i];
                if (host == null || HostWindowProperty?.GetValue(host) is not M3FoldWindow window)
                {
                    continue;
                }

                ResetWindow(window);
                ResetHost(host);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            GatherBuddy.Log.Error($"Unable to reset Dalamud window presets: {ex}");
        }
    }

    private static void ResetWindows(WindowSystem windowSystem)
    {
        var windows = windowSystem.Windows;
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i] is M3FoldWindow window)
            {
                ResetWindow(window);
            }
        }
    }

    private static void ResetWindow(Window window)
    {
        if (window.IsPinned || window.IsClickthrough)
        {
            window.IsPinned = false;
            window.IsClickthrough = false;
        }
    }

    private static void ResetHost(object host)
    {
        var changed = false;
        if (AlphaField?.GetValue(host) != null)
        {
            AlphaField.SetValue(host, null);
            changed = true;
        }

        if (BlurField?.GetValue(host) != null)
        {
            BlurField.SetValue(host, null);
            changed = true;
        }

        if (changed)
        {
            DirtyField?.SetValue(host, true);
        }
    }
}
