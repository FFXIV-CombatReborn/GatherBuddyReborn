using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private CollectableRotation? CurrentCollectableRotation;

        private unsafe bool HasCollectables()
        {
            if (!GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables
             || !CollectableTurnInRequirements.IsAvailable)
                return false;

            if (GatherBuddy.CollectableManager == null)
                return false;
            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            if (!thresholdState.ThresholdReached)
                return false;
            if (thresholdState.InventoryFullMode)
                GatherBuddy.Log.Debug($"[HasCollectables] Inventory threshold reached ({thresholdState.UsedSlots}/{thresholdState.TotalSlots}) with {thresholdState.CollectableCount} collectables - triggering turn-in");
            else
                GatherBuddy.Log.Debug($"[HasCollectables] Collectable threshold reached ({thresholdState.CollectableCount}) - triggering turn-in");

            return true;
        }

        private unsafe partial class CollectableRotation
        {
            public CollectableRotation(ConfigPreset config, Gatherable item, uint quantity)
            {
                this.config = config;
                shouldUseFullRotation = Player.Object?.CurrentGp >= config.CollectableActionsMinGP;
                this.item = item;
                this.quantity = quantity;
            }

            private readonly bool shouldUseFullRotation = false;
            private readonly ConfigPreset config;
            private readonly Gatherable item;
            private readonly uint quantity;

            [GeneratedRegex(@"\d+")]
            private static partial Regex NumberRegex();

            public Actions.BaseAction GetNextAction(GatheringMasterpieceReader masterpieceReader)
            {
                var player = Player.Object ?? throw new InvalidOperationException("Player object is null");
                var itemsLeft = (int)(quantity - item.GetTotalCount());

                if (itemsLeft <= 0 && GatherBuddy.Config.AutoGatherConfig.AbandonNodes)
                    throw new NoGatherableItemsInNodeException();

                var regex = NumberRegex();
                int collectability   = masterpieceReader.CollectabilityCurrent;
                int currentIntegrity = masterpieceReader.IntegrityCurrent;
                int maxIntegrity     = masterpieceReader.IntegrityMax;
                int scourColl        = masterpieceReader.ScourGain;
                int meticulousColl   = masterpieceReader.MeticulousGain;
                int brazenColl       = masterpieceReader.BrazenGainMax;

                if (ShouldUseWise(currentIntegrity, maxIntegrity))
                    return Actions.Wise;

                var (targetScore, minScore) = GetCollectabilityScores(masterpieceReader);

                if (collectability >= targetScore)
                {
                    if ((shouldUseFullRotation || config.CollectableAlwaysUseSolidAge)
                     && ShouldSolidAgeCollectables(player, currentIntegrity, maxIntegrity, itemsLeft))
                        return Actions.SolidAge;
                    else
                        return Actions.Collect;
                }

                if (currentIntegrity == 1
                 && collectability >= minScore)
                    return Actions.Collect;

                if (shouldUseFullRotation && NeedScrutiny(player, collectability, scourColl, meticulousColl, brazenColl, targetScore) && ShouldUseScrutiny(player))
                    return Actions.Scrutiny;

                if (meticulousColl + collectability >= targetScore
                 && ShouldUseMeticulous(player))
                    return Actions.Meticulous;

                if (Player.Status.Any(s => s.StatusId == 3911 /*Collector's High Standard*/) && ShouldUseBrazen(player))
                    return Actions.Brazen;

                if (scourColl + collectability >= targetScore
                 && ShouldUseScour(player))
                    return Actions.Scour;

                if (ShouldUseMeticulous(player))
                    return Actions.Meticulous;

                //Fallback path if some actions are disabled.
                if (Player.Status.Any(s => s.StatusId == 2418 /*Collector's Standard*/) && ShouldUseBrazen(player))
                    return Actions.Brazen;
                if (ShouldUseScour(player))
                    return Actions.Scour;
                if (ShouldUseBrazen(player))
                    return Actions.Brazen;

                throw new NoCollectableActionsException();
            }

            private (int targetScore, int minScore) GetCollectabilityScores(GatheringMasterpieceReader masterpieceReader)
            {
                if (config.CollectableManualScores)
                    return (config.CollectableTagetScore, config.CollectableMinScore);

                int targetScore, minScore;

                // Check reward tiers in descending order and use the first visible one for target score
                if (masterpieceReader.HighThreshold > 0)
                    targetScore = masterpieceReader.HighThreshold;
                else if (masterpieceReader.MidThreshold > 0)
                    targetScore = masterpieceReader.MidThreshold;
                else
                    targetScore = masterpieceReader.LowThreshold;

                // For minScore, pick the lowest non-zero threshold
                int[] thresholds = { masterpieceReader.LowThreshold, masterpieceReader.MidThreshold, masterpieceReader.HighThreshold };
                minScore = thresholds.Where(t => t > 0).DefaultIfEmpty(1).Min();

                // For custom deliveries and quest items, we always want max collectability
                if (item.GatheringData.Unknown3 is 3 or 4 or 6)
                    minScore = targetScore;

                GatherBuddy.Log.Verbose($"Using target collectability {targetScore} and minimum collectability {minScore} for {item.Name}.");
                return (targetScore, minScore);
            }

            private bool NeedScrutiny(IPlayerCharacter player, int collectability, int scourColl, int meticulousColl, int brazenColl, int targetScore)
            {
                if (scourColl + collectability >= targetScore && ShouldUseScour(player))
                    return false;
                if (meticulousColl + collectability >= targetScore && ShouldUseMeticulous(player))
                    return false;
                if (brazenColl + collectability >= targetScore && ShouldUseBrazen(player))
                    return false;

                return true;
            }
            private bool ShouldUseMeticulous(IPlayerCharacter player)
            {
                if (player.Level < Actions.Meticulous.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Meticulous.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Meticulous.MinGP
                 || player.CurrentGp > config.CollectableActions.Meticulous.MaxGP)
                    return false;

                return config.CollectableActions.Meticulous.Enabled;
            }

            private bool ShouldUseScour(IPlayerCharacter player)
            {
                if (player.Level < Actions.Brazen.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Brazen.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Scour.MinGP
                 || player.CurrentGp > config.CollectableActions.Scour.MaxGP)
                    return false;

                return config.CollectableActions.Scour.Enabled;
            }

            private bool ShouldUseBrazen(IPlayerCharacter player)
            {
                if (player.Level < Actions.Meticulous.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Meticulous.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Brazen.MinGP
                 || player.CurrentGp > config.CollectableActions.Brazen.MaxGP)
                    return false;

                return config.CollectableActions.Brazen.Enabled;
            }

            private bool ShouldUseScrutiny(IPlayerCharacter player)
            {
                if (player.Level < Actions.Scrutiny.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Scrutiny.GpCost)
                    return false;
                if (Player.Status.Any(s => s.StatusId == Actions.Scrutiny.EffectId))
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Scrutiny.MinGP
                 || player.CurrentGp > config.CollectableActions.Scrutiny.MaxGP)
                    return false;

                return config.CollectableActions.Scrutiny.Enabled;
            }

            private bool ShouldSolidAgeCollectables(IPlayerCharacter player, int integrity, int maxIntegrity, int itemsLeft)
            {
                if (integrity > Math.Min(2, maxIntegrity - 1))
                    return false;
                if (itemsLeft <= integrity)
                    return false;
                if (player.Level < Actions.SolidAge.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.SolidAge.GpCost)
                    return false;
                if (Player.Status.Any(s => s.StatusId == Actions.SolidAge.EffectId))
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.SolidAge.MinGP
                 || player.CurrentGp > config.CollectableActions.SolidAge.MaxGP)
                    return false;

                return config.CollectableActions.SolidAge.Enabled;
            }
        }
    }
}
