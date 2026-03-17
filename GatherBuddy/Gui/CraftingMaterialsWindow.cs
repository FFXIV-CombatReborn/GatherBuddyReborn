using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public class CraftingMaterialsWindow : Window
{
    private CraftingListEditor? _editor;
    private bool _matsOvercapPercent = false;
    private bool _matsShowPrecrafts  = false;

    public CraftingMaterialsWindow() : base("Materials###CraftingMaterials")
    {
        Size           = new Vector2(400, 480);
        SizeCondition  = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 140),
            MaximumSize = new Vector2(800, 1000),
        };
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        _editor = editor;
    }

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
        var missing = new List<(uint itemId, int have, int retainer, int needed, string name, ushort iconId)>();
        var ready   = new List<(uint itemId, int have, int retainer, int needed, string name, ushort iconId)>();

        foreach (var (itemId, needed) in materials)
        {
            if (!itemSheet.TryGetRow(itemId, out var item)) continue;
            var have     = _editor.GetInventoryCount(itemId);
            var retainer = showRetainer ? _editor.GetRetainerCount(itemId) : 0;
            var entry    = (itemId, have, retainer, needed, item.Name.ExtractText(), item.Icon);
            if (have + retainer < needed) missing.Add(entry);
            else ready.Add(entry);
        }

        var missingPrecrafts = new List<(uint itemId, int have, int retainer, int needed, string name, ushort iconId)>();
        var readyPrecrafts   = new List<(uint itemId, int have, int retainer, int needed, string name, ushort iconId)>();

        if (_matsShowPrecrafts)
        {
            var precrafts = _editor.GetCachedPrecraftMaterials();
            foreach (var (itemId, needed) in precrafts)
            {
                if (!itemSheet.TryGetRow(itemId, out var item)) continue;
                var have     = _editor.GetInventoryCount(itemId);
                var retainer = showRetainer ? _editor.GetRetainerCount(itemId) : 0;
                var entry    = (itemId, have, retainer, needed, item.Name.ExtractText(), item.Icon);
                if (have + retainer < needed) missingPrecrafts.Add(entry);
                else readyPrecrafts.Add(entry);
            }
            missingPrecrafts.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            readyPrecrafts.Sort((a, b)   => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        missing.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        ready.Sort((a, b)   => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        var totalMissing = missing.Count + missingPrecrafts.Count;
        var totalReady   = ready.Count   + readyPrecrafts.Count;

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"{totalMissing} missing  \u00b7  {totalReady} ready");
        ImGui.SameLine();
        ImGui.Checkbox("150%##overcap", ref _matsOvercapPercent);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show % beyond 100 when you have more than needed");
        ImGui.SameLine();
        ImGui.Checkbox("Precrafts##precrafts", ref _matsShowPrecrafts);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include intermediate craftable components");
        ImGui.Separator();

        const float barWidth      = 80f;
        const float countColWidth = 50f;
        var colCount = showRetainer ? 5 : 4;

        var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("##mat_table", colCount, tableFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, countColWidth);
            if (showRetainer)
                ImGui.TableSetupColumn("Ret", ImGuiTableColumnFlags.WidthFixed, countColWidth);
            ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, countColWidth);
            ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthFixed, barWidth);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableSetColumnIndex(0);
            ImGui.TableHeader("Item");
            ImGui.TableSetColumnIndex(1);
            var haveHdrOff = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("Have").X) * 0.5f;
            if (haveHdrOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + haveHdrOff);
            ImGui.TableHeader("Have");
            if (showRetainer)
            {
                ImGui.TableSetColumnIndex(2);
                var retHdrOff = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("Ret").X) * 0.5f;
                if (retHdrOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + retHdrOff);
                ImGui.TableHeader("Ret");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Retainer inventory count (via Allagan Tools)");
            }
            ImGui.TableSetColumnIndex(showRetainer ? 3 : 2);
            var needHdrOff = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("Need").X) * 0.5f;
            if (needHdrOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + needHdrOff);
            ImGui.TableHeader("Need");
            ImGui.TableSetColumnIndex(showRetainer ? 4 : 3);
            ImGui.TableHeader("");

            foreach (var (_, have, retainer, needed, name, iconId) in missing)
                DrawMaterialRow(have, retainer, needed, name, iconId, false, showRetainer, false, _matsOvercapPercent);

            foreach (var (_, have, retainer, needed, name, iconId) in missingPrecrafts)
                DrawMaterialRow(have, retainer, needed, name, iconId, false, showRetainer, true, _matsOvercapPercent);

            foreach (var (_, have, retainer, needed, name, iconId) in ready)
                DrawMaterialRow(have, retainer, needed, name, iconId, true, showRetainer, false, _matsOvercapPercent);

            foreach (var (_, have, retainer, needed, name, iconId) in readyPrecrafts)
                DrawMaterialRow(have, retainer, needed, name, iconId, true, showRetainer, true, _matsOvercapPercent);

            ImGui.EndTable();
        }
    }

    private static void DrawMaterialRow(int have, int retainerCount, int needed, string name, ushort iconId, bool satisfied, bool showRetainer, bool isPrcraft = false, bool overcapPercent = false)
    {
        ImGui.TableNextRow();
        Vector4 rowColor = (satisfied, isPrcraft) switch
        {
            (true,  false) => new Vector4(0.15f, 0.50f, 0.15f, 0.25f),
            (true,  true)  => new Vector4(0.05f, 0.40f, 0.45f, 0.25f),
            (false, false) => new Vector4(0.50f, 0.15f, 0.15f, 0.25f),
            (false, true)  => new Vector4(0.55f, 0.35f, 0.05f, 0.25f),
        };
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(rowColor));

        var lineH    = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);

        ImGui.TableNextColumn();
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        if (icon.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, iconSize);
        else
            ImGui.Dummy(iconSize);
        ImGui.SameLine(0, 4);
        ImGui.TextUnformatted(name);

        ImGui.TableNextColumn();
        var haveStr   = have.ToString();
        var haveColor = satisfied ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.45f, 1f);
        var haveOff   = (ImGui.GetColumnWidth() - ImGui.CalcTextSize(haveStr).X) * 0.5f;
        if (haveOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + haveOff);
        ImGui.TextColored(haveColor, haveStr);

        if (showRetainer)
        {
            ImGui.TableNextColumn();
            var retStr   = retainerCount > 0 ? retainerCount.ToString() : "-";
            var retColor = retainerCount > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
            var retOff   = (ImGui.GetColumnWidth() - ImGui.CalcTextSize(retStr).X) * 0.5f;
            if (retOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + retOff);
            ImGui.TextColored(retColor, retStr);
        }

        ImGui.TableNextColumn();
        var needStr = needed.ToString();
        var needOff = (ImGui.GetColumnWidth() - ImGui.CalcTextSize(needStr).X) * 0.5f;
        if (needOff > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + needOff);
        ImGui.TextUnformatted(needStr);

        ImGui.TableNextColumn();
        var ratio    = needed > 0 ? (float)have / needed : 1f;
        var progress = Math.Clamp(ratio, 0f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, satisfied ? new Vector4(0.2f, 0.65f, 0.2f, 0.9f) : new Vector4(0.65f, 0.2f, 0.2f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.12f, 0.12f, 0.9f));
        ImGui.ProgressBar(progress, new Vector2(ImGui.GetContentRegionAvail().X, lineH), "");
        ImGui.PopStyleColor(2);
        var pctText = overcapPercent ? $"{ratio * 100f:F0}%" : $"{progress * 100f:F0}%";
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
