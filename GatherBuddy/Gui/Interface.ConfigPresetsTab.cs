using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using GatherBuddy.AutoGather;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI;
using GatherBuddy.RebornUI.Settings;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using static GatherBuddy.AutoGather.AutoGather;

namespace GatherBuddy.Gui
{
    public partial class Interface
    {
        private static readonly (string name, uint id)[] CrystalTypes =
            [("Elemental Shards", 2), ("Elemental Crystals", 8), ("Elemental Clusters", 14)];

        private readonly ConfigPresetsSelector _configPresetsSelector = new();

        public IReadOnlyCollection<ConfigPreset> GatherActionsPresets
            => _configPresetsSelector.Presets;

        public class ConfigPresetsSelector : ItemSelector<ConfigPreset>
        {
            private const string FileName = "actions.json";

            public ConfigPresetsSelector()
                : base([], Flags.All ^ Flags.Drop)
            {
                Load();
            }

            public IReadOnlyCollection<ConfigPreset> Presets
                => Items.AsReadOnly();

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
            {
                var isSelected = ListRow(CheckUnnamed(Items[idx].Name), idx == CurrentIdx, !Items[idx].Enabled);

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && idx != Items.Count - 1)
                {
                    Items[idx].Enabled = !Items[idx].Enabled;
                    Save();
                }

                return isSelected;
            }

            protected override string NameOf(ConfigPreset item)
                => item.Name;

            protected override bool OnDelete(int idx)
            {
                if (idx == Items.Count - 1)
                {
                    return false;
                }

                Items.RemoveAt(idx);
                Save();
                return true;
            }

            protected override bool OnAdd(string name)
            {
                Items.Insert(Items.Count - 1, new()
                {
                    Name = name,
                });
                Save();
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                var preset = ConfigPreset.FromBase64String(data);
                if (preset == null)
                {
                    Communicator.PrintError("Failed to load config preset from clipboard. Are you sure it's valid?");
                    return false;
                }

                preset.Name = name;

                Items.Insert(Items.Count - 1, preset);
                Save();
                Communicator.Print($"Imported config preset {preset.Name} from clipboard successfully.");
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                var preset = Items[idx] with
                {
                    Enabled = false,
                    Name = name
                };
                Items.Insert(Math.Min(idx + 1, Items.Count - 1), preset);
                Save();
                return true;
            }

            protected override bool OnMove(int idx1, int idx2)
            {
                idx2 = Math.Min(idx2, Items.Count - 2);
                if (idx1 >= Items.Count - 1)
                {
                    return false;
                }

                if (idx1 < 0 || idx2 < 0)
                {
                    return false;
                }

                Plugin.Functions.Move(Items, idx1, idx2);
                Save();
                return true;
            }

            public void Save()
            {
                var file = Plugin.Functions.ObtainSaveFile(FileName);
                if (file == null)
                {
                    return;
                }

                try
                {
                    var text = JsonConvert.SerializeObject(Items, Formatting.Indented);
                    File.WriteAllText(file.FullName, text);
                }
                catch (Exception e)
                {
                    GatherBuddy.Log.Error($"Error serializing config presets data:\n{e}");
                }
            }

            private void Load()
            {
                try
                {
                    List<ConfigPreset>? items = null;

                    var file = Plugin.Functions.ObtainSaveFile(FileName);
                    if (file != null && file.Exists)
                    {
                        var text = File.ReadAllText(file.FullName);
                        items = JsonConvert.DeserializeObject<List<ConfigPreset>>(text);
                    }

                    if (items != null && items.Count > 0)
                    {
                        foreach (var item in items)
                        {
                            MigrateHQItemIds(item);
                            Items.Add(item);
                        }

                        Items[Items.Count - 1] = Items[Items.Count - 1].MakeDefault();
                    }
                    else
                    {
                        Items.Add(new ConfigPreset().MakeDefault());
                    }
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Error($"Error loading config presets, creating default: {ex}");
                    Items.Clear();
                    try
                    {
                        Items.Add(new ConfigPreset().MakeDefault());
                    }
                    catch (Exception fallbackEx)
                    {
                        GatherBuddy.Log.Error($"Critical error creating default preset: {fallbackEx}");
                    }
                }
            }

