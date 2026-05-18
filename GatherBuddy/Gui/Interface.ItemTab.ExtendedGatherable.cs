using System;
using System.Linq;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Time;

namespace GatherBuddy.Gui;

public partial class Interface
{
    public class ExtendedGatherable
    {
        public enum LogState
        {
            Unknown,
            NotTracked,
            Ungathered,
            Gathered,
        }
        public Gatherable              Data;
        public ISharedImmediateTexture Icon;
        public string                  Territories;
        public string                  Uptimes;
        public string                  Folklore;
        public string                  Level;
        public string                  NodeNames;
        public string                  Expansion;
        public string                  Aetherytes;
        public LogState                GatheredState;
        public bool                    Leveling;
        public bool                    NotTrackedByGatheringLog;

        private bool _gatheredStatusFailureLogged;

        public (ILocation, TimeInterval) Uptime
            => GatherBuddy.UptimeManager.BestLocation(Data);

        public ExtendedGatherable(Gatherable data)
        {
            Data = data;
            Icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(data.ItemData.Icon));

            Territories = string.Join("\n", data.NodeList.Select(n => n.Territory.Name).Distinct());
            if (!Territories.Contains('\n'))
                Territories = '\0' + Territories;

            Folklore = data.NodeList.Count == 0 || data.NodeList.Any(n => n.Folklore.Length == 0)
                ? string.Empty
                : data.NodeList.First().Folklore;
            Uptimes = data.NodeType switch
            {
                NodeType.Regular => "Always",
                NodeType.Unknown => "Unknown",
                _                => data.NodeList.Select(n => n.Times).Aggregate(BitfieldUptime.Combine).PrintHours(true),
            };
            Level     = Data.LevelString();
            NodeNames = string.Join("\n", data.NodeList.Select(n => n.Name).Distinct());
            if (!NodeNames.Contains('\n'))
                NodeNames = '\0' + NodeNames;

            Expansion = data.ExpansionIdx switch
            {
                0 => "ARR",
                1 => "HW",
                2 => "SB",
                3 => "ShB",
                4 => "EW",
                5 => "DT",
                _ => "Unk",
            };
            Aetherytes = string.Join("\n",
                data.NodeList.Where(n => n.ClosestAetheryte != null).Select(n => n.ClosestAetheryte!.Name).Distinct());
            if (!Aetherytes.Contains('\n'))
                Aetherytes = '\0' + Aetherytes;

            var levelingStates = data.NodeList.Select(n => n.IsLeveling).Distinct().ToList();
            Leveling = levelingStates.Contains(true);
            if (levelingStates.Count > 1)
                GatherBuddy.Log.Debug($"[GatherablesTab] Gatherable {Data.ItemId} has mixed leveling classification across nodes.");

            NotTrackedByGatheringLog = Data.ItemData.IsCollectable
             || Data.ItemData.AlwaysCollectable
             || Data.IsTreasureMap
             || Data.ItemData.ItemSearchCategory.RowId == 0;

            UpdateGatheredStatus();
        }

        public void UpdateGatheredStatus()
        {
            if (NotTrackedByGatheringLog)
            {
                GatheredState = LogState.NotTracked;
                _gatheredStatusFailureLogged = false;
                return;
            }
            if (Data.GatheringId == 0 || Data.GatheringId > ushort.MaxValue)
            {
                if (!_gatheredStatusFailureLogged)
                {
                    GatherBuddy.Log.Debug(
                        $"[GatherablesTab] Unable to check gathered status for item {Data.ItemId}: invalid gathering item id {Data.GatheringId}.");
                    _gatheredStatusFailureLogged = true;
                }
                GatheredState = LogState.Unknown;
                return;
            }

            try
            {
                GatheredState = QuestManager.IsGatheringItemGathered((ushort)Data.GatheringId)
                    ? LogState.Gathered
                    : LogState.Ungathered;
                _gatheredStatusFailureLogged = false;
            }
            catch (Exception ex)
            {
                if (!_gatheredStatusFailureLogged)
                {
                    GatherBuddy.Log.Debug(
                        $"[GatherablesTab] Failed to check gathered status for item {Data.ItemId} / gathering item {Data.GatheringId}: {ex.Message}");
                    _gatheredStatusFailureLogged = true;
                }
                GatheredState = LogState.Unknown;
            }
        }
    }
}
