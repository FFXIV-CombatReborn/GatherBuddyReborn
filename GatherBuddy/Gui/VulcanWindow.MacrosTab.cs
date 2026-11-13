using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.Crafting;
using GatherBuddy.RebornUI;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private string _inGameMacroText = string.Empty;
    private string _inGameMacroName = string.Empty;
    private string? _inGameMacroError = null;
    private UserMacro? _previewInGameMacro = null;
    private int _previewMinCraft;
    private int _previewMinCtrl;
    private int _previewMinCP;
    private string? _selectedMacroId = null;
    private string _macroSearch = string.Empty;
    private string _editingMacroStatsId = string.Empty;
    private int _editingMacroMinCraft;
    private int _editingMacroMinCtrl;
    private int _editingMacroMinCP;
    private readonly Dictionary<uint, uint> _skillIconCache = [];

    private const string BrowsingwayErrorDialogId = "##browsingwayError";

    private void DrawMacrosPage()
    {
        var skipUnusable = GatherBuddy.Config.SkipMacroStepIfUnable;
        if (M3Widgets.LabeledCheckbox("Skip a step that can't be used##skipUnusable", ref skipUnusable,
                tooltip: "When a macro step can't be used right now, go on with the next one instead of stopping."))
        {
            GatherBuddy.Config.SkipMacroStepIfUnable = skipUnusable;
            GatherBuddy.Config.Save();
        }

        ImGui.SameLine(0, M3.Space3 * 2);
        var fallbackEnabled = GatherBuddy.Config.MacroFallbackEnabled;
        if (M3Widgets.LabeledCheckbox("Use the solver once a macro runs out##fallbackEnabled", ref fallbackEnabled,
                tooltip: "When a macro ends before the craft is finished, let the solver finish it."))
        {
            GatherBuddy.Config.MacroFallbackEnabled = fallbackEnabled;
            GatherBuddy.Config.Save();
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = VulcanUiScaling.Scaled(290f);
        using (var left = M3Panel.Begin("##macrosLeft", new Vector2(leftWidth, avail.Y), false, VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawMacroListPanel();
            }
        }

        ImGui.SameLine(0, M3.Space2);
        using (var right = M3Panel.Begin("##macrosRight", new Vector2(0, avail.Y)))
        {
            if (right)
            {
                var macroLibrary = CraftingGameInterop.UserMacroLibrary;
                var selectedMacro = _selectedMacroId != null ? macroLibrary.GetMacroByStringId(_selectedMacroId) : null;
                if (selectedMacro != null)
                {
                    DrawMacroDetail(selectedMacro, macroLibrary);
                }
                else
                {
                    DrawImportPanel();
                }
            }
        }

        DrawBrowsingwayErrorDialog();
    }

    private void DrawImportPanel()
    {
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted("Import a macro");
        }

        M3Text.Draw("Paste a crafting macro from Teamcraft or Artisan. /ac lines, plain one-action-per-line imports, and Artisan JSON exports are supported.",
            M3TextStyle.Muted, true);
        ImGui.Dummy(new Vector2(0, M3.Space2));

        if (M3Widgets.Button("##browseTC", "Browse Teamcraft", M3ButtonStyle.Tonal, FontAwesomeIcon.Globe,
                tooltip: "Opens Teamcraft community rotations in a Browsingway overlay.\n\n"
                  + "Setup:\n"
                  + "1. Install the Browsingway plugin\n"
                  + "2. Run /bw config\n"
                  + "3. Create a new overlay (+ button)\n"
                  + "4. Set its Command Name to 'teamcraft'\n"
                  + "5. Close the config and click this button\n\n"
                  + "You can also browse https://ffxivteamcraft.com/community-rotations in your web browser."))
        {
            try
            {
                Dalamud.Commands.ProcessCommand("/bw overlay teamcraft disabled off");
                Dalamud.Commands.ProcessCommand("/bw overlay teamcraft url https://ffxivteamcraft.com/community-rotations");
                GatherBuddy.Log.Information("Opening Teamcraft in Browsingway overlay");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"Could not open Browsingway overlay: {ex.Message}");
                M3Dialog.Open(BrowsingwayErrorDialogId);
            }
        }

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##hideTC", "Hide overlay", M3ButtonStyle.Text, FontAwesomeIcon.EyeSlash))
        {
            try
            {
                Dalamud.Commands.ProcessCommand("/bw overlay teamcraft disabled on");
                GatherBuddy.Log.Information("Hiding Teamcraft overlay");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"Could not hide Browsingway overlay: {ex.Message}");
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space2));
        var width = ImGui.GetContentRegionAvail().X;
        M3Widgets.TextField("##macroName", "Macro name", ref _inGameMacroName, width, 100);
        ImGui.Dummy(new Vector2(0, M3.Space1));
        M3Widgets.SectionLabel("Macro text");
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, M3.ShapeExtraSmall)
                   .Push(ImGuiStyleVar.FramePadding, new Vector2(M3.Space3, M3.Space2)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, M3.Scheme.SurfaceContainerHighest))
        {
            ImGui.InputTextMultiline("##macroText", ref _inGameMacroText, 500000, new Vector2(width, VulcanUiScaling.Scaled(200f)));
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        if (M3Widgets.Button("##parseBtn", "Parse and preview", M3ButtonStyle.Filled, FontAwesomeIcon.Eye,
                enabled: !string.IsNullOrWhiteSpace(_inGameMacroText)))
        {
            ParseInGameMacro();
        }

        if (_inGameMacroError != null)
        {
            ImGui.Dummy(new Vector2(0, M3.Space1));
            M3Widgets.Banner("##macroError", _inGameMacroError, M3Severity.Error, FontAwesomeIcon.ExclamationCircle);
        }

        if (_previewInGameMacro != null)
        {
            ImGui.Dummy(new Vector2(0, M3.Space2));
            DrawInGameMacroPreview(_previewInGameMacro);
        }
    }

    private static void DrawBrowsingwayErrorDialog()
    {
        using var dialog = M3Dialog.Begin(BrowsingwayErrorDialogId, "Browsingway isn't available",
            "The Browsingway plugin was not found or is not loaded. You can browse Teamcraft in your web browser and paste macros here.",
            FontAwesomeIcon.ExclamationTriangle);
        if (dialog.IsOpen)
        {
            M3Dialog.Actions([new M3DialogAction("OK", M3ButtonStyle.Filled)]);
        }
    }

    private void DrawInGameMacroPreview(UserMacro macro)
    {
        using var card = M3Card.Begin("##macroPreview", "Preview", FontAwesomeIcon.Eye, M3.AccentFor(2), subtitle: $"{macro.Name}, {macro.Actions.Count} actions");
        M3Text.Draw("Minimum stats, optional", M3TextStyle.Label);
        M3Widgets.RowNumber("Craftsmanship##previewMinCraft", ref _previewMinCraft, 10, 0);
        M3Widgets.RowNumber("Control##previewMinCtrl", ref _previewMinCtrl, 10, 0);
        M3Widgets.RowNumber("CP##previewMinCP", ref _previewMinCP, 5, 0);
        _previewMinCraft = Math.Max(0, _previewMinCraft);
        _previewMinCtrl = Math.Max(0, _previewMinCtrl);
        _previewMinCP = Math.Max(0, _previewMinCP);

        if (M3Widgets.Button("##importInGameBtn", "Import", M3ButtonStyle.Filled, FontAwesomeIcon.FileImport))
        {
            macro.MinCraftsmanship = _previewMinCraft;
            macro.MinControl = _previewMinCtrl;
            macro.MinCP = _previewMinCP;
            ImportInGameMacro(macro);
        }

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##cancelInGameBtn", "Cancel", M3ButtonStyle.Text))
        {
            _previewInGameMacro = null;
            _inGameMacroError = null;
        }
    }
    private void ParseInGameMacro()
    {
        _inGameMacroError = null;
        _previewInGameMacro = null;

        try
        {
            var macroName = string.IsNullOrWhiteSpace(_inGameMacroName) ? null : _inGameMacroName;
            var macro = MacroParser.ParseInGameMacro(_inGameMacroText, macroName);

            if (macro == null || macro.Actions.Count == 0)
            {
                _inGameMacroError = "Failed to parse macro. Ensure it contains recognizable crafting action names or a supported Artisan JSON export.";
            }
            else
            {
                _previewInGameMacro = macro;
                _previewMinCraft = macro.MinCraftsmanship;
                _previewMinCtrl = macro.MinControl;
                _previewMinCP = macro.MinCP;
            }
        }
        catch (Exception ex)
        {
            _inGameMacroError = $"Failed to parse macro: {ex.Message}";
            GatherBuddy.Log.Error($"Failed to parse in-game macro: {ex.Message}");
        }
    }

    private void ImportInGameMacro(UserMacro macro)
    {
        try
        {
            var macroLibrary = CraftingGameInterop.UserMacroLibrary;
            macroLibrary.AddMacro(macro, 0);

            GatherBuddy.Log.Information($"Imported in-game macro: {macro.Name}");

            _selectedMacroId = macro.Id;
            _previewInGameMacro = null;
            _inGameMacroText = string.Empty;
            _inGameMacroName = string.Empty;
            _inGameMacroError = null;
        }
        catch (Exception ex)
        {
            _inGameMacroError = $"Failed to import macro: {ex.Message}";
            GatherBuddy.Log.Error($"Failed to import in-game macro: {ex.Message}");
        }
    }


    private void DrawMacroListPanel()
    {
        M3Widgets.SearchField("##macroSearch", "Search macros", ref _macroSearch, ImGui.GetContentRegionAvail().X - M3Widgets.IconButtonSize - M3.Space1);
        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.IconButton("##newImport", FontAwesomeIcon.Plus, "Import a macro", M3ButtonStyle.Tonal))
        {
            _selectedMacroId = null;
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var macroLibrary = CraftingGameInterop.UserMacroLibrary;
        var allMacros = macroLibrary.GetAllMacros();
        if (allMacros.Count == 0)
        {
            M3Widgets.EmptyState("##noMacros", "No macros yet", "Import one with the panel on the right.", FontAwesomeIcon.Scroll);
            return;
        }

        var filtered = string.IsNullOrWhiteSpace(_macroSearch)
            ? allMacros
            : allMacros.FindAll(m => m.Name.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase)
                || (m.Author?.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase) ?? false));

        if (filtered.Count == 0)
        {
            M3Text.Draw("No macros match your search.", M3TextStyle.Faint);
            return;
        }

        var iconSize = VulcanUiScaling.Scaled(28f);
        using var rows = ImRaii.Child("##macroRows", new Vector2(-1, -1));
        if (!rows)
        {
            return;
        }

        foreach (var macro in filtered)
        {
            var isSelected = _selectedMacroId == macro.Id;
            var firstIconId = macro.Actions.Count > 0 ? GetSkillIconId(macro.Actions[0]) : 0u;
            var displayName = string.IsNullOrEmpty(macro.Author) ? macro.Name : $"{macro.Name}  ({macro.Author})";
            if (Ui.IconListRow($"##sel_{macro.Id}", firstIconId, displayName, isSelected, iconSize, macro.Actions.Count.ToString()))
            {
                _selectedMacroId = isSelected ? null : macro.Id;
            }

            M3Tooltip.Hovered(macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0
                ? $"{macro.Actions.Count} actions, needs at least {macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP}"
                : $"{macro.Actions.Count} actions");
        }
    }

    private void DrawMacroDetail(UserMacro macro, UserMacroLibrary macroLibrary)
    {
        var largeIconSize = VulcanUiScaling.Scaled(48f);
        var start = ImGui.GetCursorScreenPos();
        var firstIconId = macro.Actions.Count > 0 ? GetSkillIconId(macro.Actions[0]) : 0u;
        if (firstIconId > 0)
        {
            M3Widgets.GameIcon(firstIconId, largeIconSize);
            ImGui.SameLine(0, M3.Space3);
        }

        using (ImRaii.Group())
        {
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y + (largeIconSize - ImGui.GetTextLineHeightWithSpacing() * 2f) * 0.5f));
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                ImGui.TextUnformatted(macro.Name);
            }

            M3Text.Draw(string.IsNullOrEmpty(macro.Author) ? macro.Source : $"by {macro.Author}", M3TextStyle.Muted);
        }

        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X - M3Widgets.IconButtonSize, start.Y));
        if (M3Widgets.IconButton("##closeDetail", FontAwesomeIcon.Times, "Close"))
        {
            _selectedMacroId = null;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + MathF.Max(largeIconSize, M3Widgets.IconButtonSize) + M3.Space2));
        if (_editingMacroStatsId != macro.Id)
        {
            _editingMacroStatsId = macro.Id;
            _editingMacroMinCraft = macro.MinCraftsmanship;
            _editingMacroMinCtrl = macro.MinControl;
            _editingMacroMinCP = macro.MinCP;
        }

        using (M3Card.Begin("##macroStats", "Minimum stats", FontAwesomeIcon.ChartBar, M3.AccentFor(0),
                   subtitle: $"Created {macro.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"))
        {
            M3Widgets.RowNumber("Craftsmanship##editMinCraft", ref _editingMacroMinCraft, 10, 0);
            M3Widgets.RowNumber("Control##editMinCtrl", ref _editingMacroMinCtrl, 10, 0);
            M3Widgets.RowNumber("CP##editMinCP", ref _editingMacroMinCP, 5, 0);
            _editingMacroMinCraft = Math.Max(0, _editingMacroMinCraft);
            _editingMacroMinCtrl = Math.Max(0, _editingMacroMinCtrl);
            _editingMacroMinCP = Math.Max(0, _editingMacroMinCP);
            var changed = _editingMacroMinCraft != macro.MinCraftsmanship || _editingMacroMinCtrl != macro.MinControl || _editingMacroMinCP != macro.MinCP;
            if (M3Widgets.Button("##saveStats", "Save", M3ButtonStyle.Filled, FontAwesomeIcon.Save, enabled: changed))
            {
                macro.MinCraftsmanship = _editingMacroMinCraft;
                macro.MinControl = _editingMacroMinCtrl;
                macro.MinCP = _editingMacroMinCP;
                MacroValidator.InvalidateByMacroId(macro.Id);
                macroLibrary.Save();
                GatherBuddy.Log.Debug($"[MacrosTab] Saved min stats for macro '{macro.Name}'");
                M3Snackbar.Show("Saved the minimum stats.");
            }

            if (!string.IsNullOrEmpty(macro.TeamcraftUrl))
            {
                M3Widgets.RowCopy("Teamcraft link", macro.TeamcraftUrl, iconOnly: true);
            }
        }

        M3Widgets.SectionLabel($"Actions ({macro.Actions.Count})");
        var actionIconSize = VulcanUiScaling.Scaled(24f);
        using (var actions = ImRaii.Child("##macroActions", new Vector2(-1, ImGui.GetContentRegionAvail().Y - M3Widgets.ButtonHeight - M3.Space2), false))
        {
            if (actions)
            {
                for (var i = 0; i < macro.Actions.Count; i++)
                {
                    var actionId = macro.Actions[i];
                    var iconId = GetSkillIconId(actionId);
                    var rowStart = ImGui.GetCursorScreenPos();
                    if (iconId > 0)
                    {
                        M3Widgets.GameIcon(iconId, actionIconSize);
                    }
                    else
                    {
                        ImGui.Dummy(new Vector2(actionIconSize));
                    }

                    ImGui.SameLine(0, M3.Space2);
                    ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowStart.Y + (actionIconSize - ImGui.GetTextLineHeight()) * 0.5f));
                    M3Text.Draw($"{i + 1}.", M3TextStyle.Faint);
                    ImGui.SameLine(0, M3.Space1);
                    ImGui.TextUnformatted(((VulcanSkill)actionId).ToString());
                }
            }
        }

        if (M3Widgets.HoldButton($"##deleteMacro_{macro.Id}", "Delete macro", 1f, M3ButtonStyle.Danger, FontAwesomeIcon.Trash,
                tooltip: "Press and hold to delete this macro."))
        {
            macroLibrary.RemoveMacro(macro.Id);
            _selectedMacroId = null;
            GatherBuddy.Log.Debug($"[MacrosTab] Deleted macro '{macro.Name}'");
            M3Snackbar.Show($"Deleted {macro.Name}.");
        }
    }
    private uint GetSkillIconId(uint skillId)
    {
        if (_skillIconCache.TryGetValue(skillId, out var cached))
        {
            return cached;
        }

        uint iconId = 0;
        try
        {
            if (skillId >= 100000)
            {
                var sheet = Dalamud.GameData.GetExcelSheet<CraftAction>();
                if (sheet != null && sheet.TryGetRow(skillId, out var row))
                {
                    iconId = row.Icon;
                }
            }
            else if (skillId > 0)
            {
                var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet != null && sheet.TryGetRow(skillId, out var row))
                {
                    iconId = row.Icon;
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[MacrosTab] Failed to get icon for skill {skillId}: {ex.Message}");
        }

        _skillIconCache[skillId] = iconId;
        return iconId;
    }
}
