using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Vendors;

public static class VendorDevExclusions
{
    private static readonly HashSet<uint> ExcludedNpcIds =
    [
        1008119u,
        1016296u,
        1019797u,
        1026074u,
        1028250u,
        1034489u,
        1033259u,
        1036894u,
        1042833u,
        1044880u,
        1046491u,
        1049034u,
    ];
    private static readonly HashSet<string> ExcludedRouteKeys = new(StringComparer.Ordinal);

    public static IReadOnlyList<VendorNpc> GetSelectableNpcs(IReadOnlyList<VendorNpc> npcs, string context, string? itemName = null)
    {
        if (npcs.Count == 0 || !HasExclusions)
            return npcs;

        var selectableNpcs = new List<VendorNpc>(npcs.Count);
        foreach (var npc in npcs)
            if (!TryGetExclusionReason(npc, out _))
                selectableNpcs.Add(npc);

        return selectableNpcs;
    }

    public static bool IsExcluded(VendorNpc npc)
        => TryGetExclusionReason(npc, out _);

    private static bool HasExclusions
        => ExcludedNpcIds.Count > 0 || ExcludedRouteKeys.Count > 0;

    private static bool TryGetExclusionReason(VendorNpc npc, out string reason)
    {
        var routeKey = VendorPreferenceHelper.GetRouteKey(npc);
        if (ExcludedRouteKeys.Contains(routeKey))
        {
            reason = $"route={routeKey}";
            return true;
        }

        if (ExcludedNpcIds.Contains(npc.NpcId))
        {
            reason = $"npcId={npc.NpcId}";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
