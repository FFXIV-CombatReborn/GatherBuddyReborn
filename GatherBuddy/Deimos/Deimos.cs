using System;
using Dalamud.Interface.Windowing;
using GatherBuddy.AutoGather;
using GatherBuddy.Deimos.Data;
using GatherBuddy.Deimos.Scheduling;
using GatherBuddy.Deimos.Ui;

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

    private readonly Scheduler    _scheduler;
    private readonly WindowSystem _windows;
    private readonly DeimosWindow _window;

    /// runtime on/off; boots idle
    public bool Enabled { get; private set; }

    public DeimosState State => _scheduler.State;

    public int MissionsDone => _scheduler.MissionsDone;

    public Deimos(GatherBuddy plugin)
    {
        _plugin     = plugin;
        Config      = DeimosConfig.Load();
        TaskManager = new TaskManager(Dalamud.Framework) { ShowDebug = false };
        _scheduler  = new Scheduler(Config);

        // own window system so the UI stays separate from the GBR interface
        _windows = new WindowSystem("Deimos");
        _window  = new DeimosWindow(this);
        _windows.AddWindow(_window);
        Dalamud.PluginInterface.UiBuilder.Draw += _windows.Draw;

        DeimosLog.Info("Initialized.");
    }

    public void ToggleUi() => _window.Toggle();

    public void Enable()
    {
        if (Enabled)
            return;

        Enabled = true;
        _scheduler.Start();
        if (CosmicZone.InCosmicZone)
            DeimosLog.Info("Enabled.");
        else
            DeimosLog.Info($"Enabled (armed) - waiting until you enter a cosmic zone (current territory {CosmicZone.Current}).");
    }

    public void Disable()
    {
        if (!Enabled)
            return;

        Enabled = false;
        _scheduler.Stop();
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
            // stay armed and idle until we're in a cosmic zone - don't tear down
            if (DeimosThrottle.Throttle("await-zone", 10000))
                DeimosLog.Info($"Waiting: not in a cosmic zone (territory {CosmicZone.Current}).");
            return;
        }

        MissionData.EnsureBuilt();
        _scheduler.Tick();
    }

    public void Dispose()
    {
        Dalamud.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        _windows.RemoveAllWindows();
        TaskManager.Dispose();
        Config.SaveIfDirty();
    }
}
