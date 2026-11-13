using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using GatherBuddy.Classes;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Levenshtein;
using GatherBuddy.Structs;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using Aetheryte = GatherBuddy.Classes.Aetheryte;
using AetheryteRow = Lumina.Excel.Sheets.Aetheryte;
using Fish = GatherBuddy.Classes.Fish;
using FishingSpot = GatherBuddy.Classes.FishingSpot;
using FishingSpotRow = Lumina.Excel.Sheets.FishingSpot;
using Weather = GatherBuddy.Structs.Weather;
using WeatherRow = Lumina.Excel.Sheets.Weather;

namespace GatherBuddy;

public class GameData
{
    public readonly string OverrideFile;
    internal IDataManager DataManager { get; }
    internal FrozenDictionary<byte, CumulativeWeatherRates> CumulativeWeatherRates = FrozenDictionary<byte, CumulativeWeatherRates>.Empty;

    public readonly Logger Log;

    public FrozenDictionary<uint, Weather> Weathers { get; } = FrozenDictionary<uint, Weather>.Empty;
    public Territory[] WeatherTerritories { get; } = [];

    public Dictionary<uint, Territory> Territories { get; } = [];
    public FrozenDictionary<uint, Aetheryte> Aetherytes { get; } = FrozenDictionary<uint, Aetheryte>.Empty;
    public FrozenDictionary<uint, Gatherable> Gatherables { get; } = FrozenDictionary<uint, Gatherable>.Empty;
    public FrozenDictionary<uint, Gatherable> GatherablesByGatherId { get; } = FrozenDictionary<uint, Gatherable>.Empty;
    public FrozenDictionary<uint, GatheringNode> GatheringNodes { get; } = FrozenDictionary<uint, GatheringNode>.Empty;
    public FrozenDictionary<uint, Bait> Bait { get; } = FrozenDictionary<uint, Bait>.Empty;
    public FrozenDictionary<uint, Fish> Fishes { get; } = FrozenDictionary<uint, Fish>.Empty;
    public FrozenDictionary<uint, FishingSpot> FishingSpots { get; } = FrozenDictionary<uint, FishingSpot>.Empty;
    public FrozenDictionary<ushort, CosmicMission> CosmicFishingMissions { get; } = FrozenDictionary<ushort, CosmicMission>.Empty;
    public Dictionary<uint, List<Vector3>> WorldCoords { get; init; } = [];

    public IReadOnlyList<OceanRoute> OceanRoutes { get; } = [];
    public OceanTimeline OceanTimeline { get; } = null!;

    public PatriciaTrie<Gatherable> GatherablesTrie { get; } = [];
    public PatriciaTrie<Fish> FishTrie { get; } = [];

    public GatheringIcons GatheringIcons { get; } = null!;

    public int TimedGatherables { get; }
    public int MultiNodeGatherables { get; }
    public int OverriddenFish { get; private set; }

    public (IGatherable? Item, ILocation? Location) GetConfig(ObjectType type, uint itemId, uint locationId)
        => type switch
        {
            ObjectType.Gatherable => (itemId == 0 ? null : Gatherables.GetValueOrDefault(itemId),
                locationId == 0 ? null : GatheringNodes.GetValueOrDefault(locationId)),
            ObjectType.Fish => (itemId == 0 ? null : Fishes.GetValueOrDefault(itemId),
                locationId == 0 ? null : FishingSpots.GetValueOrDefault(locationId)),
            _ => (null, null),
        };

    public bool ReimportOverrides()
    {
        if (!this.ApplyOverrides())
        {
            return false;
        }

        OverriddenFish = CountOverriddenFish();
        return true;
    }

    private int CountOverriddenFish()
    {
        var count = 0;
        foreach (var fish in Fishes.Values)
        {
            if (fish.HasOverridenData)
            {
                ++count;
            }
        }

        return count;
    }

