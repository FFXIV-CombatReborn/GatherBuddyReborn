using System;
using System.Collections;
using System.Collections.Generic;
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
            if (!TryGetInstalledPluginEntries(internalName, out var pluginEntries, false))
            {
                instance = null;
                return false;
            }

            if (TrySelectPluginInstance(pluginEntries, IsLoadedPluginEntry, out instance)
             || TrySelectPluginInstance(pluginEntries, AlwaysMatchPluginEntry, out instance))
                return true;

            GatherBuddy.Log.Debug($"[ReflectionHelpers] Found {pluginEntries.Count} matching plugin entries for {internalName}, but no live plugin instance could be resolved.");
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

    internal static bool IsPluginLoaded(string internalName, bool logIfMissing = false)
    {
        if (!TryGetInstalledPluginEntries(internalName, out var pluginEntries, logIfMissing))
            return false;

        var foundReadableLoadState = false;
        foreach (var pluginEntry in pluginEntries)
        {
            if (pluginEntry.GetFoP("IsLoaded") is not bool isLoaded)
                continue;

            foundReadableLoadState = true;
            if (isLoaded)
                return true;
        }

        if (foundReadableLoadState)
            return false;

        GatherBuddy.Log.Debug($"[ReflectionHelpers] Matching plugin entries for {internalName} were found, but none exposed IsLoaded. Falling back to plugin instance resolution.");
        return TryGetDalamudPlugin(internalName, out _);
    }

    internal static bool TryGetInstalledPluginEntry(string internalName, out object? pluginEntry, bool logIfMissing)
    {
        if (!TryGetInstalledPluginEntries(internalName, out var pluginEntries, logIfMissing))
        {
            pluginEntry = null;
            return false;
        }

        var selected = TrySelectPluginEntry(pluginEntries, IsLoadedInstalledPluginEntry, out pluginEntry)
                    || TrySelectPluginEntry(pluginEntries, IsLoadedPluginEntry, out pluginEntry)
                    || TrySelectPluginEntry(pluginEntries, IsInstalledPluginEntry, out pluginEntry)
                    || TrySelectPluginEntry(pluginEntries, AlwaysMatchPluginEntry, out pluginEntry);

        if (selected && pluginEntries.Count > 1)
            GatherBuddy.Log.Debug($"[ReflectionHelpers] Found {pluginEntries.Count} matching plugin entries for {internalName}; selected {DescribePluginEntry(pluginEntry!)}.");

        return selected;
    }

    private static bool TryGetInstalledPluginEntries(string internalName, out List<object> pluginEntries, bool logIfMissing)
    {
        pluginEntries = [];

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
                pluginEntries.Add(plugin);
        }

        if (pluginEntries.Count > 0)
            return true;

        if (logIfMissing)
            GatherBuddy.Log.Debug($"[ReflectionHelpers] Plugin entry {internalName} was not found in InstalledPlugins.");

        return false;
    }

    private static bool TrySelectPluginEntry(List<object> pluginEntries, Predicate<object> predicate, out object? pluginEntry)
    {
        foreach (var candidate in pluginEntries)
        {
            if (!predicate(candidate))
                continue;

            pluginEntry = candidate;
            return true;
        }

        pluginEntry = null;
        return false;
    }

    private static bool TrySelectPluginInstance(List<object> pluginEntries, Predicate<object> predicate, out IDalamudPlugin? pluginInstance)
    {
        foreach (var candidate in pluginEntries)
        {
            if (!predicate(candidate))
                continue;

            if (!TryGetPluginInstance(candidate, out pluginInstance))
                continue;

            return true;
        }

        pluginInstance = null;
        return false;
    }

    private static bool TryGetPluginInstance(object pluginEntry, out IDalamudPlugin? pluginInstance)
    {
        pluginInstance = pluginEntry.GetFoP<IDalamudPlugin>("instance")
                      ?? pluginEntry.GetFoP<IDalamudPlugin>("Instance")
                      ?? pluginEntry.GetFoP<IDalamudPlugin>("pluginInstance")
                      ?? pluginEntry.GetFoP<IDalamudPlugin>("Plugin");
        return pluginInstance != null;
    }

    private static bool IsLoadedPluginEntry(object pluginEntry)
        => pluginEntry.GetFoP("IsLoaded") is bool isLoaded && isLoaded;

    private static bool IsInstalledPluginEntry(object pluginEntry)
        => !IsLocalDevPlugin(pluginEntry);

    private static bool IsLoadedInstalledPluginEntry(object pluginEntry)
        => IsLoadedPluginEntry(pluginEntry) && IsInstalledPluginEntry(pluginEntry);

    private static bool AlwaysMatchPluginEntry(object _)
        => true;

    private static bool IsLocalDevPlugin(object pluginEntry)
        => string.Equals(pluginEntry.GetType().Name, "LocalDevPlugin", StringComparison.Ordinal)
         || pluginEntry.GetType().FullName?.Contains("LocalDevPlugin", StringComparison.Ordinal) == true;

    private static string DescribePluginEntry(object pluginEntry)
    {
        var kind = IsLocalDevPlugin(pluginEntry) ? "dev" : "installed";
        var isLoaded = IsLoadedPluginEntry(pluginEntry);
        var hasInstance = TryGetPluginInstance(pluginEntry, out _);
        return $"{kind} entry {pluginEntry.GetType().FullName} loaded={isLoaded} instance={hasInstance}";
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
