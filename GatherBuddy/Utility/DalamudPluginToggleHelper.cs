using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace GatherBuddy.Utility;

internal sealed record PluginToggleState(bool IsInstalled, bool IsLoaded, bool CanToggle, string? BlockedReason);

internal static class DalamudPluginToggleHelper
{
    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private sealed record PluginToggleContext(
        object LocalPlugin,
        Type LocalPluginType,
        Guid WorkingPluginId,
        bool IsLoaded,
        object? ApplicableProfile,
        string? BlockedReason);

    public static PluginToggleState GetPluginToggleState(string internalName)
    {
        try
        {
            if (!ReflectionHelpers.TryGetInstalledPluginEntry(internalName, out var localPlugin, false))
                return new PluginToggleState(false, false, false, $"{internalName} is not installed.");

            var isLoaded = localPlugin.GetFoP("IsLoaded") is bool loaded && loaded;
            if (!TryBuildPluginToggleContext(localPlugin, internalName, out var context, out var failureReason))
                return new PluginToggleState(true, isLoaded, false, failureReason ?? $"Could not inspect {internalName}.");

            return new PluginToggleState(true, context.IsLoaded, context.BlockedReason == null, context.BlockedReason);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Failed to inspect toggle state for {internalName}: {e}");
            return new PluginToggleState(true, false, false, $"Could not inspect {internalName}.");
        }
    }

    public static bool TrySetPluginEnabled(string internalName, bool enable, out Task? operationTask, out string? failureReason)
    {
        operationTask = null;
        failureReason = null;

        try
        {
            if (!ReflectionHelpers.TryGetInstalledPluginEntry(internalName, out var localPlugin, true))
            {
                failureReason = $"{internalName} is not installed.";
                return false;
            }

            if (!TryBuildPluginToggleContext(localPlugin, internalName, out var context, out failureReason))
                return false;

            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Plugin {internalName} loaded={context.IsLoaded}, requested={enable}, blocked={context.BlockedReason != null}.");

            if (context.BlockedReason != null)
            {
                failureReason = context.BlockedReason;
                GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Refusing to toggle {internalName}: {failureReason}");
                return false;
            }

            if (context.IsLoaded == enable)
            {
                operationTask = Task.CompletedTask;
                return true;
            }

            if (context.ApplicableProfile == null)
            {
                failureReason = $"Could not find a collection entry for {internalName}.";
                GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
                return false;
            }

            operationTask = enable
                ? EnablePluginAsync(context, internalName)
                : DisablePluginAsync(context, internalName);
            return true;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Failed to {(enable ? "enable" : "disable")} {internalName}: {e}");
            failureReason = $"Failed to {(enable ? "enable" : "disable")} {internalName}.";
            operationTask = null;
            return false;
        }
    }

    private static async Task EnablePluginAsync(PluginToggleContext context, string internalName)
    {
        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Enabling {internalName} via profile-aware reflected toggle.");
        await InvokeProfileAddOrUpdateAsync(context.ApplicableProfile!, context.WorkingPluginId, internalName, true).ConfigureAwait(false);
        await InvokePluginLoadAsync(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Finished enabling {internalName}.");
    }

    private static async Task DisablePluginAsync(PluginToggleContext context, string internalName)
    {
        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Disabling {internalName} via profile-aware reflected toggle.");
        await InvokePluginUnloadAsync(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await InvokeProfileAddOrUpdateAsync(context.ApplicableProfile!, context.WorkingPluginId, internalName, false).ConfigureAwait(false);
        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Finished disabling {internalName}.");
    }

    private static bool TryBuildPluginToggleContext(object localPlugin, string internalName, out PluginToggleContext context, out string? failureReason)
    {
        context = null!;
        failureReason = null;

        var pluginType = localPlugin.GetType();
        var isLoaded = localPlugin.GetFoP("IsLoaded") is bool loaded && loaded;
        if (localPlugin.GetFoP("EffectiveWorkingPluginId") is not Guid workingPluginId || workingPluginId == Guid.Empty)
        {
            failureReason = $"Could not resolve {internalName}'s plugin id.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
            return false;
        }

        var profileManager = ReflectionHelpers.GetDalamudService("Dalamud.Plugin.Internal.Profiles.ProfileManager");
        if (profileManager == null)
        {
            failureReason = "Could not resolve the Dalamud profile manager.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
            return false;
        }

        if (profileManager.GetFoP("Profiles") is not IEnumerable profiles)
        {
            failureReason = "Could not read collections from the Dalamud profile manager.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
            return false;
        }

        var matchingProfiles = new List<object>();
        foreach (var profile in profiles)
        {
            if (profile == null)
                continue;

            if (!TryProfileContainsPlugin(profile, workingPluginId, out var containsPlugin))
            {
                failureReason = "Could not inspect the Dalamud collection assignments.";
                GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
                return false;
            }

            if (containsPlugin)
                matchingProfiles.Add(profile);
        }

        var defaultProfile = profileManager.GetFoP("DefaultProfile");
        if (!TryInvokeBoolMethod(profileManager, "IsInDefaultProfile", [typeof(Guid)], [workingPluginId], out var isInDefaultProfile))
        {
            failureReason = "Could not determine the default collection state.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
            return false;
        }

        object? applicableProfile = null;
        string? blockedReason = null;

        if (matchingProfiles.Count == 0)
        {
            applicableProfile = defaultProfile;
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {internalName} was not declared in any collection; defaulting to the default collection.");
        }
        else if (isInDefaultProfile)
        {
            applicableProfile = defaultProfile;
        }
        else if (matchingProfiles.Count == 1)
        {
            applicableProfile = matchingProfiles[0];
        }
        else
        {
            blockedReason = $"{internalName} is assigned to multiple collections. Use the Plugin Installer to change it.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {blockedReason}");
        }

        if (blockedReason == null && applicableProfile == null)
        {
            failureReason = $"Could not find an applicable collection for {internalName}.";
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
            return false;
        }

        if (blockedReason == null && applicableProfile != null)
        {
            var isDefaultProfileEntry = applicableProfile.GetFoP("IsDefaultProfile") is bool profileIsDefault && profileIsDefault;
            if (!isDefaultProfileEntry)
            {
                var profileName = applicableProfile.GetFoP<string>("Name") ?? "unknown";
                var isProfileEnabled = applicableProfile.GetFoP("IsEnabled") is bool profileEnabled && profileEnabled;
                if (!isProfileEnabled)
                {
                    blockedReason = $"{internalName} belongs to the disabled collection '{profileName}'. Enable that collection first.";
                    GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {blockedReason}");
                }
                else
                {
                    if (!TryInvokeBoolMethod(applicableProfile, "CheckWantsActiveFromGameState", [typeof(ulong)], [Dalamud.PlayerState.ContentId], out var wantsActive))
                    {
                        failureReason = $"Could not determine whether the collection '{profileName}' is active.";
                        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {failureReason}");
                        return false;
                    }

                    if (!wantsActive)
                    {
                        blockedReason = $"{internalName} belongs to the collection '{profileName}', but that collection is not active right now.";
                        GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {blockedReason}");
                    }
                }
            }
        }

        context = new PluginToggleContext(localPlugin, pluginType, workingPluginId, isLoaded, applicableProfile, blockedReason);
        return true;
    }

    private static bool TryProfileContainsPlugin(object profile, Guid workingPluginId, out bool containsPlugin)
    {
        containsPlugin = false;

        var wantsPluginMethod = profile.GetType().GetMethod("WantsPlugin", AllFlags, null, [typeof(Guid)], null);
        if (wantsPluginMethod == null)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Could not find WantsPlugin on {profile.GetType().FullName}.");
            return false;
        }

        containsPlugin = wantsPluginMethod.Invoke(profile, [workingPluginId]) != null;
        return true;
    }

