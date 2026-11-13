using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Classes;
using System;
using System.Collections.Generic;
using System.Numerics;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private const uint SwimmingShadowsMarkerIconId = 60930;
        private const long SwimmingShadowsMarkerLogCooldown = 5000;

        private long _lastSwimmingShadowsMarkerLog;

        private readonly record struct SpearfishingNodeState(uint RowId, uint BaseRowId, byte RemainingCount);

        internal bool IsSwimmingShadowsAvailable(FishingSpot shadowSpot)
            => shadowSpot.IsShadowNode
            && (HasAvailableSpearfishingNode(shadowSpot) || TryGetSwimmingShadowsMarker(shadowSpot, out _));

        private static bool HasAvailableSpearfishingNode(FishingSpot spot)
        {
            foreach (var gameObject in Dalamud.Objects)
            {
                if (TryGetSpearfishingNodeState(gameObject, out var state)
                    && state.RemainingCount > 0
                    && MatchesSpearfishingSpot(spot, state))
                {
                    return true;
                }
            }

            return false;
        }

        private static unsafe bool TryGetSpearfishingNodeState(IGameObject gameObject, out SpearfishingNodeState state)
        {
            state = default;
            if (gameObject.ObjectKind != DalamudObjectKind.GatheringPoint || gameObject.Address == nint.Zero)
            {
                return false;
            }

            var gatheringPoint = (GatheringPointObject*)gameObject.Address;
            return TryReadSpearfishingNodeState(gatheringPoint->Impl, out state)
                || TryReadSpearfishingNodeState(&gatheringPoint->ObjectImplBase, out state)
                || TryReadSpearfishingNodeState((GatheringPointObject.GatheringPointObjectImplBase*)&gatheringPoint->ObjectImpl, out state);
        }

        private static unsafe bool TryReadSpearfishingNodeState(
            GatheringPointObject.GatheringPointObjectImplBase* implementation,
            out SpearfishingNodeState state)
        {
            state = default;
            if (implementation == null || implementation->EventHandler == null)
            {
                return false;
            }

            var handler = implementation->EventHandler;
            if ((byte)handler->GatheringType is not (4 or 5))
            {
                return false;
            }

            state = new SpearfishingNodeState(handler->RowId, handler->BaseRowId, handler->RemainingCount);
            return state.RowId != 0 || state.BaseRowId != 0;
        }

        private static bool MatchesSpearfishingSpot(FishingSpot spot, SpearfishingNodeState state)
        {
            if (state.RowId != 0 && spot.WorldPositions.ContainsKey(state.RowId))
            {
                return true;
            }

            var baseRowId = spot.SpearfishingSpotData?.GatheringPointBase.RowId ?? 0;
            return baseRowId != 0 && state.BaseRowId == baseRowId;
        }

        private bool IsVisitedSpearfishingNode(SpearfishingNodeState state)
            => state.RowId != 0 && VisitedNodes.Contains(state.RowId);

        private unsafe bool TryGetSwimmingShadowsMarker(FishingSpot shadowSpot, out Vector3 position)
        {
            position = default;
            var territoryId = Dalamud.ClientState.TerritoryType;
            if (!shadowSpot.IsShadowNode || shadowSpot.Territory.Id != territoryId)
            {
                return false;
            }

            var agentMap = AgentMap.Instance();
            if (agentMap == null || agentMap->CurrentTerritoryId != territoryId)
            {
                return false;
            }

            var markers = new List<Vector3>(1);
            foreach (var marker in agentMap->MiniMapGatheringMarkers)
            {
                if (marker.ShouldRender == 0
                 || marker.MapMarker.IconId != SwimmingShadowsMarkerIconId
                 || marker.MapMarker.X == 0
                 || marker.MapMarker.Y == 0)
                {
                    continue;
                }

                markers.Add(new Vector3(marker.MapMarker.X / 16f, 0, marker.MapMarker.Y / 16f));
            }

            if (markers.Count != 1)
            {
                if (markers.Count > 1)
                {
                    LogSwimmingShadowsMarkerAmbiguity(shadowSpot);
                }

                return false;
            }

            var markerPosition = markers[0];
            var matched = false;
            foreach (var spot in GatherBuddy.GameData.FishingSpots.Values)
            {
                if (!spot.IsShadowNode || spot.Territory.Id != territoryId || !IsMarkerNearSpot(markerPosition, spot))
                {
                    continue;
                }

                if (spot.Id != shadowSpot.Id)
                {
                    matched = false;
                    break;
                }

                matched = true;
            }

            if (!matched)
            {
                LogSwimmingShadowsMarkerAmbiguity(shadowSpot);
                return false;
            }

            position = markerPosition;
            return true;
        }

        private void LogSwimmingShadowsMarkerAmbiguity(FishingSpot shadowSpot)
        {
            var now = Environment.TickCount64;
            if (now - _lastSwimmingShadowsMarkerLog < SwimmingShadowsMarkerLogCooldown)
            {
                return;
            }

            _lastSwimmingShadowsMarkerLog = now;
            GatherBuddy.Log.Verbose($"Swimming Shadows marker could not be associated uniquely with {shadowSpot.Name}.");
        }

        private static bool IsMarkerNearSpot(Vector3 markerPosition, FishingSpot spot)
        {
            var marker = new Vector2(markerPosition.X, markerPosition.Z);
            var maximumDistanceSquared = NodeVisibilityDistance * NodeVisibilityDistance;
            foreach (var positions in spot.WorldPositions.Values)
            {
                foreach (var position in positions)
                {
                    if (Vector2.DistanceSquared(marker, new Vector2(position.X, position.Z)) <= maximumDistanceSquared)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
