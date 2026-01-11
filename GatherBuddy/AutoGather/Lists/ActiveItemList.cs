using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Utility;
using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Time;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using UmbralNodes = GatherBuddy.Data.UmbralNodes;
using EnhancedCurrentWeather = GatherBuddy.SeFunctions.EnhancedCurrentWeather;
namespace GatherBuddy.AutoGather.Lists
{
    internal class ActiveItemList : IEnumerable<GatherTarget>, IDisposable
    {
        private readonly List<GatherTarget>                      _gatherableItems = [];
        private readonly AutoGatherListsManager                  _listsManager;
        private readonly AutoGather                              _autoGather;
        private readonly Dictionary<uint, int>                   _teleportationCosts = [];
        private readonly Dictionary<GatheringNode, TimeInterval> _visitedTimedNodes  = [];
        private          TimeStamp                               _lastUpdateTime     = TimeStamp.MinValue;
        private          uint                                    _lastTerritoryId;
        private          bool                                    _activeItemsChanged;
        private          bool                                    _gatheredSomething;
        private          bool                                    _forceUpdateUnconditionally;
        private          GatheringType                           _lastJob            = GatheringType.Unknown;

        internal ReadOnlyDictionary<GatheringNode, TimeInterval> DebugVisitedTimedLocations
            => _visitedTimedNodes.AsReadOnly();

        /// <summary>
        /// First item on the list as of the last enumeration or default.
        /// </summary>
        public GatherTarget CurrentOrDefault
            => _gatherableItems.FirstOrDefault();

        /// <summary>
        /// Determines whether there are any items that need to be gathered,
        /// including items that are not up yet.
        /// </summary>
        /// <value>
        /// True if there are items that need to be gathered; otherwise, false.
        /// </value>
        public bool HasItemsToGather
            => _listsManager.ActiveItems.Where(NeedsGathering).Any();

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
            return _gatherableItems.GetEnumerator();
        }