    private static async Task InvokeProfileAddOrUpdateAsync(object profile, Guid workingPluginId, string internalName, bool state)
    {
        if (!TryInvokeTaskMethod(profile, "AddOrUpdateAsync", [typeof(Guid), typeof(string), typeof(bool), typeof(bool)], [workingPluginId, internalName, state, false], out var operationTask))
            throw new InvalidOperationException($"Could not update {internalName}'s collection state.");

        await operationTask.ConfigureAwait(false);
    }

    private static async Task InvokePluginLoadAsync(object localPlugin, Type pluginType, string internalName)
    {
        var loadMethod = pluginType.GetMethod("LoadAsync", AllFlags, null, [typeof(PluginLoadReason), typeof(bool)], null);
        if (loadMethod == null)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Could not find LoadAsync on {pluginType.FullName}.");
            throw new InvalidOperationException($"Could not load {internalName}.");
        }

        if (loadMethod.Invoke(localPlugin, [PluginLoadReason.Installer, false]) is not Task operationTask)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] LoadAsync for {internalName} did not return a task.");
            throw new InvalidOperationException($"Could not load {internalName}.");
        }

        await operationTask.ConfigureAwait(false);
    }

    private static async Task InvokePluginUnloadAsync(object localPlugin, Type pluginType, string internalName)
    {
        var dalamudAssembly = Dalamud.PluginInterface.GetType().Assembly;
        var disposalModeType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode", true);
        if (disposalModeType == null)
        {
            GatherBuddy.Log.Debug("[DalamudPluginToggleHelper] Could not resolve PluginLoaderDisposalMode.");
            throw new InvalidOperationException($"Could not unload {internalName}.");
        }

        var unloadMethod = pluginType.GetMethod("UnloadAsync", AllFlags, null, [disposalModeType], null);
        if (unloadMethod == null)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Could not find UnloadAsync on {pluginType.FullName}.");
            throw new InvalidOperationException($"Could not unload {internalName}.");
        }

        var disposalMode = Enum.Parse(disposalModeType, "WaitBeforeDispose");
        if (unloadMethod.Invoke(localPlugin, [disposalMode]) is not Task operationTask)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] UnloadAsync for {internalName} did not return a task.");
            throw new InvalidOperationException($"Could not unload {internalName}.");
        }

        await operationTask.ConfigureAwait(false);
    }

    private static object? InvokeMethod(object target, string name, Type[] parameterTypes, object?[] args)
    {
        var method = target.GetType().GetMethod(name, AllFlags, null, parameterTypes, null);
        if (method == null)
        {
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] Could not find {name} on {target.GetType().FullName}.");
            return null;
        }

        return method.Invoke(target, args);
    }

    private static bool TryInvokeBoolMethod(object target, string name, Type[] parameterTypes, object?[] args, out bool result)
    {
        result = false;
        var invocationResult = InvokeMethod(target, name, parameterTypes, args);
        if (invocationResult is bool boolResult)
        {
            result = boolResult;
            return true;
        }

        if (invocationResult != null)
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {name} on {target.GetType().FullName} did not return a boolean.");
        return false;
    }

    private static bool TryInvokeTaskMethod(object target, string name, Type[] parameterTypes, object?[] args, out Task? operationTask)
    {
        operationTask = null;
        var invocationResult = InvokeMethod(target, name, parameterTypes, args);
        if (invocationResult is Task task)
        {
            operationTask = task;
            return true;
        }

        if (invocationResult != null)
            GatherBuddy.Log.Debug($"[DalamudPluginToggleHelper] {name} on {target.GetType().FullName} did not return a task.");
        return false;
    }
}