    public GameData(IDataManager gameData, Logger log, Dictionary<uint, List<Vector3>> worldCoordsDict, string overrideFile)
    {
        Log = log;
        DataManager = gameData;
        WorldCoords = worldCoordsDict;
        OverrideFile = overrideFile;
        try
        {
            GatheringIcons = new GatheringIcons(gameData);

            Weathers = DataManager.GetExcelSheet<WeatherRow>()
                .ToFrozenDictionary(w => w.RowId, w => new Weather(w));
            Log.Verbose("Collected {NumWeathers} different Weathers.", Weathers.Count);

            CumulativeWeatherRates = DataManager.GetExcelSheet<WeatherRate>()
                .ToFrozenDictionary(w => (byte)w.RowId, w => new CumulativeWeatherRates(this, w));

            var weatherTerritories = new List<Territory>();
            var weatherTerritoryNames = new HashSet<string>();
            foreach (var territoryRow in DataManager.GetExcelSheet<TerritoryType>())
            {
                if (territoryRow.PCSearch
                 && territoryRow.WeatherRate.RowId != 0
                 && FindOrAddTerritory(territoryRow) is { WeatherRates.Rates.Length: > 1 } weatherTerritory
                 && weatherTerritoryNames.Add(weatherTerritory.Name))
                {
                    weatherTerritories.Add(weatherTerritory);
                }
            }

            weatherTerritories.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            WeatherTerritories = [.. weatherTerritories];
            Log.Verbose("Collected {NumWeatherTerritories} different territories with dynamic weather.", WeatherTerritories.Length);

            var aetherytes = new Dictionary<uint, Aetheryte>();
            foreach (var aetheryteRow in DataManager.GetExcelSheet<AetheryteRow>())
            {
                if (aetheryteRow is { IsAetheryte: true, RowId: > 1 } && aetheryteRow.PlaceName.RowId != 0)
                {
                    aetherytes.Add(aetheryteRow.RowId, new Aetheryte(this, aetheryteRow));
                }
            }

            Aetherytes = aetherytes.ToFrozenDictionary();
            Log.Verbose("Collected {NumAetherytes} different aetherytes.", Aetherytes.Count);
            ForcedAetherytes.ApplyMissingAetherytes(this);
            if (Aetherytes.Count is 0)
            {
                throw new Exception("Could not fetch any aetherytes, this is certainly an error, terminating.");
            }

            var newestGatheringItems = new Dictionary<uint, GatheringItem>();
            foreach (var gatheringItem in DataManager.GetExcelSheet<GatheringItem>())
            {
                var itemId = gatheringItem.Item.RowId;
                if (itemId == 0 || itemId >= 1000000 || !gatheringItem.Item.TryGetValue<Item>(out var item) || item.Name.IsEmpty)
                {
                    continue;
                }

                // The Diadem items have multiple matching GatheringItem rows; take the newest
                if (!newestGatheringItems.TryGetValue(itemId, out var newest) || gatheringItem.RowId > newest.RowId)
                {
                    newestGatheringItems[itemId] = gatheringItem;
                }
            }

            var gatherables = new Dictionary<uint, Gatherable>(newestGatheringItems.Count);
            foreach (var (itemId, gatheringItem) in newestGatheringItems)
            {
                gatherables.Add(itemId, new Gatherable(this, gatheringItem));
            }

            Gatherables = gatherables.ToFrozenDictionary();
            GatherablesByGatherId = Gatherables.Values.ToFrozenDictionary(g => g.GatheringId, g => g);
            Log.Verbose("Collected {NumGatherables} different gatherable items.", Gatherables.Count);
            if (Gatherables.Count is 0)
            {
                throw new Exception("Could not fetch any gatherables, this is certainly an error, terminating.");
            }

            // Create GatheringItemPoint dictionary.
            var gatheringItemPoints = new Dictionary<uint, List<uint>>();
            foreach (var subrows in DataManager.GetSubrowExcelSheet<GatheringItemPoint>())
            {
                foreach (var itemPoint in subrows)
                {
                    AddDistinct(gatheringItemPoints, itemPoint.GatheringPoint.RowId, itemPoint.RowId);
                }
            }

            var tmpGatheringItemPoint = gatheringItemPoints.ToFrozenDictionary();

            uint[] OddlyDelicateItemIds = [Gatherables[31767].GatheringId, Gatherables[31769].GatheringId];

            // The Diadem Umbral nodes have PlaceName.RowId == 0, so we have to disable this filter
            // and filter by TerritoryType.RowId instead.
            // Filter out invalid or deleted territories (0 or 1) and old instances of The Diadem (901 or 929),
            // exept for the Oddly Delicate items which are mapped to the old insance of The Diadem (901).
            var gatheringPoints = new Dictionary<uint, List<uint>>();
            foreach (var gatheringPoint in DataManager.GetExcelSheet<GatheringPoint>())
            {
                if (gatheringPoint.TerritoryType.RowId is not (0 or 1 or 901 or 929)
                 || gatheringPoint.TerritoryType.RowId == 901 && ContainsAnyItem(gatheringPoint.GatheringPointBase.Value, OddlyDelicateItemIds))
                {
                    AddDistinct(gatheringPoints, gatheringPoint.GatheringPointBase.RowId, gatheringPoint.RowId);
                }
            }

            var tmpGatheringPoints = gatheringPoints.ToFrozenDictionary();

            var gatheringNodes = new Dictionary<uint, GatheringNode>();
            foreach (var pointBase in DataManager.GetExcelSheet<GatheringPointBase>())
            {
                if (pointBase.GatheringType.RowId >= (int)Enums.GatheringType.Spearfishing)
                {
                    continue;
                }

                var node = new GatheringNode(this, tmpGatheringPoints, tmpGatheringItemPoint, pointBase);
                if (node.Territory.Id > 1 && node.Items.Count > 0)
                {
                    gatheringNodes.Add(node.Id, node);
                }
            }

            GatheringNodes = gatheringNodes.ToFrozenDictionary();
            Log.Verbose("Collected {NumGatheringNodes} different gathering nodes", GatheringNodes.Count);
            if (GatheringNodes.Count is 0)
            {
                throw new Exception("Could not fetch any gathering nodes, this is certainly an error, terminating.");
            }

            var cosmicMissions = new Dictionary<ushort, CosmicMission>();
            foreach (var mission in DataManager.GetExcelSheet<WKSMissionUnit>())
            {
                if (mission.Name.ByteLength > 0 && (mission.ClassJobCategory[0].RowId is 19 || mission.ClassJobCategory[1].RowId is 19))
                {
                    cosmicMissions.Add((ushort)mission.RowId, new CosmicMission(mission));
                }
            }

            CosmicFishingMissions = cosmicMissions.ToFrozenDictionary();
            Log.Verbose("Collected {NumCosmicMissions} different cosmic fishing missions.", CosmicFishingMissions.Count);

            var bait = new Dictionary<uint, Bait>();
            foreach (var baitItem in DataManager.GetExcelSheet<Item>())
            {
                if (baitItem.ItemSearchCategory.RowId == Structs.Bait.FishingTackleRow)
                {
                    bait.Add(baitItem.RowId, new Bait(baitItem));
                }
            }

            foreach (var itemInfo in DataManager.GetExcelSheet<WKSItemInfo>())
            {
                if (itemInfo.WKSItemSubCategory.RowId is 5)
                {
                    var baitItem = itemInfo.Item.Value;
                    bait.Add(baitItem.RowId, new Bait(baitItem));
                }
            }

            Bait = bait.ToFrozenDictionary();
            Log.Verbose("Collected {NumBaits} different types of bait.", Bait.Count);
            if (Bait.Count is 0)
            {
                throw new Exception("Could not fetch any bait, this is certainly an error, terminating.");
            }

            var catchData = DataManager.GetExcelSheet<FishingNoteInfo>();
            var fishes = new Dictionary<uint, Fish>();
            foreach (var fishParameter in DataManager.GetExcelSheet<FishParameter>())
            {
                if (fishParameter.Item.RowId != 0 && fishParameter.Item.RowId < 1000000)
                {
                    var fish = new Fish(DataManager, fishParameter, catchData);
                    fishes.TryAdd(fish.ItemId, fish);
                }
            }

            foreach (var spearfishingItem in DataManager.GetExcelSheet<SpearfishingItem>())
            {
                if (spearfishingItem.Item.RowId != 0 && spearfishingItem.Item.RowId < 1000000)
                {
                    var fish = new Fish(DataManager, spearfishingItem, catchData);
                    fishes.TryAdd(fish.ItemId, fish);
                }
            }

            Fishes = fishes.ToFrozenDictionary();
            Log.Verbose("Collected {NumFishes} different types of fish.", Fishes.Count);
            if (Fishes.Count is 0)
            {
                throw new Exception("Could not fetch any fish, this is certainly an error, terminating.");
            }

            Data.Fish.Apply(this);
            OverriddenFish = CountOverriddenFish();

            var fishingSpots = new Dictionary<uint, FishingSpot>();
            foreach (var spotRow in DataManager.GetExcelSheet<FishingSpotRow>())
            {
                if ((spotRow.PlaceName.RowId != 0 || spotRow.RowId >= 10017) && (spotRow.TerritoryType.RowId > 0 || spotRow.RowId == 10000 || spotRow.RowId >= 10017))
                {
                    AddFishingSpot(fishingSpots, new FishingSpot(this, spotRow));
                }
            }

            foreach (var notebookRow in DataManager.GetExcelSheet<SpearfishingNotebook>())
            {
                if (notebookRow.PlaceName.RowId != 0 && notebookRow.TerritoryType.RowId > 0)
                {
                    AddFishingSpot(fishingSpots, new FishingSpot(this, notebookRow));
                }
            }

            FishingSpots = fishingSpots.ToFrozenDictionary();
            Log.Verbose("Collected {NumFishingSpots} different fishing spots.", FishingSpots.Count);
            if (FishingSpots.Count is 0)
            {
                throw new Exception("Could not fetch any fishing spots, this is certainly an error, terminating.");
            }

            Data.SpearfishingData.Apply(this);

            HiddenMaps.Apply(this);
            ForcedAetherytes.Apply(this);

            OceanRoutes = SetupOceanRoutes(gameData, FishingSpots);
            OceanTimeline = new OceanTimeline(gameData, OceanRoutes);
            SetOceanFish(OceanRoutes, Fishes.Values);

            foreach (var gatherable in Gatherables.Values)
            {
                if (gatherable.NodeType != NodeType.Unknown && !gatherable.NodeList.Exists(n => n.Times.AlwaysUp()))
                {
                    gatherable.InternalLocationId = ++TimedGatherables;
                }
                else if (gatherable.NodeList.Count > 1)
                {
                    gatherable.InternalLocationId = -++MultiNodeGatherables;
                }

                GatherablesTrie.Add(gatherable.Name[gameData.Language].ToLowerInvariant(), gatherable);
            }

            foreach (var fish in Fishes.Values)
            {
                if (fish.FishingSpots.Count > 0 && !fish.OceanFish && fish.FishRestrictions != FishRestrictions.None
                    && (fish.CurrentWeather.Length == 0 || !fish.CurrentWeather[0].IsUmbral)
                 || fish is { OceanFish: true, FishRestrictions: FishRestrictions.Time })
                {
                    fish.InternalLocationId = ++TimedGatherables;
                }
                else if (fish.FishingSpots.Count > 0)
                {
                    fish.InternalLocationId = -++MultiNodeGatherables;
                }

                FishTrie.Add(fish.Name[gameData.Language].ToLowerInvariant(), fish);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Error while setting up data:\n{e}");
        }
    }

    private static void AddDistinct(Dictionary<uint, List<uint>> groups, uint key, uint value)
    {
        if (!groups.TryGetValue(key, out var values))
        {
            values = [];
            groups.Add(key, values);
        }

        if (!values.Contains(value))
        {
            values.Add(value);
        }
    }

    private static bool ContainsAnyItem(GatheringPointBase pointBase, uint[] itemIds)
    {
        foreach (var item in pointBase.Item)
        {
            if (Array.IndexOf(itemIds, item.RowId) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddFishingSpot(Dictionary<uint, FishingSpot> fishingSpots, FishingSpot spot)
    {
        if (spot.Territory.Id != 0)
        {
            fishingSpots.Add(spot.Id, spot);
        }
    }

    public Territory? FindOrAddTerritory(TerritoryType? t)
    {
        if (t == null || t.Value.RowId < 2)
        {
            return null;
        }

        // Upgrade The Diadem territory to the latest instance
        if (t.Value.RowId is 901 or 929)
        {
            t = DataManager.GetExcelSheet<TerritoryType>().GetRow(939);
        }

        if (Territories.TryGetValue(t.Value.RowId, out var territory))
        {
            return territory;
        }

        // Create territory if it does not exist.
        var aether = DataManager.GetExcelSheet<TerritoryTypeTelepo>().GetRowOrDefault(t.Value.RowId);
        territory = new Territory(this, t.Value, aether);
        Territories.Add(t.Value.RowId, territory);
        return territory;
    }

    private static void SetOceanFish(IEnumerable<OceanRoute> routes, IEnumerable<Fish> fishes)
    {
        var set = new Dictionary<uint, OceanArea>(128);
        foreach (var route in routes)
        {
            set.TryAdd(route.SpotDay.Normal.Id, route.Area);
            set.TryAdd(route.SpotDay.Spectral.Id, route.Area);
            set.TryAdd(route.SpotNight.Normal.Id, route.Area);
            set.TryAdd(route.SpotNight.Spectral.Id, route.Area);
            set.TryAdd(route.SpotSunset.Normal.Id, route.Area);
            set.TryAdd(route.SpotSunset.Spectral.Id, route.Area);
        }

        foreach (var fish in fishes)
        {
            var spot = fish.FishData?.FishingSpot.RowId ?? 0u;
            if (set.TryGetValue(spot, out var area))
            {
                fish.OceanArea = fish.OceanArea is OceanArea.None || fish.OceanArea == area ? area : OceanArea.Unknown;
            }
        }
    }

    private static OceanRoute[] SetupOceanRoutes(IDataManager manager, FrozenDictionary<uint, FishingSpot> fishingSpots)
    {
        var routeSheet = manager.GetExcelSheet<IKDRoute>(ClientLanguage.English);
        var spotSheet = manager.GetExcelSheet<IKDSpot>();
        var ret = new OceanRoute[routeSheet.Count - 1];

        var spots = new List<(FishingSpot Normal, FishingSpot Spectral)>(spotSheet.Count);
        var skippedFirst = false;
        foreach (var r in spotSheet)
        {
            if (!skippedFirst)
            {
                skippedFirst = true;
                continue;
            }

            spots.Add(fishingSpots.TryGetValue(r.SpotMain.RowId, out var main) && fishingSpots.TryGetValue(r.SpotSub.RowId, out var sub)
                ? (main, sub)
                : throw new Exception("Invalid fishing spot!"));
        }

        for (var i = 1u; i < routeSheet.Count; ++i)
        {
            var row = routeSheet.GetRow(i);
            var (start, day, sunset, night) = row.Time[0].RowId switch
            {
                1 => (OceanTime.Sunset, spots[(int)row.Spot[1].RowId - 1], spots[(int)row.Spot[2].RowId - 1],
                    spots[(int)row.Spot[0].RowId - 1]),
                2 => (OceanTime.Night, spots[(int)row.Spot[0].RowId - 1], spots[(int)row.Spot[1].RowId - 1],
                    spots[(int)row.Spot[2].RowId - 1]),
                3 => (OceanTime.Day, spots[(int)row.Spot[2].RowId - 1], spots[(int)row.Spot[0].RowId - 1],
                    spots[(int)row.Spot[1].RowId - 1]),
                _ => (OceanTime.Sunset, spots[(int)row.Spot[1].RowId - 1], spots[(int)row.Spot[2].RowId - 1],
                    spots[(int)row.Spot[0].RowId - 1]),
            };
            ret[i - 1] = new OceanRoute
            {
                Id = (byte)i,
                Name = row.Name.ToDalamudString().TextValue,
                StartTime = start,
                SpotDay = day,
                SpotSunset = sunset,
                SpotNight = night,
                Area = i switch
                {
                    < 13 => OceanArea.Aldenard,
                    < 22 => OceanArea.Othard,
                    _ => OceanArea.Unknown,
                },
            };
        }

        return ret;
    }
}
