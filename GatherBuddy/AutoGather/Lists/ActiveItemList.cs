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
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using EnhancedCurrentWeather = GatherBuddy.SeFunctions.EnhancedCurrentWeather;
namespace GatherBuddy.AutoGather.Lists
{
    internal sealed class ActiveItemList : IEnumerable<GatherTarget>, IDisposable
    {
        private readonly List<GatherTarget>                      _gatherableItems    = [];
        private readonly AutoGatherListsManager                  _listsManager;
        private readonly AutoGather                              _autoGather;
        private readonly Dictionary<uint, int>                   _teleportationCosts = [];
        private readonly Dictionary<GatheringNode, TimeInterval> _visitedTimedNodes  = [];
        private          TimeStamp                               _lastUpdateTime     = TimeStamp.MinValue;
        private          uint                                    _lastTerritoryId;
        private          int                                     _lastWeatherId;
        private          bool                                    _activeItemsChanged;
        private          bool                                    _consumedCloudedNode;
        private          bool                                    _forceUpdateUnconditionally;
        private          GatheringType                           _lastJob            = GatheringType.Unknown;
        private          GatherTarget                            _currentItem;

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
            => _gatherableItems.Any(NeedsGathering);

        public bool IsCloudedNodeConsumed
            => _consumedCloudedNode;

        public bool IsInitialized
            => _lastUpdateTime != TimeStamp.MinValue;

        public ActiveItemList(AutoGatherListsManager listsManager, AutoGather autoGather)
        {
            _listsManager                    =  listsManager;
            _autoGather                      =  autoGather;
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
            return _gatherableItems.Where(NeedsGathering).GetEnumerator();
        }

        /// <summary>
        /// Refreshes the list of items to gather (if needed) and returns the first item.
        /// </summary>
        /// <returns>The next item to gather. </returns>
        public GatherTarget GetNextOrDefault()
        {
            if (IsUpdateNeeded())
                DoUpdate();

            return _currentItem = _gatherableItems
                .FirstOrDefault(x => x.Time.InRange(_lastUpdateTime) && NeedsGathering(x));
        }

        /// <summary>
        /// Returns next timed item that is not up yet
        /// </summary>
        /// <returns></returns>
        public GatherTarget PeekNextTimed()
        {
            return _gatherableItems
                .FirstOrDefault(x => !x.Time.InRange(_lastUpdateTime) && x.Time != TimeInterval.Invalid);
        }