        /// <summary>
        /// Refreshes the list of items to gather (if needed) and returns the first item.
        /// </summary>
        /// <returns>The next item to gather. </returns>
        public IEnumerable<GatherTarget> GetNextOrDefault(IEnumerable<uint> nearbyNodes)
        {
            if (IsUpdateNeeded())
                DoUpdate();

            //GatherBuddy.Log.Verbose($"Nearby nodes: {string.Join(", ", nearbyNodes.Select(x => x.ToString("X8")))}.");
            
            GatherTarget firstItemNeedingGathering;
            if (Plugin.Functions.InTheDiadem())
            {
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
                
                if (isUmbralWeather)
                {
                    var umbralWeatherType = (UmbralNodes.UmbralWeatherType)currentWeather;
                    var currentJob = Player.Job switch
                    {
                        16 /* MIN */ => GatheringType.Miner,
                        17 /* BTN */ => GatheringType.Botanist,
                        _ => GatheringType.Unknown
                    };
                    
                    var priorityUmbralItem = _gatherableItems
                        .Where(NeedsGathering)
                        .Where(target => target.Gatherable != null && UmbralNodes.IsUmbralItem(target.Gatherable.ItemId))
                        .FirstOrDefault(target => 
                        {
                            var umbralInfo = UmbralNodes.GetUmbralItemInfo(target.Gatherable.ItemId);
                            return umbralInfo.HasValue && 
                                   umbralInfo.Value.Weather == umbralWeatherType;
                        });
                    
                    if (priorityUmbralItem != default)
                    {
                        firstItemNeedingGathering = priorityUmbralItem;
                    }
                    else
                    {
                        firstItemNeedingGathering = _gatherableItems
                            .Where(NeedsGathering)
                            .FirstOrDefault(target => target.Gatherable == null || !UmbralNodes.IsUmbralItem(target.Gatherable.ItemId));
                    }
                }
                else
                {
                    firstItemNeedingGathering = _gatherableItems.FirstOrDefault(NeedsGathering);
                }
            }
            else
            {
                firstItemNeedingGathering = _gatherableItems.FirstOrDefault(NeedsGathering);
            }
            
            if (firstItemNeedingGathering == default)
                return [];
            
            IEnumerable<GatherTarget> nearbyItems = [];
            
            if (this.Any(n => n.Time != TimeInterval.Always))
            {
                nearbyItems = [this.First(n => n.Time.InRange(AutoGather.AdjustedServerTime))];
            }
            else
            {
                var isUmbralItem = firstItemNeedingGathering.Gatherable != null && 
                                  UmbralNodes.IsUmbralItem(firstItemNeedingGathering.Gatherable.ItemId);
                                  
                if (isUmbralItem && Plugin.Functions.InTheDiadem())
                {
                    var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                    var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
                    
                    if (isUmbralWeather)
                    {
                        
                        var currentJob = Player.Job switch
                        {
                            16 /* MIN */ => GatheringType.Miner,
                            17 /* BTN */ => GatheringType.Botanist,
                            _ => GatheringType.Unknown
                        };
                        var umbralWeather = (UmbralNodes.UmbralWeatherType)currentWeather;
                        
                        nearbyItems = _gatherableItems
                            .Where(target => target.Gatherable != null && UmbralNodes.IsUmbralItem(target.Gatherable.ItemId))
                            .Where(target => 
                            {
                                var umbralInfo = UmbralNodes.GetUmbralItemInfo(target.Gatherable.ItemId);
                                return umbralInfo.HasValue && 
                                       umbralInfo.Value.Weather == umbralWeather &&
                                       UmbralNodes.GetGatheringType(umbralInfo.Value.NodeType) == currentJob;
                            })
                            .Where(NeedsGathering);
                    }
                    else
                    {
                        nearbyItems = [];
                    }
                }
                else
                {
                    nearbyItems = this
                        .Where(i => i.Item == firstItemNeedingGathering.Item)
                        .Where(i => i.Node?.WorldPositions.Keys.Any(nearbyNodes.Contains) ?? false);
                }
                    
            }

            var result = nearbyItems.Any() ? nearbyItems : [firstItemNeedingGathering];
            return result;
        }

        /// <summary>
        /// Marks a node as visited.
        /// </summary>
        /// <param name="info">The GatherTarget containing the node to mark as visited.</param>
        public void MarkVisited(IGameObject target)
        {
            _gatheredSomething = true;
            // In almost all cases, the target is the first item in the list, so it's O(1).
            var x = _gatherableItems.FirstOrDefault(x => 
                (x.Node?.WorldPositions.ContainsKey(target.BaseId) ?? false) ||
                (x.FishingSpot?.WorldPositions.ContainsKey(target.BaseId) ?? false));
            if (x != default && x.Time != TimeInterval.Always && x.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled)
                _visitedTimedNodes[x.Node] = x.Time;
        }

        internal void DebugMarkVisited(GatherTarget x)
        {
            _gatheredSomething = true;
            if (x.Time != TimeInterval.Always && x.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled)
                _visitedTimedNodes[x.Node] = x.Time;
        }

        private bool NeedsGathering((IGatherable item, uint quantity) value)
        {
            var (item, quantity) = value;
            return item.GetInventoryCount() < (item.IsTreasureMap ? 1 : quantity);
        }

