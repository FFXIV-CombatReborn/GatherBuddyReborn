using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GatherBuddy.Plugin;

namespace GatherBuddy.Vulcan.Vendors;

public sealed partial class VendorBuyListManager
{
    public readonly record struct TeamCraftImportResult(VendorBuyListDefinition? List, string? Error, string? Warning);
    private readonly record struct TeamCraftVendorImportLine(string ItemName, uint Quantity);

    public TeamCraftImportResult ImportTeamCraftList(string text, Guid? destinationListId = null, string? newListName = null)
    {
        if (IsBusy)
        {
            GatherBuddy.Log.Debug("[VendorBuyListManager] Ignoring TeamCraft vendor import because the manager is busy.");
            return new TeamCraftImportResult(null, "Vendor lists cannot be modified while a purchase is running.", null);
        }

        EnsureVendorCachesAvailable();
        if (!VendorShopResolver.IsInitialized)
        {
            GatherBuddy.Log.Debug("[VendorBuyListManager] TeamCraft vendor import requested before vendor data finished loading.");
            return new TeamCraftImportResult(null, "Vendor data is still loading.", null);
        }

        VendorBuyListDefinition? destinationList = null;
        if (destinationListId.HasValue)
        {
            destinationList = GetList(destinationListId.Value);
            if (destinationList == null)
            {
                GatherBuddy.Log.Debug($@"[VendorBuyListManager] TeamCraft vendor import requested for missing vendor list {destinationListId.Value}.");
                return new TeamCraftImportResult(null, "The selected vendor list no longer exists.", null);
            }

            GatherBuddy.Log.Debug($@"[VendorBuyListManager] TeamCraft vendor import will update existing vendor list '{destinationList.Name}'.");
        }

        var parsedLines = ParseTeamCraftVendorImport(text);
        if (parsedLines.Count == 0)
        {
            GatherBuddy.Log.Debug("[VendorBuyListManager] TeamCraft vendor import contained no parsable vendor lines.");
            return new TeamCraftImportResult(null, "No TeamCraft vendor items were found in the pasted text.", null);
        }

        var resolvedEntries = new List<(VendorShopEntry Entry, VendorNpc Vendor, uint TargetQuantity)>();
        var unresolvedItems = new List<string>();
        foreach (var line in parsedLines)
        {
            if (!TryResolveTeamCraftImportEntry(line.ItemName, out var entry, out var vendor))
            {
                unresolvedItems.Add(line.ItemName);
                GatherBuddy.Log.Debug($"[VendorBuyListManager] TeamCraft vendor import could not resolve '{line.ItemName}' to a supported vendor route.");
                continue;
            }

            var targetQuantity = line.Quantity;
            resolvedEntries.Add((entry, vendor, targetQuantity));
            GatherBuddy.Log.Debug(
                $"[VendorBuyListManager] TeamCraft vendor import resolved {line.Quantity:N0}x {line.ItemName} to {DescribeShopType(entry.ShopType)} vendor '{vendor.Name}' with target {targetQuantity:N0}.");
        }

        if (resolvedEntries.Count == 0)
        {
            var errorMessage = unresolvedItems.Count == 1
                ? $"Could not resolve '{unresolvedItems[0]}' in the vendor data."
                : $"Could not resolve any of the {unresolvedItems.Count:N0} pasted items in the vendor data.";
            GatherBuddy.Log.Debug($"[VendorBuyListManager] TeamCraft vendor import failed: {errorMessage}");
            return new TeamCraftImportResult(null, errorMessage, null);
        }

        var createdList = false;
        var list = destinationList;
        if (list == null)
        {
            var listName = string.IsNullOrWhiteSpace(newListName)
                ? "Imported from TeamCraft"
                : newListName.Trim();
            list = CreateList(listName, false);
            createdList = true;
            GatherBuddy.Log.Debug($@"[VendorBuyListManager] TeamCraft vendor import created destination list '{list.Name}'.");
        }
        var importedEntryCount = 0;
        foreach (var resolvedEntry in resolvedEntries)
        {
            if (!TrySetResolvedTarget(list, resolvedEntry.Entry, resolvedEntry.Vendor, resolvedEntry.TargetQuantity))
            {
                unresolvedItems.Add(resolvedEntry.Entry.ItemName);
                GatherBuddy.Log.Debug(
                    $"[VendorBuyListManager] TeamCraft vendor import failed to add {resolvedEntry.Entry.ItemName} to '{list.Name}' after it was resolved.");
                continue;
            }

            importedEntryCount++;
        }

        if (importedEntryCount == 0)
        {
            if (createdList)
            {
                GatherBuddy.Config.VendorBuyLists.Remove(list);
                GatherBuddy.Config.Save();
            }
            GatherBuddy.Log.Debug("[VendorBuyListManager] TeamCraft vendor import produced no importable entries after resolution.");
            return new TeamCraftImportResult(null, "No supported vendor routes were available for the pasted items.", null);
        }

        GatherBuddy.Config.ActiveVendorBuyListId = list.Id;
        GatherBuddy.Config.Save();

        var warning = BuildTeamCraftImportWarning(unresolvedItems);
        _statusText = warning == null
            ? $"Imported {importedEntryCount:N0} TeamCraft vendor item(s) into '{list.Name}'."
            : $"Imported {importedEntryCount:N0} TeamCraft vendor item(s) into '{list.Name}'. {warning}";
        GatherBuddy.Log.Information($"[VendorBuyListManager] {_statusText}");
        return new TeamCraftImportResult(list, null, warning);
    }

