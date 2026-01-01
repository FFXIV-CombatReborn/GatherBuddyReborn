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
        private bool _wasGathering;
        private readonly byte[] _indexes = new byte[TotalPaths];
        private readonly bool[] _initialized = new bool[TotalPaths];
        public static FrozenDictionary<GatheringType, ImmutableArray<uint>> ShortestPaths { get; private set; }
        public static FrozenSet<GatheringNode> RegularBaseNodes { get; private set; }
        public static FrozenSet<Gatherable> RegularItems { get; private set; }
        public static FrozenSet<Gatherable> OddlyDelicateItems { get; private set; }

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
            if (!Functions.InTheDiadem())
            {
                Array.Fill(_indexes, (byte)0);
                Array.Fill(_initialized, true);
                return;
            }

            var isGathering = Dalamud.Conditions[ConditionFlag.Gathering];
            if (isGathering && !_wasGathering)
            {
                // Advance index to the next node
                _wasGathering = true;
                var target = Dalamud.Targets.Target;
                if (target != null && target.ObjectKind == ObjectKind.GatheringPoint)
                {
                    var nodeId = target.BaseId;
                    if (nodeId >= FirstNode && nodeId <= LastNode)
                    {
                        var pathIndex = (nodeId - FirstNode) / NodesPerPath;
                        var nodeIndex = (nodeId - FirstNode + 1u) % NodesPerChain; // +1 to target the next node in chain
                        if (_indexes[pathIndex] != nodeIndex)
                        {
                            //GatherBuddy.Log.Debug($"[Diadem] Changing path {pathIndex} index from {_indexes[pathIndex]} to {nodeIndex}");
                            _indexes[pathIndex] = (byte)nodeIndex;
                        }
                        _initialized[pathIndex] = true;
                    }
                }
            }
            else if (!isGathering && (_wasGathering || _initialized.Any(x => !x)))
            {
                // This is needed to counter manual intervention, e.g. enabling/rebuilding the plugin
                // or closing a node without gathering it.

                var jobOffset = Player.Job switch { 16 /*MIN*/ => 0, 17 /*BTN*/ => (int)PathsPerJob, _ => -1 };
                if (jobOffset < 0) return;

                var effectId = AutoGather.Actions.Prospect.EffectId;
                if (!Player.Status?.Any(s => s.StatusId == effectId) ?? true) return;

                _wasGathering = false;

                foreach (var obj in Dalamud.Objects.Where(obj => obj.ObjectKind == ObjectKind.GatheringPoint))
                {
                    var nodeId = obj.BaseId;
                    if (nodeId < FirstNode || nodeId > LastNode) continue;

                    var pathIndex = (int)((nodeId - FirstNode) / NodesPerPath);
                    var nodeIndex = (nodeId - FirstNode) % NodesPerChain;
                    if (obj.IsTargetable)
                    {
                        if (_indexes[pathIndex] != nodeIndex)
                        {
                            GatherBuddy.Log.Debug($"[Diadem] Changing path {pathIndex} index from {_indexes[pathIndex]} to {nodeIndex}");
                            _indexes[pathIndex] = (byte)nodeIndex;
                        }
                        _initialized[pathIndex] = true;
                    }
                    else if (!_initialized[pathIndex])
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