        private bool NeedsGathering(GatherTarget target)
        {
            return target.Item.GetInventoryCount() < target.Quantity;
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
            DateTime? nextAllowance = null;

            var targets = _listsManager.ActiveItems
                // Filter out items that are already gathered.
                .Where(NeedsGathering)
                .Where(x => RequiresHomeWorld(x) && Functions.OnHomeWorld() || !RequiresHomeWorld(x))
                // If treasure map, only gather if the allowance is up.
                .Where(x => !x.Item.IsTreasureMap || (nextAllowance ??= DiscipleOfLand.NextTreasureMapAllowance) < adjustedServerTime.DateTime)
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
                // Remove nodes with a level higher than the player can gather.
                .Where(x => x.Location.GatheringType.ToGroup() switch
                {
                    GatheringType.Miner => (x.Location as GatheringNode)!.Level <= minerLevel,
                    GatheringType.Botanist => (x.Location as GatheringNode)!.Level <= botanistLevel,
                    _ => true
                })
                // Apply predators and mooch dependencies time restrictions
                .Select(x => x with { Time = IntersectMoochUptime(x.Item, x.Location, x.Time, adjustedServerTime) })
                .Select(x => x with { Time = IntersectPredatorUptime(x.Item, x.Location, x.Time, adjustedServerTime) })
                .Select(x => x with { Location = CorrectForPredatorLocation(x.Item, x.Location) })
                // Remove nodes that are not up.
                .Where(x => x.Time.InRange(adjustedServerTime))
                // Remove nodes that are already gathered.
                .Where(x => x.Location is not GatheringNode node || !_visitedTimedNodes.ContainsKey(node))
                // Group by item and select the best node.
                .GroupBy(x => x.Item, x => x, (_, g) => g
                    // Prioritize preferred location, then current job, then preferred job, then the rest.
                    .OrderBy(x =>
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
                // Prioritize timed nodes first.
                .OrderBy(x => x.Time == TimeInterval.Always);

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
            
            _gatherableItems.AddRange(targets);

            AddUmbralItemsIfAvailable(adjustedServerTime, minerLevel, botanistLevel);
            
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
                var (firstPredator, requiredCount) = fish.Predators.First();
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
                        // First predator not met - use parent node to gather it
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

        private static bool RequiresHomeWorld((IGatherable Item, uint Quantity) valueTuple)
        {
            if (valueTuple.Item is Gatherable item)
            {
                return item.NodeType == NodeType.Legendary
                 || item.NodeType == NodeType.Unspoiled
                 || item.NodeList.Any(nl => nl.Territory.Id is 901 or 929 or 939); // The Diadem
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

            return time;
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

            return time;
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
        /// Checks if the active item list should be updated while fishing.
        /// This is used to detect when timed/weather fish become available.
        /// </summary>
        /// <returns>
        /// True if the list should update (e.g., hour changed, active items changed); otherwise, false.
        /// </returns>
        public bool ShouldUpdateWhileFishing()
        {
            return _activeItemsChanged
                || _gatheredSomething
                || _lastUpdateTime.TotalEorzeaHours() != AutoGather.AdjustedServerTime.TotalEorzeaHours();
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
            
            if (_forceUpdateUnconditionally)
                return true;
            
            if (_activeItemsChanged
             || _lastUpdateTime.TotalEorzeaHours() != AutoGather.AdjustedServerTime.TotalEorzeaHours()
             || _lastTerritoryId != Dalamud.ClientState.TerritoryType
             || _lastJob != currentJob)
                return true;

            if (_gatheredSomething)
            {
                _gatheredSomething = false;
                var current = CurrentOrDefault;
                foreach (var item in _gatherableItems.Where(NeedsGathering).Where(x => x.Node == null || !_visitedTimedNodes.ContainsKey(x.Node)))
                {
                    if (item == current)
                        return false;

                    if (item.Node != null && item.Node.Territory.Id != _lastTerritoryId)
                        break;
                    if (item.FishingSpot != null && item.FishingSpot.Territory.Id != _lastTerritoryId)
                        break;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Updates the active item list, teleportation costs, and clears expired visited nodes.
        /// </summary>
        private void DoUpdate()
        {
            var territoryId        = Dalamud.ClientState.TerritoryType;
            var adjustedServerTime = AutoGather.AdjustedServerTime;
            var eorzeaHour         = adjustedServerTime.TotalEorzeaHours();
            var lastTerritoryId    = _lastTerritoryId;
            var lastEorzeaHour     = _lastUpdateTime.TotalEorzeaHours();
            var currentJob = Player.Job switch
            {
                16 /* MIN */ => GatheringType.Miner,
                17 /* BTN */ => GatheringType.Botanist,
                18 /* FSH */ => GatheringType.Fisher,
                _ => GatheringType.Unknown
            };

            _activeItemsChanged = false;
            _gatheredSomething  = false;
            _forceUpdateUnconditionally = false;
            _lastUpdateTime     = adjustedServerTime;
            _lastTerritoryId    = territoryId;
            _lastJob            = currentJob;

            if (territoryId != lastTerritoryId)
                UpdateTeleportationCosts();

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
            _lastUpdateTime  = TimeStamp.MinValue;
            _gatherableItems.Clear();
            _gatherableItems.TrimExcess();
            _teleportationCosts.Clear();
            _teleportationCosts.TrimExcess();
        }

        private void AddUmbralItemsIfAvailable(TimeStamp adjustedServerTime, int minerLevel, int botanistLevel)
        {
            var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
            var isInDiadem = Plugin.Functions.InTheDiadem();
            var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
            
            if (!isUmbralWeather || !isInDiadem)
                return;
                
            var umbralWeatherType = (UmbralNodes.UmbralWeatherType)currentWeather;
            
            var umbralItemsToGather = _listsManager.ActiveItems
                .Where(x => x.Item is Gatherable)
                .Where(NeedsGathering)
                .Select(x => (Item: (x.Item as Gatherable)!, x.Quantity))
                .Where(x => UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(x.Item.ItemId)))
                .Where(x => (RequiresHomeWorld(x) && Plugin.Functions.OnHomeWorld()) || !RequiresHomeWorld(x))
                .ToList();
                
            if (!umbralItemsToGather.Any())
                return;
                
            foreach (var itemEntry in umbralItemsToGather)
            {
                var item = itemEntry.Item;
                var quantity = itemEntry.Quantity;
                
                var umbralNodeEntry = UmbralNodes.UmbralNodeData.FirstOrDefault(entry => 
                    entry.ItemIds.Contains(item.ItemId));
                    
                if (umbralNodeEntry.NodeId == 0)
                    continue;
                    
                var itemGatheringType = item.GatheringType.ToGroup();
                
                var umbralGatheringType = umbralNodeEntry.NodeType switch
                {
                    UmbralNodes.CloudedNodeType.CloudedRockyOutcrop => GatheringType.Miner,
                    UmbralNodes.CloudedNodeType.CloudedMineralDeposit => GatheringType.Miner,
                    UmbralNodes.CloudedNodeType.CloudedMatureTree => GatheringType.Botanist,
                    UmbralNodes.CloudedNodeType.CloudedLushVegetation => GatheringType.Botanist,
                    _ => itemGatheringType
                };
                
                if (umbralNodeEntry.Weather != umbralWeatherType)
                    continue;
                
                var requiredLevel = item.Level;
                var playerLevel = umbralGatheringType switch
                {
                    GatheringType.Miner => minerLevel,
                    GatheringType.Botanist => botanistLevel,
                    _ => 0
                };
                
                if (requiredLevel > playerLevel)
                    continue;
                    
                var currentTerritoryId = Dalamud.ClientState.TerritoryType;
                var templateNode = GatherBuddy.GameData.GatheringNodes.Values
                    .Where(node => node.Territory.Id is 901 or 929 or 939 && 
                        node.GatheringType.ToGroup() == umbralGatheringType)
                    .OrderBy(node => node.Territory.Id == currentTerritoryId ? 0 : 1)
                    .FirstOrDefault();
                        
                if (templateNode != null)
                {
                    var gatherTarget = new GatherTarget(item, templateNode, TimeInterval.Always, quantity);
                    _gatherableItems.Add(gatherTarget);
                }
            }
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