        /// <summary>
        /// Marks a node as visited.
        /// </summary>
        /// <param name="target">The game object of the node to mark as visited.</param>
        public void MarkVisited(IGameObject target)
        {
            // In almost all cases, the target is the first item in the list, so it's O(1).
            var (_, loc, time, _) = _gatherableItems.FirstOrDefault(x => x.Node?.WorldPositions.ContainsKey(target.BaseId) ?? false);
            var node = loc as GatheringNode;

            // Could happen with manual navigation if gathered node isn't on the list.
            if (node == null)
            {
                node = GatherBuddy.GameData.GatheringNodes.Values.FirstOrDefault(n => n.WorldPositions.ContainsKey(target.BaseId));
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
                _visitedTimedNodes[x.Node] = x.Time;
            if (x.Node?.NodeType == NodeType.Clouded)
                _consumedCloudedNode = EnhancedCurrentWeather.GetCurrentWeatherId() == x.Node.UmbralWeather.Id;
        }

        private bool NeedsGathering((IGatherable item, uint quantity) value)
        {
            var (item, quantity) = value;
            return item.GetTotalCount() < quantity && CheckOvercap(item);
        }

        private bool NeedsGathering(GatherTarget target)
            => NeedsGathering((target.Item, target.Quantity));

        private static bool CheckOvercap(IGatherable item)
        {
            if (item.IsTreasureMap)
                return item.GetInventoryCount() < 1;
            if (item.IsCrystal)
                return item.GetInventoryCount() < 9999;
            return true;
        }

        private void OnActiveItemsChanged()
        {
            _activeItemsChanged = true;
        }

        private void RemoveExpiredVisited(TimeStamp adjustedServerTime)
        {
            foreach (var (loc, time) in _visitedTimedNodes)
                if (time.End <= adjustedServerTime)
                    _visitedTimedNodes.Remove(loc);
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

            var targets = _listsManager.ActiveItems
                // Filter out items that are already gathered.
                .Where(NeedsGathering)
                // Fetch preferred location.
                .Select(x => (x.Item, x.Quantity, PreferredLocation: _listsManager.GetPreferredLocation(x.Item)))
                // Flatten node list and calculate the next uptime.
                .SelectMany(x => x.Item.Locations.Select(Location
                    => (x.Item, Location, Time: Location switch
                    {
                        GatheringNode node => node.Times.NextUptime(adjustedServerTime),
                        FishingSpot spot => GatherBuddy.UptimeManager.NextUptime((x.Item as Fish)!, spot.Territory, adjustedServerTime),
                        _ => throw new InvalidOperationException()
                    }, x.Quantity, x.PreferredLocation)))
                // If treasure map, only gather if the allowance is up.
                .Select(x => x.Item.IsTreasureMap && (nextAllowance ??= DiscipleOfLand.NextTreasureMapAllowance) > adjustedServerTime.DateTime ? x with { Time = TimeInterval.Invalid } : x)
                // Remove nodes that require the player to be on the home world.
                .Where(x => !RequiresHomeWorld(x.Location) || Functions.OnHomeWorld())
                // Remove nodes with a level higher than the player can gather.
                .Where(x => x.Location.GatheringType.ToGroup() switch
                {
                    GatheringType.Miner => (x.Location as GatheringNode)!.Level <= minerLevel,
                    GatheringType.Botanist => (x.Location as GatheringNode)!.Level <= botanistLevel,
                    _ => true
                })
                // Apply predators and mooch dependencies time restrictions.
                .Select(x => x with { Time = IntersectMoochUptime(x.Item, x.Location, x.Time, adjustedServerTime) })
                .Select(x => x with { Location = CorrectForPredatorLocation(x.Item, x.Location) })
                .Select(x => x with { Time = IntersectPredatorUptime(x.Item, x.Location, x.Time, adjustedServerTime) })
                // Remove uptime for nodes that have already been gathered.
                .Select(x => x.Location is GatheringNode node && _visitedTimedNodes.ContainsKey(node) ? x with { Time = TimeInterval.Invalid } : x)
                // Group by item and select the best node.
                .GroupBy(x => x.Item, x => x, (_, g) => g
                    // Prioritize active nodes
                    .OrderBy(x => !x.Time.InRange(adjustedServerTime))
                    // Prioritize preferred location, then current job, then preferred job, then the rest.
                    .ThenBy(x =>
                        x.Location == x.PreferredLocation ? 0
                        : x.Location.GatheringType.ToGroup() == Player.Job switch
                        {
                            16 /* MIN */ => GatheringType.Miner,
                            17 /* BTN */ => GatheringType.Botanist,
                            18 /* FSH */ => GatheringType.Fisher,
                            _ => GatheringType.Unknown
                        } ? 1
                        : x.Location.GatheringType.ToGroup() == GatherBuddy.Config.PreferredGatheringType ? 2
                        : 3)
                    // Bring Shadow Nodes to the end
                    .ThenBy(x => x.Location is FishingSpot spot && spot.IsShadowNode)
                    // Prioritize closest nodes in the current territory.
                    .ThenBy(x => GetHorizontalSquaredDistanceToPlayer(x.Location))
                    // Order by end time, longest first as in the original UptimeManager.NextUptime().
                    .ThenByDescending(x => x.Time.End)
                    .ThenBy(x => GatherBuddy.Config.AetherytePreference switch
                    {
                        // Order by distance to the closest aetheryte.
                        AetherytePreference.Distance => AutoGather.FindClosestAetheryte(x.Location)
                                ?.WorldDistance(x.Location.Territory.Id, x.Location.IntegralXCoord, x.Location.IntegralYCoord)
                         ?? int.MaxValue,
                        // Order by teleportation cost.
                        AetherytePreference.Cost => GetTeleportationCost(x.Location),
                        _ => 0
                    })
                    .First()
                )
                .Select(x => new GatherTarget(x.Item, x.Location, x.Time, x.Quantity))
                // Put inactive timed nodes to the end, ordered by start time.
                .OrderBy(x => x.Time.InRange(adjustedServerTime) ? TimeStamp.MinValue : x.Time.Start)
                // Bring active timed nodes to the front.
                .ThenBy(x => x.Time == TimeInterval.Always);

            if (GatherBuddy.Config.AutoGatherConfig.SortingMethod == AutoGatherConfig.SortingType.Location)
            {
                targets = targets
                    // Order by node type.
                    .ThenBy(x => x.Gatherable != null ? GetNodeTypeAsPriority(x.Gatherable) : 9)
                    // Then by teleportation cost.
                    .ThenBy(x => x.Location.Territory.Id == territoryId ? 0 : GetTeleportationCost(x.Location))
                    // Try not to change job within the same territory.
                    .ThenBy(x => x.Location.GatheringType.ToGroup() != Player.Job switch
                    {
                        16 /* MIN */ => GatheringType.Miner,
                        17 /* BTN */ => GatheringType.Botanist,
                        18 /* FSH */ => GatheringType.Fisher,
                        _ => GatheringType.Unknown
                    })
                    // Then by distance to the player (for current territory).
                    .ThenBy(x => GetHorizontalSquaredDistanceToPlayer(x.Location));
            }

            _gatherableItems.Clear();

            if (Diadem.IsInside)
            {
                var frontDiadem = true;
                var frontUmbral = false;
                // Bring the current-weather umbral items to the front.
                // Within the leading Diadem items, move umbral items without matching weather to the back.
                var targetsDiadem = targets
                    .Select(x => (Target: x, Priority: GetDiademPriority(x, ref frontDiadem, ref frontUmbral)))
                    .OrderBy(x => x.Priority)
                    .Select(x => x.Target);
                _gatherableItems.AddRange(targetsDiadem);
            }
            else
            {
                _gatherableItems.AddRange(targets);
            }

            GatherBuddy.Log.Verbose($"Gatherable items: ({_gatherableItems.Count}): {string.Join(", ", _gatherableItems.Select(x => x.Item.Name))}.");
        }

        private ILocation CorrectForPredatorLocation(IGatherable item, ILocation location)
        {
            if (item is not Fish fish)
                return location;

            // Check if THIS SPECIFIC FISH has predator requirements (not the whole shadow node)
            if (fish.Predators.Length != 0)
            {
                // Only check FIRST predator for shadow node spawning (rest are caught within shadow node)
                var (firstPredator, requiredCount) = fish.Predators[0];
                var caughtCount = _autoGather.SpearfishingSessionCatches.GetValueOrDefault(firstPredator.ItemId, 0);
                var firstPredatorMet = caughtCount >= requiredCount;

                var shadowSpot = fish.FishingSpots.FirstOrDefault(fs => fs.IsShadowNode);
                if (shadowSpot != null)
                {
                    if (firstPredatorMet)
                    {
                        // First predator met - shadow node spawns, use it
                        location = shadowSpot;
                        GatherBuddy.Log.Debug($"[ActiveItemList] First predator met for {fish.Name[GatherBuddy.Language]}, using shadow node");
                    }
                    else if (shadowSpot.ParentNode != null)
                    {
                        location = shadowSpot.ParentNode;
                        GatherBuddy.Log.Debug($"[ActiveItemList] First predator not met for {fish.Name[GatherBuddy.Language]}, using parent node");
                    }
                }
            }
            // Fallback: if preferred location is a shadow node, check its requirements
            else if (location is FishingSpot spot && spot.IsShadowNode && spot.ParentNode != null)
            {
                if (!_autoGather.AreSpawnRequirementsMet(spot))
                {
                    location = spot.ParentNode;
                }
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
                return time;

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
                var predatorUptime = GatherBuddy.UptimeManager.NextUptime(predatorFish, territory, now);

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
                return time;

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
                var moochUptime = GatherBuddy.UptimeManager.NextUptime(moochFish, territory, now);

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
                return float.MaxValue;

            var player = Player.Object;
            if (player == null) return float.MaxValue;
            // Node coordinates are map coordinates multiplied by 100.
            var playerPos3D = player.GetMapCoordinates();
            var playerPos   = new Vector2(playerPos3D.X * 100f,             playerPos3D.Y * 100f);
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
                NodeType.Clouded   => 3,
                NodeType.Regular   => 9,
                _                  => 99,
            };
        }

        private int GetTeleportationCost(ILocation location)
        {
            var aetheryte = AutoGather.FindClosestAetheryte(location);
            if (aetheryte == null)
                return int.MaxValue; // If there's no aetheryte, put it at the end

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
                return;

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
            var currentJob = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };
            
            if (_activeItemsChanged
                || _forceUpdateUnconditionally
                || _lastUpdateTime.TotalEorzeaHours() != AutoGather.AdjustedServerTime.TotalEorzeaHours()
                || _lastTerritoryId != Dalamud.ClientState.TerritoryType
                || Diadem.IsInside && _lastWeatherId != EnhancedCurrentWeather.GetCurrentWeatherId()
                || _lastJob != currentJob)
                return true;

            return false;
        }