            private static void MigrateHQItemIds(ConfigPreset preset)
            {
                MigrateConsumableItemId(preset.Consumables.Cordial);
                MigrateConsumableItemId(preset.Consumables.Food);
                MigrateConsumableItemId(preset.Consumables.Potion);
                MigrateConsumableItemId(preset.Consumables.Manual);
                MigrateConsumableItemId(preset.Consumables.SquadronManual);
                MigrateConsumableItemId(preset.Consumables.SquadronPass);
            }

            private static void MigrateConsumableItemId(ConfigPreset.ActionConfigConsumable consumable)
            {
                if (consumable.ItemId >= 100_000 && consumable.ItemId < 1_000_000)
                {
                    consumable.ItemId = consumable.ItemId - 100_000 + 1_000_000;
                }
            }

            public ConfigPreset Match(IGatherable? item)
            {
                var defaultPreset = Items[Items.Count - 1];
                if (item == null)
                {
                    return defaultPreset;
                }

                for (var i = 0; i < Items.Count - 1; i++)
                {
                    if (Items[i].Match(item))
                    {
                        return Items[i];
                    }
                }

                return defaultPreset;
            }
        }

        public ConfigPreset MatchConfigPreset(IGatherable? item)
            => _configPresetsSelector.Match(item);

        private const string PresetCheckerDialogId = "Config Presets Checker###gbr_preset_checker";

        private static readonly M3Segment[] LevelKindSegments =
        [
            new("Item level", Tooltip: "The level shown in the gathering log and the gathering window."),
            new("Gathering level", Tooltip: "The hidden gathering level (glv). Use it to tell different tiers of legendary nodes apart."),
        ];

        private bool _presetsDirty;

        private readonly Dictionary<string, (DateTime Built, List<(string Name, uint RowId)> Items)> _consumableChoices = [];

        private void MarkPresetsDirty()
            => _presetsDirty = true;

        internal void FlushConfigPresets()
        {
            if (!_presetsDirty)
            {
                return;
            }

            _presetsDirty = false;
            _configPresetsSelector.Save();
        }

        public void DrawConfigPresetsPage()
        {
            if (_presetsDirty && !ImGui.IsAnyItemActive())
            {
                FlushConfigPresets();
            }

            var selector = _configPresetsSelector;
            using (VulcanUiStyle.PushTheme())
            {
                selector.Draw(SelectorWidth);
            }

            ImGui.SameLine();
            using var details = ImRaii.Child("##config_preset_details", -Vector2.One, false);
            if (!details)
            {
                return;
            }

            var preset = selector.EnsureCurrent()!;
            var isDefault = selector.CurrentIdx == selector.Presets.Count - 1;
            using var id = ImRaii.PushId(selector.CurrentIdx);

            DrawPresetOverview(preset, isDefault);
            if (!isDefault)
            {
                DrawPresetFilters(preset);
            }

            DrawPresetGeneral(preset);
            DrawPresetActions(preset);
            DrawPresetConsumables(preset);
            DrawPresetChecker();
        }

        private void DrawPresetOverview(ConfigPreset preset, bool isDefault)
        {
            using var card = M3Card.Begin("preset_overview", isDefault ? "Default preset" : CheckUnnamed(preset.Name), FontAwesomeIcon.SlidersH,
                M3.AccentFor(0), M3CardStyle.Filled,
                "Presets are checked against the item being gathered from top to bottom, and only the first match is used. "
              + "The Default preset always comes last and covers every item no other preset matches.");

            if (!isDefault)
            {
                var name = preset.Name;
                if (M3Widgets.RowText("Name", ref name, "Preset name", null, 64))
                {
                    preset.Name = name;
                    MarkPresetsDirty();
                }

                var enabled = preset.Enabled;
                if (M3Widgets.RowSwitch("Enabled", ref enabled, "Disabled presets are skipped. Double-click a preset in the list to toggle it too."))
                {
                    preset.Enabled = enabled;
                    MarkPresetsDirty();
                }
            }

            if (M3Widgets.RowButton("Share this preset", "Copy", "Copies the preset to your clipboard, ready to import with the list's import button.",
                    M3ButtonStyle.Tonal, FontAwesomeIcon.Copy))
            {
                ImGui.SetClipboardText(preset.ToBase64String());
                Communicator.Print($"Successfully copied {preset.Name} to clipboard.");
                M3Snackbar.Show($"Copied {CheckUnnamed(preset.Name)} to the clipboard.", severity: M3Severity.Success);
            }

            if (M3Widgets.RowButton("Check which preset each item uses", "Check",
                    "Lists every item in your enabled auto-gather lists, and crystals, with the preset it will use.", M3ButtonStyle.Tonal,
                    FontAwesomeIcon.Tasks))
            {
                M3Dialog.Open(PresetCheckerDialogId);
            }
        }

