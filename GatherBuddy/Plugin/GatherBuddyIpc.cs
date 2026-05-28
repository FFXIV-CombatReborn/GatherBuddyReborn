using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 2;

    private readonly GatherBuddy _plugin;

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    [EzIPCEvent]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    public Action<bool> AutoGatherEnabledChanged;

    [EzIPC]
    public string[] GetAutoGatherListPaths()
        => _plugin.AutoGatherListsManager.GetIpcListPaths();

    [EzIPC]
    public string CreateAutoGatherList(string name, string description)
        => _plugin.AutoGatherListsManager.CreateIpcList(name, description);

    [EzIPC]
    public int UpdateAutoGatherList(string path, Dictionary<uint, uint> targets, bool enabled, bool removeCompletedItems)
        => _plugin.AutoGatherListsManager.UpdateIpcList(path, targets, enabled, removeCompletedItems);

    [EzIPC]
    public bool SetAutoGatherListEnabled(string path, bool enabled)
        => _plugin.AutoGatherListsManager.SetIpcListEnabled(path, enabled);

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        // EzIPC disposal is handled in GatherBuddy.cs Dispose method
    }
}
