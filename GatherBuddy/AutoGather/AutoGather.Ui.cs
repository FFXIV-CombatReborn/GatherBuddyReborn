using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using GatherBuddy.CustomInfo;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.AutoGather
{
    public static class AutoGatherUI
    {
        public static void DrawDebugTables()
        {
            if (ImGui.Button("Import Node Offsets from Clipboard"))
            {
                var settings = new JsonSerializerSettings();
                var text = RebornUI.Ui.ReadClipboard();
                var vectors = JsonConvert.DeserializeObject<List<OffsetPair>>(text, settings) ?? [];
                foreach (var offset in vectors)
                {
                    WorldData.NodeOffsets[offset.Original] = offset.Offset;
                    GatherBuddy.Log.Information($"Added offset {offset} to dictionary");
                }
                WorldData.SaveOffsetsToFile();
                GatherBuddy.Log.Information("Import complete");
            }
            ImGui.SameLine();
            if (ImGui.Button("Export Node Offsets to Clipboard"))
            {
                var settings = new JsonSerializerSettings();
                var offsets = new List<OffsetPair>(WorldData.NodeOffsets.Count);
                foreach (var (original, offset) in WorldData.NodeOffsets)
                {
                    offsets.Add(new OffsetPair(original, offset));
                }

                var offsetString = JsonConvert.SerializeObject(offsets, Formatting.Indented, settings);
                ImGui.SetClipboardText(offsetString);
                GatherBuddy.Log.Information("Node offsets exported to clipboard");
            }
            // First column: Nearby nodes table
            if (ImGui.BeginTable("##nearbyNodesTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Targetable");
                ImGui.TableSetupColumn("NodeId");
                ImGui.TableSetupColumn("Position");
                ImGui.TableSetupColumn("Distance");
                ImGui.TableSetupColumn("Auto Offsets");
                ImGui.TableSetupColumn("Action");

                ImGui.TableHeadersRow();

                var playerPosition = Player.Object?.Position ?? Vector3.Zero;
                var nearbyNodes = new List<(IGameObject Node, float Distance, int Index)>();
                foreach (var obj in Dalamud.Objects)
                {
                    if (obj.ObjectKind == ObjectKind.GatheringPoint)
                    {
                        nearbyNodes.Add((obj, Vector3.Distance(obj.Position, playerPosition), nearbyNodes.Count));
                    }
                }

                nearbyNodes.Sort(static (lhs, rhs) =>
                {
                    var order = lhs.Distance.CompareTo(rhs.Distance);
                    return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
                });
                foreach (var (node, _, _) in nearbyNodes)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(node.Name.ToString());
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(node.IsTargetable ? "Y" : "N");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(node.BaseId.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text(node.Position.ToString());
                    ImGui.TableSetColumnIndex(4);
                    var distance = Vector3.Distance(playerPosition, node.Position);
                    ImGui.Text(distance.ToString());
                    ImGui.TableSetColumnIndex(5);
                    var autoOffsetCount = AutoOffsets.GetOffsetCount(node.BaseId, node.Position);
                    ImGui.Text(autoOffsetCount.ToString());
                    ImGui.TableSetColumnIndex(6);

                    var territoryId = Dalamud.ClientState.TerritoryType;
                    var isBlacklisted = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.TryGetValue(territoryId, out var list)
                     && list.Contains(node.Position);

                    if (isBlacklisted && list != null)
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
                                list = [];
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId[territoryId] = list;
                            }

                            list.Add(node.Position);
                            GatherBuddy.Config.Save();
                        }
                    }

                    if (ImGui.Button($"Navigate##{node.Position}"))
                    {
                        if (GatherBuddy.AutoGather.Enabled)
                        {
                            Communicator.PrintError("[GatherBuddyReborn] Auto-Gather is enabled! Unable to navigate.");
                            return;
                        }
                        //VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                        VNavmesh.Path.Stop();
                        VNavmesh.SimpleMove.PathfindAndMoveTo(node.Position, GatherBuddy.AutoGather.ShouldFly(node.Position));
                    }

                    if (WorldData.NodeOffsets.TryGetValue(node.Position, out var offset))
                    {
                        if (ImGui.Button($"Remove Offset##{node.Position}"))
                        {
                            WorldData.NodeOffsets.Remove(node.Position);
                            WorldData.SaveOffsetsToFile();
                        }
                        ImGui.Text(offset.ToString());
                        if (ImGui.Button($"Navigate to Offset##{node.Position}"))
                        {
                            if (GatherBuddy.AutoGather.Enabled)
                            {
                                Communicator.PrintError("[GatherBuddyReborn] Auto-Gather is enabled! Unable to navigate.");
                                return;
                            }
                            //VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                            VNavmesh.Path.Stop();
                            VNavmesh.SimpleMove.PathfindAndMoveTo(offset, GatherBuddy.AutoGather.ShouldFly(offset));
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Add Offset##{node.Position}"))
                        {
                            WorldData.AddOffset(node.Position, playerPosition);
                        }
                        ImGui.Text(playerPosition.ToString());
                    }

                }

                ImGui.EndTable();
            }
        }

        /// <summary>
        /// Extension method to convert the string to Proper Case.
        /// </summary>
        /// <param name="input">The string input.</param>
        /// <returns>The string in Proper Case.</returns>
        public static string ToProperCase(this string input)
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower());
        }
    }
}

