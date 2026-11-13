using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using GatherBuddy.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using EnhancedCurrentWeather = GatherBuddy.SeFunctions.EnhancedCurrentWeather;
namespace GatherBuddy.AutoGather.Lists
{
    internal sealed class ActiveItemList : IEnumerable<GatherTarget>, IDisposable
    {
        private readonly List<GatherTarget> _gatherableItems = [];
        private readonly AutoGatherListsManager _listsManager;
        private readonly AutoGather _autoGather;
        private readonly Dictionary<uint, int> _teleportationCosts = [];
        private readonly Dictionary<GatheringNode, TimeInterval> _visitedTimedNodes = [];
        private TimeStamp _lastUpdateTime = TimeStamp.MinValue;
        private uint _lastTerritoryId;
        private int _lastWeatherId;
        private bool _activeItemsChanged;
        private bool _consumedCloudedNode;
        private bool _forceUpdateUnconditionally;
        private GatheringType _lastJob = GatheringType.Unknown;
        private GatherTarget _currentItem;

        internal ReadOnlyDictionary<GatheringNode, TimeInterval> DebugVisitedTimedLocations
            => _visitedTimedNodes.AsReadOnly();

        /// <summary>
        /// First item on the list as of the last enumeration or default.
        /// </summary>
        public GatherTarget CurrentOrDefault
            => IsInitialized ? _currentItem : GetNextOrDefault();

        /// <summary>
        /// Determines whether there are any items that need to be gathered,
        /// including items that are not up yet.
        /// </summary>
        /// <value>
        /// True if there are items that need to be gathered; otherwise, false.
        /// </value>
        public bool HasItemsToGather
            => _gatherableItems.Exists(NeedsGathering);

        public bool IsCloudedNodeConsumed
            => _consumedCloudedNode;

        public bool IsInitialized
            => _lastUpdateTime != TimeStamp.MinValue;

        public ActiveItemList(AutoGatherListsManager listsManager, AutoGather autoGather)
        {
            _listsManager = listsManager;
            _autoGather = autoGather;
            _listsManager.ActiveItemsChanged += OnActiveItemsChanged;
        }

        /// <summary>
        /// Returns an enumerator that iterates through the available gather targets.
        /// </summary>
        /// <returns>
        /// An enumerator for the available gather targets.
        /// </returns>
        public IEnumerator<GatherTarget> GetEnumerator()
        {
            foreach (var target in _gatherableItems)
            {
                if (NeedsGathering(target))
                {
                    yield return target;
                }
            }
        }

        /// <summary>
        /// Refreshes the list of items to gather (if needed) and returns the first item.
        /// </summary>
        /// <returns>The next item to gather. </returns>
        public GatherTarget GetNextOrDefault()
        {
            if (IsUpdateNeeded())
            {
                DoUpdate();
            }

            return _currentItem = _gatherableItems.Find(x => x.Time.InRange(_lastUpdateTime) && NeedsGathering(x));
        }

        /// <summary>
        /// Returns next timed item that is not up yet
        /// </summary>
        /// <returns></returns>
        public GatherTarget PeekNextTimed()
        {
            return _gatherableItems.Find(x => !x.Time.InRange(_lastUpdateTime) && x.Time != TimeInterval.Invalid);
        }

        /// <summary>
        /// Marks a node as visited.
        /// </summary>
        /// <param name="target">The game object of the node to mark as visited.</param>
        public void MarkVisited(IGameObject target)
        {
            // In almost all cases, the target is the first item in the list, so it's O(1).
            var (_, loc, time, _) = _gatherableItems.Find(x => x.Node?.WorldPositions.ContainsKey(target.BaseId) ?? false);
            var node = loc as GatheringNode;

            // Could happen with manual navigation if gathered node isn't on the list.
            if (node == null)
            {
                foreach (var gatheringNode in GatherBuddy.GameData.GatheringNodes.Values)
                {
                    if (gatheringNode.WorldPositions.ContainsKey(target.BaseId))
                    {
                        node = gatheringNode;
                        break;
                    }
                }

                time = node?.Times.NextUptime(IsInitialized ? _lastUpdateTime : GatherBuddy.Time.ServerTime) ?? default;
            }

            switch (node?.NodeType)
            {
                case NodeType.Legendary:
                case NodeType.Unspoiled:
                    _visitedTimedNodes[node] = time;
                    _forceUpdateUnconditionally = true;
                    break;
                case NodeType.Clouded:
                    _consumedCloudedNode = true;
                    _forceUpdateUnconditionally = true;
                    break;
            }
        }

