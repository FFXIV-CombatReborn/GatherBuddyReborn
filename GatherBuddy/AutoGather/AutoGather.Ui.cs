﻿using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using OtterGui.Raii;

namespace GatherBuddy.AutoGather
{
    public static class AutoGatherUI
    {
        public class CollectableDebugUi : Window
        {
            public unsafe override bool DrawConditions()
            {
                var gatheringMasterpiece = (AddonGatheringMasterpiece*)Dalamud.GameGui.GetAddonByName("GatheringMasterpiece", 1);
                if (gatheringMasterpiece == null)
                    return false;

                return !gatheringMasterpiece->AtkUnitBase.IsVisible;
            }

            public CollectableDebugUi()
                : base("GBR Collectable Replacement",
                    ImGuiWindowFlags.Modal | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNavFocus, false)
            {
                Size          = new Vector2(100, 60);
                SizeCondition = ImGuiCond.FirstUseEver;
                IsOpen        = true;
            }

            public override void Draw()
            {
                ImGui.Text($"GBR Collectable Replacement Window");
                ImGui.Text($"Collectable Score: {GatherBuddy.AutoGather.LastCollectability}");
                ImGui.Text($"Integrity: {GatherBuddy.AutoGather.LastIntegrity}/4");
            }
        }

        private static bool _gatherDebug;

        public static void DrawAutoGatherStatus()
        {
            var enabled = GatherBuddy.AutoGather.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
            {
                GatherBuddy.AutoGather.Enabled = enabled;
            }

            ImGui.Text($"Status: {GatherBuddy.AutoGather.AutoStatus}");
            var lastNavString = GatherBuddy.AutoGather.LastNavigationResult.HasValue
                ? GatherBuddy.AutoGather.LastNavigationResult.Value
                    ? "Successful"
                    : "Failed (If you're seeing this you probably need to restart your game)"
                : "None";
            ImGui.Text($"Navigation: {lastNavString}");
            if (GatherBuddy.AutoGather.ItemsToGatherInZone.Count() > 1)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                ImGui.Text($"WARNING: There is more than 1 desired item in the zone. GBR may behave unexpectedly.");
                color.Pop();
            }
        }


