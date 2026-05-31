using System;
using GatherBuddy.AutoGather;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos;

/// <summary>
/// Cosmic Exploration automation. Gated, idle until enabled, fully removable.
/// </summary>
public sealed class Deimos : IDisposable
{
    private readonly GatherBuddy _plugin;

    public DeimosConfig Config { get; }

    /// own scheduler, separate from AutoGather
    public TaskManager TaskManager { get; }

    /// runtime on/off; boots idle
    public bool Enabled { get; private set; }

    public Deimos(GatherBuddy plugin)
    {
        _plugin     = plugin;
        Config      = DeimosConfig.Load();
        TaskManager = new TaskManager(Dalamud.Framework) { ShowDebug = false };
        DeimosLog.Info("Initialized.");
    }

    public void Enable()
    {
        if (Enabled)
            return;

        Enabled = true;
        DeimosLog.Info("Enabled.");
    }

    public void Disable()
    {
        if (!Enabled)
            return;

        Enabled = false;
        TaskManager.Abort();
        DeimosLog.Info("Disabled.");
    }

    /// per-frame; cheap when idle
    public void Tick()
    {
        if (!Enabled)
            return;

        if (!CosmicZone.InCosmicZone)
        {
            // left the zone, stop
            Disable();
            return;
        }

        MissionData.EnsureBuilt();

        // gating + data only for now
        if (DeimosThrottle.Throttle("heartbeat", 5000))
            DeimosLog.Verbose($"Active in {CosmicZone.Name(CosmicZone.Current)}: mission {Wks.CurrentMissionId}, {MissionData.Missions.Count} loaded.");
    }

    public void Dispose()
    {
        TaskManager.Dispose();
        Config.SaveIfDirty();
    }
}