        internal void DebugMarkVisited(GatherTarget x)
        {
            _forceUpdateUnconditionally = true;
            if (x.Time != TimeInterval.Always && x.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled)
            {
                _visitedTimedNodes[x.Node] = x.Time;
            }

            if (x.Node?.NodeType == NodeType.Clouded)
            {
                _consumedCloudedNode = EnhancedCurrentWeather.GetCurrentWeatherId() == x.Node.UmbralWeather.Id;
            }
        }

        private bool NeedsGathering((IGatherable item, uint quantity) value)
        {
            var (item, quantity) = value;
            return item.GetTotalCount(_listsManager.UsesRetainerInventory(item)) < quantity && CheckOvercap(item);
        }

        private bool NeedsGathering(GatherTarget target)
            => NeedsGathering((target.Item, target.Quantity));

        private static bool CheckOvercap(IGatherable item)
        {
            if (item.IsTreasureMap)
            {
                return item.GetInventoryCount() < 1;
            }

            if (item.IsCrystal)
            {
                return item.GetInventoryCount() < 9999;
            }

            return true;
        }

        private readonly record struct FishWindowPriority(bool Applicable, long EffectiveDuration, TimeStamp WindowEnd, TimeStamp WindowStart);

        private sealed class FishWindowPriorityComparer : IComparer<FishWindowPriority>
        {
            public static readonly FishWindowPriorityComparer Instance = new();

            public int Compare(FishWindowPriority lhs, FishWindowPriority rhs)
            {
                if (!lhs.Applicable || !rhs.Applicable)
                {
                    return 0;
                }

                var durationComparison = lhs.EffectiveDuration.CompareTo(rhs.EffectiveDuration);
                if (durationComparison != 0)
                {
                    return durationComparison;
                }

                var endComparison = lhs.WindowEnd.CompareTo(rhs.WindowEnd);
                if (endComparison != 0)
                {
                    return endComparison;
                }

                return lhs.WindowStart.CompareTo(rhs.WindowStart);
            }
        }

        private static int GetAvailabilityPriority(GatherTarget target, TimeStamp now)
        {
            if (target.Time == TimeInterval.Invalid || target.Time == TimeInterval.Never)
            {
                return 3;
            }

            if (target.Time == TimeInterval.Always)
            {
                return 1;
            }

            return target.Time.InRange(now) ? 0 : 2;
        }

        private static TimeStamp GetAvailabilityStart(GatherTarget target, TimeStamp now)
        {
            if (target.Time == TimeInterval.Invalid || target.Time == TimeInterval.Never)
            {
                return TimeStamp.MaxValue;
            }

            return target.Time.InRange(now) ? TimeStamp.MinValue : target.Time.Start;
        }

        private static TimeStamp GetLegacyAvailabilityStart(GatherTarget target, TimeStamp now)
            => target.Time.InRange(now) ? TimeStamp.MinValue : target.Time.Start;

        private static FishWindowPriority GetFishWindowPriority(GatherTarget target, TimeStamp now)
        {
            if (target.Fish == null || target.Time == TimeInterval.Always || target.Time == TimeInterval.Invalid || target.Time == TimeInterval.Never)
            {
                return default;
            }

            var effectiveStart = target.Time.Start.Max(now);
            return new FishWindowPriority(true, target.Time.End - effectiveStart, target.Time.End, target.Time.Start);
        }

        private static string FormatFishTimingForDebug(in GatherTarget target, TimeStamp now)
        {
            if (target.Time == TimeInterval.Always)
            {
                return "always";
            }

            if (target.Time == TimeInterval.Invalid || target.Time == TimeInterval.Never)
            {
                return "invalid";
            }

            var effectiveStart = target.Time.Start.Max(now);
            var window = TimeInterval.DurationString(effectiveStart, target.Time.End, true);
            return target.Time.InRange(now)
                ? $"active, remaining={window}"
                : $"future, startsIn={TimeInterval.DurationString(target.Time.Start, now, true)}, window={window}";
        }

