using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class SkillActionMap
{
    private static Dictionary<uint, VulcanSkill> _actionToSkill = new();
    private static Dictionary<int, uint[]> _skillToAction = new();

    public static VulcanSkill ActionToSkill(uint actionId) => _actionToSkill.GetValueOrDefault(actionId);

    public static uint ActionId(this VulcanSkill skill, uint jobId)
    {
        if (!_skillToAction.TryGetValue((int)skill, out var actionIds))
            return 0;
        
        var jobIndex = jobId - 8;
        if (jobIndex < 0 || jobIndex >= 8)
            return 0;
        
        return actionIds[jobIndex];
    }

    static SkillActionMap()
    {
        foreach (VulcanSkill skill in (VulcanSkill[])Enum.GetValues(typeof(VulcanSkill)))
        {
            if (skill == VulcanSkill.None)
                continue;
            AssignActionIDs(skill);
        }
    }

    private static void AssignActionIDs(VulcanSkill skill)
    {
        var baseActionId = (uint)skill;
        if (baseActionId == 0 || baseActionId >= 200000)
            return;

        try
        {
            var isCraftAction = baseActionId >= 100000;
            var actionIds = new uint[8];
            string skillName = null;

            if (isCraftAction)
            {
                var craftActionsSheet = Dalamud.GameData.GetExcelSheet<CraftAction>();
                if (craftActionsSheet == null || !craftActionsSheet.TryGetRow(baseActionId, out var craftRow))
                    return;
                
                skillName = craftRow.Name.ToString().Trim();
                if (string.IsNullOrEmpty(skillName))
                    return;

                for (int jobIdx = 0; jobIdx < 8; jobIdx++)
                {
                    var classJobCategory = jobIdx + 9;
                    uint convertedId = 0;
                    foreach (var row in craftActionsSheet)
                    {
                        if (row.ClassJobCategory.RowId == classJobCategory && row.Name.ToString().Trim() == skillName)
                        {
                            convertedId = row.RowId;
                            break;
                        }
                    }
                    actionIds[jobIdx] = convertedId;
                    if (convertedId != 0)
                        _actionToSkill[convertedId] = skill;
                }
            }
            else
            {
                var actionSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (actionSheet == null || !actionSheet.TryGetRow(baseActionId, out var actionRow))
                    return;
                
                skillName = actionRow.Name.ToString();
                if (string.IsNullOrEmpty(skillName))
                    return;

                for (int jobIdx = 0; jobIdx < 8; jobIdx++)
                {
                    var classJobId = jobIdx + 8;
                    uint convertedId = 0;
                    foreach (var row in actionSheet)
                    {
                        if (row.ClassJob.RowId == classJobId && row.Name.ToString() == skillName)
                        {
                            convertedId = row.RowId;
                            break;
                        }
                    }
                    actionIds[jobIdx] = convertedId;
                    if (convertedId != 0)
                        _actionToSkill[convertedId] = skill;
                }
            }

            _skillToAction[(int)skill] = actionIds;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to assign action IDs for skill {skill}: {ex.Message}");
        }
    }
}
