using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class MacroParser
{
    private static readonly Regex _actionRegex = new(
        @"/ac(?:tion)?\s+""([^""]+)""|/ac(?:tion)?\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static Dictionary<string, VulcanSkill>? _localizedLookup;
    private static Dictionary<string, VulcanSkill>? _englishLookup;

    public static UserMacro? ParseInGameMacro(string macroText, string name = "Imported Macro")
    {
        if (string.IsNullOrWhiteSpace(macroText))
        {
            GatherBuddy.Log.Warning("[MacroParser] Empty macro text provided");
            return null;
        }

        var actions = new List<uint>();
        var lines = macroText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//") || trimmedLine.StartsWith("#"))
                continue;

            var match = _actionRegex.Match(trimmedLine);
            if (match.Success)
            {
                var actionName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var actionId = GetActionIdFromName(actionName);

                if (actionId > 0)
                {
                    actions.Add(actionId);
                    GatherBuddy.Log.Debug($"[MacroParser] Parsed action: {actionName} -> {actionId}");
                }
                else
                {
                    GatherBuddy.Log.Warning($"[MacroParser] Unknown action: {actionName}");
                }
            }
        }

        if (actions.Count == 0)
        {
            GatherBuddy.Log.Warning("[MacroParser] No valid actions found in macro");
            return null;
        }

        GatherBuddy.Log.Information($"[MacroParser] Parsed {actions.Count} actions from macro");

        return new UserMacro
        {
            Name = name,
            Actions = actions,
            Source = "In-Game Macro",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static uint GetActionIdFromName(string actionName)
    {
        var normalized = NormalizeName(actionName);

        var localized = _localizedLookup ??= BuildLookup(GatherBuddy.Language);
        if (localized.TryGetValue(normalized, out var skill))
            return (uint)skill;

        var english = _englishLookup ??= BuildLookup(ClientLanguage.English);
        if (english.TryGetValue(normalized, out skill))
            return (uint)skill;

        return 0;
    }

    private static string NormalizeName(string name)
        => name.Trim().ToLowerInvariant()
            .Replace(" ", "")
            .Replace("'", "")
            .Replace("\u2019", "")
            .Replace("-", "");

    private static Dictionary<string, VulcanSkill> BuildLookup(ClientLanguage language)
    {
        var lookup = new Dictionary<string, VulcanSkill>();

        foreach (VulcanSkill skill in Enum.GetValues<VulcanSkill>())
        {
            var id = (uint)skill;
            if (id < 3 || id >= 200000)
                continue;

            try
            {
                string name;
                if (id >= 100000)
                {
                    var sheet = Dalamud.GameData.GetExcelSheet<CraftAction>(language);
                    if (sheet == null || !sheet.TryGetRow(id, out var row)) continue;
                    name = row.Name.ToString().Trim();
                }
                else
                {
                    var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>(language);
                    if (sheet == null || !sheet.TryGetRow(id, out var row)) continue;
                    name = row.Name.ToString().Trim();
                }

                if (string.IsNullOrEmpty(name)) continue;

                var normalized = NormalizeName(name);
                lookup.TryAdd(normalized, skill);

                GatherBuddy.Log.Debug($"[MacroParser] Registered '{name}' -> {skill} ({language})");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Debug($"[MacroParser] Failed to register skill {skill}: {ex.Message}");
            }
        }

        GatherBuddy.Log.Information($"[MacroParser] Built {language} lookup with {lookup.Count} entries");
        return lookup;
    }
}
