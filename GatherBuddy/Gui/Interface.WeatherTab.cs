using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using GatherBuddy.Config;
using GatherBuddy.RebornUI.Tables;
using GatherBuddy.Time;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private sealed class WeatherTable : Table<CachedWeather>, IDisposable
    {
        private static readonly string[] WeatherTimeStrings = new string[CachedWeather.NumWeathers];

        private static float _textHeightIconOffset;
        private static float _centerOffset;
        private static float _zoneSize;
        private static float _weatherSize;
        private static float _headerSize;
        private bool _weathersDirty = true;

        private static Column<CachedWeather>[] CreateWeatherColumns()
        {
            var columns = new Column<CachedWeather>[CachedWeather.NumWeathers + 1];
            columns[0] = new ZoneHeader();
            for (var i = 0; i < CachedWeather.NumWeathers; i++)
            {
                columns[i + 1] = new WeatherHeader(i);
            }

            return columns;
        }

        public WeatherTable()
            : base("WeatherTable", CachedWeather.CreateWeatherCache(), CreateWeatherColumns())
        {
            GatherBuddy.Time.WeatherChanged += SetDirty;
            Flags &= ~ImGuiTableFlags.NoBordersInBody;
            Flags &= ~ImGuiTableFlags.BordersInnerH;
            Sortable = false;
        }

        public void Dispose()
            => GatherBuddy.Time.WeatherChanged -= SetDirty;

        internal void SetDirty()
            => _weathersDirty = true;

        private sealed class ZoneHeader : ColumnString<CachedWeather>
        {
            public ZoneHeader()
                => Label = "Filter zones";

            public override float Width
                => _zoneSize * ImGuiHelpers.GlobalScale;

            public override string ToName(CachedWeather item)
                => item.Zone;

            public override void DrawColumn(CachedWeather item, int _)
            {
                var pos = ImGui.GetCursorPosY();
                ImGui.SetCursorPosY(pos + _textHeightIconOffset);
                ImGui.Text(item.Zone);
            }
        }

        private sealed class WeatherHeader(int idx) : Column<CachedWeather>
        {
            private readonly ColorId _headerColor = idx switch
            {
                0 => ColorId.WeatherTabHeaderLast,
                1 => ColorId.WeatherTabHeaderCurrent,
                _ => 0,
            };
            private readonly ColorId _cellColor = idx switch
            {
                0 => ColorId.WeatherTabLast,
                1 => ColorId.WeatherTabCurrent,
                _ => 0,
            };
            private readonly int _idx = idx;

            public override float Width
                => GatherBuddy.Config.ShowWeatherNames
                    ? _weatherSize * ImGuiHelpers.GlobalScale + WeatherIconSize.X + ItemSpacing.X
                    : _headerSize;

            public override bool DrawFilter()
            {
                if (_headerColor != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, _headerColor.Value());
                }

                TableFilter.HeaderLabel(WeatherTimeStrings[_idx]);
                return false;
            }

            public override void DrawColumn(CachedWeather line, int _)
            {
                if (_cellColor != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, _cellColor.Value());
                }

                var (weather, icon) = line.Weathers[_idx];
                if (GatherBuddy.Config.ShowWeatherNames)
                {
                    NamedWeather(icon, weather.Name);
                }
                else
                {
                    CenteredWeather(icon, weather, _centerOffset);
                }
            }
        }

        protected override void PreDraw()
        {
            if (_weatherSize == 0)
            {
                var zoneSize = 0f;
                foreach (var cachedWeather in Items)
                {
                    var width = ImGui.CalcTextSize(cachedWeather.Zone).X;
                    if (width > zoneSize)
                    {
                        zoneSize = width;
                    }
                }

                var weatherSize = 0f;
                foreach (var weather in GatherBuddy.GameData.Weathers.Values)
                {
                    var width = ImGui.CalcTextSize(weather.Name).X;
                    if (width > weatherSize)
                    {
                        weatherSize = width;
                    }
                }

                _zoneSize = zoneSize / ImGuiHelpers.GlobalScale;
                _weatherSize = weatherSize / ImGuiHelpers.GlobalScale;
                _headerSize = ImGui.CalcTextSize(" 88:88:88 ").X / ImGuiHelpers.GlobalScale;
            }

            _centerOffset = (_headerSize - WeatherIconSize.X - ImGui.GetStyle().ItemInnerSpacing.X / 2) / 2;
            _textHeightIconOffset = (WeatherIconSize.Y - TextHeight) / 2;

            if (!_weathersDirty)
            {
                return;
            }

            // Update times
            var sync = GatherBuddy.Time.ServerTime.SyncToEorzeaWeather();
            for (var i = 0; i < CachedWeather.NumWeathers; ++i)
            {
                var time = sync.AddEorzeaHours((i - 1) * 8).LocalTime;
                WeatherTimeStrings[i] = $" {time.TimeOfDay} ";
            }

            // Update weathers
            foreach (var item in Items)
            {
                item.Update();
            }

            _weathersDirty = false;
        }

        private static void NamedWeather(ISharedImmediateTexture icon, string name)
        {
            var cursor = ImGui.GetCursorPos();
            if (icon.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, WeatherIconSize);
            }
            else
            {
                ImGui.Dummy(WeatherIconSize);
            }

            ImGui.SetCursorPos(cursor + new Vector2(WeatherIconSize.X + ItemSpacing.X / 2, _textHeightIconOffset));
            ImGui.Text(name);
        }

        private static void CenteredWeather(ISharedImmediateTexture icon, Structs.Weather weather, float offset)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
            if (icon.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, WeatherIconSize);
            }
            else
            {
                ImGui.Dummy(WeatherIconSize);
            }

            M3Tooltip.Hovered($"{weather.Name} ({weather.Id})");
        }
    }

    private readonly WeatherTable _weatherTable = new();

    private void DrawWeatherPage()
    {
        using var id = ImRaii.PushId("Weather");
        _weatherTable.Draw(WeatherIconSize.Y + ImGui.GetStyle().ItemSpacing.Y);
    }
}
