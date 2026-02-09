using GatherBuddy.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
namespace GatherBuddy.Plugin
{
    internal static class IPCSubscriber
    {
        public static bool IsReady(string pluginName)
            => ReflectionHelpers.TryGetDalamudPlugin(pluginName, out _);
    }

    internal static class VNavmesh
    {
        internal static bool Enabled
            => IPCSubscriber.IsReady("vnavmesh");

        internal static class Nav
        {
            static Nav()
            {
                EzIPC.Init(typeof(Nav), "vnavmesh");
                Debug.Assert(IsReady != null);
                Debug.Assert(BuildProgress != null);
                Debug.Assert(Reload != null);
                Debug.Assert(Rebuild != null);
                Debug.Assert(Pathfind != null);
                Debug.Assert(PathfindCancelable != null);
                Debug.Assert(PathfindCancelAll != null);
                Debug.Assert(PathfindInProgress != null);
                Debug.Assert(PathfindNumQueued != null);
                Debug.Assert(IsAutoLoad != null);
                Debug.Assert(SetAutoLoad != null);
            }

            [EzIPC("vnavmesh.Nav.IsReady", applyPrefix: false)]
            internal static readonly Func<bool> IsReady;

            [EzIPC("vnavmesh.Nav.BuildProgress", applyPrefix: false)]
            internal static readonly Func<float> BuildProgress;

            [EzIPC("vnavmesh.Nav.Reload", applyPrefix: false)]
            internal static readonly Func<bool> Reload;

            [EzIPC("vnavmesh.Nav.Rebuild", applyPrefix: false)]
            internal static readonly Func<bool> Rebuild;

            [EzIPC("vnavmesh.Nav.Pathfind", applyPrefix: false)]
            internal static readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

            [EzIPC("vnavmesh.Nav.PathfindCancelable", applyPrefix: false)]
            internal static readonly Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> PathfindCancelable;

            [EzIPC("vnavmesh.Nav.PathfindCancelAll", applyPrefix: false)]
            internal static readonly Action PathfindCancelAll;

            [EzIPC("vnavmesh.Nav.PathfindInProgress", applyPrefix: false)]
            internal static readonly Func<bool> PathfindInProgress;

            [EzIPC("vnavmesh.Nav.PathfindNumQueued", applyPrefix: false)]
            internal static readonly Func<int> PathfindNumQueued;

            [EzIPC("vnavmesh.Nav.IsAutoLoad", applyPrefix: false)]
            internal static readonly Func<bool> IsAutoLoad;

            [EzIPC("vnavmesh.Nav.SetAutoLoad", applyPrefix: false)]
            internal static readonly Action<bool> SetAutoLoad;
        }

        internal static class Query
        {
            internal static class Mesh
            {
                static Mesh()
                {
                    EzIPC.Init(typeof(Mesh), "vnavmesh");
                    Debug.Assert(NearestPoint != null);
                    Debug.Assert(PointOnFloor != null);
                }

                /// <summary>
                /// Returns the nearest point on the ground to the given position,
                /// within the specified horizontal and vertical range (box-shaped).
                /// </summary>
                [EzIPC("vnavmesh.Query.Mesh.NearestPoint", applyPrefix: false)]
                internal static readonly Func<Vector3, float, float, Vector3?> NearestPoint;

                /// <summary>
                /// Returns the highest point on the mesh within the specified horizontal
                /// range (box-shaped) that is not above the given position.
                /// </summary>
                [EzIPC("vnavmesh.Query.Mesh.PointOnFloor", applyPrefix: false)]
                internal static readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;
            }
        }

        internal static class Path
        {
            static Path()
            {
                EzIPC.Init(typeof(Path), "vnavmesh");
                Debug.Assert(MoveTo != null);
                Debug.Assert(Stop != null);
                Debug.Assert(IsRunning != null);
                Debug.Assert(NumWaypoints != null);
                Debug.Assert(ListWaypoints != null);
                Debug.Assert(GetMovementAllowed != null);
                Debug.Assert(SetMovementAllowed != null);
                Debug.Assert(GetAlignCamera != null);
                Debug.Assert(SetAlignCamera != null);
                Debug.Assert(GetTolerance != null);
                Debug.Assert(SetTolerance != null);
            }

            [EzIPC("vnavmesh.Path.MoveTo", applyPrefix: false)]
            internal static readonly Action<List<Vector3>, bool> MoveTo;

            [EzIPC("vnavmesh.Path.Stop", applyPrefix: false)]
            internal static readonly Action Stop;

