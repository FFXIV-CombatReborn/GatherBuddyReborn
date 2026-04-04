using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using ElliLib.Raii;
using ElliLib.Text;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public class CraftingMaterialsWindow : Window
{
    private CraftingListEditor? _editor;
    private bool _matsOvercapPercent;
    private bool _matsShowPrecrafts;
    private bool _matsPreferVendors;

    private static readonly Vector4 AccentGather = new(0.45f, 1.00f, 0.45f, 1f);
    private static readonly Vector4 AccentDrop   = new(1.00f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 AccentShop   = new(0.80f, 0.55f, 1.00f, 1f);
    private static readonly Vector4 AccentVendor = new(1.00f, 0.85f, 0.20f, 1f);
    private static readonly Vector4 AccentCraft  = new(0.35f, 0.90f, 0.90f, 1f);
    private static readonly Vector4 PanelBg      = new(0.08f, 0.08f, 0.10f, 1.00f);
    private enum RetainerColumnMode
    {
        None,
        Total,
        Split,
    }
    private readonly record struct MaterialEntry(
        uint ItemId,
        int Have,
        int RetNQ,
        int RetHQ,
        int Needed,
        int EffectiveAvailable,
        string Name,
        ushort IconId,
        bool IsPrecraft);

    public CraftingMaterialsWindow() : base("Materials###CraftingMaterials")
    {
        Size           = new Vector2(560, 520);
        SizeCondition  = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(1200, 1400),
        };
    }

    public void SetEditor(CraftingListEditor? editor) => _editor = editor;

    public override void PreDraw()
    {
        if (_editor != null)
            WindowName = $"Materials \u2014 {_editor.ListName}###CraftingMaterials";
    }

    public override void Draw()
    {
        if (_editor == null)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No list open.");
            return;
        }

        if (!_editor.HasCachedMaterials && !_editor.IsGeneratingMaterials)
            _editor.TriggerMaterialsRegeneration();

        if (_editor.IsGeneratingMaterials)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Calculating materials...");
            return;
        }

        var materials = _editor.GetCachedMaterials();

        if (materials.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No materials needed.");
            return;
        }

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null) return;

        var showRetainer = AllaganTools.Enabled;
        var countRetainersTowardNeed = showRetainer && _editor.RetainerRestockEnabled;
        var hideSatisfiedRows = _editor.SkipIfEnoughEnabled;
        var precrafts = _matsShowPrecrafts
            ? _editor.GetCachedPrecraftMaterials()
            : null;
        var snapshotItemIds = materials.Keys.Concat(precrafts != null ? precrafts.Keys : Enumerable.Empty<uint>());
        var retainerSnapshot = showRetainer
            ? _editor.GetRetainerSnapshot(snapshotItemIds)
            : RetainerItemSnapshot.Empty;

        var allEntries = new List<MaterialEntry>();

        foreach (var (itemId, needed) in materials)
        {
            if (!itemSheet.TryGetRow(itemId, out var item)) continue;
            var have  = _editor.GetInventoryCount(itemId);
            var retNQ = retainerSnapshot.GetCountNQ(itemId);
            var retHQ = retainerSnapshot.GetCountHQ(itemId);
            var effectiveAvailable = _editor.GetQualityAwareAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed);
            allEntries.Add(new MaterialEntry(itemId, have, retNQ, retHQ, needed, effectiveAvailable, item.Name.ExtractText(), item.Icon, false));
        }
        if (precrafts != null)
        {
            foreach (var (itemId, needed) in precrafts)
            {
                if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                var have  = _editor.GetInventoryCount(itemId);
                var retNQ = retainerSnapshot.GetCountNQ(itemId);
                var retHQ = retainerSnapshot.GetCountHQ(itemId);
                var effectiveAvailable = _editor.GetQualityAwareAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed);
                allEntries.Add(new MaterialEntry(itemId, have, retNQ, retHQ, needed, effectiveAvailable, item.Name.ExtractText(), item.Icon, true));
            }
        }

        var totalMissing = allEntries.Count(e => e.EffectiveAvailable < e.Needed);
        var totalReady   = allEntries.Count - totalMissing;
        var visibleEntries = hideSatisfiedRows
            ? allEntries.Where(e => e.EffectiveAvailable < e.Needed).ToList()
            : allEntries;

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"{totalMissing} missing  \u00b7  {totalReady} ready");
        ImGui.SameLine();
        ImGui.Checkbox("150%##overcap", ref _matsOvercapPercent);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show % beyond 100 when you have more than needed");
        ImGui.SameLine();
        ImGui.Checkbox("Precrafts##precrafts", ref _matsShowPrecrafts);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include intermediate craftable components");
        ImGui.SameLine();
        ImGui.Checkbox("Prefer Vendors##preferVendors", ref _matsPreferVendors);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Default priority:\n" +
                "  Gather > Fish > Scrip > Drops > Craft > Vendor > Tomes > Other\n\n" +
                "When ON: Gil Vendor overrides Gather, Fish, Drops, and Craft\n" +
                "for items also sold at a Gil shop.");
        if (showRetainer)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh Retainers"))
                _editor.InvalidateRetainerSnapshot();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh retainer NQ/HQ counts. Automatic refresh is disabled here to avoid UI hitching.");
        }
        ImGui.Separator();

        if (visibleEntries.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "All materials ready.");
            return;
        }

        var avail   = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var panelW  = (avail.X - spacing.X) / 2f;

        var preferVendors = _matsPreferVendors;
        MaterialSource Cls(uint id) => MaterialSourceClassifier.Classify(id, preferVendors);

        var gatherList = visibleEntries.Where(e => Cls(e.ItemId) is MaterialSource.Gatherable or MaterialSource.Fish).ToList();
        var dropList   = visibleEntries.Where(e => Cls(e.ItemId) is MaterialSource.Drop).ToList();
        var shopList   = visibleEntries.Where(e => Cls(e.ItemId) is MaterialSource.Scrip or MaterialSource.SpecialCurrency).ToList();
        var vendorList = visibleEntries.Where(e => Cls(e.ItemId) is MaterialSource.GilVendor or MaterialSource.Other).ToList();
        var craftList  = _matsShowPrecrafts ? visibleEntries.Where(e => Cls(e.ItemId) is MaterialSource.Craftable).ToList() : null;

        var nonCraftRetainerMode = showRetainer ? RetainerColumnMode.Total : RetainerColumnMode.None;
        var craftRetainerMode = showRetainer ? RetainerColumnMode.Split : RetainerColumnMode.None;
        var panels = new List<(string Id, string Label, Vector4 Accent, IEnumerable<MaterialEntry> Entries, RetainerColumnMode RetainerColumns)>();
        if (gatherList.Count > 0)         panels.Add(("##gather", "Gather",          AccentGather, gatherList, nonCraftRetainerMode));
        if (dropList.Count > 0)           panels.Add(("##drop",   "Drops / Bicolor", AccentDrop,   dropList,   nonCraftRetainerMode));
        if (shopList.Count > 0)           panels.Add(("##shop",   "Scrip / Tomes",   AccentShop,   shopList,   nonCraftRetainerMode));
        if (vendorList.Count > 0)         panels.Add(("##vendor", "Vendor",          AccentVendor, vendorList, nonCraftRetainerMode));
        if (craftList is { Count: > 0 })  panels.Add(("##craft",  "Craft",           AccentCraft,  craftList,  craftRetainerMode));

        if (panels.Count == 0) return;

        var rows   = (panels.Count + 1) / 2;
        var panelH = (avail.Y - spacing.Y * (rows - 1)) / rows;

        for (var i = 0; i < panels.Count; i++)
        {
            var (id, label, accent, entries, retainerColumns) = panels[i];
            var isLast   = i == panels.Count - 1;
            var spanFull = isLast && panels.Count % 2 == 1;
            DrawMaterialPanel(id, label, accent, entries, retainerColumns, spanFull ? avail.X : panelW, panelH);
            if (!spanFull && i % 2 == 0)
                ImGui.SameLine();
        }
    }

    private void DrawMaterialPanel(
        string id, string label, Vector4 accent,
        IEnumerable<MaterialEntry> source,
        RetainerColumnMode retainerColumnMode, float width, float height)
    {
        static void DrawCenteredHeader(string text, string? tooltip = null)
        {
            var textWidth = ImGui.CalcTextSize(text).X;
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var offset = (availableWidth - textWidth) * 0.5f;
            if (offset > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

            ImGui.TextUnformatted(text);
            if (tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
        var entries = source
            .OrderBy(e => e.EffectiveAvailable >= e.Needed)
            .ThenBy(e => e.Name)
            .ToList();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBg))
        {
            ImGui.BeginChild(id, new Vector2(width, height), true);
            var colCount = retainerColumnMode switch
            {
                RetainerColumnMode.None  => 4,
                RetainerColumnMode.Total => 5,
                RetainerColumnMode.Split => 6,
                _                        => 4,
            };
            const float numW = 36f;
            const float barW = 46f;
            var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                           | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;

            if (ImGui.BeginTable($"{id}_tbl", colCount, tableFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, numW);
                switch (retainerColumnMode)
                {
                    case RetainerColumnMode.Total:
                        ImGui.TableSetupColumn("Ret", ImGuiTableColumnFlags.WidthFixed, numW);
                        break;
                    case RetainerColumnMode.Split:
                        ImGui.TableSetupColumn("RNQ", ImGuiTableColumnFlags.WidthFixed, numW);
                        ImGui.TableSetupColumn("RHQ", ImGuiTableColumnFlags.WidthFixed, numW);
                        break;
                }
                ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, numW);
                ImGui.TableSetupColumn("%",    ImGuiTableColumnFlags.WidthFixed, barW);
                var needIdx = retainerColumnMode switch
                {
                    RetainerColumnMode.None  => 2,
                    RetainerColumnMode.Total => 3,
                    RetainerColumnMode.Split => 4,
                    _                        => 2,
                };

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableSetColumnIndex(0);
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                    ImGui.TableHeader(label);
                ImGui.TableSetColumnIndex(1);
                DrawCenteredHeader("Have");
                switch (retainerColumnMode)
                {
                    case RetainerColumnMode.Total:
                        ImGui.TableSetColumnIndex(2);
                        DrawCenteredHeader("Ret", "Retainer total (via Allagan Tools)");
                        break;
                    case RetainerColumnMode.Split:
                        ImGui.TableSetColumnIndex(2);
                        DrawCenteredHeader("RNQ", "Retainer NQ (via Allagan Tools)");
                        ImGui.TableSetColumnIndex(3);
                        DrawCenteredHeader("RHQ", "Retainer HQ (via Allagan Tools)");
                        break;
                }
                ImGui.TableSetColumnIndex(needIdx);
                DrawCenteredHeader("Need");
                ImGui.TableSetColumnIndex(needIdx + 1);
                DrawCenteredHeader("%");

                if (entries.Count == 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "  \u2014");
                }
                else
                {
                    foreach (var e in entries)
                    {
                        DrawPanelRow(e.ItemId, e.Have, e.RetNQ, e.RetHQ, e.Needed, e.Name, e.IconId,
                            e.EffectiveAvailable >= e.Needed, retainerColumnMode, e.IsPrecraft, e.EffectiveAvailable);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.EndChild();
        }
    }

    private void DrawPanelRow(uint itemId, int have, int retNQ, int retHQ, int needed, string name, ushort iconId,
        bool satisfied, RetainerColumnMode retainerColumnMode, bool isPrecraft, int effectiveAvailable)
    {
        ImGui.TableNextRow();
        Vector4 rowColor = (satisfied, isPrecraft) switch
        {
            (true,  false) => new Vector4(0.15f, 0.50f, 0.15f, 0.25f),
            (true,  true)  => new Vector4(0.05f, 0.40f, 0.45f, 0.25f),
            (false, false) => new Vector4(0.50f, 0.15f, 0.15f, 0.25f),
            (false, true)  => new Vector4(0.55f, 0.35f, 0.05f, 0.25f),
        };
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(rowColor));

        var lineH    = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);

        static string Trunc(int v) => v > 9999 ? "9999" : v.ToString();
        void CenterNum(int raw, Vector4 color)
        {
            var s   = Trunc(raw);
            var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize(s).X) * 0.5f;
            if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off);
            ImGui.TextColored(color, s);
            if (raw > 9999 && ImGui.IsItemHovered())
                ImGui.SetTooltip(raw.ToString());
        }

        ImGui.TableNextColumn();
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        if (icon.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, iconSize);
        else
            ImGui.Dummy(iconSize);
        ImGui.SameLine(0, 4);
        ImUtf8.CopyOnClickSelectable(name.AsSpan());
        if (ImGui.BeginPopupContextItem($"##mbctx_{itemId}"))
        {
            if (ImGui.Selectable("Create Link"))
                Communicator.Print(SeString.CreateItemLink(itemId));
            if (ImGui.Selectable("Search Marketboard"))
            {
                GatherBuddy.MarketboardService?.QueueLookup(itemId, name, iconId);
                GatherBuddy.VulcanWindow?.OpenToMarketboardItem(itemId);
            }
            ImGui.EndPopup();
        }

        var haveColor = satisfied ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.45f, 1f);
        ImGui.TableNextColumn();
        CenterNum(have, haveColor);
        switch (retainerColumnMode)
        {
            case RetainerColumnMode.Total:
            {
                ImGui.TableNextColumn();
                var totalRetainer = retNQ + retHQ;
                var retainerColor = totalRetainer > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (totalRetainer > 0)
                    CenterNum(totalRetainer, retainerColor);
                else
                {
                    var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f;
                    if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off);
                    ImGui.TextColored(retainerColor, "-");
                }
                break;
            }
            case RetainerColumnMode.Split:
            {
                ImGui.TableNextColumn();
                var nqColor = retNQ > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (retNQ > 0) CenterNum(retNQ, nqColor);
                else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(nqColor, "-"); }

                ImGui.TableNextColumn();
                var hqColor = retHQ > 0 ? new Vector4(0.5f, 0.85f, 1.0f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (retHQ > 0) CenterNum(retHQ, hqColor);
                else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(hqColor, "-"); }
                break;
            }
            case RetainerColumnMode.None:
            default:
                break;
        }

        ImGui.TableNextColumn();
        CenterNum(needed, new Vector4(1f, 1f, 1f, 1f));

        ImGui.TableNextColumn();
        var ratio    = needed > 0 ? (float)effectiveAvailable / needed : 1f;
        var progress = Math.Clamp(ratio, 0f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram,
            satisfied ? new Vector4(0.2f, 0.65f, 0.2f, 0.9f) : new Vector4(0.65f, 0.2f, 0.2f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.12f, 0.12f, 0.9f));
        ImGui.ProgressBar(progress, new Vector2(ImGui.GetContentRegionAvail().X, lineH), "");
        ImGui.PopStyleColor(2);
        var pctText = _matsOvercapPercent ? $"{ratio * 100f:F0}%" : $"{progress * 100f:F0}%";
        var pctSize = ImGui.CalcTextSize(pctText);
        var barMin  = ImGui.GetItemRectMin();
        var barMax  = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddText(
            new Vector2(
                barMin.X + (barMax.X - barMin.X - pctSize.X) * 0.5f,
                barMin.Y + (barMax.Y - barMin.Y - pctSize.Y) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text),
            pctText);
    }

}
