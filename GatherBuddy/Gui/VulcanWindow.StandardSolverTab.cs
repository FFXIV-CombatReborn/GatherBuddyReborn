using Dalamud.Interface;
using GatherBuddy.RebornUI.Settings;
using RebornMaterial;
using System;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static readonly string[] CollectibleModeNames = ["Tier 1 (lowest)", "Tier 2 (middle)", "Tier 3 (highest)"];

    private static void DrawStandardSolverPage()
    {
        var config = GatherBuddy.Config.StandardSolverConfig;

        using (M3Card.Begin("standard_solver_tricks", "Tricks of the Trade", FontAwesomeIcon.Magic, M3.AccentFor(0)))
        {
            SettingRows.Switch("Use on Good condition", "Use Tricks of the Trade when the condition is Good.",
                config.UseTricksGood, v => config.UseTricksGood = v);
            SettingRows.Switch("Use on Excellent condition", "Use Tricks of the Trade when the condition is Excellent.",
                config.UseTricksExcellent, v => config.UseTricksExcellent = v);
        }

        using (M3Card.Begin("standard_solver_quality", "Quality", FontAwesomeIcon.Star, M3.AccentFor(1)))
        {
            SettingRows.Slider("Target HQ chance", "The HQ percentage to aim for on normal crafts.",
                config.MaxPercentage, 0, 100, v => config.MaxPercentage = v);
            SettingRows.Switch("Use a quality starter (Reflect)", "Open with Reflect for quality instead of Muscle Memory for progress.",
                config.UseQualityStarter, v => config.UseQualityStarter = v);
            SettingRows.Slider("Max Inner Quiet for Preparatory Touch", "The most Inner Quiet stacks before Preparatory Touch is used.",
                config.MaxIQPrepTouch, 0, 10, v => config.MaxIQPrepTouch = v);
        }

        using (M3Card.Begin("standard_solver_collectables", "Collectables", FontAwesomeIcon.Gem, M3.AccentFor(2)))
        {
            var mode = Math.Clamp(config.SolverCollectibleMode - 1, 0, CollectibleModeNames.Length - 1);
            SettingRows.Combo("Collectable target", "Which collectability tier to aim for.", mode, CollectibleModeNames,
                i => config.SolverCollectibleMode = i + 1);
        }

        using (M3Card.Begin("standard_solver_specialist", "Specialist", FontAwesomeIcon.Award, M3.AccentFor(0)))
        {
            SettingRows.Switch("Use specialist actions", "Use Careful Observation and Heart and Soul when they are available.",
                config.UseSpecialist, v => config.UseSpecialist = v);
        }

        using (M3Card.Begin("standard_solver_miracle", "Material Miracle", FontAwesomeIcon.Gift, M3.AccentFor(1)))
        {
            SettingRows.Switch("Use Material Miracle", "Use Material Miracle during crafts.",
                config.UseMaterialMiracle, v => config.UseMaterialMiracle = v);
            if (config.UseMaterialMiracle)
            {
                using var sub = M3SubGroup.Begin();
                SettingRows.Slider("Minimum steps before Material Miracle", "How many crafting steps to take before it is used.",
                    config.MinimumStepsBeforeMiracle, 1, 10, v => config.MinimumStepsBeforeMiracle = v);
                SettingRows.Switch("Allow more than one Material Miracle", "Let it be used several times in one craft.",
                    config.MaterialMiracleMulti, v => config.MaterialMiracleMulti = v);
            }
        }

        if (M3Widgets.RowHoldButton("Reset the Standard Solver settings", "Reset to defaults",
                "Puts every setting on this page back to its default. Hold the button to confirm.", 0.6f, M3ButtonStyle.Outlined, FontAwesomeIcon.Undo))
        {
            GatherBuddy.Config.StandardSolverConfig = new Vulcan.StandardSolverConfig();
            GatherBuddy.Config.Save();
            M3Snackbar.Show("Reset the Standard Solver settings.");
        }
    }
}
