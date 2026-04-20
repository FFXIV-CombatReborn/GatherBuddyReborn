using System;
using System.Collections;
using System.Reflection;
using Dalamud.Plugin;

namespace GatherBuddy.Utility;

public static class ReflectionHelpers
{
    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static object? GetFoP(this object obj, string name)
    {
        var type = obj.GetType();
        while (type != null)
        {
            var fieldInfo = type.GetField(name, AllFlags);
            if (fieldInfo != null)
                return fieldInfo.GetValue(obj);

            var propertyInfo = type.GetProperty(name, AllFlags);
            if (propertyInfo != null)
                return propertyInfo.GetValue(obj);

            type = type.BaseType;
        }
        return null;
    }

    public static T? GetFoP<T>(this object obj, string name)
        => (T?)GetFoP(obj, name);

    public static bool TryGetDalamudPlugin(string internalName, out IDalamudPlugin? instance)
    {
        try
        {
            if (!TryGetInstalledPluginEntry(internalName, out var plugin, false))
            {
                instance = null;
                return false;
            }

            var type = plugin.GetType().Name == "LocalDevPlugin" ? plugin.GetType().BaseType : plugin.GetType();
            if (type == null)
            {
                instance = null;
                return false;
            }

            var pluginInstance = (IDalamudPlugin?)type.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(plugin);
            if (pluginInstance != null)
            {
                instance = pluginInstance;
                return true;
            }

            instance = null;
            return false;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"Failed to get plugin {internalName}: {e.Message}");
            instance = null;
            return false;
        }
    }

    internal static bool TryGetInstalledPluginEntry(string internalName, out object? pluginEntry, bool logIfMissing)
    {
        pluginEntry = null;

        var pluginManager = GetDalamudService("Dalamud.Plugin.Internal.PluginManager");
        if (pluginManager == null)
        {
            GatherBuddy.Log.Debug("[ReflectionHelpers] Could not resolve the Dalamud plugin manager.");
            return false;
        }

        var installedPlugins = pluginManager.GetType().GetProperty("InstalledPlugins")?.GetValue(pluginManager) as IEnumerable;
        if (installedPlugins == null)
        {
            GatherBuddy.Log.Debug("[ReflectionHelpers] Could not read InstalledPlugins from the Dalamud plugin manager.");
            return false;
        }

        foreach (var plugin in installedPlugins)
        {
            var pluginInternalName = plugin.GetFoP<string>("InternalName");
            if (string.Equals(pluginInternalName, internalName, StringComparison.OrdinalIgnoreCase))
            {
                pluginEntry = plugin;
                return true;
            }
        }

        if (logIfMissing)
            GatherBuddy.Log.Debug($"[ReflectionHelpers] Plugin entry {internalName} was not found in InstalledPlugins.");
        return false;
    }

    internal static object? GetDalamudService(string serviceName)
    {
        try
        {
            var dalamudAssembly = Dalamud.PluginInterface.GetType().Assembly;
            var serviceType = dalamudAssembly.GetType("Dalamud.Service`1", true);
            var targetType = dalamudAssembly.GetType(serviceName, true);

            if (serviceType == null || targetType == null)
                return null;

            var genericServiceType = serviceType.MakeGenericType(targetType);
            var getMethod = genericServiceType.GetMethod("Get");

            return getMethod?.Invoke(null, BindingFlags.Default, null, Array.Empty<object>(), null);
        }
        catch
        {
            return null;
        }
    }
}
