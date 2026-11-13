using Dalamud.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.RebornUI.Tables;
using GatherBuddy.Structs;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private const string AutomaticallyGenerated = "Automatically generated from context menu.";

    private void DrawAddAlarm(IGatherable item)
    {
        // Only timed items.
        if (item.InternalLocationId <= 0)
        {
            return;
        }

        var current = _alarmCache.Selector.EnsureCurrent();
        if (!M3Menu.Item("##addAlarm", "Add to alarm preset", FontAwesomeIcon.Bell,
                tooltip: $"Add {item.Name[GatherBuddy.Language]} to {(current == null ? "a new alarm preset." : CheckUnnamed(current.Name))}"))
        {
            return;
        }

        if (current == null)
        {
            _plugin.AlarmManager.AddGroup(new AlarmGroup()
            {
                Description = AutomaticallyGenerated,
                Enabled = true,
                Alarms = [new(item) { Enabled = true }],
            });
            _alarmCache.Selector.EnsureCurrent();
        }
        else
        {
            _plugin.AlarmManager.AddAlarm(current, new Alarm(item));
        }
    }

    private void DrawAddToGatherGroup(IGatherable item)
    {
        var current = _gatherGroupCache.Selector.EnsureCurrent();
        if (M3Menu.Item("##addGroup", "Add to gather group", FontAwesomeIcon.LayerGroup, current != null,
                tooltip: current == null
                    ? "Set up and select a gather group first."
                    : $"Add {item.Name[GatherBuddy.Language]} to {current.Name}")
         && current != null
         && GatherGroup.GatherGroupManager.ChangeGroupNode(current, current.Nodes.Count, item, null, null, null, false))
        {
            _plugin.GatherGroupManager.Save();
        }
    }

    private void DrawAddGatherWindow(IGatherable item)
    {
        var current = _gatherWindowCache.Selector.EnsureCurrent();
        if (!M3Menu.Item("##addWindow", "Add to gather window preset", FontAwesomeIcon.ListUl,
                tooltip: $"Add {item.Name[GatherBuddy.Language]} to {(current == null ? "a new gather window preset." : CheckUnnamed(current.Name))}"))
        {
            return;
        }

        if (current == null)
        {
            _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset
            {
                Enabled = true,
                Items = [item],
                Description = AutomaticallyGenerated,
            });
        }
        else
        {
            _plugin.GatherWindowManager.AddItem(current, item);
        }
    }

    private static string TeamCraftAddressEnd(string type, uint id)
    {
        var lang = GatherBuddy.Language switch
        {
            ClientLanguage.English => "en",
            ClientLanguage.German => "de",
            ClientLanguage.French => "fr",
            ClientLanguage.Japanese => "ja",
            _ => "en",
        };

        return $"db/{lang}/{type}/{id}";
    }

    private static string TeamCraftAddressEnd(FishingSpot s)
        => s.Spearfishing
            ? TeamCraftAddressEnd("spearfishing-spot", s.SpearfishingSpotData!.Value.GatheringPointBase.RowId)
            : TeamCraftAddressEnd("fishing-spot", s.Id);

    private static string GarlandToolsItemAddress(uint itemId)
        => $"https://www.garlandtools.org/db/#item/{itemId}";

    private static void DrawCreateLink(uint itemId)
    {
        if (M3Menu.Item("##link", "Link in chat", FontAwesomeIcon.Link))
        {
            Communicator.Print(SeString.CreateItemLink(itemId));
        }
    }

    private static void DrawOpenInGarlandTools(uint itemId)
    {
        if (itemId == 0 || !M3Menu.Item("##garland", "Open in GarlandTools", FontAwesomeIcon.ExternalLinkAlt))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(GarlandToolsItemAddress(itemId)) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Could not open GarlandTools:\n{e.Message}");
        }
    }

    private static void DrawOpenInTeamCraft(uint itemId)
    {
        if (itemId == 0)
        {
            return;
        }

        DrawOpenInTeamCraft(TeamCraftAddressEnd("item", itemId));
    }

    private static void DrawOpenInTeamCraft(FishingSpot fs)
    {
        if (fs.Id == 0)
        {
            return;
        }

        DrawOpenInTeamCraft(TeamCraftAddressEnd(fs));
    }

    private static void DrawOpenInTeamCraft(string addressEnd)
    {
        if (M3Menu.Item("##tcWeb", "Open in TeamCraft (browser)", FontAwesomeIcon.Globe))
        {
            OpenInTeamCraftWeb(addressEnd);
        }

        if (M3Menu.Item("##tcApp", "Open in TeamCraft (app)", FontAwesomeIcon.Desktop))
        {
            OpenInTeamCraftLocal(addressEnd);
        }
    }

    private static void OpenInTeamCraftWeb(string addressEnd)
    {
        Process.Start(new ProcessStartInfo($"https://ffxivteamcraft.com/{addressEnd}")
        {
            UseShellExecute = true,
        });
    }

    private static void OpenInTeamCraftLocal(string addressEnd)
    {
        Task.Run(() =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:14500/{addressEnd}");
                using var response = GatherBuddy.HttpClient.Send(request);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ffxiv-teamcraft")))
                    {
                        Process.Start(new ProcessStartInfo($"teamcraft:///{addressEnd}")
                        {
                            UseShellExecute = true,
                        });
                    }
                }
                catch
                {
                    GatherBuddy.Log.Error("Could not open local teamcraft.");
                }
            }
        });
    }

    public void CreateContextMenu(IGatherable item)
    {
        using var menu = M3Menu.BeginContext(item.Name[GatherBuddy.Language]);
        if (!menu.IsOpen)
        {
            return;
        }

        M3Menu.Heading(item.Name[GatherBuddy.Language]);
        DrawAddAlarm(item);
        DrawAddToGatherGroup(item);
        DrawAddGatherWindow(item);
        DrawAddToAutoGather(item);
        M3Menu.Divider();
        DrawCreateLink(item.ItemId);
        DrawOpenInGarlandTools(item.ItemId);
        DrawOpenInTeamCraft(item.ItemId);
    }

    private const string PresetName = "From Gatherables List";

    private void DrawAddToAutoGather(IGatherable item)
    {
        var current = _autoGatherListsCache.Selector.Selected;
        if (!M3Menu.Item("##addAutoGather", "Add to auto-gather list", FontAwesomeIcon.Robot,
                tooltip: $"Add {item.Name[GatherBuddy.Language]} to {(current == null ? "a new auto-gather list." : CheckUnnamed(current.Name))}"))
        {
            return;
        }

        if (current == null)
        {
            CreateAndAddPreset(item);
        }
        else
        {
            _plugin.AutoGatherListsManager.AddItem(current, item);
        }
    }

    private static bool DrawAddAllFilteredToAutoGather<T>(Table<T> table, Func<T, IGatherable> selector, string label)
    {
        var lists = new List<AutoGatherList>(_plugin.AutoGatherListsManager.Lists);
        if (lists.Count == 0)
        {
            return false;
        }

        var popupId = $"##AddAllFiltered{label}Popup";
        var lower = label.ToLowerInvariant();
        if (M3Widgets.Button($"##addAllFiltered{label}", $"Add shown {lower} to a list", M3ButtonStyle.Tonal, FontAwesomeIcon.Robot,
                enabled: table.CurrentItems > 0,
                tooltip: $"Add all {lower} the table shows to an auto-gather list.\nItems already in the list are skipped."))
        {
            M3Menu.Open(popupId);
        }

        using var menu = M3Menu.Begin(popupId);
        if (!menu.IsOpen)
        {
            return true;
        }

        M3Menu.Heading("Add to auto-gather list");
        for (var index = 0; index < lists.Count; ++index)
        {
            var list = lists[index];
            if (!M3Menu.Item($"##list{index}", CheckUnnamed(list.Name), list.Enabled ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.Circle))
            {
                continue;
            }

            var added = 0;
            foreach (var (row, _) in table.GetFilteredItems())
            {
                var item = selector(row);
                if (item.Locations.Count > 0 && list.Add(item))
                {
                    added++;
                }
            }

            _plugin.AutoGatherListsManager.Save();
            if (list.Enabled)
            {
                _plugin.AutoGatherListsManager.SetActiveItems();
            }

            GatherBuddy.Log.Information($"[GatherUI] Added {added} {lower} to auto-gather list '{list.Name}'.");
            M3Snackbar.Show(added == 1 ? $"Added 1 item to {CheckUnnamed(list.Name)}." : $"Added {added} items to {CheckUnnamed(list.Name)}.");
        }

        return true;
    }

    private static AutoGatherList CreateAndAddPreset(IGatherable item)
    {
        var preset = new AutoGatherList
        {
            Enabled = true,
            Description = AutomaticallyGenerated,
            Name = PresetName
        };
        preset.Add(item);
        _plugin.AutoGatherListsManager.AddList(preset);

        return preset;
    }

    public static void CreateGatherWindowContextMenu(IGatherable item, bool clicked)
    {
        if (clicked)
        {
            M3Menu.Open(item.Name[GatherBuddy.Language]);
        }

        using var menu = M3Menu.Begin(item.Name[GatherBuddy.Language]);
        if (!menu.IsOpen)
        {
            return;
        }

        DrawCreateLink(item.ItemId);
        DrawOpenInGarlandTools(item.ItemId);
        DrawOpenInTeamCraft(item.ItemId);
    }

    public static void CreateContextMenu(Bait bait)
    {
        if (bait.Id == 0)
        {
            return;
        }

        using var menu = M3Menu.BeginContext(bait.Name);
        if (!menu.IsOpen)
        {
            return;
        }

        DrawCreateLink(bait.Id);
        DrawOpenInGarlandTools(bait.Id);
        DrawOpenInTeamCraft(bait.Id);
    }

    public static void CreateContextMenu(FishingSpot? spot)
    {
        if (spot == null)
        {
            return;
        }

        using var menu = M3Menu.BeginContext(spot.Name);
        if (!menu.IsOpen)
        {
            return;
        }

        DrawOpenInTeamCraft(spot);
    }
}
