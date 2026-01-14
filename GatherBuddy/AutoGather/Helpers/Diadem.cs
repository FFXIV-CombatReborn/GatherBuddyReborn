using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using ElliLib.Extensions;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using PathCache = System.Collections.Generic.Dictionary<(System.Numerics.Vector3, uint, uint), (float, System.Collections.Immutable.ImmutableArray<byte>)>;

namespace GatherBuddy.AutoGather.Helpers
{
    // Normal nodes in The Diadem take up a continuous range of IDs starting at 33644.
    // There are 4 independent paths; the first 2 are for Miner and the other 2 are for Botanist.
    // For each class, the first path's node bonuses require the Gathering stat, and the second path's require Perception.
    // Each path consists of 6 chains, with 8 nodes each.
    // At any given time, only 6 nodes in each path are visible: one per chain, all with the same index within the chain.
    // Gathering a node increments the visible node index in all chains in the given path.

    internal class Diadem : IDisposable
    {
        private const uint FirstNode = 33644;
        private const uint LastNode = FirstNode + TotalPaths * NodesPerPath - 1;
        private const uint TotalPaths = 4;
        private const uint PathsPerJob = 2;
        private const uint ChainsPerPath = 6;
        private const uint NodesPerChain = 8;
        private const uint NodesPerPath = ChainsPerPath * NodesPerChain;
        private uint _lastNode;
        private readonly byte[] _indexes = new byte[TotalPaths];
        private int _initialized; // bitfield
        public static Territory Territory { get; } = GatherBuddy.GameData.Territories[939];
        public static bool IsInside => Dalamud.ClientState.TerritoryType == 939;
        public static FrozenDictionary<GatheringType, ImmutableArray<uint>> ShortestPaths { get; }
        public static FrozenSet<GatheringNode> RegularBaseNodes { get; }
        public static FrozenSet<Gatherable> RegularItems { get; }
        public static FrozenSet<Gatherable> OddlyDelicateItems { get; }
        public static FrozenDictionary<uint, uint> RawToApprovedItemIds { get; }
        public static FrozenDictionary<uint, uint> ApprovedToRawItemIds { get; }
        public static ImmutableArray<(Vector3 From, Vector3 To)> Windmires { get; } = [
            (Vector3.Create(-724.649f,270.846f,-27.428f),Vector3.Create(-540.327f,317.677f,323.471f)),
            (Vector3.Create(-558.688f,318.068f,308.976f),Vector3.Create(-723.915f,271.008f,-49.867f)),
            (Vector3.Create(-287.557f,318.234f,558.728f),Vector3.Create(-118.239f,114.119f,537.373f)),
            (Vector3.Create(-140.415f,113.127f,519.463f),Vector3.Create(-311.086f,317.677f,560.815f)),
            (Vector3.Create(274.797f,85.048f,470.526f),Vector3.Create(359.416f,-4.959f,450.553f)),
            (Vector3.Create(467.156f,-25.674f,187.769f),Vector3.Create(661.631f,223.636f,-57.878f)),
            (Vector3.Create(713.130f,218.108f,-334.355f),Vector3.Create(640.518f,251.972f,-401.531f)),
            (Vector3.Create(603.883f,251.803f,-411.563f),Vector3.Create(546.746f,192.386f,-516.838f)),
            (Vector3.Create(469.004f,191.349f,-667.111f),Vector3.Create(388.159f,290.028f,-713.512f)),
            (Vector3.Create(177.985f,292.541f,-737.720f),Vector3.Create(129.320f,-49.587f,-512.261f)),
            (Vector3.Create(-590.303f,33.309f,-230.903f),Vector3.Create(-623.508f,282.778f,-175.324f)),
            (Vector3.Create(-629.557f,280.432f,-187.958f),Vector3.Create(-575.189f,34.246f,-236.601f)),
            (Vector3.Create(340.000f,-4.986f,471.190f),Vector3.Create(257.618f,86.381f,487.877f)),
            (Vector3.Create(137.765f,-50.296f,-481.785f),Vector3.Create(203.764f,293.528f,-736.416f)),
            (Vector3.Create(434.434f,289.603f,-735.568f),Vector3.Create(473.259f,200.712f,-636.561f)),
            (Vector3.Create(528.247f,189.653f,-494.262f),Vector3.Create(624.109f,251.972f,-423.270f)),
            (Vector3.Create(636.711f,251.792f,-389.120f),Vector3.Create(713.730f,219.938f,-311.411f)),
            (Vector3.Create(657.981f,222.430f,-33.609f),Vector3.Create(466.911f,-23.926f,202.930f)),
        ];

