using GatherBuddy.Plugin;

namespace GatherBuddy.Vulcan.Vendors;

internal static class VendorAutomationRequirements
{
    public const string AllaganItemSearchInternalName = "AllaganItemSearch";
    public const string UnavailableStatusText = "Vendor automation requires Allagan Tools or Allagan Item Search.";
    public const string UnavailableHelpText = "Load Allagan Tools or Allagan Item Search to use vendor automation.";

    public static bool IsAvailable
        => AllaganTools.Enabled || IPCSubscriber.IsReady(AllaganItemSearchInternalName);
}
