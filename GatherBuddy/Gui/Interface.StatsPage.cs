using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using GatherBuddy.RebornUI;
using RebornMaterial;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Bait = GatherBuddy.Structs.Bait;
using Effects = GatherBuddy.Models.Effects;
using FishingSpot = GatherBuddy.Classes.FishingSpot;


namespace GatherBuddy.Gui;

public partial class Interface
{
    private static readonly FrozenDictionary<uint, FishingSpot> Locations = GatherBuddy.GameData.FishingSpots;
    private static readonly FrozenDictionary<uint, Bait> Baits = GatherBuddy.GameData.Bait;
    private static readonly FrozenDictionary<ushort, CosmicMission> Missions = GatherBuddy.GameData.CosmicFishingMissions;
    private List<FishRecord> _records = [];
    private int _selectedFishingSpotIdx = GatherBuddy.Config.FishStatsSelectedIdx;
    private FishingSpot _selectedSpot = Locations.Values[GatherBuddy.Config.FishStatsSelectedIdx];

    private static string CosmicHandler(FishingSpot spot)
    {
        var name = spot.Name.AsSpan();
        if (name.EndsWith(')'))
        {
            if (ushort.TryParse(name[^5..^1], out var parsedNumber))
            {
                name = name[..^8];
                return $"{name} ({Missions[parsedNumber].Name})";
            }
        }

        return $"{name}";
    }

    private void DrawSelectorDropdown()
    {
        var spots = Locations.Values;
        var newIdx = _selectedFishingSpotIdx;
        if (M3Widgets.Combo("##FishingSpotSelector", ref newIdx, spots.Length, i => CosmicHandler(spots[i]), 420f * M3.Scale, "Choose a fishing spot",
                true)
         && newIdx != _selectedFishingSpotIdx)
        {
            _selectedFishingSpotIdx = newIdx;
            _selectedSpot = Locations.Values[_selectedFishingSpotIdx];
            GatherBuddy.Config.FishStatsSelectedIdx = _selectedFishingSpotIdx;
            GatherBuddy.Config.Save();
        }
    }

    private void DrawCopier()
    {
        var copyStatsString = GenerateSpotReport();
        M3Widgets.CopyButton("##copyStats", copyStatsString, "Copy report", "Copied", tooltip: copyStatsString,
            snackbar: $"Copied the fish stats for {CosmicHandler(_selectedSpot)}.");
    }

