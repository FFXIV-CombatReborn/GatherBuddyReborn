using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public class MacroParser
{
    private static readonly Regex _actionRegex = new(
        @"/ac(?:tion)?\s+""([^""]+)""|/ac(?:tion)?\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

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
        var normalizedName = actionName.Trim().ToLowerInvariant()
            .Replace(" ", "")
            .Replace("'", "")
            .Replace("-", "");

        var skill = normalizedName switch
        {
            "basicsynthesis" => VulcanSkill.BasicSynthesis,
            "carefulsynthesis" => VulcanSkill.CarefulSynthesis,
            "rapidsynthesis" => VulcanSkill.RapidSynthesis,
            "groundwork" => VulcanSkill.Groundwork,
            "intensivesynthesis" => VulcanSkill.IntensiveSynthesis,
            "prudentsynthesis" => VulcanSkill.PrudentSynthesis,
            "musclememory" => VulcanSkill.MuscleMemory,
            
            "basictouch" => VulcanSkill.BasicTouch,
            "standardtouch" => VulcanSkill.StandardTouch,
            "advancedtouch" => VulcanSkill.AdvancedTouch,
            "hastytouch" => VulcanSkill.HastyTouch,
            "preparatorytouch" => VulcanSkill.PreparatoryTouch,
            "precisetouch" => VulcanSkill.PreciseTouch,
            "prudenttouch" => VulcanSkill.PrudentTouch,
            "trainedfinesse" => VulcanSkill.TrainedFinesse,
            "reflect" => VulcanSkill.Reflect,
            "refinedtouch" => VulcanSkill.RefinedTouch,
            "daringtouch" => VulcanSkill.DaringTouch,
            
            "byregotsblessing" => VulcanSkill.ByregotsBlessing,
            "trainedeye" => VulcanSkill.TrainedEye,
            "delicatesynthesis" => VulcanSkill.DelicateSynthesis,
            
            "veneration" => VulcanSkill.Veneration,
            "innovation" => VulcanSkill.Innovation,
            "greatstrides" => VulcanSkill.GreatStrides,
            "tricksofthetrade" => VulcanSkill.TricksOfTrade,
            "mastersmend" => VulcanSkill.MastersMend,
            "manipulation" => VulcanSkill.Manipulation,
            "wastenot" => VulcanSkill.WasteNot,
            "wastenotii" => VulcanSkill.WasteNot2,
            "observe" => VulcanSkill.Observe,
            "carefulobservation" => VulcanSkill.CarefulObservation,
            "finalappraisal" => VulcanSkill.FinalAppraisal,
            "heartandsoul" => VulcanSkill.HeartAndSoul,
            "quickinnovation" => VulcanSkill.QuickInnovation,
            "immaculatemend" => VulcanSkill.ImmaculateMend,
            "trainedperfection" => VulcanSkill.TrainedPerfection,
            
            _ => VulcanSkill.None
        };

        return (uint)skill;
    }
}