    private static List<TeamCraftVendorImportLine> ParseTeamCraftVendorImport(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var parsedLines = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        var sawSectionHeader = false;
        var inVendorSection = false;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Length == 0)
                continue;

            if (TryGetTeamCraftSectionHeader(trimmedLine, out var sectionHeader))
            {
                sawSectionHeader = true;
                inVendorSection = sectionHeader.Equals("Vendor", StringComparison.OrdinalIgnoreCase)
                    || sectionHeader.Equals("Vendors", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (sawSectionHeader && !inVendorSection)
                continue;

            if (!TryParseTeamCraftVendorImportLine(trimmedLine, out var itemName, out var quantity))
                continue;

            if (parsedLines.TryGetValue(itemName, out var existingQuantity))
                parsedLines[itemName] = SaturatingAdd(existingQuantity, quantity);
            else
                parsedLines[itemName] = quantity;
        }

        return parsedLines
            .Select(kvp => new TeamCraftVendorImportLine(kvp.Key, kvp.Value))
            .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetTeamCraftSectionHeader(string line, out string header)
    {
        header = string.Empty;
        var separatorIndex = line.LastIndexOf(':');
        if (separatorIndex < 0 || separatorIndex != line.Length - 1)
            return false;

        header = line[..separatorIndex].Trim();
        return header.Length > 0;
    }

    private static bool TryParseTeamCraftVendorImportLine(string line, out string itemName, out uint quantity)
    {
        itemName = string.Empty;
        quantity = 0;

        var quantitySeparatorIndex = line.IndexOf('x');
        if (quantitySeparatorIndex <= 0)
            return false;

        if (!uint.TryParse(line[..quantitySeparatorIndex].Trim(), out quantity) || quantity == 0)
            return false;

        itemName = line[(quantitySeparatorIndex + 1)..].Trim();
        return itemName.Length > 0;
    }

    private static bool TryResolveTeamCraftImportEntry(string itemName, out VendorShopEntry entry, out VendorNpc vendor)
    {
        foreach (var candidate in GetTeamCraftImportCandidates(itemName))
        {
            var supportedVendors = VendorDevExclusions.GetSelectableNpcs(
                candidate.Npcs
                    .Where(npc => VendorPurchaseManager.IsPurchaseSupported(candidate, npc))
                    .ToList(),
                "resolving a TeamCraft vendor import item",
                itemName);
            if (supportedVendors.Count == 0)
                continue;

            var preferredVendor = VendorPreferenceHelper.ResolvePreferredNpc(candidate, supportedVendors);
            if (preferredVendor == null)
                continue;

            entry = candidate;
            vendor = preferredVendor;
            return true;
        }

        entry = null!;
        vendor = null!;
        return false;
    }

    private static IEnumerable<VendorShopEntry> GetTeamCraftImportCandidates(string itemName)
        => VendorShopResolver.GilShopEntries
            .Where(entry => entry.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            .Concat(VendorShopResolver.GcShopEntries
                .Where(VendorShopResolver.MatchesCurrentGrandCompany)
                .Where(entry => entry.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
            .Concat(VendorShopResolver.SpecialShopEntries
                .Where(entry => entry.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(GetTeamCraftImportPriority)
            .ThenBy(entry => entry.Cost)
            .ThenBy(entry => entry.CurrencyItemId)
            .ThenBy(entry => entry.ItemId);

    private static int GetTeamCraftImportPriority(VendorShopEntry entry)
        => entry.ShopType switch
        {
            VendorShopType.GilShop           => 0,
            VendorShopType.GrandCompanySeals => 1,
            VendorShopType.SpecialCurrency   => 2,
            _                                => 3,
        };

    private static string? BuildTeamCraftImportWarning(IReadOnlyList<string> unresolvedItems)
    {
        if (unresolvedItems.Count == 0)
            return null;

        var displayedItems = unresolvedItems
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var remainingCount = unresolvedItems
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(displayedItems.Count)
            .Count();
        var itemPreview = string.Join(", ", displayedItems);
        return remainingCount > 0
            ? $"Skipped {unresolvedItems.Count:N0} item(s): {itemPreview}, and {remainingCount:N0} more."
            : $"Skipped {unresolvedItems.Count:N0} item(s): {itemPreview}.";
    }
}