        private void DrawPresetFilters(ConfigPreset preset)
        {
            using var card = M3Card.Begin("preset_filters", "Applies to", FontAwesomeIcon.Filter, M3.AccentFor(1), M3CardStyle.Filled,
                "The items this preset is used for.");

            var kind = preset.ItemLevel.UseGlv ? 1 : 0;
            if (M3Widgets.RowSegmented("Level type", ref kind, LevelKindSegments))
            {
                SwitchLevelKind(preset, kind == 1);
            }

            var low = preset.ItemLevel.Min;
            var high = preset.ItemLevel.Max;
            var useGlv = preset.ItemLevel.UseGlv;
            if (M3Widgets.RowIntRange(useGlv ? "Gathering level range" : "Item level range", ref low, ref high, 1, useGlv ? ConfigPreset.MaxGvl : ConfigPreset.MaxLevel,
                    supporting: "The lowest and highest level this preset is used for."))
            {
                preset.ItemLevel.Min = low;
                preset.ItemLevel.Max = high;
                MarkPresetsDirty();
            }

            var nodes = preset.NodeType;
            DrawChipGroup("preset_nodes", "Node types", null,
                ("Regular", nodes.Regular, v => nodes.Regular = v),
                ("Unspoiled", nodes.Unspoiled, v => nodes.Unspoiled = v),
                ("Legendary", nodes.Legendary, v => nodes.Legendary = v),
                ("Ephemeral", nodes.Ephemeral, v => nodes.Ephemeral = v),
                ("Clouded", nodes.Clouded, v => nodes.Clouded = v));

            var items = preset.ItemType;
            DrawChipGroup("preset_items", "Item types", "Choosing fish turns off deciding actions automatically.",
                ("Crystals", items.Crystals, v => items.Crystals = v),
                ("Collectables", items.Collectables, v => items.Collectables = v),
                ("Gatherables", items.Other, v => items.Other = v),
                ("Fish", items.Fish, v =>
                {
                    items.Fish = v;
                    if (v)
                    {
                        preset.ChooseBestActionsAutomatically = false;
                    }
                }
            ));
        }

        private void SwitchLevelKind(ConfigPreset preset, bool useGlv)
        {
            if (useGlv == preset.ItemLevel.UseGlv)
            {
                return;
            }

            var min = int.MaxValue;
            var max = int.MinValue;
            foreach (var item in GatherBuddy.GameData.Gatherables.Values)
            {
                var glv = (int)item.GatheringData.GatheringItemLevel.RowId;
                var (oldLevel, newLevel) = useGlv ? (item.Level, glv) : (glv, item.Level);
                if (oldLevel >= preset.ItemLevel.Min && newLevel < min)
                {
                    min = newLevel;
                }

                if (oldLevel <= preset.ItemLevel.Max && newLevel > max)
                {
                    max = newLevel;
                }
            }

            if (min == int.MaxValue)
            {
                min = useGlv ? ConfigPreset.MaxGvl : ConfigPreset.MaxLevel;
            }

            if (max == int.MinValue)
            {
                max = 1;
            }

            preset.ItemLevel.UseGlv = useGlv;
            preset.ItemLevel.Min = min;
            preset.ItemLevel.Max = max;
            MarkPresetsDirty();
        }

