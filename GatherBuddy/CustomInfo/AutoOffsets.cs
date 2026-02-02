using GatherBuddy.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.CustomInfo
{
    internal static class AutoOffsets
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OffsetEntry
        {
            public uint NodeId;
            public Vector3 NodePosition;
            public Vector3 Offset;
            public uint Crc32;

            public const int Size = 4 + 12 + 12 + 4;
            public readonly uint ComputeCrc32()
            {
                var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<OffsetEntry>(in this));
                var data = MemoryMarshal.Cast<byte, uint>(bytes);
                var crc = ~0u;
                for (var i = 0; i < Size / 4 - 1; i++)
                    crc = BitOperations.Crc32C(crc, data[i]);
                return ~crc;
            }
        }
        private record struct Node(uint Id, Vector3 Position);

        private const int MaxOffsetsPerNode = 100;
        private const int TargetOffsetsPerNode = 50;
        private static readonly string AutoOffsetsDbPath = Path.Combine(Dalamud.PluginInterface.ConfigDirectory.FullName, "auto_offsets.dat");
        private static readonly Dictionary<Node, Dictionary<Vector2, Vector3>> OffsetsDict = [];
        private static readonly object WriteLock = new();

        static AutoOffsets()
        {
            Debug.Assert(Marshal.SizeOf<OffsetEntry>() == OffsetEntry.Size, "AutoOffsetEntry size mismatch.");
            ReadOffsets();
        }

        private static void ReadOffsets()
        {
            try
            {
                using var fs = new FileStream(AutoOffsetsDbPath, FileMode.Open, FileAccess.Read);
                Span<byte> buffer = stackalloc byte[OffsetEntry.Size];
                for (;;)
                {
                    var num = fs.Read(buffer);
                    if (num != OffsetEntry.Size)
                        break;

                    ref var entry = ref MemoryMarshal.AsRef<OffsetEntry>(buffer);
                    if (entry.ComputeCrc32() != entry.Crc32)
                        continue;

                    var node = new Node(entry.NodeId, entry.NodePosition);
                    var offset = entry.Offset;

                    if (!CheckSeparation(node, offset))
                        continue;

                    if (OffsetsDict.TryGetValue(node, out var offsets))
                    {
                        offsets[QuantizeVector(offset)] = offset;
                    }
                    else
                    {
                        OffsetsDict[node] = new()
                        {
                            [QuantizeVector(offset)] = offset
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error loading auto offsets: {ex.Message}");
            }
        }

        private static void WriteAllOffsetsBackground()
        {
            Task.Run(async () =>
            {
                await Task.Delay(1);
                lock (WriteLock)
                {
                    WriteAllOffsets();
                }
            });
        }

        private static void AppendOffsetBackground(Node node, Vector3 offset)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1);
                lock (WriteLock)
                {
                    try
                    {
                        using var fs = new FileStream(AutoOffsetsDbPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                        if (fs.Position % OffsetEntry.Size != 0)
                        {
                            GatherBuddy.Log.Warning("Auto offsets file is corrupted. Rewriting the entire file.");
                            fs.Close();
                            WriteAllOffsets();
                            return;
                        }
                        WriteOffsetToFile(fs, node, offset);
                    }
                    catch (Exception ex)
                    {
                        GatherBuddy.Log.Error($"Error writing auto offset to file: {ex.Message}");
                    }
                }
            });
        }

        private static void WriteAllOffsets()
        {
            try
            {
                var tempPath = AutoOffsetsDbPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    foreach (var (node, offsets) in OffsetsDict)
                    {
                        foreach (var offset in offsets.Values)
                        {
                            WriteOffsetToFile(fs, node, offset);
                        }
                    }
                }
                File.Move(tempPath, AutoOffsetsDbPath, true);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error writing auto offsets to file: {ex.Message}");
            }
        }

        private static void WriteOffsetToFile(FileStream fs, in Node node, in Vector3 offset)
        {
            var entry = new OffsetEntry
            {
                NodeId = node.Id,
                NodePosition = node.Position,
                Offset = offset
            };
            entry.Crc32 = entry.ComputeCrc32();

            var data = MemoryMarshal.AsBytes(new ReadOnlySpan<OffsetEntry>(in entry));
            fs.Write(data);
        }

        public static void AddOffset(uint nodeId, in Vector3 nodePosition, in Vector3 offset)
        {
            var node = new Node(nodeId, nodePosition);
            if (!CheckSeparation(node, offset))
            {
                //GatherBuddy.Log.Verbose($"[AutoOffsets] Rejected offset for node {nodeId}: failed separation check (h: {Vector2.Distance(node.Position.ToVector2(), offset.ToVector2()):F2}y, v: {MathF.Abs(node.Position.Y - offset.Y):F2}y)");
                return;
            }

            if (!Monitor.TryEnter(WriteLock, 0))
            {
                GatherBuddy.Log.Verbose($"[AutoOffsets] Rejected offset for node {nodeId}: background I/O in progress");
                return;
            }

            try
            {
                bool added;
                if (OffsetsDict.TryGetValue(node, out var offsets))
                {
                    added = offsets.TryAdd(QuantizeVector(offset), offset);
                    //if (!added) GatherBuddy.Log.Verbose($"[AutoOffsets] Rejected offset for node {nodeId}: quantized duplicate (distance to existing: {Vector3.Distance(offset, offsets[QuantizeVector(offset)]):F2}y)");
                }
                else
                {
                    offsets = new()
                    {
                        [QuantizeVector(offset)] = offset
                    };
                    OffsetsDict[node] = offsets;
                    added = true;
                }

                if (added)
                {
                    GatherBuddy.Log.Verbose($"[AutoOffsets] Added new offset for node {nodeId}, distance {Vector2.Distance(node.Position.ToVector2(), offset.ToVector2()):F2}y, total offsets: {offsets.Count}");

                    if (offsets.Count > MaxOffsetsPerNode)
                    {
                        ThinCollection(node, offsets);
                        WriteAllOffsetsBackground();
                    }
                    else
                        AppendOffsetBackground(node, offset);
                }
            }
            finally
            {
                Monitor.Exit(WriteLock);
            }
        }

        public static bool TryGetRandomOffset(uint nodeId, in Vector3 nodePosition, in Vector3 player, out Vector3 offset)
        {
            var node = new Node(nodeId, nodePosition);
            offset = default;

            if (!OffsetsDict.TryGetValue(node, out var offsets) || offsets.Count == 0)
                return false;

            var toPlayer = Vector2.Normalize(player.ToVector2() - nodePosition.ToVector2());
            var count = offsets.Count;
            
            Span<ulong> preferredMask = stackalloc ulong[(count + 64 - 1) / 64];

            var preferredCount = 0;
            var index = 0;
            foreach (var offsetValue in offsets.Values)
            {
                var toOffset = Vector2.Normalize(offsetValue.ToVector2() - nodePosition.ToVector2());
                // Prefer offsets that are within a 120° sector towards the player in the horizontal plane.
                if (Vector2.Dot(toPlayer, toOffset) >= 0.5f) // cos(60°) = 0.5
                {
                    preferredMask[index / 64] |= 1UL << (index % 64);
                    preferredCount++;
                }
                index++;
            }

            var offsetIndex = Random.Shared.Next(preferredCount > 0 ? preferredCount : count);

            if (preferredCount > 0)
            {
                index = 0;
                // Find global index by incrementing offsetIndex for each non-preferred offset (0 bit)
                // encountered on the way to the selected N-th preferred offset (1 bit).
                // offsetIndex - index + 1 is the number of 1 bits left to find.
                while (index <= offsetIndex)
                {
                    if ((preferredMask[index / 64] & 1UL << (index % 64)) == 0)
                        offsetIndex++;
                    index++;
                }
            }

            offset = offsets.Values.ElementAt(offsetIndex);
            return true;
        }

        public static int GetOffsetCount(uint nodeId, in Vector3 nodePosition)
        {
            var node = new Node(nodeId, nodePosition);
            return OffsetsDict.TryGetValue(node, out var offsets) ? offsets.Count : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector2 QuantizeVector(in Vector3 v)
        {
            // To ignore vectors too close to existing ones, we quantize them to a grid with 0.1f spacing,
            // and use that as a key in the dictionary.
            const float step = 0.1f;
            return new Vector2(
                MathF.Round(v.X / step) * step,
                MathF.Round(v.Z / step) * step
            );
        }

        private static bool CheckSeparation(in Node node, in Vector3 offset)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            if (MathF.Abs(node.Position.Y - offset.Y) > MaxVerticalSeparation)
                return false;
            if (Vector2.DistanceSquared(node.Position.ToVector2(), offset.ToVector2()) > MaxHorizontalSeparation * MaxHorizontalSeparation)
                return false;
            return true;
        }

        private static void ThinCollection(Node node, Dictionary<Vector2, Vector3> offsets)
        {
            var points = offsets.Values.ToArray();
            var count = points.Length;

            var sw = Stopwatch.StartNew();

            // Track removed points using a bitmask (1 = removed, 0 = active).
            var removedMask = new ulong[(count + 63) / 64];

            // Track the nearest neighbor index and squared distance for each point.
            var nearest = new (int idx, float dist)[count];
            for (var i = 0; i < count; i++)
            {
                nearest[i] = FindNearest(points, removedMask, i);
            }

            var pq = new PriorityQueue<int, float>(count);
            for (var i = 0; i < count; i++)
            {
                pq.Enqueue(i, nearest[i].dist);
            }

            var activeCount = count;

            while (activeCount > TargetOffsetsPerNode && pq.TryDequeue(out var removed, out var priority))
            {
                // Skip if already removed
                if ((removedMask[removed / 64] & (1UL << (removed % 64))) != 0)
                    continue;

                // Skip if priority is stale (point was already re-enqueued with updated distance).
                if (priority != nearest[removed].dist)
                    continue;

                // Mark this point as removed.
                removedMask[removed / 64] |= 1UL << (removed % 64);
                activeCount--;

                // Update points that had the removed point as their nearest neighbor.
                for (var i = 0; i < count; i++)
                {
                    if ((removedMask[i / 64] & (1UL << (i % 64))) == 0 && nearest[i].idx == removed)
                    {
                        nearest[i] = FindNearest(points, removedMask, i);
                        pq.Enqueue(i, nearest[i].dist);
                    }
                }
            }

            // Remove items marked for deletion.
            for (var i = 0; i < count; i++)
            {
                if ((removedMask[i / 64] & (1UL << (i % 64))) != 0)
                    offsets.Remove(QuantizeVector(points[i]));
            }
            offsets.TrimExcess();

            sw.Stop();
            GatherBuddy.Log.Debug($"[AutoOffsets] Thinned node {node.Id} collection from {count} to {offsets.Count} offsets in {sw.Elapsed.TotalMilliseconds:F3}ms");

            static (int idx, float dist) FindNearest(Vector3[] points, ulong[] mask, int pointIdx)
            {
                var nearestIdx = -1;
                var nearestDist = float.MaxValue;
                var pointXZ = points[pointIdx].ToVector2();

                for (var i = 0; i < points.Length; i++)
                {
                    if (i != pointIdx && (mask[i / 64] & (1UL << (i % 64))) == 0)
                    {
                        var dist = Vector2.DistanceSquared(pointXZ, points[i].ToVector2());
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestIdx = i;
                        }
                    }
                }
                return (nearestIdx, nearestDist);
            }
        }
    }
}