        private void LogFishPriorityOrder(TimeStamp now)
        {
            List<string>? prioritizedFish = null;
            foreach (var target in _gatherableItems)
            {
                if (target.Fish == null || target.Time == TimeInterval.Invalid || target.Time == TimeInterval.Never)
                {
                    continue;
                }

                prioritizedFish ??= [with(5)];
                prioritizedFish.Add($"{target.Item.Name[GatherBuddy.Language]} ({FormatFishTimingForDebug(target, now)})");
                if (prioritizedFish.Count == 5)
                {
                    break;
                }
            }

            if (prioritizedFish == null)
            {
                return;
            }

            GatherBuddy.Log.Debug($"[ActiveItemList] Fish priority order: {string.Join(" | ", prioritizedFish)}");
        }

        private static int CompareLegacyTargetOrder(GatherTarget lhs, GatherTarget rhs, TimeStamp now)
        {
            var timeComparison = GetLegacyAvailabilityStart(lhs, now).CompareTo(GetLegacyAvailabilityStart(rhs, now));
            if (timeComparison != 0)
            {
                return timeComparison;
            }

            return (lhs.Time == TimeInterval.Always).CompareTo(rhs.Time == TimeInterval.Always);
        }

        private static int CompareFishTargetOrder(GatherTarget lhs, GatherTarget rhs, TimeStamp now)
        {
            var availabilityComparison = GetAvailabilityPriority(lhs, now).CompareTo(GetAvailabilityPriority(rhs, now));
            if (availabilityComparison != 0)
            {
                return availabilityComparison;
            }

            var windowComparison = FishWindowPriorityComparer.Instance.Compare(GetFishWindowPriority(lhs, now), GetFishWindowPriority(rhs, now));
            if (windowComparison != 0)
            {
                return windowComparison;
            }

            return CompareLegacyTargetOrder(lhs, rhs, now);
        }

        private static List<GatherTarget> ApplyFishPriorityOrdering(List<GatherTarget> orderedTargets, TimeStamp now)
        {
            var fishSlots = new List<int>(orderedTargets.Count);
            var fishTargets = new List<GatherTarget>(orderedTargets.Count);
            foreach (var (target, index) in orderedTargets.WithIndex())
            {
                if (target.Fish == null)
                {
                    continue;
                }

                fishSlots.Add(index);
                fishTargets.Add(target);
            }

            if (fishTargets.Count < 2)
            {
                return orderedTargets;
            }

            fishTargets.Sort((lhs, rhs) => CompareFishTargetOrder(lhs, rhs, now));
            for (var i = 0; i < fishSlots.Count; ++i)
            {
                orderedTargets[fishSlots[i]] = fishTargets[i];
            }

            return orderedTargets;
        }

        private void OnActiveItemsChanged()
        {
            _activeItemsChanged = true;
        }

        private void RemoveExpiredVisited(TimeStamp adjustedServerTime)
        {
            foreach (var (loc, time) in _visitedTimedNodes)
            {
                if (time.End <= adjustedServerTime)
                {
                    _visitedTimedNodes.Remove(loc);
                }
            }
        }

        internal void DebugClearVisited()
        {
            _visitedTimedNodes.Clear();
            _forceUpdateUnconditionally = true;
        }

        public void ForceRefresh()
        {
            _forceUpdateUnconditionally = true;
        }

        /// <summary>
        /// Updates the list of items to gather based on the current territory and player levels.
        /// </summary>
        private void UpdateItemsToGather()
        {
            // Items are unlocked in tiers of 5 levels, so we round up to the nearest 5.
            var minerLevel = (DiscipleOfLand.MinerLevel + 5) / 5 * 5;
            var botanistLevel = (DiscipleOfLand.BotanistLevel + 5) / 5 * 5;
            var adjustedServerTime = _lastUpdateTime;
            var territoryId = _lastTerritoryId;
            var weatherId = _lastWeatherId;
            DateTime? nextAllowance = null;
            var currentJobType = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };

