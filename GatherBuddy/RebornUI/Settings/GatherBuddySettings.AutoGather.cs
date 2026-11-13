using Dalamud.Interface;
using GatherBuddy.AutoGather;
using GatherBuddy.Classes;
using GatherBuddy.RebornUI.Settings;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;

namespace GatherBuddy.RebornUI;

internal sealed partial class GatherBuddySettings
{
    private static readonly M3PluginRequirement Lifestream = new("Lifestream");
    private static readonly M3PluginRequirement AllaganTools = new("InventoryTools", "Allagan Tools");
    private static readonly M3PluginRequirement AutoHook = new("AutoHook");
    private static readonly M3PluginRequirement AutoRetainer = new("AutoRetainer");

    private List<(uint Id, string Name)> _mounts = [];
    private DateTime _mountsRefreshed = DateTime.MinValue;

    private Fish[]? _presetFish;
    private string[] _presetFishNames = [];
    private int _presetFishIndex = -1;
    private string _presetName = string.Empty;

    private M3SettingsPage BuildAutoGatherPage()
        => new(AutoGatherPageId, "Auto-Gather settings", FontAwesomeIcon.Robot, "How auto-gather moves, gathers, fishes and looks after your gear",
            new M3SettingsGroup("general", "General", FontAwesomeIcon.Leaf, null,
                Custom("Select mount mount roulette", DrawMountSelector),
                Float("Mount up distance", "The distance at which you will mount up to move to a node.",
                    () => AutoCfg.MountUpDistance, v => AutoCfg.MountUpDistance = v, 0.1f, 100f, 0.5f, unit: "y"),
                Float("Landing distance",
                    "The fixed distance from the node at which you will try to land. Used when random landing positions are disabled, "
                  + "or when no collected data is available. Low values make it harder to dismount; high values may produce odd paths. "
                  + "Reasonable values are between 4 and 8 yalms.",
                    () => AutoCfg.LandingDistance, v => AutoCfg.LandingDistance = v, 0f, 50f, 0.5f, unit: "y"),
                Toggle("Move while mounting up", "Begin pathfinding to the next node while summoning a mount.",
                    () => AutoCfg.MoveWhileMounting, v => AutoCfg.MoveWhileMounting = v),
                Custom("Play a sound when done gathering honk playback volume", () =>
                {
                    SettingRows.Switch("Play a sound when done gathering",
                        "Play a sound when auto-gathering shuts down because your list is complete.",
                        AutoCfg.HonkMode, v => AutoCfg.HonkMode = v);
                    if (!AutoCfg.HonkMode)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.Slider("Playback volume", null, AutoCfg.SoundPlaybackVolume, 0, 100, v => AutoCfg.SoundPlaybackVolume = v);
                }),
                PluginToggle("Check retainer inventories", "Use Allagan Tools to check retainer inventories when doing inventory calculations.",
                    () => AutoCfg.CheckRetainers, v => AutoCfg.CheckRetainers = v, AllaganTools),
                Toggle("Teleport to next timed item",
                    "Teleport to an upcoming timed node or fishing spot and wait at the aetheryte when there is nothing else to gather. "
                  + "This option has priority over going home when idle.",
                    () => AutoCfg.TeleportToNextNode, v => AutoCfg.TeleportToNextNode = v),
                PluginToggle("Go home when done", "Uses the '/li auto' command to take you home when done gathering.",
                    () => AutoCfg.GoHomeWhenDone, v => AutoCfg.GoHomeWhenDone = v, Lifestream),
                PluginToggle("Go home when idle", "Uses the '/li auto' command to take you home when waiting for timed nodes.",
                    () => AutoCfg.GoHomeWhenIdle, v => AutoCfg.GoHomeWhenIdle = v, Lifestream),
                Toggle("Gather any crystals when The Giving Land is off cooldown",
                    "Gather random crystals on any regular node when The Giving Land is available, regardless of the current target item.",
                    () => AutoCfg.UseGivingLandOnCooldown, v => AutoCfg.UseGivingLandOnCooldown = v),
                Toggle("Use skills for fallback items", "Use skills when gathering items from fallback presets.",
                    () => AutoCfg.UseSkillsForFallbackItems, v => AutoCfg.UseSkillsForFallbackItems = v),
                Toggle("Abandon nodes without needed items",
                    "Stop gathering and abandon the node when you have gathered enough items, or if the node didn't have any needed items in the first place.",
                    () => AutoCfg.AbandonNodes, v => AutoCfg.AbandonNodes = v),
                Toggle("Always gather maps when available", "GBR will always grab maps first if it sees one in a node.",
                    () => AutoCfg.AlwaysGatherMaps, v => AutoCfg.AlwaysGatherMaps = v)),
            new M3SettingsGroup("fishing", "Fishing", FontAwesomeIcon.Fish, null,
                PluginToggle("Use AutoHook global preset",
                    "Clear AutoHook's selected custom preset and let AutoHook use its built-in Global Preset for rod fishing. "
                  + "This takes precedence over both GBR-generated presets and fish ID AutoHook presets. Spearfishing still uses GBR-generated AutoGig presets.",
                    () => AutoCfg.UseAutoHookGlobalPreset, v => AutoCfg.UseAutoHookGlobalPreset = v, AutoHook),
                PluginToggle("Use existing AutoHook presets",
                    "Use your own AutoHook presets instead of GBR-generated ones. Name your preset using the fish's item ID (for example '46188' for Goldentail); "
                  + "find fish IDs by hovering over fish in the Fish page. Ignored when the AutoHook global preset is in use. "
                  + "Your presets are never deleted; only GBR-generated presets are cleaned up.",
                    () => AutoCfg.UseExistingAutoHookPresets, v => AutoCfg.UseExistingAutoHookPresets = v, AutoHook),
                IntSlider("Max fishing spot minutes", "The maximum number of minutes you will fish at a fishing spot.",
                    () => AutoCfg.MaxFishingSpotMinutes, v => AutoCfg.MaxFishingSpotMinutes = v, 1, 40),
                Toggle("Opt in to fishing data collection",
                    "Whenever you catch a fish, the data for that fish is uploaded to a remote server, to make a usable auto-fishing feature possible. "
                  + "No personal information about you or your character is collected, only data relevant to the caught fish. You can opt out again at any time.",
                    () => AutoCfg.FishDataCollection, v => AutoCfg.FishDataCollection = v, "share"),
                Toggle("Auto collectables", "Auto accept or decline collectable fish based on minimum collectability.",
                    () => AutoCfg.AutoCollectablesFishing, v => AutoCfg.AutoCollectablesFishing = v),
                Toggle("Defer repairs during fishing buffs",
                    "Prevents GBR from stopping fishing for repairs while you have active fishing buffs, like Patience, Surface Slap, Identical Cast or Prize Catch.",
                    () => AutoCfg.DeferRepairDuringFishingBuffs, v => AutoCfg.DeferRepairDuringFishingBuffs = v),
                Toggle("Defer aetherial reduction during fishing buffs",
                    "Prevents GBR from stopping fishing for aetherial reduction while you have active fishing buffs.",
                    () => AutoCfg.DeferReductionDuringFishingBuffs, v => AutoCfg.DeferReductionDuringFishingBuffs = v),
                Toggle("Defer materia extraction during fishing buffs",
                    "Prevents GBR from stopping fishing for materia extraction while you have active fishing buffs.",
                    () => AutoCfg.DeferMateriaExtractionDuringFishingBuffs, v => AutoCfg.DeferMateriaExtractionDuringFishingBuffs = v),
                PluginToggle("Use hook timers in AutoHook presets", "Enable bite timer windows in generated AutoHook presets.",
                    () => AutoCfg.UseHookTimers, v => AutoCfg.UseHookTimers = v, AutoHook),
                Custom("Manual preset generator AutoHook preset generate fish", DrawManualPresetGenerator)),
            new M3SettingsGroup("advanced", "Advanced", FontAwesomeIcon.Wrench, null,
                Custom("Repair gear when needed repair threshold durability", () =>
                {
                    SettingRows.Switch("Repair gear when needed", "Repair gear when it is almost broken.",
                        AutoCfg.DoRepair, v => AutoCfg.DoRepair = v);
                    if (!AutoCfg.DoRepair)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.Slider("Repair threshold", "The percentage of durability at which you will repair your gear.",
                        AutoCfg.RepairThreshold, 1, 100, v => AutoCfg.RepairThreshold = v);
                }),
                Toggle("Enable materia extraction", "Automatically extract materia from items with a complete spiritbond.",
                    () => AutoCfg.DoMaterialize, v => AutoCfg.DoMaterialize = v),
                Custom("Enable aetherial reduction always reduce all items", () =>
                {
                    SettingRows.Switch("Enable aetherial reduction",
                        "Automatically perform aetherial reduction when idling, or if the number of free inventory slots drops below 20.",
                        AutoCfg.DoReduce, v => AutoCfg.DoReduce = v);
                    if (!AutoCfg.DoReduce)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.Switch("Always reduce all items",
                        "When free inventory slots drop below 20 while gathering, emergency reduction is performed for all items at once "
                      + "instead of only one item type.",
                        AutoCfg.AlwaysReduceAllItems, v => AutoCfg.AlwaysReduceAllItems = v);
                }),
                Custom("Wait for AutoRetainer multi-mode threshold seconds delay timed nodes", () =>
                {
                    SettingRows.PluginSwitch("Wait for AutoRetainer multi-mode",
                        "Pause GBR automatically when AutoRetainer has retainers to process during multi-mode.",
                        AutoCfg.AutoRetainerMultiMode, v => AutoCfg.AutoRetainerMultiMode = v, AutoRetainer);
                    if (!AutoCfg.AutoRetainerMultiMode)
                    {
                        return;
                    }

                    using var sub = M3SubGroup.Begin();
                    SettingRows.Number("AutoRetainer threshold",
                        "How many seconds before a retainer venture completes GBR should pause and wait for multi-mode.",
                        AutoCfg.AutoRetainerMultiModeThreshold, 0, 3600, v => AutoCfg.AutoRetainerMultiModeThreshold = v, 10, "s");
                    SettingRows.Switch("Delay AutoRetainer for timed nodes",
                        "Wait to process retainers until after active and upcoming timed nodes are gathered.",
                        AutoCfg.AutoRetainerDelayForTimedNodes, v => AutoCfg.AutoRetainerDelayForTimedNodes = v);
                }),
                Toggle("Diadem auto-aethercannon",
                    "Automatically target and fire the aethercannon at nearby enemies when the gauge is ready (200 or more). "
                  + "Only fires while not pathing, with a 2 second cooldown between uses.",
                    () => AutoCfg.DiademAutoAetherCannon, v => AutoCfg.DiademAutoAetherCannon = v),
                Toggle("Diadem windmire jumps",
                    "Allows the use of windmires for jumping between islands in the Diadem, when they give a significant distance advantage over normal movement.",
                    () => AutoCfg.DiademWindmireJumps, v => AutoCfg.DiademWindmireJumps = v),
                Toggle("Re-enter the Diadem to reset clouded nodes",
                    "After gathering umbral items from a clouded node, re-enter the instance to make the node reappear.",
                    () => AutoCfg.DiademFarmCloudedNodes, v => AutoCfg.DiademFarmCloudedNodes = v),
                Custom("Item sorting method location none", () => SettingRows.Combo("Item sorting method",
                    "What method to use when sorting items internally.", AutoCfg.SortingMethod, v => AutoCfg.SortingMethod = v)),
                Custom("Lifestream command auto", () => SettingRows.Text("Lifestream command",
                    "The command used when idling or done gathering. Do not include '/li'. Be careful when changing this; GBR does not validate it.",
                    AutoCfg.LifestreamCommand, v => AutoCfg.LifestreamCommand = string.IsNullOrEmpty(v) ? "auto" : v, "auto", 100)),
                Float("Anti-stuck cooldown", "The time in seconds before the navigation system resets if you are stuck.",
                    () => AutoCfg.NavResetCooldown, v => AutoCfg.NavResetCooldown = v, 0.1f, 10f, 0.1f, unit: "s"),
                Float("Stuck threshold", "The time in seconds before the navigation system considers you stuck.",
                    () => AutoCfg.NavResetThreshold, v => AutoCfg.NavResetThreshold = v, 0.1f, 10f, 0.1f, unit: "s"),
                Int("Timed node precognition", "How many seconds before a node is actually up GBR should consider it up.",
                    () => AutoCfg.TimedNodePrecog, v => AutoCfg.TimedNodePrecog = v, 0, 600, 5, "s", "seconds"),
                Int("Execution delay", "Delay executing each action by this amount.",
                    () => (int)AutoCfg.ExecutionDelay, v => AutoCfg.ExecutionDelay = (uint)Math.Clamp(v, 0, 10000), 0, 10000, 50, "ms", "milliseconds"),
                Toggle("Enable gathering window interaction",
                    "Automatically gather items. Disable this for navigation-only mode; disabling it is unsupported.",
                    () => AutoCfg.DoGathering, v => AutoCfg.DoGathering = v, "nav only mode"),
                Toggle("Disable map marker navigation", "Whether or not to navigate using map markers (timed nodes only).",
                    () => AutoCfg.DisableFlagPathing, v => AutoCfg.DisableFlagPathing = v, "flag"),
                Toggle("Use vnavmesh navigation", "Use vnavmesh navigation to move your character automatically.",
                    () => AutoCfg.UseNavigation, v => AutoCfg.UseNavigation = v),
                Toggle("Force walking", "Force walking to nodes instead of using mounts.",
                    () => AutoCfg.ForceWalking, v => AutoCfg.ForceWalking = v),
                Toggle("Disable random landing positions",
                    "GBR collects the positions where players were seen gathering and lands at one of them at random. "
                  + "Turn this on to use the fixed landing distance instead.",
                    () => AutoCfg.DisableRandomLandingPositions, v => AutoCfg.DisableRandomLandingPositions = v, "offsets")));