    private string GenerateSpotReport()
    {
        var spotRecords = _records.FindAll(r => r.SpotId == _selectedSpot.Id && r is { HasCatch: true, HasBait: true });
        var recordsAtSpot = new List<(Bait Bait, List<FishRecord> Records, int Index)>();
        foreach (var (_, baitGroupRecords) in GroupRecords(spotRecords, static r => r.BaitId))
        {
            recordsAtSpot.Add((baitGroupRecords[0].Bait, baitGroupRecords, recordsAtSpot.Count));
        }

        recordsAtSpot.Sort(static (lhs, rhs) =>
        {
            var order = Comparer<Bait>.Default.Compare(lhs.Bait, rhs.Bait);
            return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
        });

        var sb = new StringBuilder();

        // Location Name + Special Handler for Cosmic Missions
        var name = _selectedSpot.Name.AsSpan();
        ushort numbers = 0;
        if (name.EndsWith(')'))
        {
            if (ushort.TryParse(name[^5..^1], out var parsedNumber))
            {
                numbers = parsedNumber;
                name = name[..^8];
            }
        }

        sb.AppendLine($"Location: {name}");
        if (numbers > 0)
        {
            sb.AppendLine($"Mission: {Missions[numbers].Name}");
        }

        sb.AppendLine("Collection Method: GatherBuddy Report Generator");
        sb.AppendLine("");

        foreach (var (_, baitRecords, _) in recordsAtSpot)
        {
            var bait = baitRecords[0].BaitId;
            var baitName = GatherBuddy.GameData.Bait.TryGetValue(bait, out var b) ? b.Name :
                GatherBuddy.GameData.Fishes.TryGetValue(bait, out var f) ? $"Mooch - {new Bait(f.ItemData).Name}" : Bait.Unknown.Name;

            // Bait Name
            sb.AppendLine($"Bait: {baitName}");

            var fishIdToIndex = new Dictionary<uint, int>(_selectedSpot.Items.Length);
            for (var i = 0; i < _selectedSpot.Items.Length; i++)
            {
                fishIdToIndex.Add(_selectedSpot.Items[i].FishId, i);
            }

            var fishGroups = new List<(List<FishRecord> Records, int Order, int Index)>();
            foreach (var (_, fishGroupRecords) in GroupRecords(baitRecords, static r => r.CatchId))
            {
                fishGroups.Add((fishGroupRecords, fishIdToIndex.GetValueOrDefault(fishGroupRecords[0].Catch!.FishId, int.MaxValue), fishGroups.Count));
            }

            fishGroups.Sort(static (lhs, rhs) =>
            {
                var order = lhs.Order.CompareTo(rhs.Order);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });

            foreach (var (fishRecords, _, _) in fishGroups)
            {
                var fish = fishRecords[0].Catch!;
                var count = fishRecords.Count;

                if (count == 0)
                {
                    sb.AppendLine($"{fish.Name}:\n- (0 Caught)");
                    continue;
                }

                // Always include fish name
                sb.AppendLine($"{fish.Name}:");

                // Time
                if (GatherBuddy.Config.EnableReportTime)
                {
                    var timeRecords = fishRecords.FindAll(r => (!r.Flags.HasLure() || r.Flags.HasValidLure()) && !r.Flags.HasFlag(Effects.Chum));

                    if (timeRecords.Count == 0)
                    {
                        sb.AppendLine("- No valid time data.");
                    }
                    else
                    {
                        int minBite = timeRecords[0].Bite;
                        var maxBite = minBite;
                        var minLureBite = int.MaxValue;
                        var hasLureRecord = false;
                        foreach (var timeRecord in timeRecords)
                        {
                            if (timeRecord.Bite < minBite)
                            {
                                minBite = timeRecord.Bite;
                            }

                            if (timeRecord.Bite > maxBite)
                            {
                                maxBite = timeRecord.Bite;
                            }

                            if (timeRecord.Flags.HasFlag(Effects.ValidLure))
                            {
                                hasLureRecord = true;
                                if (timeRecord.Bite < minLureBite)
                                {
                                    minLureBite = timeRecord.Bite;
                                }
                            }
                        }

                        var minTime = minBite / 1000f;
                        var maxTime = maxBite / 1000f;
                        var isMinLure = hasLureRecord && minLureBite / 1000f == minTime;

                        var timeLine = $"- Times: {minTime} - {maxTime} ({timeRecords.Count} caught)";
                        if (isMinLure)
                        {
                            timeLine += " (Lure Min!)";
                        }

                        sb.AppendLine(timeLine);
                    }
                }

                if (GatherBuddy.Config.EnableReportSize)
                {
                    var filterFlags = new[]
                    {
                        Effects.Patience,
                        Effects.Patience2,
                        Effects.PrizeCatch,
                    };

                    (string label, List<ushort> sizes, Dictionary<Effects, int> effectCounts, int count) Classify(
                        Predicate<FishRecord> match, string label)
                    {
                        var sizes = new List<ushort>();
                        var effectCounter = new Dictionary<Effects, int>();
                        foreach (var flag in filterFlags)
                        {
                            effectCounter[flag] = 0;
                        }

                        foreach (var fishRecord in fishRecords)
                        {
                            if (!match(fishRecord))
                            {
                                continue;
                            }

                            sizes.Add(fishRecord.Size);
                            foreach (var flag in filterFlags)
                            {
                                if (fishRecord.Flags.HasFlag(flag))
                                {
                                    effectCounter[flag]++;
                                }
                            }
                        }

                        return (label, sizes, effectCounter, sizes.Count);
                    }

                    var normal = Classify(
                        static r => !r.Flags.HasFlag(Effects.Large) && !r.Flags.HasFlag(Effects.BigGameFishing),
                        "Average");

                    var large = Classify(
                        static r => r.Flags.HasFlag(Effects.Large) && !r.Flags.HasFlag(Effects.BigGameFishing),
                        "Large");

                    var bgf = Classify(
                        static r => r.Flags.HasFlag(Effects.BigGameFishing),
                        "BGF");


                    string Format((string label, List<ushort> sizes, Dictionary<Effects, int> effects, int count) group)
                    {
                        if (group.sizes.Count == 0)
                        {
                            return $"No {group.label} Fish";
                        }

                        var minRaw = group.sizes[0];
                        var maxRaw = minRaw;
                        foreach (var size in group.sizes)
                        {
                            if (size < minRaw)
                            {
                                minRaw = size;
                            }

                            if (size > maxRaw)
                            {
                                maxRaw = size;
                            }
                        }

                        var min = minRaw / 10f;
                        var max = maxRaw / 10f;

                        var effectParts = new List<string>(filterFlags.Length);
                        foreach (var flag in filterFlags)
                        {
                            if (group.effects[flag] > 0)
                            {
                                effectParts.Add($"{flag}: {group.effects[flag]}");
                            }
                        }

                        var effectText = string.Join(", ", effectParts);

                        return $"{min} - {max} ({group.count}" + (effectText != "" ? $" | [{effectText}])" : ")");
                    }

                    var sizeParts = new List<string>
                    {
                        Format(normal),
                        Format(large),
                        Format(bgf),
                    };

                    sb.AppendLine("- Sizes: " + string.Join(" | ", sizeParts));
                }

                if (GatherBuddy.Config.EnableReportMulti)
                {
                    var doubleHook = AverageHookAmount(fishRecords, HookSet.DoubleHook);
                    var tripleHook = AverageHookAmount(fishRecords, HookSet.TripleHook);

                    var hookLine = new List<string>();
                    if (doubleHook > 0)
                    {
                        hookLine.Add($"Double Hook Yield: {doubleHook}");
                    }

                    if (tripleHook > 0)
                    {
                        hookLine.Add($"Triple Hook Yield: {tripleHook}");
                    }

                    if (hookLine.Count != 0)
                    {
                        sb.AppendLine($"- {string.Join(" | ", hookLine)}");
                    }
                }
            }

            sb.AppendLine("");
        }

        return sb.ToString().TrimEnd();
    }