            // Group by item and select the best node.
            var bestByItem = new Dictionary<IGatherable, TargetCandidate>();
            foreach (var (item, quantity) in _listsManager.ActiveItems)
            {
                // Filter out items that are already gathered.
                if (!NeedsGathering((item, quantity)))
                {
                    continue;
                }

                // Fetch preferred location.
                var preferredLocation = _listsManager.GetPreferredLocation(item);
                // Flatten node list and calculate the next uptime.
                foreach (var itemLocation in item.Locations)
                {
                    var location = itemLocation;
                    var time = location switch
                    {
                        GatheringNode node => node.Times.NextUptime(adjustedServerTime),
                        FishingSpot spot => UptimeManager.NextUptime((item as Fish)!, spot.Territory, adjustedServerTime),
                        _ => throw new InvalidOperationException()
                    };

                    // If treasure map, only gather if the allowance is up.
                    if (item.IsTreasureMap && (nextAllowance ??= DiscipleOfLand.NextTreasureMapAllowance) > adjustedServerTime.DateTime)
                    {
                        time = TimeInterval.Invalid;
                    }

                    // Remove nodes that require the player to be on the home world.
                    if (RequiresHomeWorld(location) && !Functions.OnHomeWorld())
                    {
                        continue;
                    }

                    // Remove nodes with a level higher than the player can gather.
                    var levelAllowed = location.GatheringType.ToGroup() switch
                    {
                        GatheringType.Miner => (location as GatheringNode)!.Level <= minerLevel,
                        GatheringType.Botanist => (location as GatheringNode)!.Level <= botanistLevel,
                        _ => true
                    };
                    if (!levelAllowed)
                    {
                        continue;
                    }

                    // Apply predators and mooch dependencies time restrictions.
                    time = IntersectMoochUptime(item, location, time, adjustedServerTime);
                    location = CorrectForPredatorLocation(item, location);
                    time = IntersectPredatorUptime(item, location, time, adjustedServerTime);
                    // Remove uptime for nodes that have already been gathered.
                    if (location is GatheringNode visitedNode && _visitedTimedNodes.ContainsKey(visitedNode))
                    {
                        time = TimeInterval.Invalid;
                    }

                    var candidate = new TargetCandidate
                    {
                        Item = item,
                        Location = location,
                        Time = time,
                        Quantity = quantity,
                        // Prioritize active nodes
                        Inactive = !time.InRange(adjustedServerTime),
                        // Prioritize preferred location, then current job, then preferred job, then the rest.
                        LocationPriority = location == preferredLocation ? 0
                            : location.GatheringType.ToGroup() == currentJobType ? 1
                            : location.GatheringType.ToGroup() == GatherBuddy.Config.PreferredGatheringType ? 2
                            : 3,
                        // Bring Shadow Nodes to the end
                        ShadowNode = location is FishingSpot shadowSpot && shadowSpot.IsShadowNode,
                        // Prioritize closest nodes in the current territory.
                        Distance = GetHorizontalSquaredDistanceToPlayer(location),
                    };

                    ref var best = ref CollectionsMarshal.GetValueRefOrAddDefault(bestByItem, item, out var exists);
                    if (!exists || CompareCandidates(ref candidate, ref best) < 0)
                    {
                        best = candidate;
                    }
                }
            }

            var sortLocation = GatherBuddy.Config.AutoGatherConfig.SortingMethod == AutoGatherConfig.SortingType.Location;
            var sortedTargets = new List<(GatherTarget Target, TimeStamp Start, bool Always, int NodeType, int Cost, bool JobChange, float Distance, int Index)>(bestByItem.Count);
            foreach (var candidate in bestByItem.Values)
            {
                var target = new GatherTarget(candidate.Item, candidate.Location, candidate.Time, candidate.Quantity);
                sortedTargets.Add((target,
                    // Put inactive timed nodes to the end, ordered by start time.
                    target.Time.InRange(adjustedServerTime) ? TimeStamp.MinValue : target.Time.Start,
                    // Bring active timed nodes to the front.
                    target.Time == TimeInterval.Always,
                    // Order by node type.
                    sortLocation ? target.Gatherable != null ? GetNodeTypeAsPriority(target.Gatherable) : 9 : 0,
                    // Then by teleportation cost.
                    sortLocation ? target.Location.Territory.Id == territoryId ? 0 : GetTeleportationCost(target.Location) : 0,
                    // Try not to change job within the same territory.
                    sortLocation && target.Location.GatheringType.ToGroup() != currentJobType,
                    // Then by distance to the player (for current territory).
                    sortLocation ? GetHorizontalSquaredDistanceToPlayer(target.Location) : 0f,
                    sortedTargets.Count));
            }

