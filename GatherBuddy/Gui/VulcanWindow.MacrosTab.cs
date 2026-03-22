using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using ElliLib.Raii;
using Lumina.Excel.Sheets;

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
    private readonly Dictionary<uint, uint> _skillIconCache = new();

    private void DrawMacrosTab()
    {
        IDisposable tabItem;
        bool tabOpen;

        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Macros##macrosTab", 2, 7);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Macros##macrosTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            var skipUnusable = GatherBuddy.Config.SkipMacroStepIfUnable;
            if (ImGui.Checkbox("Skip step if unable##skipUnusable", ref skipUnusable))
            {
                GatherBuddy.Config.SkipMacroStepIfUnable = skipUnusable;
                GatherBuddy.Config.Save();
            }
            ImGui.SameLine(0, 20);
            var fallbackEnabled = GatherBuddy.Config.MacroFallbackEnabled;
            if (ImGui.Checkbox("Fallback solver when macro exhausts##fallbackEnabled", ref fallbackEnabled))
            {
                GatherBuddy.Config.MacroFallbackEnabled = fallbackEnabled;
                GatherBuddy.Config.Save();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var avail     = ImGui.GetContentRegionAvail();
            var leftWidth = 270f;

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##macrosLeft", new Vector2(leftWidth, avail.Y), true);
                DrawMacroListPanel();
                ImGui.EndChild();
            }

            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##macrosRight", new Vector2(0, avail.Y), true);
                var macroLibrary = CraftingGameInterop.UserMacroLibrary;
                var selectedMacro = _selectedMacroId != null
                    ? macroLibrary.GetMacroByStringId(_selectedMacroId)
                    : null;
                if (selectedMacro != null)
                    DrawMacroDetail(selectedMacro, macroLibrary);
                else
                    DrawImportPanel();
                ImGui.EndChild();
            }
        }
    }

    private void DrawImportPanel()
    {
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Import Macro");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Paste a crafting macro from Teamcraft (in-game macro format).");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Browse on Teamcraft##browseTC", new Vector2(200, 0)))
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
                ImGui.OpenPopup("BrowsingwayError");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Opens Teamcraft community rotations in Browsingway overlay.\n\n" +
                "SETUP REQUIRED:\n" +
                "1. Install Browsingway plugin\n" +
                "2. Run /bw config\n" +
                "3. Create a new overlay (+ button)\n" +
                "4. Set Command Name to 'teamcraft'\n" +
                "5. Close config and click this button\n\n" +
                "Alternatively, browse https://ffxivteamcraft.com/community-rotations\n" +
                "in your web browser.");

        ImGui.SameLine();
        if (ImGui.Button("Hide Overlay##hideTC", new Vector2(120, 0)))
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

        if (ImGui.BeginPopup("BrowsingwayError"))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Browsingway plugin not found or not loaded.");
            ImGui.TextWrapped("You can browse Teamcraft in your web browser and paste macros below.");
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Name:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##macroName", "Enter macro name...", ref _inGameMacroName, 100);

        ImGui.Spacing();
        ImGui.Text("Macro Text:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##macroText", ref _inGameMacroText, 10000, new Vector2(-1, 200));

        ImGui.Spacing();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_inGameMacroText)))
        {
            if (ImGui.Button("Parse & Preview##parseBtn", new Vector2(150, 0)))
                ParseInGameMacro();
        }

        if (_inGameMacroError != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Error: {_inGameMacroError}");
        }

        if (_previewInGameMacro != null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawInGameMacroPreview(_previewInGameMacro);
        }
    }

    private void DrawInGameMacroPreview(UserMacro macro)
    {
        ImGui.TextColored(ImGuiColors.ParsedGreen, "Preview");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{macro.Name}  —  {macro.Actions.Count} actions");
        ImGui.Spacing();

        ImGui.Text("Minimum Stats (optional):");
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Craftsmanship##previewMinCraft", ref _previewMinCraft);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Control##previewMinCtrl", ref _previewMinCtrl);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("CP##previewMinCP", ref _previewMinCP);
        _previewMinCraft = Math.Max(0, _previewMinCraft);
        _previewMinCtrl  = Math.Max(0, _previewMinCtrl);
        _previewMinCP    = Math.Max(0, _previewMinCP);

        ImGui.Spacing();
        if (ImGui.Button("Import##importInGameBtn", new Vector2(120, 0)))
        {
            macro.MinCraftsmanship = _previewMinCraft;
            macro.MinControl       = _previewMinCtrl;
            macro.MinCP            = _previewMinCP;
            ImportInGameMacro(macro);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelInGameBtn", new Vector2(90, 0)))
        {
            _previewInGameMacro = null;
            _inGameMacroError   = null;
        }
    }

    private void ParseInGameMacro()
    {
        _inGameMacroError = null;
        _previewInGameMacro = null;

        try
        {
            var macroName = string.IsNullOrWhiteSpace(_inGameMacroName) ? "Imported Macro" : _inGameMacroName;
            var macro = MacroParser.ParseInGameMacro(_inGameMacroText, macroName);
            
            if (macro == null || macro.Actions.Count == 0)
            {
                _inGameMacroError = "Failed to parse macro. Ensure it contains valid /ac or /action commands.";
            }
            else
            {
                _previewInGameMacro = macro;
                _previewMinCraft    = 0;
                _previewMinCtrl     = 0;
                _previewMinCP       = 0;
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
            
            _selectedMacroId    = macro.Id;
            _previewInGameMacro = null;
            _inGameMacroText    = string.Empty;
            _inGameMacroName    = string.Empty;
            _inGameMacroError   = null;
        }
        catch (Exception ex)
        {
            _inGameMacroError = $"Failed to import macro: {ex.Message}";
            GatherBuddy.Log.Error($"Failed to import in-game macro: {ex.Message}");
        }
    }


    private void DrawMacroListPanel()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##macroSearch", "Search macros...", ref _macroSearch, 128);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var macroLibrary = CraftingGameInterop.UserMacroLibrary;
        var allMacros    = macroLibrary.GetAllMacros();

        if (allMacros.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No macros yet.");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Use the import panel to add one.");
            return;
        }

        var filtered = string.IsNullOrWhiteSpace(_macroSearch)
            ? allMacros
            : allMacros
                .Where(m => m.Name.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase)
                         || (m.Author?.Contains(_macroSearch, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

        if (filtered.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No macros match your search.");
            return;
        }

        var iconSize    = new Vector2(28f, 28f);
        var itemHeight  = iconSize.Y + ImGui.GetStyle().ItemSpacing.Y;
        var contentMaxX = ImGui.GetContentRegionMax().X;

        foreach (var macro in filtered)
        {
            var isSelected  = _selectedMacroId == macro.Id;
            var firstIconId = macro.Actions.Count > 0 ? GetSkillIconId(macro.Actions[0]) : 0u;

            if (firstIconId > 0)
            {
                var wrap = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(firstIconId))
                    .GetWrapOrDefault();
                if (wrap != null)
                    ImGui.Image(wrap.Handle, iconSize);
                else
                    ImGui.Dummy(iconSize);
            }
            else
            {
                ImGui.Dummy(iconSize);
            }

            ImGui.SameLine(0, 6);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize.Y - ImGui.GetTextLineHeight()) / 2f);

            var displayName = string.IsNullOrEmpty(macro.Author)
                ? macro.Name
                : $"{macro.Name}  ({macro.Author})";

            if (ImGui.Selectable($"{displayName}##sel_{macro.Id}", isSelected, ImGuiSelectableFlags.None,
                    new Vector2(contentMaxX - ImGui.GetCursorPosX(), 0)))
                _selectedMacroId = isSelected ? null : macro.Id;

            var statsLine = macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0
                ? $"{macro.Actions.Count} actions  |  Min: {macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP}"
                : $"{macro.Actions.Count} actions";
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(statsLine);
        }
    }

    private void DrawMacroDetail(UserMacro macro, UserMacroLibrary macroLibrary)
    {
        var largeIconSize = new Vector2(48f, 48f);

        var closeW = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2 + 4;
        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - closeW);
        if (ImGui.SmallButton("X##closeDetail"))
            _selectedMacroId = null;

        ImGui.Spacing();

        var firstIconId = macro.Actions.Count > 0 ? GetSkillIconId(macro.Actions[0]) : 0u;
        if (firstIconId > 0)
        {
            var wrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(firstIconId))
                .GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, largeIconSize);
                ImGui.SameLine(0, 10);
            }
        }

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (largeIconSize.Y - lineH * 2f) / 2f);
        ImGui.TextColored(ImGuiColors.ParsedGold, macro.Name);
        ImGui.TextColored(ImGuiColors.DalamudGrey3,
            string.IsNullOrEmpty(macro.Author) ? macro.Source : $"by {macro.Author}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (_editingMacroStatsId != macro.Id)
        {
            _editingMacroStatsId  = macro.Id;
            _editingMacroMinCraft = macro.MinCraftsmanship;
            _editingMacroMinCtrl  = macro.MinControl;
            _editingMacroMinCP    = macro.MinCP;
        }

        ImGui.TextColored(ImGuiColors.DalamudYellow, "Minimum Stats");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Craftsmanship##editMinCraft", ref _editingMacroMinCraft);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Control##editMinCtrl", ref _editingMacroMinCtrl);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("CP##editMinCP", ref _editingMacroMinCP);
        _editingMacroMinCraft = Math.Max(0, _editingMacroMinCraft);
        _editingMacroMinCtrl  = Math.Max(0, _editingMacroMinCtrl);
        _editingMacroMinCP    = Math.Max(0, _editingMacroMinCP);
        ImGui.SameLine();
        if (ImGui.SmallButton("Save##saveStats"))
        {
            macro.MinCraftsmanship = _editingMacroMinCraft;
            macro.MinControl       = _editingMacroMinCtrl;
            macro.MinCP            = _editingMacroMinCP;
            MacroValidator.InvalidateByMacroId(macro.Id);
            macroLibrary.Save();
            GatherBuddy.Log.Debug($"[MacrosTab] Saved min stats for macro '{macro.Name}'");
        }

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Created: {macro.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(macro.TeamcraftUrl))
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"URL: {macro.TeamcraftUrl}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.ParsedGold, $"Actions ({macro.Actions.Count})");
        ImGui.Spacing();

        var actionIconSize = new Vector2(24f, 24f);
        var remainH        = ImGui.GetContentRegionAvail().Y - 32f;
        ImGui.BeginChild("##macroActions", new Vector2(-1, remainH), false);

        for (var i = 0; i < macro.Actions.Count; i++)
        {
            var actionId  = macro.Actions[i];
            var skillName = ((VulcanSkill)actionId).ToString();
            var iconId    = GetSkillIconId(actionId);

            if (iconId > 0)
            {
                var wrap = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(iconId))
                    .GetWrapOrDefault();
                if (wrap != null)
                    ImGui.Image(wrap.Handle, actionIconSize);
                else
                    ImGui.Dummy(actionIconSize);
            }
            else
            {
                ImGui.Dummy(actionIconSize);
            }

            ImGui.SameLine(0, 6);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (actionIconSize.Y - ImGui.GetTextLineHeight()) / 2f);
            ImGui.Text($"{i + 1}. {skillName}");
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button($"Delete##deleteMacro_{macro.Id}", new Vector2(100, 0)))
        {
            macroLibrary.RemoveMacro(macro.Id);
            _selectedMacroId = null;
            GatherBuddy.Log.Debug($"[MacrosTab] Deleted macro '{macro.Name}'");
        }
    }

    private uint GetSkillIconId(uint skillId)
    {
        if (_skillIconCache.TryGetValue(skillId, out var cached))
            return cached;
        
        uint iconId = 0;
        try
        {
            if (skillId >= 100000)
            {
                var sheet = Dalamud.GameData.GetExcelSheet<CraftAction>();
                if (sheet != null && sheet.TryGetRow(skillId, out var row))
                    iconId = row.Icon;
            }
            else if (skillId > 0)
            {
                var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet != null && sheet.TryGetRow(skillId, out var row))
                    iconId = row.Icon;
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