        private void DrawChipGroup(string id, string label, string? supporting, params (string Label, bool Value, System.Action<bool> Set)[] chips)
        {
            var row = M3SettingRow.Begin(label, supporting, Vector2.Zero);
            M3SettingRow.End(row);

            var indent = M3Style.Spacing(12f, 8f);
            ImGui.Indent(indent);
            var flow = new M3Flow(M3.Space1, MathF.Max(1f, ImGui.GetContentRegionAvail().X - M3Card.RightInset));
            foreach (var (chipLabel, value, set) in chips)
            {
                var icon = value ? FontAwesomeIcon.Check : FontAwesomeIcon.None;
                flow.Next(M3Widgets.ChipWidth(chipLabel, icon));
                if (M3Widgets.Chip($"##{id}_{chipLabel}", chipLabel, value, icon))
                {
                    set(!value);
                    MarkPresetsDirty();
                }
            }

            ImGui.Unindent(indent);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        private void DrawPresetGeneral(ConfigPreset preset)
        {
            var types = preset.ItemType;
            var showGeneralSettings = !types.Fish || types.Crystals || types.Other || types.Collectables;
            if (!showGeneralSettings)
            {
                return;
            }

            using var card = M3Card.Begin("preset_general", "General", FontAwesomeIcon.Cog, M3.AccentFor(2));
            if (types.Crystals || types.Other)
            {
                var gp = preset.GatherableMinGP;
                if (M3Widgets.RowNumber("Minimum GP for regular items and crystals", ref gp, 10, 0, ConfigPreset.MaxGP, "GP"))
                {
                    preset.GatherableMinGP = gp;
                    MarkPresetsDirty();
                }
            }

            if (types.Collectables)
            {
                var gp = preset.CollectableMinGP;
                if (M3Widgets.RowNumber("Minimum GP for collectables", ref gp, 10, 0, ConfigPreset.MaxGP, "GP"))
                {
                    preset.CollectableMinGP = gp;
                    MarkPresetsDirty();
                }

                gp = preset.CollectableActionsMinGP;
                if (M3Widgets.RowNumber("Minimum GP for actions on collectables", ref gp, 10, 0, ConfigPreset.MaxGP, "GP"))
                {
                    preset.CollectableActionsMinGP = gp;
                    MarkPresetsDirty();
                }

                var solidAge = ConcatNames(Actions.SolidAge);
                var alwaysSolidAge = preset.CollectableAlwaysUseSolidAge;
                if (M3Widgets.RowSwitch($"Always use {solidAge}", ref alwaysSolidAge,
                        $"Use {solidAge} regardless of starting GP once the target collectability is reached."))
                {
                    preset.CollectableAlwaysUseSolidAge = alwaysSolidAge;
                    MarkPresetsDirty();
                }

                var manual = preset.CollectableManualScores;
                if (M3Widgets.RowSwitch("Set collectability scores manually", ref manual,
                        "When off, the scores are read from the game. When on, enter the target and minimum scores yourself."))
                {
                    preset.CollectableManualScores = manual;
                    MarkPresetsDirty();
                }

                if (preset.CollectableManualScores)
                {
                    using var sub = M3SubGroup.Begin();
                    var score = preset.CollectableTagetScore;
                    if (M3Widgets.RowNumber("Target collectability", ref score, 10, 0, ConfigPreset.MaxCollectability, null,
                            "Keep going until the item reaches this collectability."))
                    {
                        preset.CollectableTagetScore = score;
                        MarkPresetsDirty();
                    }

                    score = preset.CollectableMinScore;
                    if (M3Widgets.RowNumber("Minimum collectability at the last integrity point", ref score, 10, 0, ConfigPreset.MaxCollectability, null,
                            $"Collect anyway at the last integrity point if the item has at least this much. {ConfigPreset.MaxCollectability} turns it off."))
                    {
                        preset.CollectableMinScore = score;
                        MarkPresetsDirty();
                    }
                }
            }

            var isFishExclusive = types.Fish && !types.Crystals && !types.Other && !types.Collectables;
            if (!isFishExclusive)
            {
                var automatic = preset.ChooseBestActionsAutomatically;
                if (M3Widgets.RowSwitch("Automatically decide what actions to use", ref automatic,
                        "For collectables, the usual collectable rotation is used with every action. For unspoiled and legendary nodes, "
                      + "actions are chosen for the most yield. For regular nodes, actions are chosen for the most yield per GP spent."))
                {
                    preset.ChooseBestActionsAutomatically = automatic;
                    MarkPresetsDirty();
                }
            }

            if (!types.Fish && preset.ChooseBestActionsAutomatically && preset.NodeType.Regular)
            {
                var bestOnly = preset.SpendGPOnBestNodesOnly;
                if (M3Widgets.RowSwitch("Hold off spending GP until a node with the best bonuses", ref bestOnly,
                        "Regular nodes only. Keeps GP for nodes whose bonuses give the best yield per GP: make sure nodes with +2 integrity, "
                      + "+3 yield and +100% boon chance hidden bonuses exist and that you meet their requirements. "
                      + $"Ignored when {ConcatNames(Actions.Bountiful)} gives +3, since nothing beats that. Not recommended with the Revisit trait (level 91+)."))
                {
                    preset.SpendGPOnBestNodesOnly = bestOnly;
                    MarkPresetsDirty();
                }
            }
        }

        private void DrawPresetActions(ConfigPreset preset)
        {
            var types = preset.ItemType;
            if ((types.Crystals || types.Other) && !preset.ChooseBestActionsAutomatically)
            {
                using var card = M3Card.Begin("preset_gathering_actions", "Gathering actions", FontAwesomeIcon.Leaf, M3.AccentFor(0),
                    M3CardStyle.Filled, "Turn on an action to set when it is used.");
                var actions = preset.GatherableActions;
                DrawActionRow(ConcatNames(Actions.Luck), actions.Luck);
                DrawActionRow(ConcatNames(Actions.Bountiful), actions.Bountiful);
                DrawActionRow(ConcatNames(Actions.Yield1), actions.Yield1);
                DrawActionRow(ConcatNames(Actions.Yield2), actions.Yield2);
                DrawActionRow(ConcatNames(Actions.SolidAge), actions.SolidAge);
                DrawActionRow(ConcatNames(Actions.Gift1), actions.Gift1);
                DrawActionRow(ConcatNames(Actions.Gift2), actions.Gift2);
                DrawActionRow(ConcatNames(Actions.Tidings), actions.Tidings);
                if (types.Crystals)
                {
                    DrawActionRow(Actions.TwelvesBounty.Names.Botanist, actions.TwelvesBounty);
                    DrawActionRow(Actions.GivingLand.Names.Botanist, actions.GivingLand);
                }
            }

            if (types.Collectables && !preset.ChooseBestActionsAutomatically)
            {
                using var card = M3Card.Begin("preset_collectable_actions", "Collectable actions", FontAwesomeIcon.Gem, M3.AccentFor(1),
                    M3CardStyle.Filled, "Turn on an action to set when it is used.");
                var actions = preset.CollectableActions;
                DrawActionRow(Actions.Scour.Names.Botanist, actions.Scour);
                DrawActionRow(Actions.Brazen.Names.Botanist, actions.Brazen);
                DrawActionRow(Actions.Meticulous.Names.Botanist, actions.Meticulous);
                DrawActionRow(Actions.Scrutiny.Names.Botanist, actions.Scrutiny);
                DrawActionRow(ConcatNames(Actions.SolidAge), actions.SolidAge);
            }

            if (types.Fish)
            {
                using var card = M3Card.Begin("preset_fishing_actions", "Fishing actions", FontAwesomeIcon.Fish, M3.AccentFor(2),
                    M3CardStyle.Filled, "Turn on an action to set when it is used.");
                var actions = preset.FishingActions;
                DrawToggleRow("Patience / Patience II", actions.Patience);
                DrawFishingActionRow(Actions.DoubleHook.Name, actions.DoubleHook, actions.TripleHook);
                DrawFishingActionRow(Actions.TripleHook.Name, actions.TripleHook, actions.DoubleHook);
                DrawFishingActionRow(Actions.PrizeCatch.Name, actions.PrizeCatch);
                DrawFishingActionRow(Actions.Chum.Name, actions.Chum);
                DrawFishingActionRow(Actions.SurfaceSlap.Name, actions.SurfaceSlap);
                DrawFishingActionRow(Actions.IdenticalCast.Name, actions.IdenticalCast);
                DrawFishingActionRow(Actions.AmbitiousLure.Name, actions.AmbitiousLure);
                DrawFishingActionRow(Actions.ModestLure.Name, actions.ModestLure);
            }
        }

        private void DrawPresetConsumables(ConfigPreset preset)
        {
            using var card = M3Card.Begin("preset_consumables", "Consumables", FontAwesomeIcon.Flask, M3.AccentFor(0), M3CardStyle.Filled,
                "Items in your inventory are listed first, with how many you have.");
            var consumables = preset.Consumables;
            DrawActionRow("Cordial", consumables.Cordial, PossibleCordials);
            DrawActionRow("Food", consumables.Food, PossibleFoods, true);
            DrawActionRow("Medicine", consumables.Potion, PossiblePotions, true);
            DrawActionRow("Manual", consumables.Manual, PossibleManuals, true);
            DrawActionRow("Squadron Manual", consumables.SquadronManual, PossibleSquadronManuals, true);
            DrawActionRow("Squadron Pass", consumables.SquadronPass, PossibleSquadronPasses, true);
        }

        private void DrawActionRow(string name, ConfigPreset.ActionConfig action, IEnumerable<Item>? items = null, bool hideGP = false)
        {
            using var id = ImRaii.PushId(name);
            var enabled = action.Enabled;
            if (M3Widgets.RowSwitch(name, ref enabled))
            {
                action.Enabled = enabled;
                MarkPresetsDirty();
            }

            if (!action.Enabled)
            {
                return;
            }

            using var sub = M3SubGroup.Begin();
            if (action is ConfigPreset.ActionConfigIntegrity firstStep)
            {
                var firstStepOnly = firstStep.FirstStepOnly;
                if (M3Widgets.RowSwitch("Only on the first step", ref firstStepOnly, "Use it only if nothing has been gathered from the node yet."))
                {
                    firstStep.FirstStepOnly = firstStepOnly;
                    MarkPresetsDirty();
                }
            }

            if (!hideGP)
            {
                var low = action.MinGP;
                var high = action.MaxGP;
                if (M3Widgets.RowIntRange("GP", ref low, ref high, 0, ConfigPreset.MaxGP, supporting: "Use it while your GP is in this range."))
                {
                    action.MinGP = low;
                    action.MaxGP = high;
                    MarkPresetsDirty();
                }
            }

            if (action is ConfigPreset.ActionConfigBoon boon)
            {
                var low = boon.MinBoonChance;
                var high = boon.MaxBoonChance;
                if (M3Widgets.RowIntRange("Boon chance", ref low, ref high, 0, 100, "%", "Use it while the boon chance is in this range."))
                {
                    boon.MinBoonChance = low;
                    boon.MaxBoonChance = high;
                    MarkPresetsDirty();
                }
            }

            if (action is ConfigPreset.ActionConfigIntegrity integrity)
            {
                var minIntegrity = integrity.MinIntegrity;
                if (M3Widgets.RowNumber("Minimum initial node integrity", ref minIntegrity, 1, 1, ConfigPreset.MaxIntegrity))
                {
                    integrity.MinIntegrity = minIntegrity;
                    MarkPresetsDirty();
                }
            }

            if (action is ConfigPreset.ActionConfigYieldBonus yieldBonus)
            {
                var minYieldBonus = yieldBonus.MinYieldBonus;
                if (M3Widgets.RowNumber("Minimum yield bonus", ref minYieldBonus, 1, 1, 3))
                {
                    yieldBonus.MinYieldBonus = minYieldBonus;
                    MarkPresetsDirty();
                }
            }

            if (action is ConfigPreset.ActionConfigYieldTotal yieldTotal)
            {
                var minYieldTotal = yieldTotal.MinYieldTotal;
                if (M3Widgets.RowNumber("Minimum total yield", ref minYieldTotal, 1, 1, 30))
                {
                    yieldTotal.MinYieldTotal = minYieldTotal;
                    MarkPresetsDirty();
                }
            }

            if (action is ConfigPreset.ActionConfigConsumable consumable && items != null)
            {
                var choices = ConsumableChoices(name, items);
                var index = Math.Max(0, choices.FindIndex(c => c.RowId == consumable.ItemId));
                if (M3Widgets.RowCombo("Item", ref index, choices.Count, i => choices[i].Name, null, search: true)
                 && index >= 0 && index < choices.Count && choices[index].RowId != consumable.ItemId)
                {
                    consumable.ItemId = choices[index].RowId;
                    MarkPresetsDirty();
                }
            }
        }

        private void DrawToggleRow(string name, ConfigPreset.ToggleConfig action)
        {
            using var id = ImRaii.PushId(name);
            var enabled = action.Enabled;
            if (M3Widgets.RowSwitch(name, ref enabled))
            {
                action.Enabled = enabled;
                MarkPresetsDirty();
            }
        }

        private void DrawFishingActionRow(string name, ConfigPreset.FishingActionConfig action, ConfigPreset.FishingActionConfig? oppositeAction = null)
        {
            using var id = ImRaii.PushId(name);
            var enabled = action.Enabled;
            var supporting = oppositeAction != null ? "Double Hook and Triple Hook can't both be on; turning one on turns the other off." : null;
            if (M3Widgets.RowSwitch(name, ref enabled, supporting))
            {
                action.Enabled = enabled;
                if (enabled && oppositeAction != null)
                {
                    oppositeAction.Enabled = false;
                }

                MarkPresetsDirty();
            }

            if (!action.Enabled)
            {
                return;
            }

            using var sub = M3SubGroup.Begin();
            var above = action.GpThresholdAbove ? 0 : 1;
            if (M3Widgets.RowSegmented("Use when GP is", ref above, SettingRows.AboveBelowSegments))
            {
                action.GpThresholdAbove = above == 0;
                MarkPresetsDirty();
            }

            var threshold = action.GpThreshold;
            if (M3Widgets.RowNumber("GP threshold", ref threshold, 10, 0, ConfigPreset.MaxGP, "GP"))
            {
                action.GpThreshold = threshold;
                MarkPresetsDirty();
            }
        }

        private List<(string Name, uint RowId)> ConsumableChoices(string key, IEnumerable<Item> items)
        {
            if (_consumableChoices.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.Built).TotalSeconds < 2)
            {
                return cached.Items;
            }

            var entries = new List<(string Name, uint RowId, int Count, int Order)>();
            foreach (var item in items)
            {
                AddConsumableChoice(entries, item.RowId);
                if (item.CanBeHq)
                {
                    AddConsumableChoice(entries, item.RowId + 1_000_000);
                }
            }

            entries.Sort(static (a, b) =>
            {
                var order = (a.Count == 0).CompareTo(b.Count == 0);
                if (order != 0)
                {
                    return order;
                }

                order = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                return order != 0 ? order : a.Order.CompareTo(b.Order);
            });

            var list = new List<(string Name, uint RowId)>(entries.Count + 1) { ("None", 0u) };
            foreach (var (name, rowId, count, _) in entries)
            {
                list.Add(($"{name} ({count})", rowId));
            }

            _consumableChoices[key] = (DateTime.UtcNow, list);
            return list;
        }

