using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using ElliLib.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.Automation;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Time;
using GatherBuddy.Utilities;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Fish = GatherBuddy.Classes.Fish;
using GatheringType = GatherBuddy.Enums.GatheringType;
using NodeType = GatherBuddy.Enums.NodeType;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather : IDisposable
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager                  =  new(Dalamud.Framework);
            TaskManager.ShowDebug        =  false;
            _plugin                      =  plugin;
            _soundHelper                 =  new SoundHelper();
            _advancedUnstuck             =  new();
            _activeItemList              =  new ActiveItemList(plugin.AutoGatherListsManager, this);
            _diadem                      =  new Diadem();
            ArtisanExporter              =  new Reflection.ArtisanExporter(plugin.AutoGatherListsManager);
            Dalamud.Chat.CheckMessageHandled += OnMessageHandled;
            Dalamud.ToastGui.QuestToast += OnQuestToast;
            //Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
            _plugin.FishRecorder.Parser.CaughtFish += OnFishCaught;
        }
        public Fish? LastCaughtFish { get; private set; }
        public Fish? PreviouslyCaughtFish { get; private set; }
        private void OnFishCaught(Fish arg1, ushort arg2, byte arg3, bool arg4, bool arg5)
        {
            PreviouslyCaughtFish = LastCaughtFish;
            LastCaughtFish       = arg1;
            
            if (_consecutiveAmissCount > 0)
            {
                GatherBuddy.Log.Information($"[AutoGather] Fish caught successfully! Amiss counter reset from {_consecutiveAmissCount} to 0.");
                _consecutiveAmissCount = 0;
            }
            
            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                if (_currentAutoHookTarget?.Fish?.ItemId == arg1.ItemId)
                {
                    var targetQuantity = _currentAutoHookTarget.Value.Quantity;
                    var currentCount = arg1.GetInventoryCount();
                    
                    if (currentCount >= targetQuantity)
                    {
                        GatherBuddy.Log.Information($"[AutoGather] Target fish count reached ({currentCount}/{targetQuantity}), stopping fishing immediately");
                        AutoHook.SetPluginState?.Invoke(false);
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                        
                        TaskManager.Enqueue(() =>
                        {
                            if (IsFishing)
                            {
                                CleanupAutoHook();
                                QueueQuitFishingTasks();
                                _activeItemList.ForceRefresh();
                            }
                            return true;
                        });
                    }
                }
            }
        }

        // Track the current gather target for robust node handling
        private GatherTarget? _currentGatherTarget;
        private volatile bool _fishDetectedPlayer = false;
        private volatile bool _fishWaryDetected = false;
        private int _consecutiveAmissCount = 0;
        
        private const uint FishWaryMessageId = 5517;
        private const uint FishAmissMessageId = 3516;
        private Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.LogMessage>? _cachedLogMessages;

        private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
        {
            try
            {
                var text = message.TextValue;
                
                if (string.IsNullOrEmpty(text) || text.Length < 10)
                    return;
                
                _cachedLogMessages ??= Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
                if (_cachedLogMessages == null)
                    return;
                
                var logMsg = _cachedLogMessages.FirstOrDefault(x => x.Text.ExtractText() == text);
                
                if (logMsg.RowId != 0)
                {
                    if (logMsg.RowId == FishWaryMessageId)
                    {
                        GatherBuddy.Log.Warning($"[AutoGather] Fish wary warning (ID: {logMsg.RowId}): '{text}' - simple relocation.");
                        _fishWaryDetected = true;
                    }
                    else if (logMsg.RowId == FishAmissMessageId)
                    {
                        _consecutiveAmissCount++;
                        GatherBuddy.Log.Warning($"[AutoGather] Fish amiss detected (ID: {logMsg.RowId}, count: {_consecutiveAmissCount}): '{text}'");
                        _fishDetectedPlayer = true;
                    }
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"[AutoGather] Failed to handle quest toast: {e}");
            }
        }

        private void OnMessageHandled(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            try
            {
                if (type is (XivChatType)2243)
                {
                    var text = message.TextValue;
                    var id = Dalamud.GameData.GetExcelSheet<LogMessage>()
                        ?.FirstOrDefault(x => x.Text.ToString() == text).RowId;

                    LureSuccess = GatherBuddy.GameData.Fishes.Values.FirstOrDefault(f => f.FishData?.Unknown_70_1 == text) != null;

                    if (LureSuccess)
                        return;

                    LureSuccess = id is 5565 or 5569;
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to handle message: {e}");
            }
        }

        private readonly GatherBuddy      _plugin;
        private readonly SoundHelper      _soundHelper;
        private readonly AdvancedUnstuck  _advancedUnstuck;
        private readonly ActiveItemList   _activeItemList;
        private readonly Diadem           _diadem;

        public Reflection.ArtisanExporter ArtisanExporter;
        public TaskManager                TaskManager { get; }

        private           bool             _enabled { get; set; } = false;
        private           bool             _disabledBySystem = false;

        public bool Waiting
        {
            get;
            private set
            {
                field                                  = value;
            }
        } = false;

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;

                if (!value)
                {
                    AutoStatus = "Idle...";
                    TaskManager.Abort();
                    YesAlready.Unlock();

                    _activeItemList.Reset();
                    Waiting                    = false;
                    ActionSequence             = null;
                    CurrentCollectableRotation = null;
                    
                    CleanupAutoHook();

                    StopNavigation();
                    CurrentFarNodeLocation   = null;
                    _homeWorldWarning        = false;
                    _diademQueuingInProgress = false;
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.Clear();
                    _lastAetherTarget = DateTime.MinValue;
                    _diademPathIndex = -1;
                    _fishDetectedPlayer = false;
                    _fishWaryDetected = false;
                    _consecutiveAmissCount = 0;
                    Dalamud.ToastGui.ErrorToast -= HandleNodeInteractionErrorToast;

                    ClearSpearfishingSessionData();
                    
                    if (_autoRetainerMultiModeEnabled && AutoRetainer.IsEnabled)
                    {
                        try
                        {
                            AutoRetainer.DisableAllFunctions?.Invoke();
                            _autoRetainerMultiModeEnabled = false;
                            _originalCharacterNameWorld = null;
                            GatherBuddy.Log.Debug("Disabled AutoRetainer MultiMode on AutoGather shutdown");
                        }
                        catch (Exception e)
                        {
                            GatherBuddy.Log.Error($"Failed to disable AutoRetainer MultiMode: {e.Message}");
                        }
                    }
                    
                    if (GatherBuddy.CollectableManager?.IsRunning == true)
                    {
                        GatherBuddy.Log.Debug("[AutoGather] Stopping collectable turn-in (user disabled AutoGather)");
                        GatherBuddy.CollectableManager?.Stop();
                    }
                    
                    _disabledBySystem = false;
                }
            else
            {
                if (!ValidateActiveItemsPerception())
                {
                    return;
                }
                
                WentHome = true; //Prevents going home right after enabling auto-gather
                if (AutoHook.Enabled)
                {
                    AutoHook.SetPluginState(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                }
                YesAlready.Lock();
                DisableQuickGathering();
            }

                _enabled = value;
                _plugin.Ipc.AutoGatherEnabledChanged(value);
            }
        }

        public bool GoHome()
        {
            StopNavigation();

            if (WentHome)
                return false;

            WentHome = true;

            if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                return false;

            if (Lifestream.Enabled && !Lifestream.IsBusy())
            {
                var command = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
                if (command.Contains("/li "))
                    command = command.Replace("/li ", "");
                Lifestream.ExecuteCommand(command);
                TaskManager.EnqueueImmediate(() => !Lifestream.IsBusy(), 120000, "Wait until Lifestream is done");
                return true;
            }
            else
            {
                GatherBuddy.Log.Warning("Lifestream not found or not ready");
                return false;
            }
        }

        private unsafe void DisableQuickGathering()
        {
            try
            {
                var raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule == null)
                    return;

                raptureAtkModule->QuickGatheringEnabled = false;
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to disable Quick Gathering: {e.Message}");
            }
        }

        private class NoGatherableItemsInNodeException : Exception
        { }

        private class NoCollectableActionsException : Exception
        { }

        private bool _diademQueuingInProgress = false;
        private bool _homeWorldWarning        = false;
        private bool _autoRetainerMultiModeEnabled = false;
        private string? _originalCharacterNameWorld = null;
        private bool _autoRetainerWasEnabledBeforeDiadem = false;

        public void DoAutoGather()
        {
            var currentTerritory = Dalamud.ClientState.TerritoryType;
            if (_lastTerritory != currentTerritory)
            {
                _lastTerritory = currentTerritory;
                _diademPathIndex = -1;
                
                var isInDiademOrFirmament = currentTerritory == Diadem.Territory.Id;
                var wasInDiademOrFirmament = _lastTerritory == Diadem.Territory.Id;
                
                if (isInDiademOrFirmament && !wasInDiademOrFirmament)
                {
                    _autoRetainerWasEnabledBeforeDiadem = GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode;
                    if (_autoRetainerWasEnabledBeforeDiadem)
                    {
                        GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode = false;
                        GatherBuddy.Log.Information("Temporarily disabled AutoRetainer integration while in Diadem/Firmament");
                    }
                }
                else if (!isInDiademOrFirmament && wasInDiademOrFirmament)
                {
                    if (_autoRetainerWasEnabledBeforeDiadem)
                    {
                        GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode = true;
                        GatherBuddy.Log.Information("Re-enabled AutoRetainer integration after leaving Diadem/Firmament");
                        _autoRetainerWasEnabledBeforeDiadem = false;
                    }
                }
            }

            // Always check these first
            if (!IsGathering)
                LuckUsed = false; //Reset the flag even if auto-gather was disabled mid-gathering

            if (!Enabled)
            {
                return;
            }

            // If we are not gathering and _currentGatherTarget is set, we just finished gathering or left the node
            if (!IsGathering && _currentGatherTarget != null)
            {
                var gatherTarget = _currentGatherTarget;
                // Mark the node as visited if possible
                var targetNode = Dalamud.Targets.Target ?? Dalamud.Targets.PreviousTarget;
                if (targetNode != null && targetNode.ObjectKind is ObjectKind.GatheringPoint)
                {
                    _activeItemList.MarkVisited(targetNode);
                    var gatherable = gatherTarget.Value.Gatherable;
                    var node = gatherTarget.Value.Node;
                    
                    if (gatherable != null && (gatherable.NodeType == NodeType.Regular || gatherable.NodeType == NodeType.Ephemeral)
                        && VisitedNodes.LastOrDefault() != targetNode.BaseId
                        && node != null && node.WorldPositions.ContainsKey(targetNode.BaseId))
                    {
                        FarNodesSeenSoFar.Clear();

                        while (VisitedNodes.Count > (node.WorldPositions.Count <= 4 ? 2 : 4) - 1)
                            VisitedNodes.RemoveAt(0);

                        if (node.WorldPositions.Count > 2)
                            VisitedNodes.Add(targetNode.BaseId);

                    }
                }
                // Unset the current gather target when leaving the node
                _currentGatherTarget = null;
            }


                        //try
            //{
            //    if (!NavReady)
            //    {
            //        AutoStatus = "Waiting for Navmesh...";
            //        return;
            //    }
            //}
            //catch (Exception)
            //{
            //    //GatherBuddy.Log.Error(e.Message);
            //    AutoStatus = "vnavmesh communication failed. Do you have it installed??";
            //    return;
            //}

            if (HandleFishingCollectable())
                return;

            HandlePathfinding(); // This should be done before checking TaskManager

            if (Dalamud.Conditions[ConditionFlag.Jumping61] && IsPathing) // Jumping Windmire
                StopNavigation();

            if (TaskManager.IsBusy)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            if (!_homeWorldWarning && !Functions.OnHomeWorld())
            {
                _homeWorldWarning = true;
                Communicator.PrintError("You are not on your home world, some items will not be gatherable.");
            }

            if (DiscipleOfLand.NextTreasureMapAllowance == DateTime.MinValue)
            {
                //Wait for timer refresh
                AutoStatus = "Refreshing timers...";
                DiscipleOfLand.RefreshNextTreasureMapAllowance();
                return;
            }

            if (!CanAct && !_diademQueuingInProgress)
            {
                if (!Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction])
                    AutoStatus = "Player is busy...";
                return;
            }


            if (FreeInventorySlots == 0)
            {
                if (GatherBuddy.CollectableManager?.IsRunning == true)
                {
                    AutoStatus = "Turning in collectables...";
                    return;
                }
                
                if (HasReducibleItems())
                {
                    if (Player.Job == 18 /* FSH */ && GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                    {
                        return;
                    }
                    else if (IsGathering)
                        CloseGatheringAddons();
                    else
                        ReduceItems(false);
                }
                else if (HasCollectables())
                {
                    GatherBuddy.Log.Information("[AutoGather] Inventory full with collectables - starting turn-in");
                    AutoStatus = "Turning in collectables...";
                    if (IsGathering)
                        CloseGatheringAddons();
                    else
                        GatherBuddy.CollectableManager?.Start();
                }
                else
                {
                    AbortAutoGather("Inventory is full");
                }

                return;
            }

            if (IsGathering)
            {
                // Set the current gather target when entering a node
                if (_currentGatherTarget == null)
                {
                    _currentGatherTarget = _activeItemList.CurrentOrDefault;
                }

                IEnumerable<GatherTarget> gatherTarget = _currentGatherTarget != null ? new[] { (GatherTarget)_currentGatherTarget } : Array.Empty<GatherTarget>();

                if (!GatherBuddy.Config.AutoGatherConfig.DoGathering)
                    return;

                StopNavigation();

                if (Player.Job == 18 /* FSH */)
                {
                    AutoStatus = "Fishing...";
                    var fish = _currentGatherTarget.GetValueOrDefault();
                    var isSpearfishing = Dalamud.Targets.Target?.ObjectKind == ObjectKind.GatheringPoint;
                    
                    if (isSpearfishing && fish.Fish != null)
                    {
                        _wasGatheringSpearfish = true;
                        _wasAtShadowNode = _currentGatherTarget?.FishingSpot?.IsShadowNode == true;
                        
                        var currentFishId = fish.Fish.ItemId;
                        var targetFishId = _currentAutoHookTarget?.Fish?.ItemId ?? 0;
                        var now = DateTime.Now;
                        
                        if (!_currentAutoHookTarget.HasValue || targetFishId != currentFishId)
                        {
                            SetupAutoHookForFishing(fish);
                            _lastAutoHookSetupTime = now;
                            _autoHookSetupComplete = false;
                        }
                        else if (!_autoHookSetupComplete && (now - _lastAutoHookSetupTime).TotalSeconds >= 1.0)
                        {
                            SetupAutoHookForFishing(fish);
                            _lastAutoHookSetupTime = now;
                        }
                        return;
                    }

                    var nextTarget = _activeItemList.GetNextOrDefault();
                    if (!isSpearfishing && (nextTarget == default || nextTarget.Item != _currentGatherTarget?.Item))
                    {
                        if (IsFishing && AutoHook.Enabled)
                        {
                            // Wait until fishing is finished before quitting, as spawn conditions snapshot when the line is cast.
                            AutoHook.SetAutoStartFishing(false);
                        }
                        else
                        {
                            CleanupAutoHook();
                            QueueQuitFishingTasks();
                        }
                        return;
                    }

                    if (GatherBuddy.Config.AutoGatherConfig.UseNavigation)
                    {
                        DoFishMovement(fish);
                    }
                    DoFishingTasks(fish);
                    return;
                }

                AutoStatus = "Gathering...";

                try
                {
                    DoActionTasks(gatherTarget);
                }
                catch (NoGatherableItemsInNodeException)
                {
                    CloseGatheringAddons();
                }
                catch (NoCollectableActionsException)
                {
                    Communicator.PrintError(
                        "Unable to pick a collectability increasing action to use. Make sure that at least one of the collectable actions is enabled.");
                    AbortAutoGather();
                }


                return;
            }

            if (AutoRetainer.IsEnabled && GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode)
            {
                if (ShouldWaitForAutoRetainer())
                {
                    Waiting = true;
                    _plugin.Ipc.AutoGatherWaiting();
                    return;
                }
            }

            if (_wasGatheringSpearfish)
            {
                GatherBuddy.Log.Debug("[AutoGather] Finished spearfishing, updating catches");
                _wasGatheringSpearfish = false;
                GatherBuddy.Log.Debug($"[AutoGather] Was at shadow node: {_wasAtShadowNode}");
                
                // If we just finished at a shadow node, clear session data FIRST to allow respawn
                if (_wasAtShadowNode)
                {
                    GatherBuddy.Log.Information("[AutoGather] Finished fishing at shadow node, clearing session data to allow respawn");
                    ClearSpearfishingSessionData();
                    _wasAtShadowNode = false;
                }
                else
                {
                    // Only update catches if we weren't at a shadow node
                    UpdateSpearfishingCatches();
                }
                
                _activeItemList.ForceRefresh();
            }
            
            ActionSequence             = null;
            CurrentCollectableRotation = null;

            //Cache IPC call results
            var isPathGenerating = IsPathGenerating;
            var isPathing        = IsPathing;

            switch (_advancedUnstuck.Check(CurrentDestination, isPathing))
            {
                case AdvancedUnstuckCheckResult.Pass: break;
                case AdvancedUnstuckCheckResult.Fail:
                    StopNavigation();
                    AutoStatus = $"Advanced unstuck in progress!";
                    return;
            }

            if (isPathGenerating)
            {
                AutoStatus = "Generating path...";
                return;
            }

            if (Player.Job is 17 /* BTN */ or 16 /* MIN */ or 18 /* FSH */
             && !isPathing
             && !Dalamud.Conditions[ConditionFlag.Mounted])
            {
                if (TryUseFoodAndMedicine())
                    return;

                if (SpiritbondMax > 0)
                {
                    if (Player.Job == 18 /* FSH */ && GatherBuddy.Config.AutoGatherConfig.DeferMateriaExtractionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                    {
                        return;
                    }
                    else
                    {
                        GatherBuddy.Log.Debug($"[Materia] Triggering extraction. IsGathering={IsGathering}, SpiritbondMax={SpiritbondMax}");
                        if (IsGathering)
                        {
                            QueueQuitFishingTasks();
                        }

                        DoMateriaExtraction();
                        return;
                    }
                }

                if (FreeInventorySlots < 20 && HasReducibleItems())
                {
                    if (Player.Job == 18 /* FSH */ && GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                    {
                        return;
                    }
                    else
                    {
                        ReduceItems(false);
                        return;
                    }
                }
            }

            if (TryUseAetherCannon()) return;

            var next = _activeItemList.GetNextOrDefault();

            if (next.Fish != null)
            {
                if (!GatherBuddy.Config.AutoGatherConfig.FishDataCollection)
                {
                    Communicator.PrintError(
                        "You have fish on your auto-gather list but you have not opted in to fishing data collection. Auto-gather cannot continue. Please enable fishing data collection in your configuration options or remove fish from your auto-gather lists.");
                    AbortAutoGather();
                    return;
                }

                if (!AutoHook.Enabled)
                {
                    Communicator.PrintError(
                        "[GatherBuddyReborn] You have fish on your auto-gather list but AutoHook is not installed or enabled. Auto-gather cannot continue. Please install and enable AutoHook or remove fish from your auto-gather lists.");
                    AbortAutoGather();
                    return;
                }
            }

            if (Diadem.IsInside && GatherBuddy.Config.AutoGatherConfig.DiademFarmCloudedNodes && _activeItemList.IsCloudedNodeConsumed)
            {
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                if (_activeItemList.Any(x => x.Node?.NodeType == NodeType.Clouded && x.Node.UmbralWeather.Id == currentWeather))
                {
                    GatherBuddy.Log.Information($"[Umbral] Gathered umbral node - leaving Diadem to reset session");
                    StopNavigation();
                    LeaveTheDiadem();
                    return;
                }
            }

            if (next == default)
            {
                if (!_activeItemList.HasItemsToGather)
                {
                    AbortAutoGather();
                    return;
                }

                if (GatherBuddy.CollectableManager?.IsRunning == true)
                {
                    AutoStatus = "Turning in collectables...";
                    return;
                }

                if (HasCollectables())
                {
                    AutoStatus = "Turning in collectables...";
                    GatherBuddy.CollectableManager?.Start();
                    return;
                }

                var waitAtAetheryte = false;
                if (GatherBuddy.Config.AutoGatherConfig.TeleportToNextNode)
                {
                    var nextTimed = _activeItemList.PeekNextTimed();
                    waitAtAetheryte = nextTimed != default;
                    if (waitAtAetheryte && nextTimed.Location.Territory.Id != currentTerritory)
                    {
                        // Replace next target and fall through to teleport to its location.
                        next = nextTimed;
                    }
                }

                if (!waitAtAetheryte && GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle)
                    if (GoHome())
                        return;

                if (HasReducibleItems())
                {
                    if (Player.Job == 18 /* FSH */)
                    {
                        if (GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                        {
                            return;
                        }
                        else
                        {
                            if (IsGathering)
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
                        }
                    }
                    else
                    {
                        ReduceItems(true);
                    }

                    return;
                }

                if (next == default)
                {
                    if (!Waiting)
                    {
                        Waiting = true;
                        _plugin.Ipc.AutoGatherWaiting();
                    }

                    AutoStatus = "No available items to gather";
                    return;
                }
            }

            Waiting = false;

            if (next.Item.ItemData.IsCollectable
                 && !CheckCollectablesUnlocked(next.Location.GatheringType.ToGroup()))
            {
                AbortAutoGather();
                return;
            }

            if (RepairIfNeeded())
                return;

            if (GatherBuddy.CollectableManager?.IsRunning == true)
            {
                AutoStatus = "Turning in collectables...";
                return;
            }
            
            if (HasCollectables())
            {
                AutoStatus = "Turning in collectables...";
                GatherBuddy.CollectableManager?.Start();
                return;
            }

            if (!GatherBuddy.Config.AutoGatherConfig.UseNavigation)
            {
                AutoStatus = "Waiting for Gathering Point... (No Nav Mode)";
                return;
            }

            var territoryId = currentTerritory;
            var targetTerritoryId = next.Location.Territory.Id;
            
            if (((territoryId == 129 && targetTerritoryId == 128)
             || (territoryId == 128 && targetTerritoryId == 129)
             || (territoryId == 132 && targetTerritoryId == 133)
             || (territoryId == 133 && targetTerritoryId == 132)
             || (territoryId == 130 && targetTerritoryId == 131)
             || (territoryId == 131 && targetTerritoryId == 130)) && Lifestream.Enabled)
            {
                if (!Lifestream.IsBusy())
                {
                    if (Dalamud.Conditions[ConditionFlag.Gathering])
                    {
                        AutoStatus = "Closing gathering window before teleport...";
                        CloseGatheringAddons();
                        return;
                    }
                    AutoStatus = "Using aethernet...";
                    StopNavigation();
                    string name = string.Empty;
                    var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
                    var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                    
                    var targetTerritory = territorySheet.GetRow((uint)targetTerritoryId);
                    var aethernetShard = aetheryteSheet.FirstOrDefault(a => 
                        a.Territory.RowId == targetTerritoryId && 
                        !a.IsAetheryte && 
                        a.AethernetName.RowId > 0);
                    
                    if (aethernetShard.RowId > 0)
                    {
                        name = aethernetShard.AethernetName.Value.Name.ToString();
                    }
                    else
                    {
                        name = targetTerritory.PlaceName.Value.Name.ToString();
                    }

                    TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                    TaskManager.DelayNext(1000);
                    TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                }

                return;
            }
            
            var housingWardTerritories = new uint[] { 339, 340, 341, 649, 641 };
            var isTargetHousingWard = housingWardTerritories.Contains((uint)targetTerritoryId);
            
            if (isTargetHousingWard && Lifestream.Enabled)
            {
                var canAccessFromCurrentTerritory = (territoryId == 129 && targetTerritoryId == 339)  // Limsa -> Mist
                                                  || (territoryId is 130 or 131 && targetTerritoryId == 341)  // Ul'dah -> Goblet
                                                  || (territoryId == 132 && targetTerritoryId == 340)  // Gridania -> Lavender
                                                  || (territoryId == 418 && targetTerritoryId == 649)  // Foundation -> Empyreum
                                                  || (territoryId == 628 && targetTerritoryId == 641); // Kugane -> Shirogane
                
                if (canAccessFromCurrentTerritory)
                {
                    if (!Lifestream.IsBusy())
                    {
                        if (IsFishing)
                        {
                            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                            {
                                AutoHook.SetPluginState?.Invoke(false);
                                AutoHook.SetAutoStartFishing?.Invoke(false);
                            }
                            AutoStatus = "Closing fishing before teleport...";
                            QueueQuitFishingTasks();
                            return;
                        }
                        
                        if (Dalamud.Conditions[ConditionFlag.Gathering])
                        {
                            AutoStatus = "Closing gathering window before teleport...";
                            CloseGatheringAddons();
                            return;
                        }
                        
                        AutoStatus = "Teleporting to housing ward...";
                        StopNavigation();
                        
                        string wardCommand = targetTerritoryId switch
                        {
                            339 => "mist 1 1",
                            341 => "goblet 1 1",
                            340 => "lavender 1 1",
                            649 => "empyreum 1 1",
                            641 => "shirogane 1 1",
                            _ => ""
                        };
                        
                        if (!string.IsNullOrEmpty(wardCommand))
                        {
                            TaskManager.Enqueue(() => Lifestream.ExecuteCommand(wardCommand));
                            TaskManager.DelayNext(1000);
                            TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                        }
                    }
                    
                    return;
                }
            }
            
            //Idyllshire to The Dravanian Hinterlands
            if (territoryId == 478 && next.Location.Territory.Id == 399)
            {
                var aetheryte = Dalamud.Objects.Where(x => x.ObjectKind == ObjectKind.Aetheryte && x.IsTargetable)
                    .OrderBy(x => x.Position.DistanceToPlayer()).FirstOrDefault();
                if (aetheryte != null)
                {
                    if (aetheryte.Position.DistanceToPlayer() > 10)
                    {
                        AutoStatus = "Moving to aetheryte...";
                        if (!isPathing && !isPathGenerating)
                            Navigate(aetheryte.Position, false);
                    }
                    else if (!Lifestream.IsBusy())
                    {
                        if (Dalamud.Conditions[ConditionFlag.Gathering])
                        {
                            AutoStatus = "Closing gathering window before teleport...";
                            CloseGatheringAddons();
                            return;
                        }
                        AutoStatus = "Teleporting...";
                        StopNavigation();
                        var xCoord = next.Location.DefaultXCoord;
                        var exit = xCoord < 2000 ? 91u : 92u;
                        var name = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRow(exit).AethernetName.Value.Name.ToString();

                        TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                        TaskManager.DelayNext(1000);
                        TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                    }

                    return;
                }
            }

            if (territoryId == 886 && next.Location.Territory.Id == Diadem.Territory.Id)
            {
                if (JobAsGatheringType == GatheringType.Unknown)
                {
                    var requiredGatheringType = next.Location.GatheringType.ToGroup();
                    if (ChangeGearSet(requiredGatheringType, 2400))
                    {
                        return;
                    }
                    else
                    {
                        AbortAutoGather();
                        return;
                    }
                }
                
                var dutyNpc                    = Dalamud.Objects.FirstOrDefault(o => o.BaseId == 1031694);
                var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                GatherBuddy.Log.Verbose($"Addons: {selectStringAddon}, {talkAddon}, {selectYesNoAddon}, {contentsFinderConfirmAddon}");
                if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                {
                    AutoStatus = "Moving to Diadem NPC...";
                    var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000).GetValueOrDefault(dutyNpc.Position);
                    if (CurrentDestination != point || (!isPathing && !isPathGenerating))
                    {
                        Navigate(point, false);
                    }
                    return;
                }
                else
                    switch (Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent])
                    {
                        case false when contentsFinderConfirmAddon > 0:
                        {
                            var contents = new AddonMaster.ContentsFinderConfirm(contentsFinderConfirmAddon);
                            contents.Commence();
                            TaskManager.DelayNext(500);
                            TaskManager.Enqueue(() => _diademQueuingInProgress = false);
                            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BoundByDuty]);
                            return;
                        }
                        case false when contentsFinderConfirmAddon == nint.Zero
                         && selectStringAddon == nint.Zero
                         && selectYesNoAddon == nint.Zero:
                            unsafe
                            {
                                var targetSystem = TargetSystem.Instance();
                                if (targetSystem == null)
                                    return;

                                TaskManager.Enqueue(StopNavigation);
                                TaskManager.Enqueue(()
                                    => targetSystem->OpenObjectInteraction(
                                        (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)dutyNpc.Address));
                                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]);
                                TaskManager.Enqueue(() => _diademQueuingInProgress = true);
                                return;
                            }
                        case true when selectStringAddon > 0:
                        {
                            var select = new AddonMaster.SelectString(selectStringAddon);
                            TaskManager.Enqueue(() => select.Entries[0].Select());
                            return;
                        }
                        case true when selectYesNoAddon > 0:
                        {
                            var yesNo = new AddonMaster.SelectYesno(selectYesNoAddon);
                            TaskManager.Enqueue(yesNo.Yes);
                            TaskManager.DelayNext(5000);
                            return;
                        }
                        case true when talkAddon > 0:
                        {
                            var talk = new AddonMaster.Talk(talkAddon);
                            TaskManager.Enqueue(talk.Click);
                            return;
                        }
                    }
            }

            if (territoryId != Diadem.Territory.Id && territoryId != 886 && next.Location.Territory == Diadem.Territory && Lifestream.Enabled)
            {
                if (!Lifestream.IsBusy())
                {
                    AutoStatus = "Teleporting...";
                    StopNavigation();
                    TaskManager.Enqueue(() => Lifestream.ExecuteCommand("firmament"));
                    TaskManager.Enqueue(() => !Lifestream.IsBusy(), 30000);
                }
                return;
            }

            var forcedAetheryte = ForcedAetherytes.ZonesWithoutAetherytes
                .FirstOrDefault(z => z.ZoneId == next.Location.Territory.Id);
            if (forcedAetheryte.ZoneId != 0
             && GatherBuddy.GameData.Aetherytes[forcedAetheryte.AetheryteId].Territory.Id == territoryId)
            {
                var needsLifestream = territoryId == 478 || territoryId == 129 || territoryId == 128 || territoryId == 132 || territoryId == 133 || territoryId == 130 || territoryId == 131;
                if (needsLifestream && !Lifestream.Enabled)
                    AutoStatus = $"Install Lifestream or teleport to {next.Location.Territory.Name} manually";
                else
                    AutoStatus = "Manual teleporting required";
                return;
            }
            
            var housingWardTerritoriesCheck = new uint[] { 339, 340, 341, 649, 641 };
            if (housingWardTerritoriesCheck.Contains((uint)targetTerritoryId) && !Lifestream.Enabled)
            {
                AutoStatus = "Install Lifestream to access housing wards";
                return;
            }

            // At this point, we are definitely going to gather something, so we may go home after that.
            if (Lifestream.Enabled)
                Lifestream.Abort();

            WentHome = false;
            
            var isInSameCityPair = (territoryId is 128 or 129 && targetTerritoryId is 128 or 129)
                                || (territoryId is 132 or 133 && targetTerritoryId is 132 or 133)
                                || (territoryId is 130 or 131 && targetTerritoryId is 130 or 131);

            if (next.Location.Territory.Id != territoryId && !isInSameCityPair)
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Diadem.IsInside)
                {
                    AutoStatus = "Can not teleport when bound by duty";
                    return;
                }
                else if (Diadem.IsInside)
                { 
                    LeaveTheDiadem();
                    return;
                }

                if (Dalamud.Conditions[ConditionFlag.Gathering]
                 || Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]
                 || Dalamud.Conditions[ConditionFlag.Occupied]
                 || Dalamud.Conditions[ConditionFlag.Fishing]
                 || Dalamud.Conditions[ConditionFlag.Casting]
                 || Dalamud.Conditions[ConditionFlag.Mounting]
                 || Dalamud.Conditions[ConditionFlag.Mounting71])
                {
                    AutoStatus = "Waiting for current action to complete before teleport...";
                    return;
                }

                if (TaskManager.IsBusy)
                {
                    AutoStatus = "Waiting for current tasks to complete before teleport...";
                    return;
                }

                if (Environment.TickCount64 - _lastNodeInteractionTime < 5000)
                {
                    AutoStatus = "Waiting after recent node interaction before teleport...";
                    return;
                }

                AutoStatus = "Teleporting...";
                StopNavigation();

                if (!MoveToTerritory(next.Location))
                    AbortAutoGather();

                return;
            }

            var targetGatheringType = next.Location.GatheringType.ToGroup();
            
            var config = MatchConfigPreset(next.Gatherable);

            if (DoUseConsumablesWithoutCastTime(config))
                return;

            if (JobAsGatheringType != targetGatheringType)
            {
                if (!ChangeGearSet(targetGatheringType, 2400))
                    AbortAutoGather();
                return;
            }

            if (next.Fish != null)
            {
                if (next.FishingSpot?.Spearfishing == true)
                {
                    DoNodeMovement(next, config);
                    return;
                }
                
                DoFishMovement(next);
                return;
            }

            
            if (next.Gatherable != null)
            {
                DoNodeMovement(next, config);
                return;
            }

            AutoStatus = "Fell out of control loop unexpectedly. Please report this error.";
            return;
        }

        public readonly Dictionary<GatherTarget, (Vector3 Position, Angle Rotation, DateTime Expiration)> FishingSpotData = new();
        private readonly Dictionary<Vector3, DateTime> _fishingSpotDismountAttempts = new();

        private void DoFishMovement(GatherTarget next)
        {
            Debug.Assert(next.Fish != null);
            Debug.Assert(next.FishingSpot != null);

            var fish = next;
            var territoryId = Dalamud.ClientState.TerritoryType;
            
            var isPathGenerating = IsPathGenerating;
            var isPathing = IsPathing;
            
            var targetFishTerritoryId = fish.FishingSpot?.Territory.Id ?? 0;
            var housingWardTerritoriesFish = new uint[] { 339, 340, 341, 649, 641 };
            var isTargetHousingWard = housingWardTerritoriesFish.Contains((uint)targetFishTerritoryId);
            
            if (isTargetHousingWard && Lifestream.Enabled)
            {
                var canAccessFromCurrentTerritory = (territoryId == 129 && targetFishTerritoryId == 339)  // Limsa -> Mist
                                                  || (territoryId is 130 or 131 && targetFishTerritoryId == 341)  // Ul'dah -> Goblet
                                                  || (territoryId == 132 && targetFishTerritoryId == 340)  // Gridania -> Lavender
                                                  || (territoryId == 418 && targetFishTerritoryId == 649)  // Foundation -> Empyreum
                                                  || (territoryId == 628 && targetFishTerritoryId == 641); // Kugane -> Shirogane
                
                if (canAccessFromCurrentTerritory)
                {
                    if (!Lifestream.IsBusy())
                    {
                        if (IsFishing)
                        {
                            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                            {
                                AutoHook.SetPluginState?.Invoke(false);
                                AutoHook.SetAutoStartFishing?.Invoke(false);
                            }
                            AutoStatus = "Closing fishing before teleport...";
                            QueueQuitFishingTasks();
                            return;
                        }
                        
                        AutoStatus = "Teleporting to housing ward...";
                        StopNavigation();
                        
                        string wardCommand = targetFishTerritoryId switch
                        {
                            339 => "mist 1 1",
                            341 => "goblet 1 1",
                            340 => "lavender 1 1",
                            649 => "empyreum 1 1",
                            641 => "shirogane 1 1",
                            _ => ""
                        };
                        
                        if (!string.IsNullOrEmpty(wardCommand))
                        {
                            TaskManager.Enqueue(() => Lifestream.ExecuteCommand(wardCommand));
                            TaskManager.DelayNext(1000);
                            TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                        }
                    }
                    
                    return;
                }
            }

            if (territoryId == 418 && fish.FishingSpot?.Territory.Id == Diadem.Territory.Id && Lifestream.Enabled)
            {
                if (IsFishing)
                {
                    if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                    {
                        AutoHook.SetPluginState?.Invoke(false);
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                    AutoStatus = "Closing fishing before teleport...";
                    QueueQuitFishingTasks();
                    return;
                }
                if (!Lifestream.IsBusy())
                {
                    AutoStatus = "Teleporting...";
                    StopNavigation();
                    TaskManager.Enqueue(() => Lifestream.ExecuteCommand("firmament"));
                    TaskManager.Enqueue(() => !Lifestream.IsBusy(), 30000);
                }
                return;
            }
            
            if (fish.FishingSpot?.Territory.Id != territoryId)
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Diadem.IsInside)
                {
                    AutoStatus = "Can not teleport when bound by duty";
                    return;
                }
                else if (Diadem.IsInside)
                {
                    LeaveTheDiadem();
                    return;
                }
                
                if (territoryId == 886 && fish.FishingSpot?.Territory.Id == Diadem.Territory.Id)
                {
                    if (JobAsGatheringType == GatheringType.Unknown)
                    {
                        if (ChangeGearSet(GatheringType.Fisher, 2400))
                        {
                            return;
                        }
                        else
                        {
                            AbortAutoGather();
                            return;
                        }
                    }
                    
                    var dutyNpc                    = Dalamud.Objects.FirstOrDefault(o => o.BaseId == 1031694);
                    var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                    var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                    var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                    var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                    
                    if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                    {
                        AutoStatus = "Moving to Diadem NPC...";
                        var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000).GetValueOrDefault(dutyNpc.Position);
                        if (CurrentDestination != point || (!IsPathGenerating && !IsPathing))
                        {
                            Navigate(point, false);
                        }
                        return;
                    }
                    else
                        switch (Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent])
                        {
                            case false when contentsFinderConfirmAddon > 0:
                            {
                                var contents = new AddonMaster.ContentsFinderConfirm(contentsFinderConfirmAddon);
                                contents.Commence();
                                TaskManager.DelayNext(500);
                                TaskManager.Enqueue(() => _diademQueuingInProgress = false);
                                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BoundByDuty]);
                                return;
                            }
                            case false when contentsFinderConfirmAddon == nint.Zero
                             && selectStringAddon == nint.Zero
                             && selectYesNoAddon == nint.Zero:
                                unsafe
                                {
                                    var targetSystem = TargetSystem.Instance();
                                    if (targetSystem == null)
                                        return;

                                    TaskManager.Enqueue(StopNavigation);
                                    TaskManager.Enqueue(()
                                        => targetSystem->OpenObjectInteraction(
                                            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)dutyNpc.Address));
                                    TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]);
                                    TaskManager.Enqueue(() => _diademQueuingInProgress = true);
                                    return;
                                }
                            case true when selectStringAddon > 0:
                            {
                                var select = new AddonMaster.SelectString(selectStringAddon);
                                TaskManager.Enqueue(() => select.Entries[0].Select());
                                return;
                            }
                            case true when selectYesNoAddon > 0:
                            {
                                var yesNo = new AddonMaster.SelectYesno(selectYesNoAddon);
                                TaskManager.Enqueue(yesNo.Yes);
                                TaskManager.DelayNext(5000);
                                return;
                            }
                            case true when talkAddon > 0:
                            {
                                var talk = new AddonMaster.Talk(talkAddon);
                                TaskManager.Enqueue(talk.Click);
                                return;
                            }
                        }
                }
                
            if (IsFishing)
            {
                if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                {
                    AutoHook.SetPluginState?.Invoke(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                }
                AutoStatus = "Closing fishing before teleport...";
                QueueQuitFishingTasks();
                return;
            }
                
                AutoStatus = "Teleporting...";
                StopNavigation();
                
                if (!MoveToTerritory(fish.Location))
                    AbortAutoGather();
                
                return;
            }

        if (!FishingSpotData.TryGetValue(fish, out var fishingSpotData))
        {
            var existingEntryForSameSpot = FishingSpotData
                .FirstOrDefault(kvp => kvp.Key.FishingSpot?.Id == fish.FishingSpot?.Id);
            
            if (existingEntryForSameSpot.Key.Fish != null)
            {
                GatherBuddy.Log.Information($"[AutoGather] Reusing position for same fishing spot (switching from {existingEntryForSameSpot.Key.Fish.Name[GatherBuddy.Language]} to {fish.Fish!.Name[GatherBuddy.Language]})");
                FishingSpotData.Add(fish, existingEntryForSameSpot.Value);
                
                if (IsFishing)
                {
                    if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                    {
                        AutoHook.SetPluginState?.Invoke(false);
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                    AutoStatus = "Stopping fishing to change target...";
                    QueueQuitFishingTasks();
                }
                
                return;
            }
            
            if (IsFishing)
            {
                if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                {
                    AutoHook.SetPluginState?.Invoke(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                }
                AutoStatus = "Stopping fishing to change target...";
                QueueQuitFishingTasks();
                return;
            }
            
            var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(fish!.FishingSpot);
            if (!positionData.HasValue)
            {
                Communicator.PrintError(
                    $"No position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue. Please, manually fish at least once at {fish.FishingSpot.Name} so GBR can know its location.");
                AbortAutoGather();
                return;
            }

            FishingSpotData.Add(fish, (positionData.Value.Position, positionData.Value.Rotation, DateTime.MaxValue));
            return;
        }

            if (next.Fish.UmbralWeather.IsUmbral)
            {
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                if (next.Fish.UmbralWeather.Id != currentWeather)
                {
                    if (IsGathering)
                    {
                        if (IsFishing && AutoHook.Enabled)
                        {
                            AutoHook.SetAutoStartFishing(false);
                        }
                        else
                        {
                            CleanupAutoHook();
                            QueueQuitFishingTasks();
                        }
                    }
                    else
                    {
                        AutoStatus = "Waiting for correct Umbral weather";
                    }

                    return;
                }
            }

            if (IsFishing)
            {
                if (_fishWaryDetected)
                {
                    _fishWaryDetected = false;
                    GatherBuddy.Log.Information($"[AutoGather] Fish wary warning - doing simple relocation (not counted toward amiss)...");
                    
                    var oldPosition = fishingSpotData.Position;
                    const float MinRelocationDistance = 10.0f;
                    var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(
                        fish!.FishingSpot,
                        oldPosition,
                        MinRelocationDistance);

                    if (positionData.HasValue)
                    {
                        var newPos = positionData.Value.Position;
                        var newRot = positionData.Value.Rotation;
                        var dist = Vector3.Distance(newPos, oldPosition);

                        GatherBuddy.Log.Information($"[AutoGather] Wary relocation: {oldPosition} → {newPos}, distance={dist}y");
                        FishingSpotData[fish] = (newPos, newRot, DateTime.MaxValue);
                        
                        if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                        {
                            AutoHook.SetPluginState?.Invoke(false);
                            AutoHook.SetAutoStartFishing?.Invoke(false);
                        }
                        
                        AutoStatus = "Fish wary - relocating...";
                        QueueQuitFishingTasks();
                    }
                    else
                    {
                        GatherBuddy.Log.Warning("[AutoGather] No alternate position for wary relocation, continuing...");
                    }
                    
                    return;
                }
                
                if (_fishDetectedPlayer)
                {
                    _fishDetectedPlayer = false; 

                    if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                    {
                        AutoHook.SetPluginState?.Invoke(false);
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                    
                    if (_consecutiveAmissCount == 1)
                    {
                        GatherBuddy.Log.Warning($"[AutoGather] First amiss detection - relocating within spot...");
                        var oldPosition = fishingSpotData.Position;

                        const float MinRelocationDistance = 10.0f;
                        var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(
                            fish!.FishingSpot,
                            oldPosition,
                            MinRelocationDistance);

                        if (!positionData.HasValue)
                        {
                            Communicator.PrintError(
                                $"No alternate position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue.");
                            AbortAutoGather();
                            return;
                        }

                        var newPos = positionData.Value.Position;
                        var newRot = positionData.Value.Rotation;
                        var dist = Vector3.Distance(newPos, oldPosition);

                        GatherBuddy.Log.Information($"[AutoGather] Relocating within '{fish.FishingSpot.Name}' " +
                                      $"from {oldPosition} to {newPos}, distance={dist}y");

                        FishingSpotData[fish] = (newPos, newRot, DateTime.MaxValue);
                        
                        AutoStatus = "Fish detected! Relocating and waiting...";
                        QueueQuitFishingTasks();
                        
                        TaskManager.DelayNext(30000);
                        TaskManager.Enqueue(() => 
                        {
                            GatherBuddy.Log.Information("[AutoGather] Wait complete, resuming fishing...");
                            return true;
                        });
                    }
                    else
                    {
                        GatherBuddy.Log.Warning($"[AutoGather] Persistent amiss (count: {_consecutiveAmissCount}) - teleporting out of zone to clear state...");
                        
                        AutoStatus = "Persistent amiss! Teleporting out to clear...";
                        QueueQuitFishingTasks();
                        
                        TaskManager.Enqueue(() => 
                        {
                            var wentHome = GoHome();
                            if (wentHome)
                            {
                                GatherBuddy.Log.Information("[AutoGather] Teleported home. Waiting before returning to fishing spot...");
                            }
                            else
                            {
                                GatherBuddy.Log.Warning("[AutoGather] Could not teleport home (Lifestream not available?). Waiting at current location...");
                            }
                            return true;
                        });
                        
                        TaskManager.DelayNext(10000);
                        
                        TaskManager.Enqueue(() => 
                        {
                            _consecutiveAmissCount = 0;
                            WentHome = false;
                            GatherBuddy.Log.Information("[AutoGather] Amiss cleared by zone teleport. Returning to fishing spot...");
                            AutoStatus = "Returning to fishing spot...";
                            return true;
                        });
                    }
                    
                    return;
                }
                
                if (GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes > 0 && fishingSpotData.Expiration < DateTime.Now)
                {
                    GatherBuddy.Log.Information($"[AutoGather] Fishing spot timer expired ({GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes} minutes), relocating...");
                    var oldPosition = fishingSpotData.Position;

                    const float MinRelocationDistance = 10.0f;
                    var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(
                        fish!.FishingSpot,
                        oldPosition,
                        MinRelocationDistance);

                    if (!positionData.HasValue)
                    {
                        Communicator.PrintError(
                            $"No alternate position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue.");
                        AbortAutoGather();
                        return;
                    }

                    var newPos = positionData.Value.Position;
                    var newRot = positionData.Value.Rotation;
                    var dist = Vector3.Distance(newPos, oldPosition);

                    GatherBuddy.Log.Debug($"[AutoGather] Relocating fishing spot for '{fish.FishingSpot.Name}' " +
                                  $"from {oldPosition} to {newPos}, distance={dist}");

                    FishingSpotData[fish] = (newPos, newRot, DateTime.MaxValue);

                    if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                    {
                        AutoHook.SetPluginState?.Invoke(false);
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                    
                    AutoStatus = "Stopping fishing to relocate to new spot...";
                    QueueQuitFishingTasks();
                    return;
                }
                
                DoFishingTasks(next);
                return;
            }
            
            if (GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes > 0 && fishingSpotData.Expiration < DateTime.Now)
            {
                GatherBuddy.Log.Debug("[AutoGather] Time for a new fishing spot!");
                var oldPosition = fishingSpotData.Position;

                const float MinRelocationDistance = 10.0f;
                var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(
                    fish!.FishingSpot,
                    oldPosition,
                    MinRelocationDistance);

                if (!positionData.HasValue)
                {
                    Communicator.PrintError(
                        $"No alternate position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue.");
                    AbortAutoGather();
                    return;
                }

                var newPos = positionData.Value.Position;
                var newRot = positionData.Value.Rotation;
                var dist = Vector3.Distance(newPos, oldPosition);

                GatherBuddy.Log.Debug($"[AutoGather] Relocating fishing spot for '{fish.FishingSpot.Name}' " +
                              $"from {oldPosition} to {newPos}, distance={dist}");

                FishingSpotData[fish] = (newPos, newRot, DateTime.MaxValue);

                AutoStatus = "Moving to new fishing spot...";
                MoveToFishingSpot(newPos, newRot);
                return;
            }

            if (Vector3.Distance(fishingSpotData.Position, Player.Position) < 1)
            {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    if (!_fishingSpotDismountAttempts.TryGetValue(fishingSpotData.Position, out var firstAttempt))
                    {
                        _fishingSpotDismountAttempts[fishingSpotData.Position] = DateTime.Now;
                    }
                    else if ((DateTime.Now - firstAttempt).TotalSeconds > 5)
                    {
                        GatherBuddy.Log.Warning("[AutoGather] Failed to dismount at fishing spot for 5+ seconds, forcing unstuck to find landable spot");
                        _fishingSpotDismountAttempts.Remove(fishingSpotData.Position);
                        _advancedUnstuck.ForceFishing();
                        AutoStatus = "Can't land here, finding landable spot...";
                        return;
                    }
                    
                    EnqueueDismount();
                    AutoStatus = "Dismounting...";
                    return;
                }
                
                if (_fishingSpotDismountAttempts.ContainsKey(fishingSpotData.Position))
                {
                    _fishingSpotDismountAttempts.Remove(fishingSpotData.Position);
                }

                var playerAngle = new Angle(Player.Rotation);
                if (playerAngle != fishingSpotData.Rotation)
                {
                    TaskManager.Enqueue(() => SetRotation(fishingSpotData.Rotation));
                    AutoStatus = "Adjusting rotation...";
                    return;
                }

                if (fishingSpotData.Expiration == DateTime.MaxValue && GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes > 0)
                {
                    var newExpiration = DateTime.Now.AddMinutes(GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes);
                    FishingSpotData[fish] = (fishingSpotData.Position, fishingSpotData.Rotation, newExpiration);
                    GatherBuddy.Log.Information($"[AutoGather] Started fishing spot timer: {GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes} minutes");
                }
                else
                {
                    GatherBuddy.Log.Debug($"Fishing Spot is valid for {(fishingSpotData.Expiration - DateTime.Now).TotalSeconds} seconds");
                }

                StopNavigation();
                AutoStatus = "Fishing...";
                DoFishingTasks(next);
                return;
            }

            AutoStatus = "Moving to fishing spot";
            if (CurrentDestination != fishingSpotData.Position)
            {
                StopNavigation();
                var autoHookArmed =
                    GatherBuddy.Config.AutoGatherConfig.UseAutoHook
                    && AutoHook.Enabled
                    && AutoHook.GetAutoStartFishing?.Invoke() == true;

                if (IsGathering || IsFishing || autoHookArmed)
                {
                    AutoStatus = "Stopping fishing to change target...";
                    QueueQuitFishingTasks();
                    return;
                }

                MoveToFishingSpot(fishingSpotData.Position, fishingSpotData.Rotation);
            }
        }

        private bool DoNodeMovementDiadem(GatherTarget next, ConfigPreset config)
        {
            Debug.Assert(next.Gatherable != null);
            Debug.Assert(next.Node != null);

            var player = Player.Position;

            // ActiveItemsList prioritizes umbral items with matching weather,
            // so we only need to check the first item in the list.
            // Let the normal navigation logic handle Skybuilders' Tools quest items and Umbral nodes.

            if (next.Node.NodeType == NodeType.Clouded)
            {
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                if (next.Node.UmbralWeather.Id != currentWeather || _activeItemList.IsCloudedNodeConsumed)
                {
                    AutoStatus = "Waiting for correct Umbral weather";
                    StopNavigation();
                    return true;
                }

                // Check if the node hasn't spawned due to a game bug.
                var flag = TimedNodePosition;
                var nodeId = next.Node.WorldPositions.Keys.First();
                if (flag.HasValue && Vector2.Distance(flag.Value, player.ToVector2()) < NodeVisibilityDistance
                    && !Dalamud.Objects.Any(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && nodeId == o.BaseId))
                {
                    GatherBuddy.Log.Warning("Looks like the Clouded node hasn't spawned due to a game bug. Trying to move away.");

                    // Pick a random node far away and move there.
                    var pos = GatherBuddy.GameData.GatheringNodes.Values
                        .Where(n => n.Territory == Diadem.Territory)
                        .SelectMany(n => n.WorldPositions.Values)
                        .SelectMany(x => x)
                        .Where(pos => Vector2.DistanceSquared(pos.ToVector2(), flag.Value) > 200f * 200f)
                        .Aggregate((Count: 0, Item: Vector3.Zero), (acc, current) => (acc.Count + 1, (Random.Shared.Next(acc.Count + 1) == 0) ? current : acc.Item))
                        .Item;

                    Navigate(pos, true, direct: true);
                    TaskManager.Enqueue(() => !IsPathGenerating);
                    TaskManager.Enqueue(() => !Dalamud.Objects.Any(o => o.ObjectKind == ObjectKind.GatheringPoint && nodeId == o.BaseId) || !IsPathing, 10000);
                    TaskManager.Enqueue(StopNavigation);

                    AutoStatus = "Trying to reset the bugged Clouded node";
                    return true;
                }
                return false;
            }

            if (Diadem.OddlyDelicateItems.Contains(next.Item))
                return false;

            // For regular nodes, we go in a full circle along the pre-calculated optimal path.
            var path = Diadem.ShortestPaths[JobAsGatheringType];
            if (_diademPathIndex == -1)
            {
                // Find the closest node to start the path.
                var closestDist = float.PositiveInfinity;
                for (var i = 0; i < path.Length; i++)
                {
                    if (!_diadem.IsNodeAvailable(path[i])) continue;

                    try
                    {
                        var dist = Vector3.Distance(player, WorldData.WorldLocationsByNodeId[path[i]].Where(p => !IsBlacklisted(p)).Average());
                        // If there are several close nodes within 50y of each other, pick the one with the lowest index.
                        if (dist + 50f < closestDist)
                        {
                            closestDist = dist;
                            _diademPathIndex = i;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        continue; // Node is blacklisted
                    }
                }
            }
            else
            {
                var prevIndex = _diademPathIndex;
                while (!_diadem.IsNodeAvailable(path[_diademPathIndex]) || WorldData.WorldLocationsByNodeId[path[_diademPathIndex]].All(IsBlacklisted))
                {
                    _diademPathIndex = (_diademPathIndex + 1) % path.Length;
                    if (prevIndex == _diademPathIndex)
                        AbortAutoGather("All active nodes in The Diadem are blacklisted");
                }
            }

            var nextNode = Dalamud.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.BaseId == path[_diademPathIndex] && o.IsTargetable).FirstOrDefault();
            if (nextNode != null)
            {
                if (IsBlacklisted(nextNode.Position))
                {
                    _diademPathIndex = (_diademPathIndex + 1) % path.Length;
                }
                else
                {
                    AutoStatus = $"Moving to next Diadem node ({Vector3.Distance(player, nextNode.Position):F0}y)...";
                    var pos = nextNode.Position;
                    if (TryWindmireJump(ref pos))
                        Navigate(pos, ShouldFly(pos), direct: true);
                    else
                        MoveToCloseNode(nextNode, next.Gatherable!, config);
                }
            }
            else
            {
                var pos = WorldData.WorldLocationsByNodeId[path[_diademPathIndex]]
                    .OrderBy(pos => Vector3.DistanceSquared(pos, player))
                    .First();

                AutoStatus = $"Moving to next Diadem node ({Vector3.Distance(player, pos):F0}y)...";

                var jump = TryWindmireJump(ref pos);
                Navigate(pos, ShouldFly(pos), direct: jump);
            }
            return true;
        }

        private bool TryWindmireJump(ref Vector3 destination)
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DiademWindmireJumps)
                return false;

            var pos = destination;
            var player = Player.Position;

            var ((windmire, _), windmireDistance) = Diadem.Windmires
                    .Select(w => (w, Distance: Vector3.Distance(player, w.From) + Vector3.Distance(w.To, pos)))
                    .MinBy(x => x.Distance);

            var directDistance = Vector3.Distance(player, pos);

            // Use Windmire only if it provides a 2x advantage in distance.
            if (windmireDistance * 2f < directDistance)
            {
                destination = windmire;
                return true;
            }
            return false;
        }

        private void DoNodeMovement(GatherTarget next, ConfigPreset config)
        {
            if (Diadem.IsInside)
            {
                if (DoNodeMovementDiadem(next, config))
                    return;
                _diademPathIndex = -1;
            }

            var allPositions = next.Location.WorldPositions
                .Where(n => !VisitedNodes.Contains(n.Key))
                .SelectMany(w => w.Value)
                .Where(v => !IsBlacklisted(v))
                .ToList();

            var visibleNodes = Dalamud.Objects
                .Where(o => allPositions.Contains(o.Position))
                .ToList();

            var closestTargetableNode = visibleNodes
                .Where(o => o.IsTargetable)
                .MinBy(o => Vector3.Distance(Player.Position, o.Position));

            var isSpearfishing = next.Fish?.IsSpearFish == true;
            if (!isSpearfishing)
            {
                var isTimedNode = next.Gatherable?.NodeType is NodeType.Unspoiled or NodeType.Legendary or NodeType.Clouded;
                if (ActivateGatheringBuffs(isTimedNode))
                    return;
            }

            if (closestTargetableNode != null)
            {
                AutoStatus = "Moving to node...";
                
                if (next.Gatherable != null)
                {
                    MoveToCloseNode(closestTargetableNode, next.Gatherable, config);
                }
                else if (next.Fish != null)
                {
                    MoveToCloseSpearfishingNode(closestTargetableNode, next.Fish);
                }
                return;
            }

            AutoStatus = "Moving to far node...";

            if (CurrentDestination != default && IsPathing)
            {
                var currentNode = visibleNodes.FirstOrDefault(o => o.Position == CurrentDestination);
                if (currentNode != null && !currentNode.IsTargetable)
                    GatherBuddy.Log.Verbose($"Far node is not targetable, distance {currentNode.Position.DistanceToPlayer()}.");

                //It takes some time (roundtrip to the server) before a node becomes targetable after it becomes visible,
                //so we need to delay excluding it. But instead of measuring time, we use distance, since character is traveling at a constant speed.
                //Value 50 was determined empirically.
                foreach (var node in allPositions.Where(o => o.DistanceToPlayer() < NodeVisibilityDistance))
                    FarNodesSeenSoFar.Add(node);

                if (CurrentDestination.DistanceToPlayer() < NodeVisibilityDistance)
                {
                    GatherBuddy.Log.Verbose("Far node is not targetable, choosing another");
                }
                else
                {
                    return;
                }
            }

            Vector3 selectedFarNode;

            // Only Legendary, Unspoiled, and Clouded nodes show a map marker.
            var mapMarkerAvailable = next.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled or NodeType.Clouded;
            // Wait an additional 8 seconds because it takes a few seconds for the previous flag to disappear.
            var gracePeriod = next.Time == TimeInterval.Always ? 0 : next.Time.Start - GatherBuddy.Time.ServerTime.AddSeconds(-8);
            var mapMarker = mapMarkerAvailable && gracePeriod <= 0 ? TimedNodePosition : null;

            if (mapMarkerAvailable && ShouldUseFlag)
            {
                if (mapMarker == null)
                {
                    AutoStatus = "Waiting for map marker to show up" + (gracePeriod > 0 ? $" (grace period: {gracePeriod / RealTime.MillisecondsPerSecond}s)" : "");
                    return;
                }

                selectedFarNode = allPositions
                    .DefaultIfEmpty()
                    .MinBy(o => Vector2.DistanceSquared(mapMarker.Value, o.ToVector2()));

                if (selectedFarNode == default || Vector2.DistanceSquared(mapMarker.Value, selectedFarNode.ToVector2()) > 10 * 10)
                {
                    var point = new Vector3(mapMarker.Value.X, 0, mapMarker.Value.Y);
                    selectedFarNode = VNavmesh.Query.Mesh.NearestPoint(point, 10, 10000).GetValueOrDefault(point);
                }
            }
            else
            {
                //Select the closest node
                selectedFarNode = allPositions
                    .Where(n => !FarNodesSeenSoFar.Contains(n))
                    .DefaultIfEmpty()
                    .MinBy(v => Vector2.DistanceSquared(mapMarker ?? Player.Position.ToVector2(), v.ToVector2()));

                if (selectedFarNode == default)
                {
                    FarNodesSeenSoFar.Clear();
                    GatherBuddy.Log.Verbose($"Selected node was null and far node filters have been cleared");
                    return;
                }
            }

            var jump = Diadem.IsInside && TryWindmireJump(ref selectedFarNode);

            Navigate(selectedFarNode, ShouldFly(selectedFarNode), direct: jump);
        }

        private unsafe void LeaveTheDiadem()
        {
            TaskManager.Enqueue(() =>
            {
                AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu)->Show();
            });
            
            TaskManager.Enqueue(() =>
            {
                if (GenericHelpers.TryGetAddonByName("ContentsFinderMenu", out AtkUnitBase* addon) && addon->IsReady)
                {
                    var leaveCallback = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[1];
                    addon->FireCallback(1, leaveCallback);
                    return true;
                }
                return false;
            }, "Wait for ContentsFinderMenu addon");
            
            TaskManager.DelayNext(500);
            
            TaskManager.Enqueue(() =>
            {
                if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* yesnoAddon) && yesnoAddon->IsReady)
                {
                    var yesNo = new AddonMaster.SelectYesno((nint)yesnoAddon);
                    yesNo.Yes();
                    return;
                }
            });
            
            TaskManager.DelayNext(500);
            TaskManager.Enqueue(() => !GenericHelpers.TryGetAddonByName("SelectYesno", out _), "Wait for SelectYesno to close");
            TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
        }

        private void AbortAutoGather(string? status = null)
        {
            if (Diadem.IsInside)
            {
                LeaveTheDiadem();
                return;
            }

            if (HasReducibleItems())
            {
                if (Player.Job == 18 && GatherBuddy.Config.AutoGatherConfig.DeferReductionDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
                {
                    GatherBuddy.Log.Debug("[AutoGather] Skipping reduction during abort due to active fishing or buffs");
                }
                else
                {
                    GatherBuddy.Log.Debug("[AutoGather] Found reducible items during abort, reducing before shutdown");
                    
                    if (Player.Job == 18)
                    {
                        if (IsGathering)
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
                            AbortAutoGather(status);
                        });
                    }
                    else
                    {
                        ReduceItems(true, () =>
                        {
                            AbortAutoGather(status);
                        });
                    }
                    
                    return;
                }
            }

            if (!string.IsNullOrEmpty(status))
                AutoStatus = status;
            if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                Task.Run(() => _soundHelper.StartHonkSoundTask(3));
            CloseGatheringAddons();
            if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone)
                EnqueueActionWithDelay(() => { GoHome(); });
            _disabledBySystem = true;
            TaskManager.Enqueue(() =>
            {
                Enabled    = false;
                AutoStatus = status ?? AutoStatus;
            });
        }

        private unsafe void CloseGatheringAddons(bool closeGathering = true)
        {
            var masterpieceOpen = MasterpieceAddon != null;
            var gatheringOpen   = GatheringAddon != null;
            if (masterpieceOpen)
            {
                EnqueueActionWithDelay(() =>
                {
                    if (MasterpieceAddon is var addon and not null)
                    {
                        Callback.Fire(&addon->AtkUnitBase, true, -1);
                    }
                });
                TaskManager.Enqueue(() => MasterpieceAddon == null,                 "Wait until GatheringMasterpiece addon is closed");
                TaskManager.Enqueue(() => GatheringAddon is var addon and not null, "Wait until Gathering addon pops up");
                TaskManager.DelayNext(
                    300); //There is some delay after the moment the addon pops up (and is ready) before the callback can be used to close it. We wait some time and retry the callback.
            }

            if (closeGathering && (gatheringOpen || masterpieceOpen))
            {
                TaskManager.Enqueue(() =>
                {
                    if (GatheringAddon is var gathering and not null && gathering->IsReady)
                    {
                        Callback.Fire(&gathering->AtkUnitBase, true, -1);
                        TaskManager.DelayNextImmediate(100);
                        return false;
                    }

                    var addon = SelectYesnoAddon;
                    if (addon != null)
                    {
                        EnqueueActionWithDelay(() =>
                        {
                            if (SelectYesnoAddon is var addon and not null)
                            {
                                var master = new AddonMaster.SelectYesno(addon);
                                master.Yes();
                            }
                        }, true);
                        TaskManager.EnqueueImmediate(() => !IsGathering, "Wait until Gathering addon is closed");
                        return true;
                    }

                    return !IsGathering;
                }, "Wait until Gathering addon is closed or SelectYesno addon pops up");
            }
        }

        private bool CheckCollectablesUnlocked(GatheringType gatheringType)
        {
            var level = gatheringType switch
            {
                GatheringType.Miner    => DiscipleOfLand.MinerLevel,
                GatheringType.Botanist => DiscipleOfLand.BotanistLevel,
                GatheringType.Fisher   => DiscipleOfLand.FisherLevel,
                GatheringType.Multiple => Math.Max(DiscipleOfLand.MinerLevel, DiscipleOfLand.BotanistLevel),
                _                      => 0
            };
            if (level < Actions.Collect.MinLevel)
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but your level is not high enough to gather it.");
                return false;
            }

            var questId = gatheringType switch
            {
                GatheringType.Miner    => Actions.Collect.QuestIds.Miner,
                GatheringType.Botanist => Actions.Collect.QuestIds.Botanist,
                _                      => 0u
            };

            if (questId != 0 && !QuestManager.IsQuestComplete(questId))
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but you haven't unlocked the collectables.");
                var sheet      = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Quest>()!;
                var row        = sheet.GetRow(questId)!;
                var loc        = row.IssuerLocation.Value!;
                var map        = loc.Map.Value!;
                var pos        = MapUtil.WorldToMap(new Vector2(loc.X, loc.Z), map);
                var mapPayload = new MapLinkPayload(loc.Territory.RowId, loc.Map.RowId, pos.X, pos.Y);
                var text       = new SeStringBuilder();
                text.AddText("Collectables are unlocked by ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .AddQuestLink(questId)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(row.Name.ToString())
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(" quest, which can be started in ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .Add(mapPayload)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText($"{mapPayload.PlaceName} {mapPayload.CoordinateString}")
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(".");
                Communicator.Print(text.BuiltString);
                return false;
            }

            return true;
        }

        private bool ChangeGearSet(GatheringType job, int delay)
        {
            var set = job switch
            {
                GatheringType.Miner => GatherBuddy.Config.MinerSetName,
                GatheringType.Botanist => GatherBuddy.Config.BotanistSetName,
                GatheringType.Fisher => GatherBuddy.Config.FisherSetName,
                _ => null,
            };
            if (string.IsNullOrEmpty(set))
            {
                Communicator.PrintError($"No gear set for {job} configured.");
                return false;
            }

            if (job is GatheringType.Miner or GatheringType.Botanist
                && Player.Job == 18 /* FSH */
                && GatherBuddy.Config.AutoGatherConfig.UseAutoHook
                && AutoHook.Enabled)
            {
                GatherBuddy.Log.Debug($"[AutoGather] Swapping from FSH to {job}, disabling AutoHook.");
                try
                {
                    AutoHook.SetPluginState(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                }
                catch (Exception e)
                {
                    GatherBuddy.Log.Error($"[AutoGather] Failed to disable AutoHook on gear change: {e}");
                }

                CleanupAutoHook();
            }

            _diademPathIndex = -1; // Reset The Diadem path after changing job
            Chat.ExecuteCommand($"/gearset change \"{set}\"");
            TaskManager.DelayNext(Random.Shared.Next(delay, delay + 500)); // Add a random delay to be less suspicious
            return true;
        }

        private void EnqueueEnsureAutoHookDisabled()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.UseAutoHook || !AutoHook.Enabled)
                return;

            const int maxAttempts = 10;
            var attempt = 0;

            void TryDisableOnce()
            {
                attempt++;

                var pluginOn = AutoHook.GetPluginState?.Invoke() == true;
                var autoStartOn = AutoHook.GetAutoStartFishing?.Invoke() == true;
                var stillEnabled = pluginOn || autoStartOn;

                if (!stillEnabled)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] AutoHook fully disabled after {attempt} attempt(s).");
                    return;
                }

                GatherBuddy.Log.Debug($"[AutoGather] AutoHook still enabled (plugin={pluginOn}, autoStart={autoStartOn}), " +
                              $"attempt {attempt}/{maxAttempts} – sending IPC disable.");

                AutoHook.SetAutoStartFishing?.Invoke(false);
                AutoHook.SetPluginState?.Invoke(false);

                if (attempt >= maxAttempts)
                {
                    GatherBuddy.Log.Warning("[AutoGather] Failed to fully disable AutoHook after max attempts.");
                    return;
                }

                TaskManager.Enqueue(TryDisableOnce, "EnsureAutoHookDisabled");
            }

            TaskManager.Enqueue(TryDisableOnce, "EnsureAutoHookDisabled");
        }


        internal void DebugClearVisited()
        {
            _activeItemList.DebugClearVisited();
        }

        internal void DebugMarkVisited(GatherTarget target)
        {
            _activeItemList.DebugMarkVisited(target);
        }
        
        private bool ValidateActiveItemsPerception()
        {
            try
            {
                var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
                var isMiner = currentJob == 16;
                var isBotanist = currentJob == 17;
                
                if (!isMiner && !isBotanist)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Skipping perception validation on enable - player not on Miner or Botanist (current job: {currentJob})");
                    return true;
                }
                
                var playerPerception = DiscipleOfLand.Perception;
                var insufficientPerception = new List<(string Name, int Required)>();
                
                if (_activeItemList.GetType()
                    .GetField("_listsManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(_activeItemList) is not AutoGatherListsManager listsManager)
                {
                    return true;
                }
                
                foreach (var (item, _) in listsManager.ActiveItems)
                {
                    if (item is not Gatherable gatherable)
                        continue;
                    
                    var requiredPerception = (int)gatherable.GatheringData.PerceptionReq;
                    if (requiredPerception == 0)
                        continue;
                    
                    var gatheringType = gatherable.GatheringType.ToGroup();
                    if ((isMiner && gatheringType != GatheringType.Miner) || (isBotanist && gatheringType != GatheringType.Botanist))
                    {
                        continue;
                    }
                    
                    GatherBuddy.Log.Debug($"[AutoGather] Validating {gatherable.Name[GatherBuddy.Language]}: requires {requiredPerception} perception (current: {playerPerception})");
                    
                    if (playerPerception < requiredPerception)
                    {
                        insufficientPerception.Add((gatherable.Name[GatherBuddy.Language], requiredPerception));
                    }
                }
                
                if (insufficientPerception.Count > 0)
                {
                    var itemDetails = string.Join(", ", insufficientPerception.Select(x => $"{x.Name} (needs {x.Required})"));
                    Communicator.PrintError($"[AutoGather] Cannot enable AutoGather: Insufficient perception (current: {playerPerception}): {itemDetails}");
                    GatherBuddy.Log.Error($"[AutoGather] AutoGather not enabled: Insufficient perception {playerPerception}");
                    return false;
                }
                
                return true;
            }
            catch (System.Exception ex)
            {
                GatherBuddy.Log.Error($"[AutoGather] Error validating active items perception: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }
        
        private bool ShouldWaitForAutoRetainer()
        {
            try
            {
                if (GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes)
                {
                    if (_currentGatherTarget != null)
                    {
                        var target = _currentGatherTarget.Value;
                        if (target.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled)
                        {
                            return false;
                        }
                    }
                    
                    var nextItem = _activeItemList.GetNextOrDefault();
                    if (nextItem != default)
                    {
                        if (nextItem.Node?.NodeType is NodeType.Legendary or NodeType.Unspoiled)
                        {
                            if (nextItem.Time.InRange(AdjustedServerTime) && 
                                !_activeItemList.DebugVisitedTimedLocations.ContainsKey(nextItem.Node))
                            {
                                return false;
                            }
                        }
                    }
                }
                
                if (AutoRetainer.GetEnabledRetainers == null || AutoRetainer.GetOfflineCharacterData == null)
                    return false;

                var enabledRetainers = AutoRetainer.GetEnabledRetainers();
                
                if (enabledRetainers == null || !enabledRetainers.Any())
                {
                    if (_autoRetainerMultiModeEnabled)
                    {
                        AutoRetainer.AbortAllTasks?.Invoke();
                        AutoRetainer.DisableAllFunctions?.Invoke();
                        _autoRetainerMultiModeEnabled = false;
                    }
                    return false;
                }

                var threshold = GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold;
                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool hasRetainersReady = false;
                long? closestTime = null;

                foreach (var (cid, retainerNames) in enabledRetainers)
                {
                    if (!retainerNames.Any())
                        continue;

                    var charData = AutoRetainer.GetOfflineCharacterData(cid);
                    if (charData == null || charData.RetainerData == null)
                        continue;

                    if (!charData.Enabled)
                        continue;

                    foreach (var retainer in charData.RetainerData)
                    {
                        if (!retainerNames.Contains(retainer.Name))
                            continue;

                        if (!retainer.HasVenture)
                            continue;

                        var secondsRemaining = (long)retainer.VentureEndsAt - currentTime;
                        
                        if (secondsRemaining <= 0 || secondsRemaining <= threshold)
                        {
                            hasRetainersReady = true;
                            var effectiveTime = secondsRemaining <= 0 ? 0 : secondsRemaining;
                            if (!closestTime.HasValue || effectiveTime < closestTime.Value)
                                closestTime = effectiveTime;
                        }
                    }
                }

                if (hasRetainersReady && closestTime.HasValue)
                {
                    if (!_autoRetainerMultiModeEnabled)
                    {
                        var player = Dalamud.Objects.LocalPlayer;
                        if (player != null)
                            _originalCharacterNameWorld = $"{player.Name}@{player.HomeWorld.Value.Name}";
                        
                        AutoRetainer.EnableMultiMode?.Invoke();
                        _autoRetainerMultiModeEnabled = true;
                    }
                    AutoStatus = $"Waiting for retainers ({closestTime.Value}s remaining)...";
                    return true;
                }
                else
                {
                    if (_autoRetainerMultiModeEnabled)
                    {
                        AutoRetainer.AbortAllTasks?.Invoke();
                        AutoRetainer.DisableAllFunctions?.Invoke();
                        _autoRetainerMultiModeEnabled = false;
                    }
                    
                    if (!string.IsNullOrEmpty(_originalCharacterNameWorld))
                    {
                        var currentPlayer = Dalamud.Objects.LocalPlayer;
                        if (currentPlayer != null)
                        {
                            var currentCharacter = $"{currentPlayer.Name}@{currentPlayer.HomeWorld.Value.Name}";
                            if (currentCharacter != _originalCharacterNameWorld)
                            {
                                if (Lifestream.IsBusy != null && Lifestream.IsBusy())
                                {
                                    AutoStatus = $"Waiting for character change to complete...";
                                    return true;
                                }
                                
                                if (Lifestream.Enabled && Lifestream.ChangeCharacter != null)
                                {
                                    var parts = _originalCharacterNameWorld.Split('@');
                                    if (parts.Length == 2)
                                    {
                                        var charName = parts[0];
                                        var worldName = parts[1];
                                        
                                        AutoStatus = $"Relogging to {charName}@{worldName}...";
                                        
                                        var errorCode = Lifestream.ChangeCharacter(charName, worldName);
                                        if (errorCode == 0)
                                        {
                                            return true;
                                        }
                                        else
                                        {
                                            GatherBuddy.Log.Warning($"Failed to relog to {_originalCharacterNameWorld}. Error code: {errorCode}");
                                            _originalCharacterNameWorld = null;
                                        }
                                    }
                                    else
                                    {
                                        GatherBuddy.Log.Warning($"Invalid character format: {_originalCharacterNameWorld}");
                                        _originalCharacterNameWorld = null;
                                    }
                                }
                                else
                                {
                                    GatherBuddy.Log.Warning("Cannot relog - Lifestream not available");
                                    _originalCharacterNameWorld = null;
                                }
                            }
                            else
                            {
                                if (!Player.Available || !Player.Interactable)
                                {
                                    AutoStatus = "Waiting for player to be ready...";
                                    return true;
                                }
                                
                                _originalCharacterNameWorld = null;
                            }
                        }
                        
                        return true;
                    }
                    
                    return false;
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to check AutoRetainer venture times: {e.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _advancedUnstuck.Dispose();
            _activeItemList.Dispose();
            _diadem?.Dispose();
            Dalamud.Chat.CheckMessageHandled -= OnMessageHandled;
            Dalamud.ToastGui.QuestToast -= OnQuestToast;
            //Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
        }
    }
}
