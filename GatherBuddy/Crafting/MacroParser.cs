using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class MacroParser
{
    private static readonly Regex _placeholderRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled
    );
    private static readonly Regex _whitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled
    );

    private static Dictionary<string, VulcanSkill>? _localizedLookup;
    private static Dictionary<string, VulcanSkill>? _englishLookup;

    public static UserMacro? ParseInGameMacro(string macroText, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(macroText))
        {
            GatherBuddy.Log.Warning("[MacroParser] Empty macro text provided");
            return null;
        }

        if (TryParseArtisanExport(macroText, name, out var artisanMacro))
            return artisanMacro;

        var actions = new List<uint>();
        var lines = macroText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//") || trimmedLine.StartsWith("#"))
                continue;
            var actionName = ExtractActionName(trimmedLine);
            if (!string.IsNullOrEmpty(actionName))
            {
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
            Name = string.IsNullOrWhiteSpace(name) ? "Imported Macro" : name,
            Actions = actions,
            Source = "In-Game Macro",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static bool TryParseArtisanExport(string macroText, string? nameOverride, out UserMacro? macro)
    {
        macro = null;
        if (!macroText.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return false;

        try
        {
            using var document = JsonDocument.Parse(macroText);
            var root = document.RootElement;
            if (!root.TryGetProperty("Steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                GatherBuddy.Log.Debug("[MacroParser] JSON input did not contain an Artisan Steps array");
                return false;
            }

            var actions = new List<uint>();
            var skippedCount = 0;
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("Action", out var actionElement) || !actionElement.TryGetUInt32(out var actionId))
                {
                    skippedCount++;
                    GatherBuddy.Log.Debug("[MacroParser] Skipping Artisan step without a numeric Action value");
                    continue;
                }

                if (!TryConvertImportedActionId(actionId, out var convertedActionId))
                {
                    skippedCount++;
                    GatherBuddy.Log.Warning($"[MacroParser] Unsupported Artisan action id {actionId}; skipping step");
                    continue;
                }

                actions.Add(convertedActionId);
                GatherBuddy.Log.Debug($"[MacroParser] Imported Artisan action id {actionId} -> {convertedActionId}");
            }

            if (actions.Count == 0)
            {
                GatherBuddy.Log.Warning("[MacroParser] Artisan export did not contain any supported actions");
                return true;
            }

            var name = string.IsNullOrWhiteSpace(nameOverride)
                ? ReadStringProperty(root, "Name") ?? "Imported Macro"
                : nameOverride;

            macro = new UserMacro
            {
                Name = name,
                Actions = actions,
                MinCraftsmanship = ReadIntProperty(root, "Options", "MinCraftsmanship"),
                MinControl = ReadIntProperty(root, "Options", "MinControl"),
                MinCP = ReadIntProperty(root, "Options", "MinCP"),
                Source = "Artisan Export",
                CreatedAt = DateTime.UtcNow
            };

            GatherBuddy.Log.Information(
                skippedCount > 0
                    ? $"[MacroParser] Parsed {actions.Count} actions from Artisan export and skipped {skippedCount} unsupported steps"
                    : $"[MacroParser] Parsed {actions.Count} actions from Artisan export");
            return true;
        }
        catch (JsonException ex)
        {
            GatherBuddy.Log.Debug($"[MacroParser] Input was not valid Artisan export JSON: {ex.Message}");
            return false;
        }
    }

    private static string? ExtractActionName(string line)
    {
        var firstWhitespace = line.IndexOfAny(new[] { ' ', '\t' });
        var firstToken = firstWhitespace >= 0 ? line[..firstWhitespace] : line;
        var remaining = line;

        if (firstToken.Equals("/ac", StringComparison.OrdinalIgnoreCase)
         || firstToken.Equals("/action", StringComparison.OrdinalIgnoreCase))
        {
            remaining = firstWhitespace >= 0 ? line[(firstWhitespace + 1)..] : string.Empty;
        }
        else if (firstToken.StartsWith("/", StringComparison.Ordinal))
        {
            GatherBuddy.Log.Debug($"[MacroParser] Ignoring non-action command line: {line}");
            return null;
        }

        remaining = _placeholderRegex.Replace(remaining, " ");
        var actionName = _whitespaceRegex.Replace(remaining.Replace("\"", string.Empty), " ").Trim();
        if (string.IsNullOrEmpty(actionName))
        {
            GatherBuddy.Log.Debug($"[MacroParser] Ignoring line without an action name: {line}");
            return null;
        }

        GatherBuddy.Log.Debug($"[MacroParser] Extracted action candidate '{actionName}' from line '{line}'");
        return actionName;
    }

    private static bool TryConvertImportedActionId(uint actionId, out uint convertedActionId)
    {
        convertedActionId = 0;
        if (actionId == 0)
            return false;

        if (Enum.IsDefined(typeof(VulcanSkill), (int)actionId))
        {
            convertedActionId = actionId;
            return true;
        }

        var mappedSkill = SkillActionMap.ActionToSkill(actionId);
        if (mappedSkill != VulcanSkill.None)
        {
            convertedActionId = (uint)mappedSkill;
            return true;
        }

        return false;
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

    private static string? ReadStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int ReadIntProperty(JsonElement element, string parentPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(parentPropertyName, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return 0;
        if (!parent.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
            return 0;
        return value;
    }

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
                lookup.TryAdd(NormalizeName(skill.ToString()), skill);

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
