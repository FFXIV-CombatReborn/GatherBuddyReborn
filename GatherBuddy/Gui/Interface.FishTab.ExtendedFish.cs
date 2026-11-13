using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.RebornUI;
using GatherBuddy.Time;
using GatherBuddy.Utility;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    public class ExtendedFish
    {
        public struct BaitOrder
        {
            public ISharedImmediateTexture Icon;
            public string Name;
            public object? Fish;
            public ISharedImmediateTexture? HookSet;
            public (string, uint) Bite;
        }

        public struct Predator
        {
            public Fish Fish;
            public ISharedImmediateTexture Icon;
            public string Name;
            public string Amount;
        }

        public static class Bites
        {
            public static readonly (string, uint) Weak = ("  !  ", 0xFFFF6020);
            public static readonly (string, uint) Strong = (" ! ! ", 0xFF60D030);
            public static readonly (string, uint) Legendary = (" !!! ", 0xFF0040C0);
            public static readonly (string, uint) Unknown = (" ? ? ", 0xFF404040);

            public static (string, uint) FromBiteType(BiteType bite)
                => bite switch
                {
                    BiteType.Weak => Weak,
                    BiteType.Strong => Strong,
                    BiteType.Legendary => Legendary,
                    _ => Unknown,
                };
        }

        public Fish Data;
        public ISharedImmediateTexture Icon = null!;
        public string Territories = string.Empty;
        public string SpotNames = string.Empty;
        public string Aetherytes = string.Empty;

        public string Time = string.Empty;
        public ISharedImmediateTexture[] WeatherIcons = [];
        public ISharedImmediateTexture[] TransitionIcons = [];
        public BaitOrder[] Bait = [];
        public ISharedImmediateTexture? Snagging;
        public ISharedImmediateTexture? Lure;
        public Predator[] Predators = [];
        public string Patch = string.Empty;
        public string UptimeString = string.Empty;
        public string Intuition = string.Empty;
        public string FishType = string.Empty;
        public bool UptimeDependency;
        public ushort UptimePercent;
        public bool Unlocked = false;

        public bool Collectible
            => Data.ItemData.IsCollectable;

        public byte MultiHookLower
            => Data.MultiHookLower;

        public OceanSpecies OceanSpecies
            => Data.OceanSpecies;

        public byte MutliHookUpper
            => Data.MultiHookUpper;

        public short Points
            => Data.Points;

        public (ILocation, TimeInterval) Uptime
            => GatherBuddy.UptimeManager.BestLocation(Data);

        private static ushort SetUptime(Fish fish)
        {
            var uptime = 10000L;
            if (!fish.Interval.AlwaysUp())
            {
                uptime = uptime * fish.Interval.OnTime / EorzeaTimeStampExtensions.MillisecondsPerEorzeaHour / RealTime.HoursPerDay;
            }

            ushort bestUptime = 0;
            foreach (var spot in fish.FishingSpots)
            {
                var tmp = uptime
                  * spot.Territory.WeatherRates.ChanceForWeather(fish.PreviousWeather)
                  * spot.Territory.WeatherRates.ChanceForWeather(fish.CurrentWeather)
                  / 10000;
                if (tmp > bestUptime)
                {
                    bestUptime = (ushort)tmp;
                }
            }

            return bestUptime;
        }

        private static ISharedImmediateTexture[] SetWeather(Fish fish)
        {
            if (!fish.FishRestrictions.HasFlag(FishRestrictions.Weather) || fish.CurrentWeather.Length == 0)
            {
                return [];
            }

            return Array.ConvertAll(fish.CurrentWeather, w
                => Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)w.Icon)));
        }

        private static ISharedImmediateTexture[] SetTransition(Fish fish)
        {
            if (!fish.FishRestrictions.HasFlag(FishRestrictions.Weather) || fish.PreviousWeather.Length == 0)
            {
                return [];
            }

            return Array.ConvertAll(fish.PreviousWeather, w
                => Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)w.Icon)));
        }

        private static Predator[] SetPredators(Fish fish)
        {
            if (fish.Predators.Length == 0)
            {
                return [];
            }

            return Array.ConvertAll(fish.Predators, p => new Predator
            {
                Fish = p.Item1,
                Amount = p.Item2.ToString(),
                Name = p.Item1.Name[GatherBuddy.Language],
                Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(p.Item1.ItemData.Icon)),
            });
        }

        private static BaitOrder[] SetBait(Fish fish)
        {
            if (fish.IsSpearFish)
            {
                return
                [
                    new BaitOrder
                    {
                        Name    = string.Intern($"{fish.Size.ToName()} and {fish.Speed.ToName()}"),
                        Fish    = null,
                        Icon    = Icons.FromSize(fish.Size),
                        Bite    = Bites.Unknown,
                        HookSet = null,
                    },
                ];
            }

            var ret = new BaitOrder[fish.Mooches.Length + 1];
            var bait = fish.InitialBait;
            ret[0] = new BaitOrder
            {
                Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(bait.Icon)),
                Name = bait.Name,
                Fish = bait,
            };
            for (var idx = 0; idx < fish.Mooches.Length; ++idx)
            {
                var f = fish.Mooches[idx];
                ret[idx].HookSet = Icons.FromHookSet(f.HookSet);
                ret[idx].Bite = Bites.FromBiteType(f.BiteType);
                ret[idx + 1] = new BaitOrder
                {
                    Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(f.ItemData.Icon)),
                    Name = f.Name[GatherBuddy.Language],
                    Fish = f,
                };
            }

            ret[^1].HookSet = Icons.FromHookSet(fish.HookSet);
            ret[^1].Bite = Bites.FromBiteType(fish.BiteType);
            return ret;
        }

        private static ISharedImmediateTexture? SetSnagging(Fish fish, IEnumerable<BaitOrder> baitOrder)
        {
            if (fish.Snagging == Enums.Snagging.Required)
            {
                return Icons.Snagging;
            }

            foreach (var bait in baitOrder)
            {
                if (bait.Fish is Fish { Snagging: Enums.Snagging.Required })
                {
                    return Icons.Snagging;
                }
            }

            return null;
        }

        private static ISharedImmediateTexture? SetLure(Fish fish, IEnumerable<BaitOrder> baitOrder)
        {
            var lure = fish.Lure;
            if (lure is Enums.Lure.None)
            {
                foreach (var bait in baitOrder)
                {
                    if (bait.Fish is Fish { Lure: not Enums.Lure.None } lureFish)
                    {
                        lure = lureFish.Lure;
                        break;
                    }
                }
            }

            return lure switch
            {
                Enums.Lure.Ambitious => Icons.AmbitiousLure,
                Enums.Lure.Modest => Icons.ModestLure,
                _ => null,
            };
        }

        private static bool SetUptimeDependency(Fish fish, IEnumerable<BaitOrder> baitOrder)
        {
            foreach (var bait in baitOrder)
            {
                if (bait.Fish is not Fish f)
                {
                    continue;
                }

                if (CheckRestrictions(f))
                {
                    return true;
                }
            }

            foreach (var (predator, _) in fish.Predators)
            {
                if (CheckRestrictions(predator))
                {
                    return true;
                }
            }

            return false;

            bool CheckRestrictions(Fish f)
            {
                if (f.FishRestrictions.HasFlag(FishRestrictions.Time))
                {
                    if (!fish.FishRestrictions.HasFlag(FishRestrictions.Time))
                    {
                        return true;
                    }

                    if (!f.Interval.Contains(fish.Interval))
                    {
                        return true;
                    }
                }

                if (f.FishRestrictions.HasFlag(FishRestrictions.Weather))
                {
                    if (!fish.FishRestrictions.HasFlag(FishRestrictions.Weather))
                    {
                        return true;
                    }

                    if (f.CurrentWeather.Length > 0 && HasWeatherOutside(fish.CurrentWeather, f.CurrentWeather))
                    {
                        return true;
                    }

                    if (f.PreviousWeather.Length > 0 && HasWeatherOutside(fish.PreviousWeather, f.PreviousWeather))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static bool HasWeatherOutside(Structs.Weather[] weathers, Structs.Weather[] allowed)
        {
            foreach (var weather in weathers)
            {
                if (Array.IndexOf(allowed, weather) < 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string JoinDistinctNames(List<FishingSpot> spots, Func<FishingSpot, string?> getName)
        {
            var names = new List<string>(spots.Count);
            foreach (var spot in spots)
            {
                var name = getName(spot);
                if (name != null && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            return string.Join('\n', names);
        }

        private static string SetIntuition(Fish data)
        {
            var intuition = data.IntuitionLength;
            if (intuition <= 0)
            {
                return string.Empty;
            }

            var minutes = intuition / RealTime.SecondsPerMinute;
            var seconds = intuition % RealTime.SecondsPerMinute;
            if (seconds == 0)
            {
                return minutes == 1 ? "Intuition for 1 Minute" : string.Intern($"Intuition for {minutes} Minutes");
            }

            return string.Intern($"Intuition for {minutes}:{seconds:D2} Minutes");
        }

        public ExtendedFish(Fish data)
        {
            Data = data;
            Update();
        }

        public void Update()
        {
            Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(Data.ItemData.Icon));
            Territories = JoinDistinctNames(Data.FishingSpots, static f => f.Territory.Name);
            if (!Territories.Contains('\n'))
            {
                Territories = '\0' + Territories;
            }

            SpotNames = JoinDistinctNames(Data.FishingSpots, static f => f.Name);
            if (!SpotNames.Contains('\n'))
            {
                SpotNames = '\0' + SpotNames;
            }

            Aetherytes = JoinDistinctNames(Data.FishingSpots, static f => f.ClosestAetheryte?.Name);
            if (!Aetherytes.Contains('\n'))
            {
                Aetherytes = '\0' + Aetherytes;
            }

            Patch = string.Intern($"Patch {Data.Patch.ToVersionString()}");
            FishType = Data.OceanFish ? "Ocean Fish" :
                Data.IsSpearFish ? "Spearfishing" :
                Data.IsBigFish ? "Big Fish" : "Regular Fish";

            Time = !Data.FishRestrictions.HasFlag(FishRestrictions.Time)
                ? "Always Up"
                : Data.OceanFish
                    ? PrintOceanTime(Data.OceanTime)
                    : Data.Interval.AlwaysUp()
                        ? "Unknown Uptime"
                        : string.Intern(Data.Interval.PrintHours());

            UptimePercent = SetUptime(Data);
            UptimeString = string.Intern($"{(UptimePercent / 100f).ToString("F1", CultureInfo.InvariantCulture)}%");
            if (UptimeString == "0.0%")
            {
                UptimeString = "<0.1%";
            }

            WeatherIcons = SetWeather(Data);
            TransitionIcons = SetTransition(Data);
            Predators = SetPredators(Data);
            Bait = SetBait(Data);
            Snagging = SetSnagging(Data, Bait);
            Lure = SetLure(Data, Bait);
            UptimeDependency = SetUptimeDependency(Data, Bait);
            Intuition = SetIntuition(Data);
            Unlocked = GatherBuddy.FishLog.IsUnlocked(Data);
        }

        private static string PrintOceanTime(OceanTime time)
        {
            return time switch
            {
                OceanTime.Sunset => "Sunset",
                OceanTime.Sunset | OceanTime.Night => "Sunset or Night",
                OceanTime.Sunset | OceanTime.Day => "Sunset or Day",
                OceanTime.Night => "Night",
                OceanTime.Night | OceanTime.Day => "Day or Night",
                OceanTime.Day => "Day",
                _ => "Unknown Uptime",
            };
        }

        private static void PrintName(ExtendedFish fish)
        {
            using var font = ImRaii.PushFont(M3.TitleMedium);
            ImGui.TextUnformatted(fish.Data.Name[GatherBuddy.Language]);
        }

        private static void PrintTime(ExtendedFish fish)
            => Ui.Framed(fish.Time, ColorId.HeaderEorzeaTime.Value());

        private static void PrintWeather(ExtendedFish fish, Vector2 weatherIconSize)
        {
            if (!fish.Data.FishRestrictions.HasFlag(FishRestrictions.Weather))
            {
                Ui.Framed("No weather restrictions", ColorId.HeaderWeather.Value());
                return;
            }

            if (fish.WeatherIcons.Length == 0 && fish.TransitionIcons.Length == 0)
            {
                Ui.Framed("Unknown weather restrictions", ColorId.HeaderWeather.Value());
                return;
            }

            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing / 2);
            if (fish.TransitionIcons.Length > 0)
            {
                AlignTextToSize(fish.TransitionIcons.Length > 1 ? "Requires one of" : "Requires", weatherIconSize);
                style.Push(ImGuiStyleVar.ItemSpacing, Vector2.One * ImGuiHelpers.GlobalScale);
                foreach (var w in fish.TransitionIcons)
                {
                    ImGui.SameLine();
                    if (w.TryGetWrap(out var wrap, out _))
                    {
                        ImGui.Image(wrap.Handle, weatherIconSize);
                    }
                    else
                    {
                        ImGui.Dummy(weatherIconSize);
                    }
                }

                style.Pop();

                ImGui.SameLine();
                AlignTextToSize(fish.WeatherIcons.Length > 1 ? "followed by one of" : "followed by", weatherIconSize);
                if (fish.WeatherIcons.Length == 0)
                {
                    ImGui.SameLine();
                    AlignTextToSize(" Anything", weatherIconSize);
                }
                else
                {
                    style.Push(ImGuiStyleVar.ItemSpacing, Vector2.One * ImGuiHelpers.GlobalScale);
                    foreach (var w in fish.WeatherIcons)
                    {
                        ImGui.SameLine();
                        if (w.TryGetWrap(out var wrap, out _))
                        {
                            ImGui.Image(wrap.Handle, weatherIconSize);
                        }
                        else
                        {
                            ImGui.Dummy(weatherIconSize);
                        }
                    }
                }
            }
            else if (fish.WeatherIcons.Length > 0)
            {
                AlignTextToSize(fish.WeatherIcons.Length > 1 ? "Requires one of" : "Requires", weatherIconSize);
                style.Push(ImGuiStyleVar.ItemSpacing, Vector2.One * ImGuiHelpers.GlobalScale);
                foreach (var w in fish.WeatherIcons)
                {
                    ImGui.SameLine();
                    if (w.TryGetWrap(out var wrap, out _))
                    {
                        ImGui.Image(wrap.Handle, weatherIconSize);
                    }
                    else
                    {
                        ImGui.Dummy(weatherIconSize);
                    }
                }
            }
        }

        private static void PrintBait(ExtendedFish fish, Territory territory, Vector2 iconSize, Vector2 smallIconSize)
        {
            if (fish.Bait.Length == 0)
            {
                Ui.Framed("Unknown catch method", M3.Scheme.ErrorContainer);
                return;
            }

            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing / 2);

            var startPos = ImGui.GetCursorPos();
            var size = Vector2.Zero;
            if (fish.Snagging != null)
            {
                if (fish.Snagging.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, iconSize);
                }
                else
                {
                    ImGui.Dummy(iconSize);
                }

                ImGui.SameLine();
            }

            if (fish.Lure != null)
            {
                if (fish.Lure.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, iconSize);
                }
                else
                {
                    ImGui.Dummy(iconSize);
                }

                ImGui.SameLine();
            }

            var offsetSmall = (smallIconSize.Y - ImGui.GetTextLineHeight()) / 2;
            var offsetBig = (iconSize.Y - ImGui.GetTextLineHeight()) / 2;
            foreach (var (bait, idx) in fish.Bait.WithIndex())
            {
                size = iconSize;
                if (bait.Icon.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, size);
                }
                else
                {
                    ImGui.Dummy(size);
                }

                if (!fish.Data.IsSpearFish)
                {
                    style.Push(ImGuiStyleVar.ItemSpacing, Vector2.One);
                    ImGui.SameLine();
                    using var _ = ImRaii.Group();
                    style.Push(ImGuiStyleVar.FramePadding, Vector2.Zero);
                    if (bait.HookSet!.TryGetWrap(out wrap, out var _))
                    {
                        ImGui.Image(wrap.Handle, smallIconSize);
                    }
                    else
                    {
                        ImGui.Dummy(smallIconSize);
                    }

                    Ui.Framed(bait.Bite.Item1, bait.Bite.Item2, smallIconSize);
                    style.Pop(2);
                }

                ImGui.SameLine();

                var pos = ImGui.GetCursorPosY();

                var printed = false;
                if (bait.Fish is Fish f)
                {
                    var uptime = Plugin.UptimeManager.NextUptime(f, territory, GatherBuddy.Time.ServerTime);
                    if (uptime != TimeInterval.Always)
                    {
                        using var group = ImRaii.Group();
                        ImGui.SetCursorPosY(pos + offsetSmall);
                        ImGui.TextUnformatted(bait.Name);
                        ImGui.SetCursorPosY(pos + smallIconSize.Y + offsetSmall);
                        DrawTimeInterval(uptime, false, false);
                        printed = true;
                    }
                }

                ImGui.SetCursorPosY(pos + offsetBig);
                if (!printed)
                {
                    ImGui.TextUnformatted(bait.Name);
                }

                if (idx == fish.Bait.Length - 1)
                {
                    break;
                }

                ImGui.SameLine();
                ImGui.SetCursorPosY(pos + offsetBig);
                var arrowSize = M3Draw.MeasureIcon(FontAwesomeIcon.ArrowRight);
                var arrowAt = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - arrowSize.Y) * 0.5f);
                ImGui.Dummy(new Vector2(arrowSize.X, ImGui.GetTextLineHeight()));
                M3Draw.Icon(ImGui.GetWindowDrawList(), FontAwesomeIcon.ArrowRight, arrowAt, M3Text.ColorOf(M3TextStyle.Muted));
                ImGui.SameLine();
            }

            ImGui.SetCursorPos(startPos + new Vector2(0, size.Y + ImGui.GetStyle().ItemSpacing.Y));
        }

        private static void PrintPredators(ExtendedFish fish, Territory territory, Vector2 iconSize)
        {
            if (fish.Predators.Length == 0 && fish.Intuition.Length == 0)
            {
                return;
            }

            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { X = ImGuiHelpers.GlobalScale });
            var size = iconSize / 1.5f;
            var offset = (size.Y - ImGui.GetTextLineHeightWithSpacing()) / 2f;
            foreach (var predator in fish.Predators)
            {
                var pos = ImGui.GetCursorPosY();
                using var group = ImRaii.Group();
                Ui.Framed(predator.Amount, M3.Scheme.WarningContainer, size);
                ImGui.SameLine();
                if (predator.Icon.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, size);
                }
                else
                {
                    ImGui.Dummy(size);
                }

                style.Push(ImGuiStyleVar.ItemSpacing, new Vector2(3 * ImGuiHelpers.GlobalScale, 0));
                ImGui.SameLine();
                ImGui.SetCursorPosY(pos + offset);
                ImGui.TextUnformatted(predator.Name);
                var uptime = Plugin.UptimeManager.NextUptime(predator.Fish, territory, GatherBuddy.Time.ServerTime);
                if (uptime != TimeInterval.Always)
                {
                    style.Push(ImGuiStyleVar.ItemSpacing, new Vector2(10 * ImGuiHelpers.GlobalScale, 0));
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(pos + offset);
                    DrawTimeInterval(uptime, false, false);
                    style.Pop();
                }

                style.Pop();
            }


            if (fish.Intuition.Length == 0)
            {
                return;
            }

            Ui.Framed(fish.Intuition, M3.Scheme.InfoContainer);
        }

        private static void PrintFolklore(ExtendedFish fish)
        {
            if (fish.Data.Folklore.Length != 0)
            {
                Ui.Framed(fish.Data.Folklore, M3.Scheme.TertiaryContainer);
                ImGui.SameLine();
            }

            Ui.Framed(fish.Patch, M3.Scheme.SurfaceContainerHighest);
        }

        private static void PrintPoints(ExtendedFish fish)
        {
            if (fish.Data.Points > 0)
            {
                Ui.Framed($"Worth {fish.Data.Points} points", M3.Scheme.SuccessContainer);
            }
        }

        public void SetTooltip(Territory territory, Vector2 iconSize, Vector2 smallIconSize, Vector2 weatherIconSize, bool printName,
            bool standAlone = true)
        {
            if (standAlone)
            {
                using var tooltip = ImRaii.Tooltip();
                SetTooltip(territory, iconSize, smallIconSize, weatherIconSize, printName, false);
                return;
            }

            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing * new Vector2(1f, 1.5f));
            if (printName)
            {
                PrintName(this);
            }

            Ui.Framed($"Item ID: {Data.ItemId}", M3.Scheme.SurfaceContainerHigh);
            PrintTime(this);
            PrintWeather(this, weatherIconSize);
            PrintBait(this, territory, iconSize, smallIconSize);
            PrintPredators(this, territory, iconSize);
            PrintPoints(this);
            PrintFolklore(this);
        }
    }
}