            sortedTargets.Sort(static (lhs, rhs) =>
            {
                var order = lhs.Start.CompareTo(rhs.Start);
                if (order != 0)
                {
                    return order;
                }

                order = lhs.Always.CompareTo(rhs.Always);
                if (order != 0)
                {
                    return order;
                }

                order = lhs.NodeType.CompareTo(rhs.NodeType);
                if (order != 0)
                {
                    return order;
                }

                order = lhs.Cost.CompareTo(rhs.Cost);
                if (order != 0)
                {
                    return order;
                }

                order = lhs.JobChange.CompareTo(rhs.JobChange);
                if (order != 0)
                {
                    return order;
                }

                order = lhs.Distance.CompareTo(rhs.Distance);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });

            var targets = new List<GatherTarget>(sortedTargets.Count);
            foreach (var sorted in sortedTargets)
            {
                targets.Add(sorted.Target);
            }

            _gatherableItems.Clear();

            if (Diadem.IsInside)
            {
                var frontDiadem = true;
                var frontUmbral = false;
                // Bring the current-weather umbral items to the front.
                // Within the leading Diadem items, move umbral items without matching weather to the back.
                var prioritized = new List<(GatherTarget Target, int Priority, int Index)>(targets.Count);
                for (var i = 0; i < targets.Count; ++i)
                {
                    var target = targets[i];
                    prioritized.Add((target, GetDiademPriority(target, ref frontDiadem, ref frontUmbral), i));
                }

                prioritized.Sort(static (lhs, rhs) =>
                {
                    var order = lhs.Priority.CompareTo(rhs.Priority);
                    return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
                });
                for (var i = 0; i < prioritized.Count; ++i)
                {
                    targets[i] = prioritized[i].Target;
                }
            }

            _gatherableItems.AddRange(ApplyFishPriorityOrdering(targets, adjustedServerTime));
            LogFishPriorityOrder(adjustedServerTime);

            GatherBuddy.Log.Verbose($"Gatherable items: ({_gatherableItems.Count}): {string.Join(", ", _gatherableItems.ConvertAll(x => x.Item.Name.ToString()))}.");
        }

        private struct TargetCandidate
        {
            public IGatherable Item;
            public ILocation Location;
            public TimeInterval Time;
            public uint Quantity;
            public bool Inactive;
            public int LocationPriority;
            public bool ShadowNode;
            public float Distance;
            public int? AetheryteOrder;
        }

        private int CompareCandidates(ref TargetCandidate lhs, ref TargetCandidate rhs)
        {
            var order = lhs.Inactive.CompareTo(rhs.Inactive);
            if (order != 0)
            {
                return order;
            }

            order = lhs.LocationPriority.CompareTo(rhs.LocationPriority);
            if (order != 0)
            {
                return order;
            }

            order = lhs.ShadowNode.CompareTo(rhs.ShadowNode);
            if (order != 0)
            {
                return order;
            }

            order = lhs.Distance.CompareTo(rhs.Distance);
            if (order != 0)
            {
                return order;
            }

            order = rhs.Time.End.CompareTo(lhs.Time.End);
            if (order != 0)
            {
                return order;
            }

            lhs.AetheryteOrder ??= GetAetheryteOrder(lhs.Location);
            rhs.AetheryteOrder ??= GetAetheryteOrder(rhs.Location);
            return lhs.AetheryteOrder.Value.CompareTo(rhs.AetheryteOrder.Value);
        }

        private int GetAetheryteOrder(ILocation location)
            => GatherBuddy.Config.AetherytePreference switch
            {
                AetherytePreference.Distance => AutoGather.FindClosestAetheryte(location)
                        ?.WorldDistance(location.Territory.Id, location.IntegralXCoord, location.IntegralYCoord)
                 ?? int.MaxValue,
                AetherytePreference.Cost => GetTeleportationCost(location),
                _ => 0
            };

        private ILocation CorrectForPredatorLocation(IGatherable item, ILocation location)
        {
            if (item is not Fish fish || fish.Predators.Length == 0)
            {
                return location;
            }

            var shadowSpot = fish.FishingSpots.Find(spot => spot.IsShadowNode);
            if (shadowSpot == null)
            {
                return location;
            }

            if (_autoGather.IsSwimmingShadowsAvailable(shadowSpot))
            {
                GatherBuddy.Log.Debug($"[ActiveItemList] Swimming Shadows available for {fish.Name[GatherBuddy.Language]}, using shadow node");
                return shadowSpot;
            }

            if (shadowSpot.ParentNode != null)
            {
                GatherBuddy.Log.Debug($"[ActiveItemList] Swimming Shadows unavailable for {fish.Name[GatherBuddy.Language]}, using parent node");
                return shadowSpot.ParentNode;
            }

            return location;
        }

