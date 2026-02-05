using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using System;
using System.Linq;
using GatherBuddy.CustomInfo;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using GatherBuddy.Automation;
using GatherBuddy.Utilities;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public bool ShouldUseLuck(Gatherable? gatherable)
        {
            if (gatherable == null)
                return false;
            if (LuckUsed || GatheringWindowReader!.HasUnhidden)
                return false;
            if (!gatherable.GatheringData.IsHidden && !gatherable.IsTreasureMap)
                return false;

            return true;
        }

        public bool ShouldUseBountiful(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Bountiful, config.Bountiful, slot.Item, slot))
                return false;
            if (Player.Status.Any(s => s.StatusId == Actions.BountifulII.EffectId))
                return false;
            if (CalculateBountifulBonus(slot.Item) < config.Bountiful.MinYieldBonus)
                return false;

            return true;
        }

        public bool ShouldUseKingII(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Yield2, config.Yield2, slot.Item, slot))
                return false;

            return true;
        }

        public bool ShouldUseKingI(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Yield1, config.Yield1, slot.Item, slot))
                return false;

            return true;
        }

        private bool ShouldUseGivingLand(ItemSlot slot, ConfigPreset config)
        {
            if (!CheckConditions(Actions.GivingLand, config.GatherableActions.GivingLand, slot.Item, slot,
                    config.ChooseBestActionsAutomatically))
                return false;
            if (!IsGivingLandOffCooldown)
                return false;
            if (slot.Item.GetInventoryCount() > 9999 - GivingLandYield - slot.Yield)
                return false;

            return true;
        }

        private unsafe bool ShouldUseTwelvesBounty(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.TwelvesBounty, config.TwelvesBounty, slot.Item, slot))
                return false;
            if (slot.Item.GetInventoryCount() > 9999 - 3)
                return false;

            return true;
        }

        private unsafe bool ShouldUseGift1(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Gift1, config.Gift1, slot.Item, slot))
                return false;

            return true;
        }

        private unsafe bool ShouldUseGift2(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Gift2, config.Gift2, slot.Item, slot))
                return false;

            return true;
        }

        private unsafe bool ShouldUseTiding(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.Tidings, config.Tidings, slot.Item, slot))
                return false;

            return true;
        }


        private unsafe void DoActionTasks(IEnumerable<GatherTarget> target)
        {
            if (MasterpieceReader?.IsValid == true)
            {
                if (CurrentCollectableRotation == null)
                {
                    // Player clicked the item himself, or has just enabled auto-gather.
                    // We can't detect what item is being gathered from inside the GatheringMasterpiece addon, so we need to reopen it.
                    CloseGatheringAddons(false);
                    return;
                }

                DoCollectibles();
            }
            else
            {
                CurrentCollectableRotation = null;
                if (GatheringAddon != null && GatheringWindowReader != null)
                {
                    DoGatherWindowActions(target);
                }
            }
        }

        public FishingState LastState = FishingState.None;

        private unsafe void DoFishingTasks(GatherTarget target)
        {
        if (TryUseFoodAndMedicine())
            return;
        
        if (SpiritbondMax > 0)
        {
            if (GatherBuddy.Config.AutoGatherConfig.DeferMateriaExtractionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                return;
            
            if (IsGathering || IsFishing)
            {
                if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                {
                    AutoHook.SetPluginState?.Invoke(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                }
                QueueQuitFishingTasks();
                return;
            }

            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                AutoHook.SetPluginState?.Invoke(false);
                AutoHook.SetAutoStartFishing?.Invoke(false);
            }

            DoMateriaExtraction();
            TaskManager.Enqueue(() =>
            {
                if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                {
                    AutoHook.SetPluginState?.Invoke(true);
                    AutoHook.SetAutoStartFishing?.Invoke(true);
                }
            });
                return;
            }

            if (FreeInventorySlots < 20 && HasReducibleItems())
            {
                if (GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                    return;
                
                if (IsFishing || IsGathering)
                {
                    QueueQuitFishingTasks();
                    return;
                }

            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                TaskManager.Enqueue(() =>
                {
                    AutoHook.SetPluginState?.Invoke(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                });
            }

            ReduceItems(true, () =>
            {
                if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                {
                    AutoHook.SetPluginState?.Invoke(true);
                    AutoHook.SetAutoStartFishing?.Invoke(true);
                }
            });
                return;
            }

            if (RepairIfNeededForFishing())
                return;

            var state  = GatherBuddy.EventFramework.FishingState;
            var config = MatchConfigPreset(target.Fish!);
            
            if (!GatherBuddy.Config.AutoGatherConfig.UseAutoHook || !AutoHook.Enabled)
            {
                if (DoUseConsumablesWithoutCastTime(config, true))
                {
                    TaskManager.DelayNext(1000);
                    return;
                }
            }

            if (Throttler.Throttle("GBR Fishing", 500))
            {
                switch (state)
                {
                    case FishingState.None:
                    case FishingState.PoleReady:
                        HandleReady(target, config);
                        break;
                }
            }
        }

        private void HandleReady(GatherTarget target, ConfigPreset config)
        {
            LureSuccess = false;

            SetupAutoHookForFishing(target);

            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                var hasSnagStatus = Player.Status.Any(s => s.StatusId == 761);
                var needsSnagging = target.Fish?.Snagging == Snagging.Required;
                
                if (needsSnagging && !hasSnagStatus)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Enabling Snagging for {target.Fish!.Name[GatherBuddy.Language]}");
                    EnqueueActionWithDelay(() => UseAction(Actions.Snagging));
                    return;
                }
                else if (!needsSnagging && hasSnagStatus)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Disabling Snagging for {target.Fish!.Name[GatherBuddy.Language]}");
                    EnqueueActionWithDelay(() => UseAction(Actions.Snagging));
                    return;
                }

                var autoStartIsOn = AutoHook.GetAutoStartFishing?.Invoke() == true;
                if (autoStartIsOn)
                {
                    TaskManager.DelayNext(5000);
                    TaskManager.Enqueue(() =>
                    {
                        if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook
                            && AutoHook.Enabled
                            && AutoHook.GetAutoStartFishing?.Invoke() == true)
                        {
                            AutoHook.SetAutoStartFishing?.Invoke(false);
                        }
                    });
                }

                return;
            }
        }

        private bool NeedsIdenticalCast(GatherTarget target)
        {
            if (target.Fish == null)
                return false;
            if (LastCaughtFish == null)
                return false;
            if (PreviouslyCaughtFish == LastCaughtFish)
                return false;
            if (LastCaughtFish.FishId == target.Fish.FishId
             && Player.Status.All(s => !Actions.IdenticalCast.StatusProvide.Contains(s.StatusId)))
                return true;

            return false;
        }

        private bool NeedsSurfaceSlap(GatherTarget target)
        {
            if (target.Fish == null)
                return false;
            if (LastCaughtFish == null)
                return false;
            if (PreviouslyCaughtFish == LastCaughtFish)
                return false;
            if (LastCaughtFish.FishId != target.Fish.FishId
             && Player.Status.All(s => !Actions.SurfaceSlap.StatusProvide.Contains(s.StatusId)))
                return true;

            return false;
        }


        private bool HasPatienceStatus()
        {
            var patienceAction = GetCorrectPatienceAction();
            if (patienceAction == null)
                return true;

            var statuses = patienceAction.StatusProvide;
            return Player.Status.Any(s => statuses.Contains(s.StatusId));
        }

        private Actions.FishingAction? GetCorrectPatienceAction()
        {
            if (Player.Level >= Actions.PatienceII.MinLevel)
                return Actions.PatienceII;
            if (Player.Level >= Actions.Patience.MinLevel)
                return Actions.Patience;

            return null;
        }

        private unsafe void DoGatherWindowActions(IEnumerable<GatherTarget> target)
        {
            if (GatheringWindowReader == null)
                return;

            if (LastIntegrity > 0 && GatheringWindowReader.IntegrityRemaining > LastIntegrity + 1)
            {
                ActionSequence = null;
            }
            LastIntegrity = GatheringWindowReader.IntegrityRemaining;

            foreach (var t in target)
            {
                //Use The Giving Land out of order to gather random crystals.
                if (ShouldUseGivingLandOutOfOrder(t.Gatherable))
                {
                    EnqueueActionWithDelay(() => UseAction(Actions.GivingLand));
                    return;
                }
            }

            if (!LuckUsed && GatheringWindowReader.HasUnhidden)
            {
                // If there are unhidden items, Luck skill won't reveal anything new.
                LuckUsed = true;
            }

            foreach (var t in target)
            {
                if (!HasGivingLandBuff && ShouldUseLuck(t.Gatherable))
                {
                    LuckUsed = true;
                    EnqueueActionWithDelay(() => UseAction(Actions.Luck));
                    return;
                }
            }

            var (useSkills, slot) = GetItemSlotToGather(target);
            if (useSkills)
            {
                var configPreset = MatchConfigPreset(slot.Item);
                var config       = configPreset.GatherableActions;

                if (configPreset.ChooseBestActionsAutomatically)
                {
                    if (ShouldUseWise(GatheringWindowReader.IntegrityRemaining, GatheringWindowReader.IntegrityMax))
                    {
                        ActionSequence = null; //Recalculate rotation since we've got unaccounted 6 GP and 1 integrity.
                        EnqueueActionWithDelay(() => UseAction(Actions.Wise));
                    }
                    else
                    {
                        if (ActionSequence == null)
                        {
                            var task = RotationSolver.SolveAsync(slot, configPreset, GatheringWindowReader);

                            if (task.Wait(1))
                            {
                                ActionSequence = task.Result.AsEnumerable().GetEnumerator();
                            }
                            else
                            {
                                TaskManager.Enqueue(() =>
                                {
                                    if (task.IsCompleted)
                                        ActionSequence = task.Result.GetEnumerator();
                                    return task.IsCompleted;
                                });
                                AutoStatus = "Calculating best action sequence...";
                                return;
                            }
                        }

                        if (!ActionSequence.MoveNext())
                        {
                            ActionSequence = null;
                            EnqueueGatherItem(slot);
                        }
                        else
                        {
                            var action = ActionSequence.Current;
                            if (action != null)
                                EnqueueActionWithDelay(() => UseAction(action));
                            else
                                EnqueueGatherItem(slot);
                        }
                    }
                }
                else
                {
                    if (ShouldUseWise(GatheringWindowReader.IntegrityRemaining, GatheringWindowReader.IntegrityMax))
                        EnqueueActionWithDelay(() => UseAction(Actions.Wise));
                    else if (ShouldUseTwelvesBounty(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.TwelvesBounty));
                    else if (ShouldUseGift2(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Gift2));
                    else if (ShouldUseGift1(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Gift1));
                    else if (ShouldUseTiding(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Tidings));
                    else if (ShouldUseSolidAgeGatherables(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.SolidAge));
                    else if (ShouldUseGivingLand(slot, configPreset))
                        EnqueueActionWithDelay(() => UseAction(Actions.GivingLand));
                    else if (ShouldUseKingII(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Yield2));
                    else if (ShouldUseKingI(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Yield1));
                    else if (ShouldUseBountiful(slot, config))
                        EnqueueActionWithDelay(() => UseAction(Actions.Bountiful));
                    else
                        EnqueueGatherItem(slot);
                }
            }
            else
            {
                EnqueueGatherItem(slot);
            }
        }

        private bool ShouldUseGivingLandOutOfOrder(Gatherable? desiredItem)
        {
            if (GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown
             && desiredItem != null
             && desiredItem.NodeType == Enums.NodeType.Regular)
            {
                var anyCrystal = GetAnyCrystalInNode();
                return anyCrystal != null && ShouldUseGivingLand(anyCrystal, MatchConfigPreset(anyCrystal.Item));
            }

            return false;
        }

        private unsafe void UseAction(Actions.FishingAction act)
        {
            var amInstance = ActionManager.Instance();
            if (amInstance->GetActionStatus(ActionType.Action, act.ActionId) == 0)
            {
                //Communicator.Print("Action used: " + act.Name);
                amInstance->UseAction(ActionType.Action, act.ActionId);
            }
        }

        private unsafe void UseAction(Actions.BaseAction act)
        {
            var amInstance = ActionManager.Instance();
            if (amInstance->GetActionStatus(ActionType.Action, act.ActionId) == 0)
            {
                //Communicator.Print("Action used: " + act.Name);
                amInstance->UseAction(ActionType.Action, act.ActionId);
            }
        }

        private void EnqueueActionWithDelay(Action action, bool immediate = false)
        {
            var delay = GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            if (immediate)
                TaskManager.EnqueueImmediate(action);
            else
                TaskManager.Enqueue(action);

            //Always delay the next action by at least 1 tick (2 or 3 in fact in the current implementation).
            //There is a possibility that client state update is happening the same tick when CanAct becomes true, and GBR won't see it if executed before it is done.
            //Since GBR Update() may be called in the same tick after TaskManager gets CanAct == true (depending on call order in the Update event),
            //we must always add a delay, which adds 2 extra ticks.
            if (immediate)
            {
                TaskManager.EnqueueImmediate(() => CanAct);
                TaskManager.DelayNextImmediate((int)delay);
            }
            else
            {
                TaskManager.Enqueue(() => CanAct);
                TaskManager.DelayNext((int)delay);
            }
        }

        private unsafe void DoCollectibles()
        {
            if (MasterpieceReader?.IsValid != true || CurrentCollectableRotation == null)
                return;

            int collectibility = MasterpieceReader.CollectabilityCurrent;
            int integrity = MasterpieceReader.IntegrityCurrent;

            if (integrity > 0)
            {
                LastCollectability = collectibility;
                LastIntegrity      = integrity;

                var collectibleAction = CurrentCollectableRotation.GetNextAction(MasterpieceReader);

                EnqueueActionWithDelay(() => UseAction(collectibleAction));
            }
        }

        private static bool ShouldUseWise(int integrity, int maxIntegrity)
        {
            if (integrity == maxIntegrity)
                return false;
            if (Player.Level < Actions.Wise.MinLevel)
                return false;
            if (!Player.Status.Any(s => s.StatusId == Actions.SolidAge.EffectId))
                return false;

            return true;
        }

        private bool ShouldUseSolidAgeGatherables(ItemSlot slot, ConfigPreset.GatheringActionsRec config)
        {
            if (!CheckConditions(Actions.SolidAge, config.SolidAge, slot.Item, slot))
                return false;

            var yield = slot.Yield;
            if (Dalamud.Objects.LocalPlayer!.StatusList.Any(s => s.StatusId == Actions.Bountiful.EffectId))
                yield -= 1;
            if (Dalamud.Objects.LocalPlayer!.StatusList.Any(s => s.StatusId == Actions.BountifulII.EffectId))
                yield -= CalculateBountifulBonus(slot.Item);
            if (yield < config.SolidAge.MinYieldTotal)
                return false;

            return true;
        }

        private bool CheckConditions(Actions.BaseAction action, ConfigPreset.ActionConfig config, Gatherable item, ItemSlot slot,
            bool autoMode = false)
        {
            if (GatheringWindowReader == null)
                return false;
            // autoMode = true is used for TGL out-of-order check that occurs before the rotation solver kicks in.
            if (config.Enabled == false && !autoMode)
                return false;
            if (Player.Level < action.MinLevel)
                return false;
            if (Player.Object.CurrentGp < action.GpCost)
                return false;
            if (Player.Object.CurrentGp < config.MinGP && !autoMode)
                return false;
            if (Player.Object.CurrentGp > config.MaxGP && !autoMode)
                return false;
            if (action.EffectId != 0 && Player.Status.Any(s => s.StatusId == action.EffectId))
                return false;
            if (action.QuestId != 0 && !QuestManager.IsQuestComplete(action.QuestId))
                return false;
            if (action.EffectType is Actions.EffectType.CrystalsYield && !item.IsCrystal)
                return false;
            if (action.EffectType is Actions.EffectType.Integrity && GatheringWindowReader.IntegrityRemaining > Math.Min(2, GatheringWindowReader.IntegrityMax - 1))
                return false;
            if (action.EffectType is not Actions.EffectType.Other and not Actions.EffectType.GatherChance && slot.IsRare)
                    return false;
            if (config is ConfigPreset.ActionConfigIntegrity config2
             && (!autoMode && config2.MinIntegrity > GatheringWindowReader.IntegrityMax || (config2.FirstStepOnly || autoMode) && GatheringWindowReader.Touched))
                return false;
            if (config is ConfigPreset.ActionConfigBoon config3
             && (slot.BoonChance == -1 || !autoMode && (slot.BoonChance < config3.MinBoonChance || slot.BoonChance > config3.MaxBoonChance)))
                return false;
            if (action.EffectType is Actions.EffectType.BoonChance && slot.BoonChance == 100)
                return false;

            return true;
        }

        public static sbyte CalculateBountifulBonus(Gatherable item)
        {
            if (!QuestManager.IsQuestComplete(Actions.BountifulII.QuestId))
                return 1;

            try
            {
                var glvl      = item.GatheringData.GatheringItemLevel.RowId;
                var baseValue = WorldData.IlvConvertTable[(int)glvl].BaseGathering;
                var stat      = DiscipleOfLand.Gathering;

                if (stat >= baseValue * 11 / 10)
                    return 3;
                if (stat >= baseValue * 9 / 10)
                    return 2;

                return 1;
            }
            catch (KeyNotFoundException)
            {
                return 1;
            }
        }

        private bool ActivateGatheringBuffs(bool activateTruth)
        {
            if (!Player.Status.Any(s => s.StatusId == Actions.Prospect.EffectId) && Player.Level >= Actions.Prospect.MinLevel)
            {
                EnqueueActionWithDelay(() => UseAction(Actions.Prospect));
                return true;
            }

            if (!Player.Status.Any(s => s.StatusId == Actions.Sneak.EffectId) && Player.Level >= Actions.Sneak.MinLevel)
            {
                EnqueueActionWithDelay(() => UseAction(Actions.Sneak));
                return true;
            }

            if (activateTruth && !Player.Status.Any(s => s.StatusId == Actions.Truth.EffectId))
            {
                EnqueueActionWithDelay(() => UseAction(Actions.Truth));
                return true;
            }

            return false;
        }

        private void QueueStartFishingTasks()
        {
            EnqueueActionWithDelay(() => UseAction(Actions.Cast));
        }

        private void QueueQuitFishingTasks()
        {
            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                AutoHook.SetAutoStartFishing?.Invoke(false);
                AutoHook.SetPluginState?.Invoke(false);
                EnqueueEnsureAutoHookDisabled();
            }

            EnqueueActionWithDelay(() => UseAction(Actions.Quit));

            // Delay to make sure we stand up properly before continuing.
            TaskManager.DelayNext(3000);
        }
    }
}
