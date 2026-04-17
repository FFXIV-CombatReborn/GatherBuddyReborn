using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Vendors;

public static class VendorDevExclusions
{
    private static readonly HashSet<uint> ExcludedNpcIds =
    [
        1008119u,
        1033259u,
    ];
    private static readonly HashSet<string> ExcludedRouteKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedExcludedRouteKeys = new(StringComparer.Ordinal);

    public static IReadOnlyList<VendorNpc> GetSelectableNpcs(IReadOnlyList<VendorNpc> npcs, string context, string? itemName = null)
    {
        if (npcs.Count == 0 || !HasExclusions)
            return npcs;

        var selectableNpcs = new List<VendorNpc>(npcs.Count);
        foreach (var npc in npcs)
        {
            if (!TryGetExclusionReason(npc, out var reason))
            {
                selectableNpcs.Add(npc);
                continue;
            }

            LogExcludedNpc(npc, reason, context, itemName);
        }

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

    private static void LogExcludedNpc(VendorNpc npc, string reason, string context, string? itemName)
    {
        var routeKey = VendorPreferenceHelper.GetRouteKey(npc);
        if (!LoggedExcludedRouteKeys.Add(routeKey))
            return;

        var itemSuffix = string.IsNullOrWhiteSpace(itemName)
            ? string.Empty
            : $" for {itemName}";
        GatherBuddy.Log.Debug($"[VendorDevExclusions] Excluding {npc.Name}{itemSuffix} while {context}: npcId={npc.NpcId}, menu={npc.MenuShopType}, shop={npc.ShopId}, source={npc.SourceShopId}, itemIndex={npc.ShopItemIndex}, route={routeKey}, reason={reason}");
    }
}