        static Diadem()
        {
            var cache = new PathCache((int)(NodesPerPath * NodesPerPath * 2));
            var array = ImmutableArray.CreateBuilder<uint>((int)(NodesPerPath * 2));
            var results = new ImmutableArray<uint>[2];
            for (var job = 0u; job < 2u; job++)
            {
                cache.Clear();
                array.Capacity = (int)NodesPerPath * 2;
                (_, var path) = FindShortestPath(cache, GetNodePosition(job, 0, 0), job, 1, 0); // Start at path 0 node
                uint p1 = 0, p2 = 0;

                array.Add(FirstNode + (job * PathsPerJob + 0u) * NodesPerPath + p1++); // First node
                for (var i = path.Length - 1; i >= 0; i--)
                {
                    array.Add(FirstNode + (job * PathsPerJob + path[i]) * NodesPerPath + (path[i] == 0 ? p1++ : p2++));
                }
                results[job] = array.MoveToImmutable();
            }
            ShortestPaths = new Dictionary<GatheringType, ImmutableArray<uint>>
            {
                [GatheringType.Miner] = results[0],
                [GatheringType.Botanist] = results[1]
            }.ToFrozenDictionary();

            RegularBaseNodes = GatherBuddy.GameData.GatheringNodes.Values
                .Where(n => n.WorldPositions.Keys.Any(nodeId => nodeId is >= FirstNode and <= LastNode))
                .ToFrozenSet();

            RegularItems = RegularBaseNodes.SelectMany(bn => bn.Items).ToFrozenSet();

            OddlyDelicateItems = [GatherBuddy.GameData.Gatherables[31767], GatherBuddy.GameData.Gatherables[31769]];

            RawToApprovedItemIds = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.HWDGathererInspection>()
                .SelectMany(x => x.HWDGathererInspectionData)
                .Where(x => x.RequiredItem.IsValid && x.RequiredItem.RowId != 0 && x.ItemReceived.IsValid && x.ItemReceived.RowId != 0)
                .Where(x => x.RequiredItem.Value.Item.Is<Lumina.Excel.Sheets.Item>() && x.RequiredItem.Value.Item.RowId != 0)
                .ToFrozenDictionary(x => x.RequiredItem.Value.Item.RowId, x => x.ItemReceived.RowId);

            ApprovedToRawItemIds = RawToApprovedItemIds.ToFrozenDictionary(x => x.Value, x => x.Key);
        }
        public Diadem()
        {
            Dalamud.Framework.Update += OnUpdate;
        }

        public void Dispose()
        {
            Dalamud.Framework.Update -= OnUpdate;
        }

        public IEnumerable<uint> GetAvailableNodes(GatheringType type)
        {
            var offset = type switch
            {
                GatheringType.Miner => 0u,
                GatheringType.Botanist => PathsPerJob,
                _ => throw new System.ComponentModel.InvalidEnumArgumentException(nameof(type), (int)type, typeof(GatheringType)),
            };
            return Enumerable.Range(0, (int)(ChainsPerPath * PathsPerJob))
                .Select(chain => FirstNode + offset * NodesPerPath + (uint)chain * NodesPerChain + _indexes[offset + (uint)chain / ChainsPerPath]);
        }


        private static (float, ImmutableArray<byte>) FindShortestPath(PathCache cache, Vector3 pos, uint job, uint path0, uint path1)
        {
            if (path0 == NodesPerPath && path1 == NodesPerPath)
                return (Vector3.Distance(pos, GetNodePosition(job, 0, 0)), []);

            var key = (pos, path0, path1);
            if (cache.TryGetValue(key, out var val))
                return val;

            float dist0 = float.PositiveInfinity, dist1 = float.PositiveInfinity;
            ImmutableArray<byte> list0 = default, list1 = default;

            if (path0 < NodesPerPath)
            {
                var pos0 = GetNodePosition(job, 0, path0);
                (dist0, list0) = FindShortestPath(cache, pos0, job, path0 + 1, path1);
                dist0 += Vector3.Distance(pos, pos0);
            }
            if (path1 < NodesPerPath)
            {

                var pos1 = GetNodePosition(job, 1, path1);
                (dist1, list1) = FindShortestPath(cache, pos1, job, path0, path1 + 1);
                dist1 += Vector3.Distance(pos, pos1);
            }

            (float, ImmutableArray<byte>) result;
            if (dist0 < dist1)
                result = (dist0, list0.Add(0));
            else
                result = (dist1, list1.Add(1));

            cache[key] = result;
            return result;
        }