        private static void AddConsumableChoice(List<(string Name, uint RowId, int Count, int Order)> entries, uint rowId)
        {
            var name = ItemUtil.GetItemName(rowId, includeIcon: true).ExtractText();
            if (!string.IsNullOrEmpty(name))
            {
                entries.Add((name, rowId, GetInventoryItemCount(rowId), entries.Count));
            }
        }

        private void DrawPresetChecker()
        {
            using var dialog = M3Dialog.Begin(PresetCheckerDialogId, "Which preset each item uses",
                "Items from your enabled auto-gather lists, and crystals, with the preset that matches each of them.", FontAwesomeIcon.Tasks, 640f);
            if (!dialog.IsOpen)
            {
                return;
            }

            var rows = new List<(string List, string Name, IGatherable Item)>();
            foreach (var list in _plugin.AutoGatherListsManager.Lists)
            {
                if (!list.Enabled || list.Fallback)
                {
                    continue;
                }

                foreach (var item in list.Items)
                {
                    rows.Add((list.Name, item.Name[GatherBuddy.Language], item));
                }
            }

            foreach (var (name, id) in CrystalTypes)
            {
                if (GatherBuddy.GameData.Gatherables.TryGetValue(id, out var crystal))
                {
                    rows.Add((string.Empty, name, crystal));
                }
            }

            var lineHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2f;
            var height = MathF.Min(rows.Count + 1, 14) * lineHeight + ImGui.GetStyle().CellPadding.Y * 2f;
            using (var table = ImRaii.Table("##preset_checker", 3,
                       ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                       new Vector2(0f, height)))
            {
                if (table)
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Gather list");
                    ImGui.TableSetupColumn("Item");
                    ImGui.TableSetupColumn("Config preset");
                    ImGui.TableHeadersRow();

                    foreach (var (list, name, item) in rows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(list);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(MatchConfigPreset(item).Name);
                    }
                }
            }

            M3Dialog.Actions([new M3DialogAction("Close", M3ButtonStyle.Filled)]);
        }

        private static string ConcatNames(Actions.BaseAction action)
            => $"{action.Names.Miner} / {action.Names.Botanist}";
    }
}