        private static bool RequiresHomeWorld(ILocation loc)
        {
            if (loc is GatheringNode node)
            {
                return node.NodeType == NodeType.Legendary
                 || node.NodeType == NodeType.Unspoiled
                 || node.Territory == Diadem.Territory;
            }
            else
            {
                return false;
            }
        }

        private static TimeInterval IntersectPredatorUptime(IGatherable item, ILocation location, TimeInterval time, TimeStamp now)
        {
            if (item is not Fish fish || fish.Predators.Length == 0)
            {
                return time;
            }

            var territory = location switch
            {
                FishingSpot spot => spot.Territory,
                _ => Territory.Invalid
            };

            if (territory == Territory.Invalid)
            {
                GatherBuddy.Log.Debug($"[ActiveItemList] Could not determine territory for {fish.Name[GatherBuddy.Language]}");
                return time;
            }

            foreach (var (predatorFish, _) in fish.Predators)
            {
                var predatorUptime = UptimeManager.NextUptime(predatorFish, territory, now);

                if (predatorUptime != TimeInterval.Always)
                {
                    time = time.Overlap(predatorUptime);
                }
            }

            return time == TimeInterval.Never ? TimeInterval.Invalid : time;
        }

        private static TimeInterval IntersectMoochUptime(IGatherable item, ILocation location, TimeInterval time, TimeStamp now)
        {
            if (item is not Fish fish || fish.Mooches.Length == 0)
            {
                return time;
            }

            var territory = location switch
            {
                FishingSpot spot => spot.Territory,
                _ => Territory.Invalid
            };

            if (territory == Territory.Invalid)
            {
                GatherBuddy.Log.Debug($"[ActiveItemList] Could not determine territory for {fish.Name[GatherBuddy.Language]}");
                return time;
            }

            foreach (var moochFish in fish.Mooches)
            {
                var moochUptime = UptimeManager.NextUptime(moochFish, territory, now);

                if (moochUptime != TimeInterval.Always)
                {
                    time = time.Overlap(moochUptime);
                }
            }

            return time == TimeInterval.Never ? TimeInterval.Invalid : time;
        }

        private static float GetHorizontalSquaredDistanceToPlayer(ILocation node)
        {
            if (node.Territory.Id != Dalamud.ClientState.TerritoryType)
            {
                return float.MaxValue;
            }

            var player = Player.Object;
            if (player == null)
            {
                return float.MaxValue;
            }
            // Node coordinates are map coordinates multiplied by 100.
            var playerPos3D = player.GetMapCoordinates();
            var playerPos = new Vector2(playerPos3D.X * 100f, playerPos3D.Y * 100f);
            return Vector2.DistanceSquared(new Vector2(node.IntegralXCoord, node.IntegralYCoord), playerPos);
        }

        /// <summary>
        /// For sorting items in the following order: Legendary, Unspoiled, Ephemeral, Regular.
        /// </summary>
        private static int GetNodeTypeAsPriority(Gatherable item)
        {
            return item.NodeType switch
            {
                NodeType.Legendary => 0,
                NodeType.Unspoiled => 1,
                NodeType.Ephemeral => 2,
                NodeType.Clouded => 3,
                NodeType.Regular => 9,
                _ => 99,
            };
        }

        private int GetTeleportationCost(ILocation location)
        {
            var aetheryte = AutoGather.FindClosestAetheryte(location);
            if (aetheryte == null)
            {
                return int.MaxValue; // If there's no aetheryte, put it at the end
            }

            return _teleportationCosts.GetValueOrDefault(aetheryte.Id, int.MaxValue);
        }

