using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;

namespace GatherBuddy.Deimos.Travel;

/// wee vnavmesh wrapper for cosmic movement
internal static class Navigator
{
    private const uint StellarReturnAction = 26;
    private const uint MountRouletteAction = 9;
    private const uint SprintAction        = 4;
    private const uint StellarSprintBuff   = 4398;

    public static bool Mounted => Dalamud.Conditions[ConditionFlag.Mounted];

    public static bool HasStellarSprint
    {
        get
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null)
                return false;
            foreach (var status in player.StatusList)
                if (status.StatusId == StellarSprintBuff)
                    return true;
            return false;
        }
    }

    /// keep Stellar Sprint up while on foot
    public static unsafe void EnsureSprint()
    {
        if (Mounted || HasStellarSprint)
            return;

        var actions = ActionManager.Instance();
        if (actions == null)
            return;
        if (actions->GetActionStatus(ActionType.GeneralAction, SprintAction) == 0)
            actions->UseAction(ActionType.GeneralAction, SprintAction);
    }

    /// pop mount roulette; true if the cast went off (or already mounted)
    public static unsafe bool CastMountRoulette()
    {
        if (Mounted)
            return true;

        var actions = ActionManager.Instance();
        if (actions == null)
            return false;
        if (actions->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) != 0)
            return false;
        return actions->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    public static unsafe void Dismount()
    {
        if (!Mounted)
            return;

        var actions = ActionManager.Instance();
        if (actions != null)
            actions->UseAction(ActionType.Mount, 0);
    }

    public static bool Available => VNavmesh.Enabled;

    public static bool NearHub(float range = 60f)
    {
        if (CosmicZone.HubCenter(CosmicZone.Current) is not { } hub)
            return false;
        var pos = Dalamud.Objects.LocalPlayer?.Position ?? default;
        return Vector3.Distance(pos, hub) < range;
    }

    /// pop Stellar Return; true if the cast went off
    public static unsafe bool CastStellarReturn()
    {
        var actions = ActionManager.Instance();
        if (actions == null)
            return false;
        if (actions->GetActionStatus(ActionType.GeneralAction, StellarReturnAction) != 0)
            return false;
        return actions->UseAction(ActionType.GeneralAction, StellarReturnAction);
    }

    public static bool Moving => VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress();

    public static bool MoveTo(Vector3 destination)
    {
        EnsureSprint();
        return VNavmesh.SimpleMove.PathfindAndMoveTo(destination, false);
    }

    /// resolve a walkable point near a flat x/z guess (map-marker coords are ~world x/z)
    public static Vector3? Ground(float x, float z)
        => VNavmesh.Query.Mesh.NearestPoint(new Vector3(x, 0, z), 16, 10000);

    public static void Stop()
    {
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();
    }
}
