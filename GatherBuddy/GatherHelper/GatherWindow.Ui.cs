using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Gui;
using GatherBuddy.Interfaces;
using GatherBuddy.Time;
using ElliLib;
using Functions = GatherBuddy.Plugin.Functions;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.GatherHelper;

public class GatherWindow : Window
{
    private readonly GatherBuddy _plugin;

    private int              _deleteSet        = -1;
    private int              _deleteItemIdx    = -1;
    private bool             _deleteAutoGather;
    private AutoGatherList?  _deleteListObj    = null;
    private TimeStamp _earliestKeyboardToggle = TimeStamp.Epoch;
    private Vector2   _lastSize               = Vector2.Zero;
    private Vector2   _newPosition            = Vector2.Zero;

    public GatherWindow(GatherBuddy plugin)
        : base("##GatherHelperReborn",
            ImGuiWindowFlags.AlwaysAutoResize
          | ImGuiWindowFlags.NoTitleBar
          | ImGuiWindowFlags.NoFocusOnAppearing
          | ImGuiWindowFlags.NoNavFocus
          | ImGuiWindowFlags.NoScrollbar)
    {
        _plugin            = plugin;
        IsOpen             = GatherBuddy.Config.ShowGatherWindow;
        RespectCloseHotkey = false;
        Namespace          = "GatherHelperReborn";
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Vector2.Zero,
            MaximumSize = Vector2.One * 10000,
        };
    }

    private static void DrawTime(ILocation? loc, TimeInterval time, bool uptimeDependency)
    {
        if (!GatherBuddy.Config.ShowGatherWindowTimers || !ImGui.TableNextColumn())
            return;
        if (time.Equals(TimeInterval.Always))
            return;

        var active = time.Start <= GatherBuddy.Time.ServerTime;
        var colorId = (active, uptimeDependency) switch
        {
            (true, true)   => ColorId.DependentAvailableFish.Value(),
            (true, false)  => ColorId.AvailableItem.Value(),
            (false, true)  => ColorId.DependentUpcomingFish.Value(),
            (false, false) => ColorId.UpcomingItem.Value(),
        };

        using var color = ImRaii.PushColor(ImGuiCol.Text, colorId);
        ImGui.Text(TimeInterval.DurationString(active ? time.End : time.Start, GatherBuddy.Time.ServerTime, false));
        color.Pop();

        CreateTooltip(null, loc, time);
    }

    private static string TooltipText(ILocation? loc, TimeInterval time)
    {
        var sb = new StringBuilder();
        sb.Append(loc == null
            ? "Unknown Location\nUnknown Territory\nUnknown Aetheryte\n"
            : $"{loc.Name}\n{loc.Territory.Name}\n{loc.ClosestAetheryte?.Name ?? "No Aetheryte"}\n");

        sb.Append(time.Equals(TimeInterval.Always)
            ? "Always Up"
            : $"{time.Start}\n{time.End}\n{time.DurationString()}\n{TimeInterval.DurationString(time.Start > GatherBuddy.Time.ServerTime ? time.Start : time.End, GatherBuddy.Time.ServerTime, false)}");

        return sb.ToString();
    }

    private static void CreateTooltip(IGatherable? item, ILocation? loc, TimeInterval time)
    {
        if (!ImGui.IsItemHovered())
            return;

        if (item is not Fish fish)
        {
            ImGui.SetTooltip(TooltipText(loc, time));
            return;
        }

        var extendedFish = Interface.ExtendedFishList.FirstOrDefault(f => f.Data == fish);
        if (extendedFish == null)
        {
            ImGui.SetTooltip(TooltipText(loc, time));
            return;
        }

        using var tt = ImRaii.Tooltip();

        ImGui.Text(TooltipText(loc, time));
        ImGui.NewLine();
        extendedFish.SetTooltip(loc?.Territory ?? Territory.Invalid, ImGuiHelpers.ScaledVector2(40, 40), ImGuiHelpers.ScaledVector2(20, 20),
            ImGuiHelpers.ScaledVector2(30,                                                          30),
            false);
    }

    private readonly List<(IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity)> _data = new();

    private static bool HasPredatorTimerIssue(IGatherable item)
    {
        if (item is not Fish fish || fish.Predators.Length == 0)
            return false;

        foreach (var (predatorFish, _) in fish.Predators)
        {
            if (CheckRestrictions(predatorFish, fish))
                return true;
        }

        return false;

        static bool CheckRestrictions(Fish predator, Fish target)
        {
            if (predator.FishRestrictions.HasFlag(FishRestrictions.Time))
            {
                if (!target.FishRestrictions.HasFlag(FishRestrictions.Time))
                    return true;
                if (!predator.Interval.Contains(target.Interval))
                    return true;
            }

            if (predator.FishRestrictions.HasFlag(FishRestrictions.Weather))
            {
                if (!target.FishRestrictions.HasFlag(FishRestrictions.Weather))
                    return true;

                if (predator.CurrentWeather.Length > 0 && target.CurrentWeather.Any(w => !predator.CurrentWeather.Contains(w)))
                    return true;

                if (predator.PreviousWeather.Length > 0 && target.PreviousWeather.Any(w => !predator.PreviousWeather.Contains(w)))
                    return true;
            }

            return false;
        }
    }

    private void DrawItem(IGatherable item, ILocation loc, TimeInterval time, uint quantity)
    {
        if (GatherBuddy.Config.ShowGatherWindowOnlyAvailable && time.Start > GatherBuddy.Time.ServerTime)
            return;

        var inventoryCount = item.GetTotalCount();

        if (quantity > 0 && inventoryCount >= quantity && GatherBuddy.Config.HideGatherWindowCompletedItems)
            return;

        var hasPredatorIssue = HasPredatorTimerIssue(item);
        var locationVisited = IsVisitedLocation(loc);

        if (ImGui.TableNextColumn())
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing / 2);
            if (Icons.DefaultStorage.TryLoadIcon(item.ItemData.Icon, out var icon))
                ImGuiUtil.HoverIcon(icon.Handle, icon.Size, new Vector2(ImGui.GetTextLineHeight()));
            else
                ImGui.Dummy(new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            
            var colorId = time == TimeInterval.Always    ? ColorId.GatherWindowText :
                time.Start > GatherBuddy.Time.ServerTime ? ColorId.GatherWindowUpcoming : ColorId.GatherWindowAvailable;

            if (quantity > 0 && inventoryCount >= quantity || locationVisited)
                colorId = ColorId.DisabledText;
            using var color                         = ImRaii.PushColor(ImGuiCol.Text, colorId.Value());
            var quantityText = quantity > 0 ? $" ({inventoryCount}/{quantity})" : "";
            var locationText = item.Locations.Count > 1 && quantity > 0 ? $" - {loc.Name}" : string.Empty;
            if (ImGui.Selectable($"{item.Name[GatherBuddy.Language]}{locationText}{quantityText}##{item.ItemId}-{loc.Type}-{loc.Id}", false))
            {
                _plugin.Executor.GatherLocation(loc);
            }

            var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            color.Pop();
            CreateTooltip(item, loc, time);

            if (clicked && Dalamud.Keys[VirtualKey.MENU])
            {
                if (quantity > 0)
                    foreach (var list in _plugin.AutoGatherListsManager.Lists)
                    {
                        if (!list.Enabled)
                            continue;

                        var idx = list.Entries.ToList().FindIndex(entry => entry.Item == item
                            && (entry.PreferredLocation == loc || entry.PreferredLocation == null));
                        if (idx < 0)
                            continue;

                        _plugin.AutoGatherListsManager.ChangeEnabled(list, idx, false);
                        break;
                    }
            }
            else if (clicked && Functions.CheckModifier(GatherBuddy.Config.GatherWindowDeleteModifier, false))
                if (quantity > 0)
                    foreach (var list in _plugin.AutoGatherListsManager.Lists)
                    {
                        if (!list.Enabled)
                            continue;

                        var idx = list.Entries.ToList().FindIndex(entry => entry.Item == item
                            && (entry.PreferredLocation == loc || entry.PreferredLocation == null));
                        if (idx < 0)
                            continue;

                        _deleteListObj = list;
                        _deleteItemIdx = idx;
                        _deleteAutoGather = true;
                        break;
                    }
                else
                    for (var i = 0; i < _plugin.GatherWindowManager.Presets.Count; ++i)
                    {
                        var preset = _plugin.GatherWindowManager.Presets[i];
                        if (!preset.Enabled)
                            continue;

                        var idx = preset.Items.IndexOf(item);
                        if (idx < 0)
                            continue;

                        _deleteSet = i;
                        _deleteItemIdx = idx;
                        _deleteAutoGather = false;
                        break;
                    }
            else
                Interface.CreateGatherWindowContextMenu(item, clicked);
        }

        DrawTime(loc, time, hasPredatorIssue);
    }

    private static bool IsVisitedLocation(ILocation loc)
    {
        if (loc is GatheringNode node && GatherBuddy.AutoGather.DebugVisitedTimedLocations.ContainsKey(node))
            return true;

        return loc.WorldPositions.Keys.Any(k => GatherBuddy.AutoGather.VisitedNodes.Contains(k));
    }

    private void DeleteItem()
    {
        if (_deleteItemIdx < 0)
            return;

        if (_deleteAutoGather && _deleteListObj != null)
        {
            _plugin.AutoGatherListsManager.RemoveItem(_deleteListObj, _deleteItemIdx);
            _deleteListObj = null;
        }
        else if (!_deleteAutoGather && _deleteSet >= 0)
        {
            var preset = _plugin.GatherWindowManager.Presets[_deleteSet];
            _plugin.GatherWindowManager.RemoveItem(preset, _deleteItemIdx);
            _deleteSet = -1;
        }
        _deleteItemIdx = -1;
    }

    private void CheckHotkeys()
    {
        if (_earliestKeyboardToggle > GatherBuddy.Time.ServerTime || !Functions.CheckKeyState(GatherBuddy.Config.GatherWindowHotkey, false))
            return;

        _earliestKeyboardToggle             = GatherBuddy.Time.ServerTime.AddMilliseconds(500);
        GatherBuddy.Config.ShowGatherWindow = !GatherBuddy.Config.ShowGatherWindow;
        GatherBuddy.Config.Save();
    }

    private static bool CheckHoldKey()
    {
        if (!GatherBuddy.Config.OnlyShowGatherWindowHoldingKey || GatherBuddy.Config.GatherWindowHoldKey == VirtualKey.NO_KEY)
            return false;

        return !Dalamud.Keys[GatherBuddy.Config.GatherWindowHoldKey];
    }

    private static bool CheckDuty()
        => GatherBuddy.Config.HideGatherWindowInDuty && Functions.BoundByDuty();

    private bool CheckAvailable()
    {
        _data.Clear();

        IEnumerable<(IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity)> list = _plugin.AutoGatherListsManager.ActiveItems
            .SelectMany(ToGatherWindowRows)
            .Concat(_plugin.GatherWindowManager.ActiveItems.Select(i =>
            {
                var (loc, time) = GatherBuddy.UptimeManager.BestLocation(i);
                return (Item: i, Location: loc, Uptime: time, Quantity: 0u);
            }))
            .GroupBy(x => (x.Item, x.Location))
            .Select(g => (g.Key.Item, g.Key.Location, g.First().Uptime, (uint)g.Sum(x => x.Quantity)));

        if (GatherBuddy.Config.SortGatherWindowByUptime)
            list = list.OrderBy(i => i.Uptime, Comparer<TimeInterval>.Create((x, y) => x.Compare(y)));

        _data.AddRange(list);

        return _data.Count == 0 || GatherBuddy.Config.ShowGatherWindowOnlyAvailable && _data.All(f => f.Uptime.Start > GatherBuddy.Time.ServerTime);
    }

    private static IEnumerable<(IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity)> ToGatherWindowRows(
        (IGatherable Item, uint Quantity, ILocation? PreferredLocation) entry)
    {
        if (entry.PreferredLocation != null)
        {
            yield return (entry.Item, entry.PreferredLocation, GetLocationUptime(entry.Item, entry.PreferredLocation), entry.Quantity);
            yield break;
        }

        if (GatherBuddy.Config.AutoGatherConfig.SplitGatherableForAllLocationsIfNoPreferenceSelected
         && entry.Item.Locations.Count > 1)
        {
            foreach (var location in entry.Item.Locations)
                yield return (entry.Item, location, GetLocationUptime(entry.Item, location), entry.Quantity);
            yield break;
        }

        var (loc, time) = GatherBuddy.UptimeManager.BestLocation(entry.Item);
        yield return (entry.Item, loc, time, entry.Quantity);
    }

    private static TimeInterval GetLocationUptime(IGatherable item, ILocation location)
        => location switch
        {
            GatheringNode node => node.Times.NextUptime(GatherBuddy.Time.ServerTime),
            FishingSpot spot when item is Fish fish => GatherBuddy.UptimeManager.NextUptime(fish, spot.Territory, GatherBuddy.Time.ServerTime),
            _ => TimeInterval.Always,
        };

    public override void PreOpenCheck()
    {
        CheckHotkeys();
        IsOpen = GatherBuddy.Config.ShowGatherWindow;
    }

    public override bool DrawConditions()
        => !(CheckHoldKey() || CheckDuty() || CheckAvailable()) && Dalamud.ClientState.IsLoggedIn;

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ColorId.GatherWindowBackground.Value());
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.One * 2 * ImGuiHelpers.GlobalScale);
        if (GatherBuddy.Config.LockGatherWindow)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~
                ImGuiWindowFlags.NoMove;

        if (_newPosition.Y != 0)
        {
            ImGui.SetNextWindowPos(_newPosition);
            _newPosition = Vector2.Zero;
        }
    }

    public override void PostDraw()
    {
        DeleteItem();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void CheckAnchorPosition()
    {
        if (!GatherBuddy.Config.GatherWindowBottomAnchor)
            return;

        // Can not use Y size since a single text row is smaller than the minimal window size
        // for some reason. 50 is arbitrary. Default window size was 32,32 for me.
        if (_lastSize.X < 50 * ImGuiHelpers.GlobalScale)
            _lastSize = ImGui.GetWindowSize();

        var size = ImGui.GetWindowSize();
        if (_lastSize == size)
            return;

        _newPosition   =  ImGui.GetWindowPos();
        _newPosition.Y += _lastSize.Y - size.Y;
        _lastSize      =  size;
    }

    public override void Draw()
    {
        var       colorId = GatherBuddy.AutoGather.Enabled ? ColorId.GatherWindowAvailable.Value() : ColorId.GatherWindowText.Value();
        using var color = ImRaii.PushColor(ImGuiCol.Text, colorId);
        if (ImGui.Selectable($"Auto-Gather: {GatherBuddy.AutoGather.AutoStatus}###toggle-button"))
        {
            GatherBuddy.AutoGather.Enabled = !GatherBuddy.AutoGather.Enabled;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _plugin.Interface.Toggle();
        }
        color.Pop();
        ImGuiUtil.HoverTooltip("Click to enable/disable auto-gather. Right click to toggle interface");
        using var table = ImRaii.Table("##table", GatherBuddy.Config.ShowGatherWindowTimers ? 2 : 1);
        if (!table)
            return;
        
        foreach (var (item, loc, time, quantity) in _data)
            DrawItem(item, loc, time, quantity);

        CheckAnchorPosition();
    }
}
