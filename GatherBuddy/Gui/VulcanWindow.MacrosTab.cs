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
    private UserMacro? _viewingMacro = null;
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

        ImGui.TextWrapped("Import crafting macros from Teamcraft by pasting in-game macro format.");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Macro Behavior##macroBehaviorSection"))
        {
            ImGui.Spacing();
            var skipUnusable = GatherBuddy.Config.SkipMacroStepIfUnable;
            if (ImGui.Checkbox("Skip macro step if unable to use action##skipUnusable", ref skipUnusable))
            {
                GatherBuddy.Config.SkipMacroStepIfUnable = skipUnusable;
                GatherBuddy.Config.Save();
            }
            var fallbackEnabled = GatherBuddy.Config.MacroFallbackEnabled;
            if (ImGui.Checkbox("Use fallback solver when macro exhausts##fallbackEnabled", ref fallbackEnabled))
            {
                GatherBuddy.Config.MacroFallbackEnabled = fallbackEnabled;
                GatherBuddy.Config.Save();
            }
            ImGui.Spacing();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Import Macro##inGameSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawInGameMacroSection();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Saved Macros##savedSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSavedMacrosSection();
        }
        }
    }

    private void DrawInGameMacroSection()
    {
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
                "Use 'Hide Overlay' button when done to dismiss the overlay.\n\n" +
                "Alternatively, browse https://ffxivteamcraft.com/community-rotations\n" +
                "in your web browser.");
        
        ImGui.SameLine();
        if (ImGui.Button("Hide Overlay##hideTC", new Vector2(150, 0)))
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
        ImGui.TextWrapped("Paste crafting macro from Teamcraft (use 'Convert to in-game macro' button on rotation page).");
        ImGui.Spacing();

        ImGui.Text("Macro Name:");
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##macroName", "Enter macro name...", ref _inGameMacroName, 100);

        ImGui.Spacing();
        ImGui.Text("Paste Macro Text:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##macroText", ref _inGameMacroText, 10000, new Vector2(-1, 200));

        ImGui.Spacing();

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_inGameMacroText)))
        {
            if (ImGui.Button("Parse Macro##parseBtn", new Vector2(150, 0)))
            {
                ParseInGameMacro();
            }
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
        ImGui.TextColored(ImGuiColors.ParsedGreen, "Macro Preview");
        ImGui.Spacing();

        ImGui.Text($"Name: {macro.Name}");
        ImGui.Text($"Actions: {macro.Actions.Count}");

        ImGui.Spacing();
        ImGui.Text("Minimum Stats (optional — used for validation):");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Craftsmanship##previewMinCraft", ref _previewMinCraft);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Control##previewMinCtrl", ref _previewMinCtrl);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("CP##previewMinCP", ref _previewMinCP);
        _previewMinCraft = Math.Max(0, _previewMinCraft);
        _previewMinCtrl  = Math.Max(0, _previewMinCtrl);
        _previewMinCP    = Math.Max(0, _previewMinCP);

        ImGui.Spacing();

        if (ImGui.Button("Import Macro##importInGameBtn", new Vector2(150, 0)))
        {
            macro.MinCraftsmanship = _previewMinCraft;
            macro.MinControl       = _previewMinCtrl;
            macro.MinCP            = _previewMinCP;
            ImportInGameMacro(macro);
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##cancelInGameBtn", new Vector2(100, 0)))
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


    private void DrawSavedMacrosSection()
    {
        var macroLibrary = CraftingGameInterop.UserMacroLibrary;
        var allMacros = macroLibrary.GetAllMacros();

        if (allMacros.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No macros saved yet. Import some from Teamcraft!");
            return;
        }

        ImGui.Spacing();
        ImGui.Text($"Total Macros: {allMacros.Count}");
        ImGui.Spacing();

        using var table = ImRaii.Table("##macrosTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table)
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Stats", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableHeadersRow();

        foreach (var macro in allMacros)
        {
            ImGui.TableNextRow();
            
            ImGui.TableNextColumn();
            ImGui.Text(macro.Name);
            if (!string.IsNullOrEmpty(macro.Author))
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey3, $"by {macro.Author}");
            }

            ImGui.TableNextColumn();
            ImGui.Text(macro.Actions.Count.ToString());

            ImGui.TableNextColumn();
            if (macro.MinCraftsmanship > 0 || macro.MinControl > 0 || macro.MinCP > 0)
                ImGui.Text($"{macro.MinCraftsmanship}/{macro.MinControl}/{macro.MinCP}");
            else
                ImGui.TextColored(ImGuiColors.DalamudGrey, "None");

            ImGui.TableNextColumn();
            ImGui.Text(macro.Source);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"View##{macro.Id}"))
            {
                _viewingMacro = macro;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Delete##{macro.Id}"))
            {
                macroLibrary.RemoveMacro(macro.Id);
            }
        }
        
        DrawMacroDetailsPopup();
    }

    private void DrawMacroDetailsPopup()
    {
        if (_viewingMacro == null)
            return;

        bool isOpen = true;
        ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"Macro Details: {_viewingMacro.Name}##macroDetails", ref isOpen, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, _viewingMacro.Name);
            ImGui.Separator();
            ImGui.Spacing();

            if (!string.IsNullOrEmpty(_viewingMacro.Author))
                ImGui.Text($"Author: {_viewingMacro.Author}");
            
            ImGui.Text($"Source: {_viewingMacro.Source}");
            ImGui.Text($"Total Actions: {_viewingMacro.Actions.Count}");

            if (_editingMacroStatsId != _viewingMacro.Id)
            {
                _editingMacroStatsId  = _viewingMacro.Id;
                _editingMacroMinCraft = _viewingMacro.MinCraftsmanship;
                _editingMacroMinCtrl  = _viewingMacro.MinControl;
                _editingMacroMinCP    = _viewingMacro.MinCP;
            }

            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Minimum Stats (used for validation):");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Craftsmanship##editMinCraft", ref _editingMacroMinCraft);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Control##editMinCtrl", ref _editingMacroMinCtrl);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputInt("CP##editMinCP", ref _editingMacroMinCP);
            _editingMacroMinCraft = Math.Max(0, _editingMacroMinCraft);
            _editingMacroMinCtrl  = Math.Max(0, _editingMacroMinCtrl);
            _editingMacroMinCP    = Math.Max(0, _editingMacroMinCP);
            ImGui.SameLine();
            if (ImGui.SmallButton("Save Stats##saveStats"))
            {
                _viewingMacro.MinCraftsmanship = _editingMacroMinCraft;
                _viewingMacro.MinControl       = _editingMacroMinCtrl;
                _viewingMacro.MinCP            = _editingMacroMinCP;
                MacroValidator.InvalidateByMacroId(_viewingMacro.Id);
                CraftingGameInterop.UserMacroLibrary.Save();
                GatherBuddy.Log.Debug($"[MacrosTab] Saved min stats for macro '{_viewingMacro.Name}'");
            }
            
            ImGui.Text($"Created: {_viewingMacro.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            
            if (!string.IsNullOrEmpty(_viewingMacro.TeamcraftUrl))
            {
                ImGui.Text($"URL: {_viewingMacro.TeamcraftUrl}");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.ParsedGold, "Actions:");
            ImGui.Spacing();

            ImGui.BeginChild("##actionsList", new Vector2(-1, -30), true);
            var iconSize = new Vector2(24, 24);
            for (int i = 0; i < _viewingMacro.Actions.Count; i++)
            {
                var actionId = _viewingMacro.Actions[i];
                var skillName = ((VulcanSkill)actionId).ToString();
                var iconId = GetSkillIconId(actionId);
                
                if (iconId > 0)
                {
                    var wrap = Icons.DefaultStorage.TextureProvider
                        .GetFromGameIcon(new GameIconLookup(iconId))
                        .GetWrapOrDefault();
                    if (wrap != null)
                        ImGui.Image(wrap.Handle, iconSize);
                    else
                        ImGui.Dummy(iconSize);
                }
                else
                    ImGui.Dummy(iconSize);
                
                ImGui.SameLine(0, 6);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize.Y - ImGui.GetTextLineHeight()) / 2);
                ImGui.Text($"{i + 1}. {skillName}");
            }
            ImGui.EndChild();

            ImGui.Spacing();
            if (ImGui.Button("Close##closeMacroDetails", new Vector2(100, 0)))
                _viewingMacro = null;
        }

        ImGui.End();

        if (!isOpen)
            _viewingMacro = null;
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