            [EzIPC("vnavmesh.Path.IsRunning", applyPrefix: false)]
            internal static readonly Func<bool> IsRunning;

            [EzIPC("vnavmesh.Path.NumWaypoints", applyPrefix: false)]
            internal static readonly Func<int> NumWaypoints;

            [EzIPC("vnavmesh.Path.ListWaypoints", applyPrefix: false)]
            internal static readonly Func<List<Vector3>> ListWaypoints;

            [EzIPC("vnavmesh.Path.GetMovementAllowed", applyPrefix: false)]
            internal static readonly Func<bool> GetMovementAllowed;

            [EzIPC("vnavmesh.Path.SetMovementAllowed", applyPrefix: false)]
            internal static readonly Action<bool> SetMovementAllowed;

            [EzIPC("vnavmesh.Path.GetAlignCamera", applyPrefix: false)]
            internal static readonly Func<bool> GetAlignCamera;

            [EzIPC("vnavmesh.Path.SetAlignCamera", applyPrefix: false)]
            internal static readonly Action<bool> SetAlignCamera;

            [EzIPC("vnavmesh.Path.GetTolerance", applyPrefix: false)]
            internal static readonly Func<float> GetTolerance;

            [EzIPC("vnavmesh.Path.SetTolerance", applyPrefix: false)]
            internal static readonly Action<float> SetTolerance;
        }

        internal static class SimpleMove
        {
            static SimpleMove()
            {
                EzIPC.Init(typeof(SimpleMove), "vnavmesh");
                Debug.Assert(PathfindAndMoveTo != null);
                Debug.Assert(PathfindAndMoveCloseTo != null);
                Debug.Assert(PathfindInProgress != null);
            }

            [EzIPC("vnavmesh.SimpleMove.PathfindAndMoveTo", applyPrefix: false)]
            internal static readonly Func<Vector3, bool, bool> PathfindAndMoveTo;

            [EzIPC("vnavmesh.SimpleMove.PathfindAndMoveCloseTo", applyPrefix: false)]
            internal static readonly Func<Vector3, bool, float, bool> PathfindAndMoveCloseTo;

            [EzIPC("vnavmesh.SimpleMove.PathfindInProgress", applyPrefix: false)]
            internal static readonly Func<bool> PathfindInProgress;
        }

        internal static class Window
        {
            static Window()
            {
                EzIPC.Init(typeof(Window), "vnavmesh");
                Debug.Assert(IsOpen != null);
                Debug.Assert(SetOpen != null);
            }

            [EzIPC("vnavmesh.Window.IsOpen", applyPrefix: false)]
            internal static readonly Func<bool> IsOpen;

            [EzIPC("vnavmesh.Window.SetOpen", applyPrefix: false)]
            internal static readonly Action<bool> SetOpen;
        }

        internal static class DTR
        {
            static DTR()
            {
                EzIPC.Init(typeof(DTR), "vnavmesh");
                Debug.Assert(IsShown != null);
                Debug.Assert(SetShown != null);
            }

            [EzIPC("vnavmesh.DTR.IsShown", applyPrefix: false)]
            internal static readonly Func<bool> IsShown;

            [EzIPC("vnavmesh.DTR.SetShown", applyPrefix: false)]
            internal static readonly Action<bool> SetShown;
        }
    }

    internal static class Lifestream
        {
            static Lifestream()
            {
                EzIPC.Init(typeof(Lifestream), "Lifestream");
                Debug.Assert(ExecuteCommand != null);
                Debug.Assert(IsBusy != null);
                Debug.Assert(Abort != null);
                Debug.Assert(AethernetTeleport != null);
                Debug.Assert(ChangeCharacter != null);
            }

        internal static bool Enabled
            => IPCSubscriber.IsReady("Lifestream");

        [EzIPC("Lifestream.ExecuteCommand", applyPrefix: false)]
        internal static readonly Action<string> ExecuteCommand;

        [EzIPC("Lifestream.IsBusy", applyPrefix: false)]
        internal static readonly Func<bool> IsBusy;

        [EzIPC("Lifestream.Abort", applyPrefix: false)]
        internal static readonly Action Abort;

        [EzIPC("Lifestream.AethernetTeleport", applyPrefix: false)]
        internal static readonly Func<string, bool> AethernetTeleport;
        
        [EzIPC("Lifestream.ChangeCharacter", applyPrefix: false)]
        internal static readonly Func<string, string, int> ChangeCharacter;
    }