        private static Vector3 GetNodePosition(uint job, uint path, uint node)
        {
            var nodeId = FirstNode + (job * PathsPerJob + path) * NodesPerPath + node;
            return WorldData.WorldLocationsByNodeId[nodeId].Average();
        }

        private void OnUpdate(IFramework _)
        {
            if (!IsInside)
            {
                Array.Fill(_indexes, (byte)0);
                _initialized = (1 << (int)TotalPaths) - 1;
                return;
            }

            var isGathering = Dalamud.Conditions[ConditionFlag.Gathering];

            if (isGathering)
            {
                // Advance index to the next node
                var target = Dalamud.Targets.Target;
                if (target != null && target.ObjectKind == ObjectKind.GatheringPoint)
                {
                    var nodeId = target.BaseId;
                    if (nodeId >= FirstNode && nodeId <= LastNode)
                    {
                        _lastNode = nodeId;
                    }
                }
            }
            else if (!isGathering && _lastNode != 0)
            {
                // Wait until the node becomes untargetable.
                if (!Dalamud.Objects.Any(obj => obj.ObjectKind == ObjectKind.GatheringPoint && obj.IsTargetable && obj.BaseId == _lastNode))
                {
                    var pathIndex = (_lastNode - FirstNode) / NodesPerPath;
                    var nodeIndex = (_lastNode - FirstNode + 1u) % NodesPerChain; // +1 to target the next node in chain
                    _indexes[pathIndex] = (byte)nodeIndex;
                    _initialized |= 1 << (int)pathIndex;
                    _lastNode = 0u;
                }
            }
            else if (!isGathering && _initialized != (1 << (int)TotalPaths) - 1)
            {
                // This is needed to support enabling/rebuilding the plugin inside The Diadem.

                var jobOffset = Player.Job switch { 16 /*MIN*/ => 0, 17 /*BTN*/ => (int)PathsPerJob, _ => -1 };
                if (jobOffset < 0) return;

                var effectId = AutoGather.Actions.Prospect.EffectId;
                if (!Player.Status?.Any(s => s.StatusId == effectId) ?? true) return;

                foreach (var obj in Dalamud.Objects.Where(obj => obj.ObjectKind == ObjectKind.GatheringPoint))
                {
                    var nodeId = obj.BaseId;
                    if (nodeId < FirstNode || nodeId > LastNode) continue;

                    var pathIndex = (int)((nodeId - FirstNode) / NodesPerPath);
                    var nodeIndex = (nodeId - FirstNode) % NodesPerChain;
                    
                    if ((_initialized & (1 << pathIndex)) != 0) continue;

                    if (obj.IsTargetable)
                    {
                        if (_indexes[pathIndex] != nodeIndex)
                        {
                            GatherBuddy.Log.Debug($"[Diadem] Changing path {pathIndex} index from {_indexes[pathIndex]} to {nodeIndex}");
                            _indexes[pathIndex] = (byte)nodeIndex;
                        }
                        _initialized |= 1 << pathIndex;
                    }
                    else
                    {
                        if (_indexes[pathIndex] == nodeIndex
                            && nodeId >= FirstNode + jobOffset * NodesPerPath
                            && nodeId < FirstNode + (jobOffset + PathsPerJob) * NodesPerPath
                            && GatherBuddy.GameData.WorldCoords[nodeId].All(v => v.DistanceToPlayer() < AutoGather.NodeVisibilityDistance)
                            && !Dalamud.Objects.Where(obj => obj.ObjectKind == ObjectKind.GatheringPoint && obj.BaseId == nodeId && obj.IsTargetable).Any())
                        {
                            GatherBuddy.Log.Debug($"[Diadem] Node #{nodeIndex} not visible on path {pathIndex}; incrementing the index to {(_indexes[pathIndex] + 1) % NodesPerChain} as a fallback.");
                            _indexes[pathIndex] = (byte)((_indexes[pathIndex] + 1u) % NodesPerChain);
                        }
                    }
                }
            }
        }
    }
}
