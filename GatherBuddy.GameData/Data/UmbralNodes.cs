using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Reflection;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Structs;

namespace GatherBuddy.Data;

public static class UmbralNodes
{
    public enum UmbralWeatherType : uint
    {
        UmbralFlare = 133,
        UmbralDuststorms = 134,
        UmbralLevin = 135,
        UmbralTempest = 136,
    }

    public enum CloudedNodeType
    {
        CloudedRockyOutcrop,   
        CloudedMineralDeposit,   
        CloudedMatureTree,       
        CloudedLushVegetation,   
    }

    public readonly record struct UmbralNodeDataRecord(uint BaseNodeId, uint NodeId, CloudedNodeType NodeType, UmbralWeatherType Weather, ImmutableArray<uint> ItemIds);

    public static readonly ImmutableArray<UmbralNodeDataRecord> UmbralNodeData =
    [
        new(798, 33836, CloudedNodeType.CloudedMineralDeposit, UmbralWeatherType.UmbralFlare,      [29946, 31318, 32047]),
        new(799, 33837, CloudedNodeType.CloudedRockyOutcrop,   UmbralWeatherType.UmbralLevin,      [29947, 31319, 32048]),
        new(800, 33838, CloudedNodeType.CloudedMatureTree,     UmbralWeatherType.UmbralTempest,    [29944, 31316, 32045]),
        new(801, 33839, CloudedNodeType.CloudedLushVegetation, UmbralWeatherType.UmbralDuststorms, [29945, 31317, 32046]),
    ];

    private static readonly UmbralNodeDataRecord DefaultUmbralNodeData = default;

    public static readonly FrozenDictionary<CloudedNodeType, string> NodeNames = new Dictionary<CloudedNodeType, string>()
    {
        { CloudedNodeType.CloudedRockyOutcrop, "Clouded Rocky Outcrop" },
        { CloudedNodeType.CloudedMineralDeposit, "Clouded Mineral Deposit" },
        { CloudedNodeType.CloudedMatureTree, "Clouded Mature Tree" },
        { CloudedNodeType.CloudedLushVegetation, "Clouded Lush Vegetation" },
    }.ToFrozenDictionary();

    public static GatheringType GetGatheringType(CloudedNodeType nodeType)
    {
        return nodeType switch
        {
            CloudedNodeType.CloudedRockyOutcrop => GatheringType.Miner,
            CloudedNodeType.CloudedMineralDeposit => GatheringType.Miner,
            CloudedNodeType.CloudedMatureTree => GatheringType.Botanist,
            CloudedNodeType.CloudedLushVegetation => GatheringType.Botanist,
            _ => throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null)
        };
    }

    public static bool IsUmbralWeather(uint weatherId)
    {
        return Enum.IsDefined(typeof(UmbralWeatherType), weatherId);
    }

    public static IEnumerable<uint> GetNodesForWeatherAndType(UmbralWeatherType weather, GatheringType gatheringType)
    {
        return UmbralNodeData
            .Where(entry => entry.Weather == weather && GetGatheringType(entry.NodeType) == gatheringType)
            .Select(entry => entry.NodeId);
    }

    public static ImmutableArray<uint> GetItemsForNode(uint nodeId)
    {
        var entry = UmbralNodeData.FirstOrDefault(data => data.NodeId == nodeId);
        return entry.NodeId != 0 ? entry.ItemIds : [];
    }

    public static UmbralWeatherType? GetRequiredWeatherForNode(uint nodeId)
    {
        var entry = UmbralNodeData.FirstOrDefault(data => data.NodeId == nodeId);
        return entry.NodeId == 0 ? null : entry.Weather;
    }

    public static CloudedNodeType? GetNodeType(uint nodeId)
    {
        var entry = UmbralNodeData.FirstOrDefault(data => data.NodeId == nodeId);
        return entry.NodeId == 0 ? null : entry.NodeType;
    }

    public static void Apply(GameData data)
    {
        var validItems = 0;
        var validWeatherPositions = 0;
        
        data.Log.Information($"Validating umbral system with {UmbralNodeData.Length} configurations...");
        
        foreach (var (_, nodeId, nodeType, weather, itemIds) in UmbralNodeData)
        {
            data.Log.Debug($"Validating umbral configuration {nodeId} ({nodeType}) for {weather} weather");
            
            foreach (var itemId in itemIds)
            {
                if (data.Gatherables.TryGetValue(itemId, out var item))
                {
                    data.Log.Information($"✓ Umbral item found: {item.Name[data.DataManager.Language]} (ID: {itemId})");
                    validItems++;
                }
                else
                {
                    data.Log.Warning($"✗ Umbral item {itemId} not found in game data");
                }
            }
            
            if (data.WorldCoords.TryGetValue(nodeId, out var positions) && positions.Count > 0)
            {
                data.Log.Information($"✓ Found {positions.Count} world positions for umbral node {nodeId}");
                validWeatherPositions++;
            }
            else
            {
                data.Log.Warning($"✗ No world positions found for umbral node {nodeId}");
            }
        }
        
        data.Log.Information($"Umbral validation complete. {validItems} valid items, {validWeatherPositions} nodes with positions.");
        data.Log.Information($"Umbral items will be handled by AutoGather umbral logic.");
    }

    public static bool IsUmbralItem(uint itemId) => GetUmbralItemInfo(itemId).NodeId != 0;

    public static bool IsUmbralNode(uint nodeId) => GetUmbralNodeInfo(nodeId).NodeId != 0;
    public static bool IsUmbralBaseNode(uint baseNodeId) => GetUmbralBaseNodeInfo(baseNodeId).NodeId != 0;

    public static ref readonly UmbralNodeDataRecord GetUmbralItemInfo(uint itemId)
    {
        for (var i = 0; i < UmbralNodeData.Length; i++)
            for (var j = 0; j < UmbralNodeData[i].ItemIds.Length; j++)
                if (UmbralNodeData[i].ItemIds[j] == itemId)
                    return ref UmbralNodeData.ItemRef(i);

        return ref DefaultUmbralNodeData;
    }

    public static ref readonly UmbralNodeDataRecord GetUmbralNodeInfo(uint nodeId)
    {
        for (var i = 0; i < UmbralNodeData.Length; i++)
            if (UmbralNodeData[i].NodeId == nodeId)
                return ref UmbralNodeData.ItemRef(i);

        return ref DefaultUmbralNodeData;
    }

    public static ref readonly UmbralNodeDataRecord GetUmbralBaseNodeInfo(uint baseNodeId)
    {
        for (var i = 0; i < UmbralNodeData.Length; i++)
            if (UmbralNodeData[i].BaseNodeId == baseNodeId)
                return ref UmbralNodeData.ItemRef(i);

        return ref DefaultUmbralNodeData;
    }
}