        /// <summary>
        /// Stores teleportation costs in the dictionary.
        /// </summary>
        private unsafe void UpdateTeleportationCosts()
        {
            _teleportationCosts.Clear();

            var telepo = Telepo.Instance();
            if (telepo == null)
            {
                return;
            }

            telepo->UpdateAetheryteList();
            _teleportationCosts.EnsureCapacity(telepo->TeleportList.Count);

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                var entry = telepo->TeleportList[i];
                _teleportationCosts[entry.AetheryteId] = (int)entry.GilCost;
            }
        }

        /// <summary>
        /// Returns true in the following cases:
        /// 1) The active item list has changed.
        /// 2) The Eorzea hour has changed.
        /// 3) The territory has changed.
        /// 4) The player has gathered enough of an item or it can no longer be gathered in the current territory.
        /// </summary>
        /// <returns>
        /// True if an update is needed; otherwise, false.
        /// </returns>
        private bool IsUpdateNeeded()
        {
            var adjustedServerTime = AutoGather.AdjustedServerTime;
            var currentJob = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };

            if (_activeItemsChanged
                || _forceUpdateUnconditionally
                || _lastUpdateTime.TotalEorzeaHours() != adjustedServerTime.TotalEorzeaHours()
                || HasAvailabilityChanged(adjustedServerTime)
                || _lastTerritoryId != Dalamud.ClientState.TerritoryType
                || Diadem.IsInside && _lastWeatherId != EnhancedCurrentWeather.GetCurrentWeatherId()
                || _lastJob != currentJob)
            {
                return true;
            }

            return false;
        }

        private bool HasAvailabilityChanged(TimeStamp adjustedServerTime)
        {
            foreach (var target in _gatherableItems)
            {
                if (target.Time.InRange(_lastUpdateTime) != target.Time.InRange(adjustedServerTime))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Updates the active item list, teleportation costs, and clears expired visited nodes.
        /// </summary>
        private void DoUpdate()
        {
            var territoryId = Dalamud.ClientState.TerritoryType;
            var weatherId = Diadem.IsInside ? EnhancedCurrentWeather.GetCurrentWeatherId() : 0;
            var adjustedServerTime = AutoGather.AdjustedServerTime;
            var eorzeaHour = adjustedServerTime.TotalEorzeaHours();
            var lastTerritoryId = _lastTerritoryId;
            var lastWeatherId = _lastWeatherId;
            var lastEorzeaHour = _lastUpdateTime.TotalEorzeaHours();
            var currentJob = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };

            _activeItemsChanged = false;
            _forceUpdateUnconditionally = false;
            _lastUpdateTime = adjustedServerTime;
            _lastTerritoryId = territoryId;
            _lastWeatherId = weatherId;
            _lastJob = currentJob;

            if (territoryId != lastTerritoryId)
            {
                UpdateTeleportationCosts();
            }

            if (lastWeatherId != weatherId)
            {
                _consumedCloudedNode = false;
            }

            if (eorzeaHour != lastEorzeaHour)
            {
                RemoveExpiredVisited(adjustedServerTime);
            }

            UpdateItemsToGather();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        internal void Reset()
        {
            _lastTerritoryId = 0;
            _lastWeatherId = 0;
            _lastUpdateTime = TimeStamp.MinValue;
            _gatherableItems.Clear();
            _gatherableItems.TrimExcess();
            _teleportationCosts.Clear();
            _teleportationCosts.TrimExcess();
        }

        private int GetDiademPriority(in GatherTarget target, ref bool frontDiadem, ref bool frontUmbral)
        {
            // Priority (lower values come first):
            // 0 = Umbral items that match the current weather (rush to the node).
            // 1 = Diadem regular item within the leading Diadem block (gather while waiting for weather).
            // 2 = Trailing Diadem regular items, if there were umbral items in front (gather while waiting for weather).
            // 3 = Umbral items that do NOT match the current weather, within the leading Diadem block (wait for weather).
            // 4 = All other trailing non-Diadem items and everything after that (leave The Diadem).

            if (target.Location.Territory != Diadem.Territory)
            {
                // Non-Diadem item.
                frontDiadem = false;
                return 4;
            }

            var requiredWeather = target.Item.UmbralWeather.Id; // 0 if not umbral (Weather.Invalid)

            if (requiredWeather != 0)
            {
                frontUmbral |= frontDiadem;
                return _lastWeatherId == requiredWeather && !(_consumedCloudedNode && target.Node != null) ? 0 : (frontDiadem ? 3 : 4);
            }

            return frontDiadem ? 1 : (frontUmbral ? 2 : 4);
        }

        public void Dispose()
        {
            _listsManager?.ActiveItemsChanged -= OnActiveItemsChanged;
        }
    }



    public readonly record struct GatherTarget(IGatherable Item, ILocation Location, TimeInterval Time, uint Quantity)
    {
        public GatheringNode? Node
            => Location as GatheringNode;

        public Gatherable? Gatherable
            => Item as Gatherable;

        public FishingSpot? FishingSpot
            => Location as FishingSpot;

        public Fish? Fish
            => Item as Fish;
    }
}
