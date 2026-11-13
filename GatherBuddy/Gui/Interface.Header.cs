using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private class HeaderCache : IDisposable
    {
        public readonly Vector4 LastWeatherTint = new(1f, 0.5f, 0.5f, 1f);

        private uint _currentTerritory;
        public Structs.Weather LastWeather = Structs.Weather.Invalid;
        public Structs.Weather CurrentWeather = Structs.Weather.Invalid;
        public Structs.Weather NextWeather = Structs.Weather.Invalid;
        public ISharedImmediateTexture? LastWeatherIcon;
        public ISharedImmediateTexture? CurrentWeatherIcon;
        public ISharedImmediateTexture? NextWeatherIcon;

        private void NullWeather()
        {
            LastWeatherIcon = null;
            CurrentWeatherIcon = null;
            NextWeatherIcon = null;
            LastWeather = Structs.Weather.Invalid;
            CurrentWeather = Structs.Weather.Invalid;
            NextWeather = Structs.Weather.Invalid;
        }

        private void UpdateWeather()
        {
            if (_currentTerritory == 0)
            {
                NullWeather();
            }
            else
            {
                (LastWeather, CurrentWeather, NextWeather) = GatherBuddy.WeatherManager.FindLastCurrentNextWeather(_currentTerritory);
                if (LastWeather.Id != 0)
                {
                    LastWeatherIcon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(LastWeather.Icon);
                    CurrentWeatherIcon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(CurrentWeather.Icon);
                    NextWeatherIcon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(NextWeather.Icon);
                }
                else
                {
                    NullWeather();
                }
            }
        }

        public HeaderCache()
            => GatherBuddy.Time.WeatherChanged += UpdateWeather;

        public void Dispose()
            => GatherBuddy.Time.WeatherChanged -= UpdateWeather;

        public void UpdateCurrentTerritory()
        {
            if (_currentTerritory == Dalamud.ClientState.TerritoryType)
            {
                return;
            }

            _currentTerritory = Dalamud.ClientState.TerritoryType;
            UpdateWeather();
        }
    }

    private readonly HeaderCache _headerCache = new();

    protected override void DrawPageHeader()
    {
        var page = CurrentPage;
        if (Searching || page == null || page.Section is SectionSettings or SectionTools)
        {
            return;
        }

        _headerCache.UpdateCurrentTerritory();

        var flow = new M3Flow(M3.Space2);
        DrawEorzeaTime(ref flow);
        DrawWeather(ref flow);
        DrawAlarmControls(ref flow);
        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private static Vector4 HeaderColor(ColorId color, Vector4 schemeColor)
    {
        var value = color.Value();
        return value == color.Data().DefaultColor ? schemeColor : ImGui.ColorConvertU32ToFloat4(value) with { W = 1f };
    }

    private static void DrawEorzeaTime(ref M3Flow flow)
    {
        var now = GatherBuddy.Time.ServerTime;
        var nextHourS = (now.SyncToEorzeaHour().AddEorzeaHours(1) - now) / RealTime.MillisecondsPerSecond;
        var nextHourM = nextHourS / RealTime.SecondsPerMinute;
        nextHourS -= nextHourM * RealTime.SecondsPerMinute;

        var time = $"ET {GatherBuddy.Time.EorzeaHourOfDay:D2}:{GatherBuddy.Time.EorzeaMinuteOfHour:D2}";
        flow.Next(M3Widgets.PillSize(time, FontAwesomeIcon.Clock).X);
        M3Widgets.Pill("##gbr_eorzea_time", time, HeaderColor(ColorId.HeaderEorzeaTime, M3.Scheme.Primary), FontAwesomeIcon.Clock);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            M3Tooltip.Show(OceanRoutesTooltip());
        }

        var nextHour = $"{nextHourM:D2}:{nextHourS:D2} to next hour";
        flow.Next(M3Widgets.PillSize(nextHour, FontAwesomeIcon.HourglassHalf).X);
        M3Widgets.Pill("##gbr_next_hour", nextHour, HeaderColor(ColorId.HeaderNextHour, M3.Scheme.Secondary), FontAwesomeIcon.HourglassHalf,
            "Real time until the next Eorzea hour.");
    }

    private static string OceanRoutesTooltip()
    {
        var now = TimeStamp.UtcNow;
        return "If this doesn't match your in-game Eorzea time, check that your Windows clock is accurate.\n \n"
          + "Next Aldenard ocean routes:\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Aldenard, now)}\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Aldenard, now.AddHours(2))}\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Aldenard, now.AddHours(4))}\n"
          + "Next Othard ocean routes:\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Othard, now)}\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Othard, now.AddHours(2))}\n"
          + $"• {OceanUptime.NextOceanRoute(OceanArea.Othard, now.AddHours(4))}";
    }

    private void DrawWeather(ref M3Flow flow)
    {
        var now = GatherBuddy.Time.ServerTime;
        var nextWeatherS = (now.SyncToEorzeaWeather().AddEorzeaHours(8) - now) / RealTime.MillisecondsPerSecond;
        var nextWeatherM = nextWeatherS / RealTime.SecondsPerMinute;
        nextWeatherS -= nextWeatherM * RealTime.SecondsPerMinute;

        var label = $"{nextWeatherM:D2}:{nextWeatherS:D2} to next weather";
        var pill = M3Widgets.PillSize(label, FontAwesomeIcon.CloudSun);
        var icon = new Vector2(pill.Y);
        var gap = 2f * M3.Scale;
        var hasIcons = _headerCache.CurrentWeather.Id != 0;
        var width = pill.X + (hasIcons ? (icon.X + gap) * 3f : 0f);

        flow.Next(width);
        using var group = ImRaii.Group();
        if (hasIcons)
        {
            DrawWeatherIcon(_headerCache.LastWeather, _headerCache.LastWeatherIcon, icon, _headerCache.LastWeatherTint, "Last weather");
            ImGui.SameLine(0f, gap);
            DrawWeatherIcon(_headerCache.CurrentWeather, _headerCache.CurrentWeatherIcon, icon, Vector4.One, "Current weather");
            ImGui.SameLine(0f, gap);
        }

        M3Widgets.Pill("##gbr_next_weather", label, HeaderColor(ColorId.HeaderWeather, M3.Scheme.Tertiary), FontAwesomeIcon.CloudSun,
            "Real time until the weather changes.");
        if (hasIcons)
        {
            ImGui.SameLine(0f, gap);
            DrawWeatherIcon(_headerCache.NextWeather, _headerCache.NextWeatherIcon, icon, Vector4.One, "Next weather");
        }
    }

    private static void DrawWeatherIcon(Structs.Weather weather, ISharedImmediateTexture? icon, Vector2 size, Vector4 tint, string when)
    {
        if (icon != null && icon.TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, size, Vector2.Zero, Vector2.One, tint);
            M3Tooltip.Hovered($"{when}: {weather.Name} ({weather.Id})");
        }
        else
        {
            ImGui.Dummy(size);
        }
    }

    private void DrawAlarmControls(ref M3Flow flow)
    {
        var s = M3.Scheme;
        var enabled = GatherBuddy.Config.AlarmsEnabled;
        var alarmsLabel = enabled ? "Alarms on" : "Alarms off";
        var alarmsIcon = enabled ? FontAwesomeIcon.Bell : FontAwesomeIcon.BellSlash;
        flow.Next(M3Widgets.PillSize(alarmsLabel, alarmsIcon).X);
        if (M3Widgets.Pill("##gbr_alarms_toggle", alarmsLabel, enabled ? s.Primary : s.Outline, alarmsIcon,
                enabled ? "Alarms are on. Click to turn them all off." : "Alarms are off. Click to turn them all on.", true))
        {
            if (enabled)
            {
                _plugin.AlarmManager.Disable();
            }
            else
            {
                _plugin.AlarmManager.Enable();
            }
        }

        DrawLastAlarm(ref flow, true);
        DrawLastAlarm(ref flow, false);
    }

    private static void DrawLastAlarm(ref M3Flow flow, bool item)
    {
        var s = M3.Scheme;
        var alarmData = item ? _plugin.AlarmManager.LastItemAlarm : _plugin.AlarmManager.LastFishAlarm;
        var icon = item ? FontAwesomeIcon.Seedling : FontAwesomeIcon.Fish;
        var id = item ? "##gbr_last_item_alarm" : "##gbr_last_fish_alarm";

        if (alarmData == null)
        {
            var empty = item ? "No item alarm yet" : "No fish alarm yet";
            flow.Next(M3Widgets.PillSize(empty, icon).X);
            M3Widgets.Pill(id, empty, s.Outline, icon,
                item ? "The last item alarm that went off shows here. Click it to /gather that item."
                     : "The last fish alarm that went off shows here. Click it to /gather that fish.");
            return;
        }

        var (alarm, loc, time) = alarmData.Value;
        var name = alarm.Name.Length != 0 ? alarm.Name : alarm.Item.Name[GatherBuddy.Language];
        var label = M3Navigation.Truncate(name, 200f * M3.Scale);
        var tooltip = $"Click to /gather this alarm.\n{name}\n{loc.Name} - {loc.ClosestAetheryte?.Name ?? "None"}\n{time.Start.LocalTime}\n{time.End.LocalTime}";

        flow.Next(M3Widgets.PillSize(label, icon).X);
        if (!M3Widgets.Pill(id, label, item ? s.Secondary : s.Tertiary, icon, tooltip, true))
        {
            return;
        }

        if (item)
        {
            _plugin.Executor.GatherItemByName("alarm");
        }
        else
        {
            _plugin.Executor.GatherFishByName("alarm");
        }
    }
}