    private unsafe void RefreshMounts()
    {
        var current = AutoCfg.AutoGatherMountId;
        if (_mounts.Count > 0 && (DateTime.UtcNow - _mountsRefreshed).TotalSeconds < 5 && _mounts.Exists(m => m.Id == current))
        {
            return;
        }

        _mountsRefreshed = DateTime.UtcNow;
        var sheet = Dalamud.GameData.GetExcelSheet<Mount>();
        var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        var mounts = new List<(uint Id, string Name)>();
        foreach (var mount in sheet)
        {
            if (mount.RowId == 0)
            {
                continue;
            }

            var name = mount.Singular.ToString();
            if (name.Length == 0)
            {
                continue;
            }

            var unlocked = playerState != null && playerState->IsMountUnlocked(mount.RowId);
            if (unlocked || mount.RowId == current)
            {
                mounts.Add((mount.RowId, name.ToProperCase()));
            }
        }

        mounts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        mounts.Insert(0, (0, "Mount Roulette"));
        _mounts = mounts;
    }

    private void DrawMountSelector()
    {
        RefreshMounts();
        var current = AutoCfg.AutoGatherMountId;
        var index = Math.Max(0, _mounts.FindIndex(m => m.Id == current));
        if (M3Widgets.RowCombo("Mount", ref index, _mounts.Count, i => _mounts[i].Name, "The mount auto-gather summons.", search: true)
         && index >= 0 && index < _mounts.Count && _mounts[index].Id != current)
        {
            AutoCfg.AutoGatherMountId = _mounts[index].Id;
            Config.Save();
        }
    }

