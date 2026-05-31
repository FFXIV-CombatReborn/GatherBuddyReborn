using FFXIVClientStructs.FFXIV.Client.Game.WKS;

namespace GatherBuddy.Deimos;

/// safe accessor over the game's WKSManager (cosmic state)
internal static unsafe class Wks
{
    public static WKSManager* Manager => WKSManager.Instance();

    public static bool Available => Manager != null;

    /// active mission row id, 0 if none
    public static uint CurrentMissionId
    {
        get
        {
            var m = Manager;
            // State is internal to plugins; use the obsolete top-level field
#pragma warning disable CS0618
            return m != null ? m->CurrentMissionUnitRowId : 0u;
#pragma warning restore CS0618
        }
    }

    public static bool IsMissionCompleted(uint missionUnitId)
    {
        var m = Manager;
        return m != null && m->IsMissionCompleted(missionUnitId);
    }

    public static bool IsMissionGolded(uint missionUnitId)
    {
        var m = Manager;
        return m != null && m->IsMissionGolded(missionUnitId);
    }
}