        /// <summary>
        /// Updates the active item list, teleportation costs, and clears expired visited nodes.
        /// </summary>
        private void DoUpdate()
        {
            var territoryId        = Dalamud.ClientState.TerritoryType;
            var weatherId          = Diadem.IsInside ? EnhancedCurrentWeather.GetCurrentWeatherId() : 0;
            var adjustedServerTime = AutoGather.AdjustedServerTime;
            var eorzeaHour         = adjustedServerTime.TotalEorzeaHours();
            var lastTerritoryId    = _lastTerritoryId;
            var lastWeatherId      = _lastWeatherId;
            var lastEorzeaHour     = _lastUpdateTime.TotalEorzeaHours();
            var currentJob = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };

            _activeItemsChanged = false;
            _forceUpdateUnconditionally = false;
            _lastUpdateTime     = adjustedServerTime;
            _lastTerritoryId    = territoryId;
            _lastWeatherId      = weatherId;
            _lastJob            = currentJob;

            if (territoryId != lastTerritoryId)
                UpdateTeleportationCosts();

            if (lastWeatherId != weatherId)
                _consumedCloudedNode = false;

            if (eorzeaHour != lastEorzeaHour)
                RemoveExpiredVisited(adjustedServerTime);

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
            if (_listsManager != null)
            {
                _listsManager.ActiveItemsChanged -= OnActiveItemsChanged;
            }
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