    private void DrawManualPresetGenerator()
    {
        M3Widgets.SectionLabel("Manual preset generator");
        if (_presetFish == null)
        {
            var fishes = new List<Fish>();
            foreach (var candidate in GatherBuddy.GameData.Fishes.Values)
            {
                if (!candidate.IsSpearFish)
                {
                    fishes.Add(candidate);
                }
            }

            fishes.Sort(static (a, b) =>
            {
                var order = string.Compare(a.Name[GatherBuddy.Language], b.Name[GatherBuddy.Language], StringComparison.CurrentCulture);
                return order != 0 ? order : a.ItemId.CompareTo(b.ItemId);
            });
            _presetFish = [.. fishes];
            _presetFishNames = Array.ConvertAll(_presetFish, f => f.Name[GatherBuddy.Language]);
        }

        var index = _presetFishIndex;
        if (M3Widgets.RowCombo("Target fish##preset_fish", ref index, _presetFishNames, "Generate an AutoHook preset for this fish.", search: true)
         && index >= 0 && index < _presetFish.Length && index != _presetFishIndex)
        {
            _presetFishIndex = index;
            _presetName = _presetFish[index].ItemId.ToString();
        }

        if (_presetFishIndex < 0 || _presetFishIndex >= _presetFish.Length)
        {
            return;
        }

        var fish = _presetFish[_presetFishIndex];
        M3Widgets.RowText("Preset name##preset_name", ref _presetName, fish.ItemId.ToString(),
            "The preset name should match the fish's item ID for GBR to use it automatically.", 64);
        if (M3Widgets.RowButton("Generate the preset##preset_generate", "Generate preset", null, M3ButtonStyle.Filled, FontAwesomeIcon.Magic))
        {
            GenerateManualPreset(fish, _presetName);
        }
    }

