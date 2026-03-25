using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

        var allEntries = new List<(uint itemId, int have, int retNQ, int retHQ, int needed, string name, ushort iconId, bool isPrecraft)>();

        foreach (var (itemId, needed) in materials)
        {
            if (!itemSheet.TryGetRow(itemId, out var item)) continue;
            var have  = _editor.GetInventoryCount(itemId);
            var retNQ = showRetainer ? _editor.GetRetainerCountNQ(itemId) : 0;
            var retHQ = showRetainer ? _editor.GetRetainerCountHQ(itemId) : 0;
            allEntries.Add((itemId, have, retNQ, retHQ, needed, item.Name.ExtractText(), item.Icon, false));
        }

        if (_matsShowPrecrafts)
        {
            var precrafts = _editor.GetCachedPrecraftMaterials();
            foreach (var (itemId, needed) in precrafts)
            {
                if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                var have  = _editor.GetInventoryCount(itemId);
                var retNQ = showRetainer ? _editor.GetRetainerCountNQ(itemId) : 0;
                var retHQ = showRetainer ? _editor.GetRetainerCountHQ(itemId) : 0;
                allEntries.Add((itemId, have, retNQ, retHQ, needed, item.Name.ExtractText(), item.Icon, true));
            }
        }

        var totalMissing = allEntries.Count(e => e.have + e.retNQ + e.retHQ < e.needed);
        var totalReady   = allEntries.Count - totalMissing;

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
        ImGui.Separator();

        var avail   = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var panelW  = (avail.X - spacing.X) / 2f;
        var panelH  = _matsShowPrecrafts
            ? (avail.Y - spacing.Y * 2f) / 3f
            : (avail.Y - spacing.Y) / 2f;
        var craftH  = _matsShowPrecrafts ? panelH : 0f;

        var preferVendors = _matsPreferVendors;
        MaterialSource Cls(uint id) => MaterialSourceClassifier.Classify(id, preferVendors);

        var gatherEntries = allEntries.Where(e => Cls(e.itemId) is MaterialSource.Gatherable or MaterialSource.Fish);
        var dropEntries   = allEntries.Where(e => Cls(e.itemId) is MaterialSource.Drop);
        var shopEntries   = allEntries.Where(e => Cls(e.itemId) is MaterialSource.Scrip or MaterialSource.SpecialCurrency);
        var vendorEntries = allEntries.Where(e => Cls(e.itemId) is MaterialSource.GilVendor or MaterialSource.Other);
        var craftEntries  = allEntries.Where(e => Cls(e.itemId) is MaterialSource.Craftable);

        DrawMaterialPanel("##gather", "Gather",           AccentGather, gatherEntries, showRetainer, panelW, panelH);
        ImGui.SameLine();
        DrawMaterialPanel("##drop",   "Drops / Bicolor",  AccentDrop,   dropEntries,   showRetainer, panelW, panelH);
        DrawMaterialPanel("##shop",   "Scrip / Tomes", AccentShop,   shopEntries,   showRetainer, panelW, panelH);
        ImGui.SameLine();
        DrawMaterialPanel("##vendor", "Vendor",        AccentVendor, vendorEntries, showRetainer, panelW, panelH);

        if (_matsShowPrecrafts)
            DrawMaterialPanel("##craft", "Craft", AccentCraft, craftEntries, showRetainer, avail.X, craftH);
    }

    private void DrawMaterialPanel(
        string id, string label, Vector4 accent,
        IEnumerable<(uint itemId, int have, int retNQ, int retHQ, int needed, string name, ushort iconId, bool isPrecraft)> source,
        bool showRetainer, float width, float height)
    {
        var entries = source
            .OrderBy(e => e.have + e.retNQ + e.retHQ >= e.needed)
            .ThenBy(e => e.name)
            .ToList();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBg))
        {
            ImGui.BeginChild(id, new Vector2(width, height), true);

            var colCount   = showRetainer ? 6 : 4;
            const float numW = 36f;
            const float barW = 46f;
            var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                           | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;

            if (ImGui.BeginTable($"{id}_tbl", colCount, tableFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, numW);
                if (showRetainer)
                {
                    ImGui.TableSetupColumn("RNQ", ImGuiTableColumnFlags.WidthFixed, numW);
                    ImGui.TableSetupColumn("RHQ", ImGuiTableColumnFlags.WidthFixed, numW);
                }
                ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, numW);
                ImGui.TableSetupColumn("%",    ImGuiTableColumnFlags.WidthFixed, barW);

                var needIdx = showRetainer ? 4 : 2;

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableSetColumnIndex(0);
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                    ImGui.TableHeader(label);
                ImGui.TableSetColumnIndex(1);
                ImGui.TableHeader("Have");
                if (showRetainer)
                {
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TableHeader("RNQ");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retainer NQ (via Allagan Tools)");
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TableHeader("RHQ");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retainer HQ (via Allagan Tools)");
                }
                ImGui.TableSetColumnIndex(needIdx);
                ImGui.TableHeader("Need");
                ImGui.TableSetColumnIndex(needIdx + 1);
                ImGui.TableHeader("%");

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
                        var satisfied = e.have + e.retNQ + e.retHQ >= e.needed;
                        DrawPanelRow(e.have, e.retNQ, e.retHQ, e.needed, e.name, e.iconId,
                            satisfied, showRetainer, e.isPrecraft);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.EndChild();
        }
    }

    private void DrawPanelRow(int have, int retNQ, int retHQ, int needed, string name, ushort iconId,
        bool satisfied, bool showRetainer, bool isPrecraft)
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

        var haveColor = satisfied ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.45f, 1f);
        ImGui.TableNextColumn();
        CenterNum(have, haveColor);

        if (showRetainer)
        {
            ImGui.TableNextColumn();
            var nqColor = retNQ > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
            if (retNQ > 0) CenterNum(retNQ, nqColor);
            else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(nqColor, "-"); }

            ImGui.TableNextColumn();
            var hqColor = retHQ > 0 ? new Vector4(0.5f, 0.85f, 1.0f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
            if (retHQ > 0) CenterNum(retHQ, hqColor);
            else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(hqColor, "-"); }
        }

        ImGui.TableNextColumn();
        CenterNum(needed, new Vector4(1f, 1f, 1f, 1f));

        ImGui.TableNextColumn();
        var ratio    = needed > 0 ? (float)have / needed : 1f;
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
