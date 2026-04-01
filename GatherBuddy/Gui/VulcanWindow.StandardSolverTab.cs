using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private void DrawStandardSolverConfigTab()
    {
        IDisposable tabItem;
        bool tabOpen;
        
        if (GatherBuddy.ControllerSupport != null)
        {
        var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Standard Solver##standardSolverTab", 3, 8);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Standard Solver##standardSolverTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }
        
        using (tabItem)
        {
            if (!tabOpen)
                return;

        var config = GatherBuddy.Config.StandardSolverConfig;

        ImGui.Text("Standard Solver Configuration");
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Configure the dynamic Standard Solver behavior");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Tricks of the Trade Settings");
        ImGui.Spacing();
        
        var useTricksGood = config.UseTricksGood;
        if (ImGui.Checkbox("Use Tricks on Good Condition", ref useTricksGood))
        {
            config.UseTricksGood = useTricksGood;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Tricks of the Trade when condition is Good");

        var useTricksExcellent = config.UseTricksExcellent;
        if (ImGui.Checkbox("Use Tricks on Excellent Condition", ref useTricksExcellent))
        {
            config.UseTricksExcellent = useTricksExcellent;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Tricks of the Trade when condition is Excellent");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Quality Settings");
        ImGui.Spacing();

        var maxPercentage = config.MaxPercentage;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Target HQ %##maxPercentage", ref maxPercentage, 0, 100))
        {
            config.MaxPercentage = maxPercentage;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Target HQ percentage for normal crafts (0-100)");

        var useQualityStarter = config.UseQualityStarter;
        if (ImGui.Checkbox("Use Quality Starter (Reflect)", ref useQualityStarter))
        {
            config.UseQualityStarter = useQualityStarter;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Reflect at the start for quality instead of Muscle Memory for progress");

        var maxIQPrepTouch = config.MaxIQPrepTouch;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Max IQ for Prep Touch##maxIQPrepTouch", ref maxIQPrepTouch, 0, 10))
        {
            config.MaxIQPrepTouch = maxIQPrepTouch;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum Inner Quiet stacks before using Preparatory Touch");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Collectible Settings");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        var collectibleModes = new[] { "Tier 1 (Min)", "Tier 2 (Mid)", "Tier 3 (Max)" };
        var collectibleMode = Math.Clamp(config.SolverCollectibleMode - 1, 0, collectibleModes.Length - 1);
        if (ImGui.Combo("Collectible Target##collectibleMode", ref collectibleMode, collectibleModes, collectibleModes.Length))
        {
            config.SolverCollectibleMode = collectibleMode + 1;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which collectible tier to aim for (1=lowest, 3=highest)");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Specialist Settings");
        ImGui.Spacing();

        var useSpecialist = config.UseSpecialist;
        if (ImGui.Checkbox("Use Specialist Actions", ref useSpecialist))
        {
            config.UseSpecialist = useSpecialist;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Careful Observation and Heart & Soul when available");
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginGroup();
        ImGui.Text("Material Miracle Settings");
        ImGui.Spacing();

        var useMaterialMiracle = config.UseMaterialMiracle;
        if (ImGui.Checkbox("Use Material Miracle", ref useMaterialMiracle))
        {
            config.UseMaterialMiracle = useMaterialMiracle;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use Material Miracle action during crafts");

        if (config.UseMaterialMiracle)
        {
            var minSteps = config.MinimumStepsBeforeMiracle;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Min Steps Before Miracle##minSteps", ref minSteps, 1, 10))
            {
                config.MinimumStepsBeforeMiracle = minSteps;
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Minimum crafting steps before using Material Miracle");

            var materialMiracleMulti = config.MaterialMiracleMulti;
            if (ImGui.Checkbox("Allow Multiple Material Miracles", ref materialMiracleMulti))
            {
                config.MaterialMiracleMulti = materialMiracleMulti;
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Allow using Material Miracle multiple times in a single craft");
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults", new Vector2(200, 0)))
        {
            GatherBuddy.Config.StandardSolverConfig = new Vulcan.StandardSolverConfig();
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset all Standard Solver settings to their default values");
        }
    }
}
