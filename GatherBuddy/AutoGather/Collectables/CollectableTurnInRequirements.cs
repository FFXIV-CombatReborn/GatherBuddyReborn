using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Collectables;

internal static class CollectableTurnInRequirements
{
    public const string AllaganItemSearchInternalName = "AllaganItemSearch";
    public const string UnavailableStatusText = "Collectable turn-ins require Allagan Tools or Allagan Item Search.";
    public const string UnavailableHelpText = "Load Allagan Tools or Allagan Item Search to use collectable turn-ins.";

    public static bool IsAvailable
        => AllaganTools.Enabled || IPCSubscriber.IsReady(AllaganItemSearchInternalName);
}