    private static Dictionary<uint, List<FishRecord>> GroupRecords(List<FishRecord> records, Func<FishRecord, uint> keySelector)
    {
        var groups = new Dictionary<uint, List<FishRecord>>();
        foreach (var fishRecord in records)
        {
            var key = keySelector(fishRecord);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups.Add(key, group);
            }

            group.Add(fishRecord);
        }

        return groups;
    }

    private static int CountRecords(List<FishRecord> records, Predicate<FishRecord> match)
    {
        var count = 0;
        foreach (var fishRecord in records)
        {
            if (match(fishRecord))
            {
                count++;
            }
        }

        return count;
    }

    private static double AverageHookAmount(List<FishRecord> records, HookSet hook)
    {
        long sum = 0;
        var count = 0;
        foreach (var fishRecord in records)
        {
            if (fishRecord.Hook == hook)
            {
                sum += fishRecord.Amount;
                count++;
            }
        }

        return count != 0 ? (double)sum / count : 0;
    }

    private void DrawFishingSpotInfo()
    {
        using var card = M3Card.Begin("##spotInfo", CosmicHandler(_selectedSpot), FontAwesomeIcon.MapMarkerAlt, M3.AccentFor(0));
        M3Widgets.RowValue("Territory", _selectedSpot.Territory.Name);
        M3Widgets.RowValue("Coordinates", $"{_selectedSpot.IntegralXCoord}, {_selectedSpot.IntegralYCoord}");
        M3Widgets.RowValue("Closest aetheryte", _selectedSpot.ClosestAetheryte?.Name ?? "None nearby");
        M3Widgets.SectionLabel("Fish here");
        if (_selectedSpot.Items.Length != 0)
        {
            foreach (var fish in _selectedSpot.Items)
            {
                M3Text.Bullet(fish.Name[GatherBuddy.Language]);
            }
        }
        else
        {
            M3Text.Draw("No fish available.", M3TextStyle.Faint);
        }
    }

    private void DrawFishInfo()
    {
        var recordsAtSpot = _records.FindAll(r => r.HasSpot && r.SpotId == _selectedSpot.Id && r.HasCatch && r.HasBait);

        // Group by FishId
        var fishGroups = GroupRecords(recordsAtSpot, static r => r.CatchId);

        foreach (var (fishId, allEntries) in fishGroups)
        {
            var fish = GatherBuddy.GameData.Fishes.GetValueOrDefault(fishId);
            if (fish == null)
            {
                continue;
            }

            using (ImRaii.PushFont(M3.TitleMedium))
            {
                Ui.Text(fish.Name[GatherBuddy.Language], M3.Scheme.Primary);
            }

            DrawSizeDistribution(allEntries);
            DrawBiteTimeHistogram(allEntries);

            ImGui.Spacing();

            var baitGroups = GroupRecords(allEntries, static r => r.BaitId);

            foreach (var (baitId, entries) in baitGroups)
            {
                var bait = GatherBuddy.GameData.Bait.GetValueOrDefault(baitId);
                M3Text.Bullet($"Bait: {bait?.Name ?? "Unknown"}");

                DrawAmountCaught(entries.Count);
                DrawCatchPercentage(entries, CountRecords(recordsAtSpot, r => r.BaitId == baitId));

                M3Widgets.Divider(2f);
            }

            M3Widgets.Divider();
        }
    }

    private static void DrawSizeDistribution(List<FishRecord> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var windowSize = new Vector2(ImGui.GetWindowSize().X / 2, ImGui.GetWindowSize().Y);
        var chartWidth = windowSize.X;
        var bucketCount = int.Min(10, entries.Count);
        var totalSpacing = chartWidth * 0.1f;
        var barAreaWidth = chartWidth - totalSpacing;
        var barWidth = barAreaWidth / bucketCount;
        var barSpacing = totalSpacing / (bucketCount - 1);
        const float chartHeight = 150f;
        const float leftMargin = 40f;
        const float bottomMargin = 30f;
        const int yTicks = 5;
        var padding = 5 * ImGuiHelpers.GlobalScale;

        var colorPalette = new List<Vector4>
        {
            M3Chart.SeriesColor(0),
            M3Chart.SeriesColor(1),
            M3Chart.SeriesColor(2),
        };

        var minSize = entries[0].Size / 10f;
        var maxSize = minSize;
        foreach (var fishRecord in entries)
        {
            var size = fishRecord.Size / 10f;
            if (size < minSize)
            {
                minSize = size;
            }

            if (size > maxSize)
            {
                maxSize = size;
            }
        }
        var sizeRange = maxSize - minSize;
        if (sizeRange == 0)
        {
            minSize -= 1f;
            maxSize += 1f;
            sizeRange = maxSize;
        }

        var bucketWidth = sizeRange / bucketCount;

        // Buckets
        var buckets = new (int normal, int large, int bgf)[bucketCount];
        foreach (var entry in entries)
        {
            var bucketIndex = (int)((entry.Size / 10f - minSize) / bucketWidth);
            if (bucketIndex >= bucketCount)
            {
                bucketIndex = bucketCount - 1;
            }

            ref var b = ref buckets[bucketIndex];
            if (entry.Flags.HasFlag(Effects.BigGameFishing))
            {
                b.bgf++;
            }
            else if (entry.Flags.HasFlag(Effects.Large))
            {
                b.large++;
            }
            else
            {
                b.normal++;
            }
        }

        var maxCount = buckets[0].normal + buckets[0].large + buckets[0].bgf;
        foreach (var (normalCount, largeCount, bgfCount) in buckets)
        {
            if (normalCount + largeCount + bgfCount > maxCount)
            {
                maxCount = normalCount + largeCount + bgfCount;
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var textHeight = ImGui.GetTextLineHeight();
        var origin = new Vector2(cursor.X + leftMargin, cursor.Y + chartHeight + textHeight * (float)1.5);

        // Y-axis
        for (var i = 0; i <= yTicks; i++)
        {
            var yVal = maxCount * i / (float)yTicks;
            var y = origin.Y - yVal / maxCount * chartHeight;

            drawList.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + chartWidth, y), M3.U32(M3.Scheme.OutlineVariant, 0.6f));
            drawList.AddText(new Vector2(origin.X - (ImGui.CalcTextSize($"{yVal}").X + padding), y - textHeight / 2),
                M3.U32(M3.Scheme.OnSurfaceVariant), $"{yVal}");
        }

        // Bars
        for (var i = 0; i < bucketCount; i++)
        {
            var x = origin.X + i * (barWidth + barSpacing);
            var y = origin.Y;

            var (normal, large, bgf) = buckets[i];

            DrawStack(ref y, normal, colorPalette[0]);
            DrawStack(ref y, large, colorPalette[1]);
            DrawStack(ref y, bgf, colorPalette[2]);

            // X-axis
            var fromVal = minSize + i * bucketWidth;
            var fromText = $"{fromVal:F1}";
            var toText = $"{fromVal + bucketWidth:F1}";
            var label = $"{fromText}-{toText}";
            var textSize = ImGui.CalcTextSize(label);
            var labelX = x + (barWidth - textSize.X) * 0.5f;
            drawList.AddText(new Vector2(labelX, origin.Y + 4f), M3.U32(M3.Scheme.OnSurfaceVariant), label);
            continue;

            void DrawStack(ref float yPos, int count, Vector4 color)
            {
                if (count <= 0)
                {
                    return;
                }

                var height = count / (float)maxCount * chartHeight;
                drawList.AddRectFilled(
                    new Vector2(x, yPos - height),
                    new Vector2(x + barWidth, yPos),
                    ImGui.ColorConvertFloat4ToU32(color));
                yPos -= height;
            }
        }

        // Titles
        drawList.AddText(new Vector2(cursor.X + leftMargin + chartWidth * 0.5f - 20f, origin.Y + 20f), M3.U32(M3.Scheme.OnSurfaceVariant),
            "Size");
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(cursor.X + 5f, cursor.Y), M3.U32(M3.Scheme.OnSurfaceVariant), "Count");

        // Legend
        var legendX = origin.X + chartWidth + 10f;
        var legendY = cursor.Y;
        for (var i = 0; i < colorPalette.Count; i++)
        {
            var sizeName =
                new List<string>
                {
                    "Average",
                    "Large",
                    "Big Game Fishing",
                }[i];
            var label = $"{sizeName}";
            drawList.AddRectFilled(new Vector2(legendX, legendY), new Vector2(legendX + textHeight, legendY + textHeight),
                ImGui.ColorConvertFloat4ToU32(colorPalette[i]));
            drawList.AddText(new Vector2(legendX + textHeight, legendY), M3.U32(M3.Scheme.OnSurfaceVariant), label);
            legendY += textHeight + 4f;
        }

        // Space
        ImGui.Dummy(new Vector2(leftMargin + chartWidth + 100f, chartHeight + bottomMargin + 20f));
    }

    private static void DrawAmountCaught(int count)
    {
        M3Text.Draw(count == 1 ? "Caught once" : $"Caught {count} times", M3TextStyle.Muted);
    }

    private static void DrawCatchPercentage(List<FishRecord> entries, int baitTotal)
    {
        var percentage = entries.Count / (float)baitTotal * 100f;
        M3Text.Draw($"{percentage:F2}% of the catches with this bait", M3TextStyle.Muted);
    }

    private static void DrawBiteTimeHistogram(List<FishRecord> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var windowSize = new Vector2(ImGui.GetWindowSize().X / 2, ImGui.GetWindowSize().Y);
        var chartWidth = windowSize.X;
        var bucketCount = Math.Min(10, entries.Count);
        var totalSpacing = chartWidth * 0.1f;
        var barAreaWidth = chartWidth - totalSpacing;
        var barWidth = barAreaWidth / bucketCount;
        var barSpacing = totalSpacing / (bucketCount - 1);
        const float chartHeight = 150f;
        const float leftMargin = 40f;
        const float bottomMargin = 30f;
        const int yTicks = 5;
        var padding = 5 * ImGuiHelpers.GlobalScale;

        var baitIds = new List<uint>();
        foreach (var fishRecord in entries)
        {
            if (!baitIds.Contains(fishRecord.BaitId))
            {
                baitIds.Add(fishRecord.BaitId);
            }
        }

        baitIds.Sort();
        var baitColors = new Dictionary<uint, Vector4>();
        var colorPalette = new Vector4[]
        {
            M3Chart.SeriesColor(0),
            M3Chart.SeriesColor(1),
            M3Chart.SeriesColor(2),
            M3Chart.SeriesColor(3),
            M3Chart.SeriesColor(4),
            M3Chart.SeriesColor(5),
            M3Chart.SeriesColor(6),
        };
        for (var i = 0; i < baitIds.Count; i++)
        {
            baitColors[baitIds[i]] = colorPalette[i % colorPalette.Length];
        }

        var hatchColors = new List<uint>
        {
            // Blue color for "Chum"
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.5f, 1.0f, 1.0f)), // Blue

            // Light red to dark red for "Amb"
            ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.6f, 0.6f, 1.0f)), // Light red
            ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.3f, 0.3f, 1.0f)), // Medium red
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.0f, 0.0f, 1.0f)), // Dark red

            // Light green to dark green for "Mod"
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 1.0f, 0.6f, 1.0f)), // Light green
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1.0f, 0.3f, 1.0f)), // Medium green
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.5f, 0.0f, 1.0f)), // Dark green
        };

        var minTime = entries[0].Bite / 1000f;
        var maxTime = minTime;
        foreach (var fishRecord in entries)
        {
            var biteTime = fishRecord.Bite / 1000f;
            if (biteTime < minTime)
            {
                minTime = biteTime;
            }

            if (biteTime > maxTime)
            {
                maxTime = biteTime;
            }
        }
        var timeRange = maxTime - minTime;
        var bucketWidth = timeRange / bucketCount;

        var buckets = new List<Dictionary<uint, List<FishRecord>>>();
        for (var i = 0; i < bucketCount; i++)
        {
            buckets.Add([]);
        }

        foreach (var e in entries)
        {
            var bucketIndex = (int)((e.Bite / 1000f - minTime) / bucketWidth);

            if (bucketIndex < 0)
            {
                bucketIndex = 0;
            }
            else if (bucketIndex >= bucketCount)
            {
                bucketIndex = bucketCount - 1;
            }

            var bucket = buckets[bucketIndex];

            if (!bucket.TryGetValue(e.BaitId, out var list))
            {
                list = [];
                bucket[e.BaitId] = list;
            }

            list.Add(e);
        }

        var maxCount = 0;
        foreach (var bucketRecords in buckets)
        {
            var bucketTotal = 0;
            foreach (var baitRecords in bucketRecords.Values)
            {
                bucketTotal += baitRecords.Count;
            }

            if (bucketRecords.Count > 0 && bucketTotal > maxCount)
            {
                maxCount = bucketTotal;
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var textHeight = ImGui.GetTextLineHeight();
        var origin = new Vector2(cursor.X + leftMargin, cursor.Y + chartHeight + textHeight * 1.5f);

        // Y-axis
        for (var i = 0; i <= yTicks; i++)
        {
            var yVal = maxCount * i / (float)yTicks;
            var y = origin.Y - yVal / maxCount * chartHeight;

            drawList.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + chartWidth, y), M3.U32(M3.Scheme.OutlineVariant, 0.6f));
            var label = $"{yVal:0}";
            drawList.AddText(new Vector2(origin.X - (ImGui.CalcTextSize(label).X + padding), y - textHeight / 2),
                M3.U32(M3.Scheme.OnSurfaceVariant), label);
        }

        // Histogram bars
        for (var i = 0; i < bucketCount; i++)
        {
            var x = origin.X + i * (barWidth + barSpacing);
            var y = origin.Y;
            foreach (var bait in baitIds)
            {
                if (!buckets[i].ContainsKey(bait))
                {
                    continue;
                }

                if (!buckets[i].TryGetValue(bait, out var lRecords) || lRecords.Count == 0)
                {
                    continue;
                }

                var count = lRecords.Count;
                var height = count / (float)maxCount * chartHeight;
                var baitColor = ImGui.ColorConvertFloat4ToU32(baitColors[bait]);
                var barTop = new Vector2(x + barWidth, y);
                var counter = new List<int>
                {
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && e.Flags.HasFlag(Effects.ModestLure1)
                     && e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && e.Flags.HasFlag(Effects.ModestLure1)
                     && !e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && !e.Flags.HasFlag(Effects.ModestLure1)
                     && e.Flags.HasFlag(Effects.ModestLure2)),
                    CountRecords(lRecords, static e =>
                        !e.Flags.HasFlag(Effects.Chum)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure1)
                     && !e.Flags.HasFlag(Effects.AmbitiousLure2)
                     && e.Flags.HasFlag(Effects.ModestLure1)
                     && e.Flags.HasFlag(Effects.ModestLure2)),
                };

                for (var j = 0; j < counter.Count; j++)
                {
                    var c = counter[j];
                    if (c <= 0)
                    {
                        continue;
                    }

                    var barBot = new Vector2(x, barTop.Y);
                    barTop.Y -= c / (float)maxCount * chartHeight;

                    //First Draw bait background
                    drawList.AddRectFilled(barBot, barTop, baitColor);

                    // Then draw if it was chummed
                    if (j is >= 1 and <= 7)
                    {
                        DrawHatchedRectDown(barBot, barTop, 10f, drawList, hatchColors[0]);
                    }

                    // Then draw lure if present
                    if (j is >= 2 and <= 7)
                    {
                        DrawHatchedRectUp(barBot, barTop, 10f, drawList, hatchColors[j - 1]);
                    }
                    else if (j is >= 8 and <= 13)
                    {
                        DrawHatchedRectUp(barBot, barTop, 10f, drawList, hatchColors[j - 7]);
                    }

                    var heightTextSize = ImGui.CalcTextSize($"{c}");
                    drawList.AddText(
                        new Vector2(x + barWidth / 2 - heightTextSize.X / 2,
                            barBot.Y - c / (float)maxCount * chartHeight / 2 - heightTextSize.Y / 2), M3.U32(M3.Scheme.OnSurfaceVariant), $"{c}");
                }

                y -= height;
            }

            // X-axis
            var from = minTime + i * bucketWidth;
            var to = from + bucketWidth;
            var label = $"{from:F1}-{to:F1}";
            var textSize = ImGui.CalcTextSize(label);
            var labelX = x + (barWidth - textSize.X) * 0.5f;
            drawList.AddText(new Vector2(labelX, origin.Y + 4f), M3.U32(M3.Scheme.OnSurfaceVariant), label);
        }

        // Titles
        drawList.AddText(new Vector2(cursor.X + leftMargin + chartWidth * 0.5f - 20f, origin.Y + 20f), M3.U32(M3.Scheme.OnSurfaceVariant),
            "Bite Time");
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(cursor.X + 5f, cursor.Y), M3.U32(M3.Scheme.OnSurfaceVariant), "Count");

        // Legend
        var legendX = origin.X + chartWidth + 10f;
        var legendY = cursor.Y;
        foreach (var bait in baitIds)
        {
            var baitName = GatherBuddy.GameData.Bait.TryGetValue(bait, out var b) ? b.Name :
                GatherBuddy.GameData.Fishes.TryGetValue(bait, out var f) ? new Bait(f.ItemData).Name : Bait.Unknown.Name;
            var label = $"{baitName}";
            drawList.AddRectFilled(new Vector2(legendX, legendY), new Vector2(legendX + textHeight, legendY + textHeight),
                ImGui.ColorConvertFloat4ToU32(baitColors[bait]));
            drawList.AddText(new Vector2(legendX + textHeight, legendY), M3.U32(M3.Scheme.OnSurfaceVariant), label);
            legendY += textHeight + 4f;
        }

        for (var k = 0; k < hatchColors.Count; k++)
        {
            var baitName =
                new List<string>
                {
                    "Chum",
                    "AmbitiousLure1",
                    "AmbitiousLure2",
                    "AmbitiousLure3",
                    "ModestLure1",
                    "ModestLure2",
                    "ModestLure3",
                }[k];
            var label = $"{baitName}";
            if (k == 0)
            {
                DrawHatchedRectDown(new Vector2(legendX, legendY), new Vector2(legendX + textHeight, legendY + textHeight), 4f, drawList,
                    hatchColors[k]);
            }
            else
            {
                DrawHatchedRectUp(new Vector2(legendX, legendY), new Vector2(legendX + textHeight, legendY + textHeight), 4f, drawList,
                    hatchColors[k]);
            }

            drawList.AddText(new Vector2(legendX + textHeight, legendY), M3.U32(M3.Scheme.OnSurfaceVariant), label);
            legendY += textHeight + 4f;
        }

        // Space
        ImGui.Dummy(new Vector2(leftMargin + chartWidth + 100f, chartHeight + bottomMargin + 20f));
    }

    private void DrawStatsPage()
    {
        if (!GatherBuddy.Config.EnableFishStats)
        {
            return;
        }

        _records = _plugin.FishRecorder.Records;
        using var id = ImRaii.PushId("Fishing Spots Stats");
        using var child = ImRaii.Child("##stats", Vector2.Zero);
        if (!child)
        {
            return;
        }

        DrawSelectorDropdown();
        ImGui.SameLine(0, M3.Space2);
        DrawCopier();
        ImGui.Dummy(new Vector2(0, M3.Space1));
        DrawFishingSpotInfo();
        if (GatherBuddy.Config.EnableFishStatsGraphs)
        {
            DrawFishInfo();
        }
    }

    private static void DrawHatchedRectUp(Vector2 from, Vector2 to, float thickness, ImDrawListPtr drawList, uint colorFlag)
    {
        var min = new Vector2(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y));
        var max = new Vector2(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));

        drawList.AddRectFilled(min, max, 0x00000000);
        drawList.PushClipRect(min, max, true);

        var height = max.Y - min.Y;
        var angle = max.X - min.X;
        var block = (int)((height + angle) / thickness);
        var halfThickness = thickness / 2;

        var start = new Vector2(min.X, min.Y + halfThickness);
        var end = new Vector2(max.X, min.Y + halfThickness - angle);

        for (var i = 0; i < block; ++i)
        {
            drawList.AddQuadFilled(start,
                new Vector2(start.X, start.Y - halfThickness),
                end,
                new Vector2(end.X, end.Y + halfThickness),
                colorFlag);

            start.Y += thickness;
            end.Y += thickness;
        }

        drawList.PopClipRect();
        drawList.AddRect(min, max, colorFlag);
    }

    private static void DrawHatchedRectDown(Vector2 from, Vector2 to, float thickness, ImDrawListPtr drawList, uint colorFlag)
    {
        var min = new Vector2(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y));
        var max = new Vector2(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));

        drawList.AddRectFilled(min, max, 0x00000000);
        drawList.PushClipRect(min, max, true);

        var height = max.Y - min.Y;
        var angle = max.X - min.X;
        var block = (int)((height + angle) / thickness);
        var halfThickness = thickness / 2;

        var start = new Vector2(min.X, min.Y + halfThickness - angle);
        var end = new Vector2(max.X, min.Y + halfThickness);

        for (var i = -1; i < block; ++i)
        {
            drawList.AddQuadFilled(start,
                new Vector2(start.X, start.Y - halfThickness),
                end,
                new Vector2(end.X, end.Y + halfThickness),
                colorFlag);

            start.Y += thickness;
            end.Y += thickness;
        }

        drawList.PopClipRect();
        drawList.AddRect(min, max, colorFlag);
    }
}
