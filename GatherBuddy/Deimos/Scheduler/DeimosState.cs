namespace GatherBuddy.Deimos.Scheduling;

/// where the mission loop currently is
public enum DeimosState
{
    Idle,
    Start,
    CheckState,
    GrabMission,
    ExecuteMission,
    Craft,
    TurnIn,
    Maintenance,
    Abandon,
}