    internal static class YesAlready
    {
        private static bool _locked = false;
        internal static void Lock()
        {
            if (!_locked && Dalamud.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var stopRequests))
            {
                stopRequests.Add(Dalamud.PluginInterface.InternalName);
                _locked = true;
            }
        }
        internal static void Unlock()
        {
            if (_locked && Dalamud.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var stopRequests))
            {
                stopRequests.Remove(Dalamud.PluginInterface.InternalName);
                _locked = false;
            }
        }
    }

    internal static class AllaganTools
        {
            static AllaganTools()
            {
                EzIPC.Init(typeof(AllaganTools), "AllaganTools");
                Debug.Assert(ItemCountOwned != null);
            }

            internal static bool Enabled => IPCSubscriber.IsReady("InventoryTools");

        [EzIPC("AllaganTools.ItemCountOwned", applyPrefix: false)]
        internal static readonly Func<uint, bool, uint[], uint> ItemCountOwned;
    }

    internal static class AutoHook
        {
            static AutoHook()
            {
                EzIPC.Init(typeof(AutoHook), "AutoHook");
                Debug.Assert(GetPluginState != null);
                Debug.Assert(GetAutoStartFishing != null);
                Debug.Assert(SetPluginState != null);
                Debug.Assert(SetAutoStartFishing != null);
                Debug.Assert(SetAutoGigState != null);
                Debug.Assert(CreateAndSelectAnonymousPreset != null);
                Debug.Assert(ImportAndSelectPreset != null);
                Debug.Assert(SetPreset != null);
                Debug.Assert(DeleteSelectedPreset != null);
            }

            internal static bool Enabled => IPCSubscriber.IsReady("AutoHook");

        [EzIPC("AutoHook.GetPluginState", applyPrefix: false)]
        internal static readonly Func<bool> GetPluginState;

        [EzIPC("AutoHook.GetAutoStartFishing", applyPrefix: false)]
        internal static readonly Func<bool> GetAutoStartFishing;

        [EzIPC("AutoHook.SetPluginState", applyPrefix: false)]
        internal static readonly Action<bool> SetPluginState;

        [EzIPC("AutoHook.SetAutoStartFishing", applyPrefix: false)]
        internal static readonly Action<bool> SetAutoStartFishing;

        [EzIPC("AutoHook.SetAutoGigState", applyPrefix: false)]
        internal static readonly Action<bool> SetAutoGigState;

        [EzIPC("AutoHook.CreateAndSelectAnonymousPreset", applyPrefix: false)]
        internal static readonly Action<string> CreateAndSelectAnonymousPreset;
        
        [EzIPC("AutoHook.ImportAndSelectPreset", applyPrefix: false)]
        internal static readonly Action<string> ImportAndSelectPreset;
        
        [EzIPC("AutoHook.SetPreset", applyPrefix: false)]
        internal static readonly Action<string> SetPreset;
        
        [EzIPC("AutoHook.DeleteSelectedPreset", applyPrefix: false)]
        internal static readonly Action DeleteSelectedPreset;
    }

    internal static class AutoRetainer
        {
            public class OfflineRetainerData
            {
                public string Name { get; set; } = string.Empty;
                public uint VentureEndsAt { get; set; }
                public bool HasVenture { get; set; }
            }

            public class OfflineCharacterData
            {
                public ulong CID { get; set; }
                public string Name { get; set; } = string.Empty;
                public string World { get; set; } = string.Empty;
                public bool Enabled { get; set; }
                public List<OfflineRetainerData> RetainerData { get; set; } = new();
            }

            private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(AutoRetainer), "AutoRetainer.PluginState", SafeWrapper.IPCException);

            static AutoRetainer()
            {
                Debug.Assert(IsBusy != null);
                Debug.Assert(GetEnabledRetainers != null);
                Debug.Assert(AbortAllTasks != null);
                Debug.Assert(DisableAllFunctions != null);
                Debug.Assert(EnableMultiMode != null);
                Debug.Assert(GetOfflineCharacterData != null);
            }

            internal static bool IsEnabled => IPCSubscriber.IsReady("AutoRetainer");

        [EzIPC] internal static readonly Func<bool> IsBusy;
        [EzIPC] internal static readonly Func<Dictionary<ulong, HashSet<string>>> GetEnabledRetainers;
        [EzIPC] internal static readonly Action AbortAllTasks;
        [EzIPC] internal static readonly Action DisableAllFunctions;
        [EzIPC] internal static readonly Action EnableMultiMode;
        [EzIPC("AutoRetainer.GetOfflineCharacterData", applyPrefix: false)] internal static readonly Func<ulong, OfflineCharacterData> GetOfflineCharacterData;
    }
}