        public static void DrawDebugTables()
        {
            // First column: Nearby nodes table
            if (ImGui.BeginTable("##nearbyNodesTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Targetable");
                ImGui.TableSetupColumn("Desirable");
                ImGui.TableSetupColumn("Position");
                ImGui.TableSetupColumn("Distance");
                ImGui.TableSetupColumn("Action");

                ImGui.TableHeadersRow();

                var playerPosition = Player.Object.Position;
                foreach (var node in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint)
                             .OrderBy(o => Vector3.Distance(o.Position, playerPosition)))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(node.Name.ToString());
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(node.IsTargetable ? "Y" : "N");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text("Honk (Deprecated)");
                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text(node.Position.ToString());
                    ImGui.TableSetColumnIndex(4);
                    var distance = Vector3.Distance(Player.Object.Position, node.Position);
                    ImGui.Text(distance.ToString());
                    ImGui.TableSetColumnIndex(5);

                    var territoryId = Dalamud.ClientState.TerritoryType;
                    var isBlacklisted = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.TryGetValue(territoryId, out var list)
                     && list.Contains(node.Position);

                    if (isBlacklisted)
                    {
                        if (ImGui.Button($"Unblacklist##{node.Position}"))
                        {
                            list.Remove(node.Position);
                            if (list.Count == 0)
                            {
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.Remove(territoryId);
                            }

                            GatherBuddy.Config.Save();
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Blacklist##{node.Position}"))
                        {
                            if (list == null)
                            {
                                list                                                                           = new List<Vector3>();
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId[territoryId] = list;
                            }

                            list.Add(node.Position);
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndTable();
            }
        }

        public unsafe static void DrawMountSelector()
        {
            ImGui.PushItemWidth(300);
            var ps = PlayerState.Instance();
            var preview = Dalamud.GameData.GetExcelSheet<Mount>().First(x => x.RowId == GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId)
                .Singular.ToString().ToProperCase();
            if (ImGui.BeginCombo("Select Mount", preview))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var mount in Dalamud.GameData.GetExcelSheet<Mount>().OrderBy(x => x.Singular.ToString().ToProperCase()))
                {
                    if (ps->IsMountUnlocked(mount.RowId))
                    {
                        var selected = ImGui.Selectable(mount.Singular.ToString().ToProperCase(),
                            GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == mount.RowId);

                        if (selected)
                        {
                            GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = mount.RowId;
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        /// <summary>
        /// Extension method to convert the strings to Proper Case.
        /// </summary>
        /// <param name="input">The string input.</param>
        /// <returns>The string in Proper Case.</returns>
        public static string ToProperCase(this string input)
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower());
        }

        public static unsafe void DrawCordialSelector()
        {
            // HQ items have IDs 100000 more than their NQ counterparts
            var previewItem = AutoGather.PossibleCordials.FirstOrDefault(item => new[] { item.RowId, item.RowId + 100000 }.Contains(GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId));
            // PluginLog.Information(JsonConvert.SerializeObject(previewItem.ItemAction));
            if (ImGui.BeginCombo("Select Cordial", previewItem is null
                ? ""
                : $"{(GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId > 100000 ? " " : "")}{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossibleCordials.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                    if (item.CanBeHq)
                    {
                        if (ImGui.Selectable($" {item.Name} ({AutoGather.GetInventoryItemCount(item.RowId + 100000)})", GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId == item.RowId + 100000))
                        {
                            GatherBuddy.Config.AutoGatherConfig.CordialConfig.ItemId = item.RowId + 100000;
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        public static unsafe void DrawFoodSelector()
        {
            // HQ items have IDs 100000 more than their NQ counterparts
            var previewItem = AutoGather.PossibleFoods.FirstOrDefault(item => new[] { item.RowId, item.RowId + 100000 }.Contains(GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId));
            // PluginLog.Information(JsonConvert.SerializeObject(previewItem.ItemAction));
            if (ImGui.BeginCombo("Select Food", previewItem is null
                ? ""
                : $"{(GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId > 100000 ? " " : "")}{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossibleFoods.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                    if (item.CanBeHq)
                    {
                        if (ImGui.Selectable($" {item.Name} ({AutoGather.GetInventoryItemCount(item.RowId + 100000)})", GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId == item.RowId + 100000))
                        {
                            GatherBuddy.Config.AutoGatherConfig.FoodConfig.ItemId = item.RowId + 100000;
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        public static unsafe void DrawPotionSelector()
        {
            // HQ items have IDs 100000 more than their NQ counterparts
            var previewItem = AutoGather.PossiblePotions.FirstOrDefault(item => new[] { item.RowId, item.RowId + 100000 }.Contains(GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId));
            // PluginLog.Information(JsonConvert.SerializeObject(previewItem.ItemAction));
            if (ImGui.BeginCombo("Select Potion", previewItem is null
                ? ""
                : $"{(GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId > 100000 ? " " : "")}{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossiblePotions.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                    if (item.CanBeHq)
                    {
                        if (ImGui.Selectable($" {item.Name} ({AutoGather.GetInventoryItemCount(item.RowId + 100000)})", GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId == item.RowId + 100000))
                        {
                            GatherBuddy.Config.AutoGatherConfig.PotionConfig.ItemId = item.RowId + 100000;
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        public static unsafe void DrawManualSelector()
        {
            var previewItem = AutoGather.PossibleManuals.FirstOrDefault(item => item.RowId == GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId);
            if (ImGui.BeginCombo("Select Manual", previewItem is null
                ? ""
                : $"{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossibleManuals.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.ManualConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                }

                ImGui.EndCombo();
            }
        }

        public static unsafe void DrawSquadronManualSelector()
        {
            var previewItem = AutoGather.PossibleSquadronManuals.FirstOrDefault(item => item.RowId == GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId);
            if (ImGui.BeginCombo("Select Squadron Manual", previewItem is null
                ? ""
                : $"{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossibleSquadronManuals.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.SquadronManualConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                }

                ImGui.EndCombo();
            }
        }

        public static unsafe void DrawSquadronPassSelector()
        {
            var previewItem = AutoGather.PossibleSquadronPasses.FirstOrDefault(item => item.RowId == GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId);
            if (ImGui.BeginCombo("Select Squadron Pass", previewItem is null
                ? ""
                : $"{previewItem.Name} ({AutoGather.GetInventoryItemCount(GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId)})"))
            {
                if (ImGui.Selectable("", GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var item in AutoGather.PossibleSquadronPasses.OrderBy(item => item.Name.ToString()))
                {
                    if (ImGui.Selectable($"{item.Name} ({AutoGather.GetInventoryItemCount(item.RowId)})", GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId == item.RowId))
                    {
                        GatherBuddy.Config.AutoGatherConfig.SquadronPassConfig.ItemId = item.RowId;
                        GatherBuddy.Config.Save();
                    }
                }

                ImGui.EndCombo();
            }
        }
    }
}
