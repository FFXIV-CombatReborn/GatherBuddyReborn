using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.Throttlers;
using ECommons;
using GatherBuddy.Plugin;
using System;
using ECommons.DalamudServices;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        internal void PauseArtisan()
        {
            try
            {
                if (IsArtisanOperating())
                {
                    WasArtisanPaused = true;
                    GatherBuddy.Log.Information("Paused Artisan");
                    Artisan_IPCSubscriber.SetStopRequest(true);
                    TaskManager.Enqueue(() => AutoStatus = "Waiting for Artisan to stop crafting...");
                    TaskManager.Enqueue(() => !IsCrafting, 240000);
                    TaskManager.Enqueue(() =>
                    {
                        if (IsCrafting)
                        {
                            Communicator.Print("[GatherBuddy Reborn] Requested Artisan to stop crafting but crafting state has gone longer than 4 minutes...Aborting Auto Gather");
                            AbortAutoGather();
                        }
                    });
                }
            }
            catch (IpcNotReadyError) { }
            catch (Exception ex)
            {
                {
                    ex.Log();
                }
            }
        }

        internal void RestartArtisan()
        {
            if (!IsArtisanOperating())
            {
                GatherBuddy.Log.Information("Artisan is previously paused...Attempting to restart");
                if (GenericHelpers.IsOccupied())
                {
                    EzThrottler.Throttle("ArtisanCanReenableOccupied", 2500, true);
                }
                if (EzThrottler.Check("ArtisanCanReenableOccupied"))
                {
                    GatherBuddy.Log.Information("Successfully restarted Artisan");
                    WasArtisanPaused = false;
                    Artisan_IPCSubscriber.SetStopRequest(false);
                }
            }
        }

        internal bool IsArtisanOperating()
        {
            try
            {
                return Artisan_IPCSubscriber.IsListRunning() || Artisan_IPCSubscriber.GetEnduranceStatus();
            }
            catch (IpcNotReadyError) { }
            catch (Exception ex)
            {
                ex.LogWarning();
            }
            return false;
        }
    }
}