    private void GenerateManualPreset(Fish fish, string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            presetName = fish.ItemId.ToString();
        }

        var name = fish.Name[GatherBuddy.Language];
        if (!AutoHookIntegration.AutoHookService.ExportPresetToAutoHook(presetName, [fish], _interface.MatchConfigPreset(fish)))
        {
            Dalamud.Chat.PrintError($"[GatherBuddy] Failed to generate preset for {name}");
            M3Snackbar.Show($"Couldn't generate a preset for {name}.", severity: M3Severity.Error);
            return;
        }

        if (HasOnlyRodPredators(fish))
        {
            Dalamud.Chat.Print($"[GatherBuddy] Generated 2 presets for {name}: '{presetName}_Predators' and '{presetName}_Target'");
            M3Snackbar.Show($"Generated '{presetName}_Predators' and '{presetName}_Target' for {name}.", severity: M3Severity.Success);
        }
        else
        {
            Dalamud.Chat.Print($"[GatherBuddy] Generated preset '{presetName}' for {name}");
            M3Snackbar.Show($"Generated '{presetName}' for {name}.", severity: M3Severity.Success);
        }
    }

    private static bool HasOnlyRodPredators(Fish fish)
    {
        if (fish.Predators.Length == 0)
        {
            return false;
        }

        foreach (var (predator, _) in fish.Predators)
        {
            if (predator.IsSpearFish)
            {
                return false;
            }
        }

        return true;
    }
}
