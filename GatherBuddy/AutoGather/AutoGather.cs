using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;

using GatherBuddy.Automation;
using GatherBuddy.Helpers;
using GatherBuddy.Utilities;
using GatherBuddy.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
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
            ArtisanExporter              =  new Reflection.ArtisanExporter(plugin.AutoGatherListsManager);
            Dalamud.Chat.CheckMessageHandled += OnMessageHandled;
            //Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
            _plugin.FishRecorder.Parser.CaughtFish += OnFishCaught;
        }
        public Fish? LastCaughtFish { get; private set; }
        public Fish? PreviouslyCaughtFish { get; private set; }
        private void OnFishCaught(Fish arg1, ushort arg2, byte arg3, bool arg4, bool arg5)
        {
            PreviouslyCaughtFish = LastCaughtFish;
            LastCaughtFish       = arg1;
            
            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                if (_currentAutoHookTarget?.Fish?.ItemId == arg1.ItemId)
                {
                    var targetQuantity = _currentAutoHookTarget.Value.Quantity;
                    var currentCount = arg1.GetInventoryCount();
                    
                    if (currentCount >= targetQuantity)
                    {
                        GatherBuddy.Log.Information($"[AutoGather] Target fish count reached ({currentCount}/{targetQuantity}), disabling auto-cast");
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                }
            }
        }

        // Track the current gather target for robust node handling
        private GatherTarget? _currentGatherTarget;

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

        private readonly GatherBuddy     _plugin;
        private readonly SoundHelper     _soundHelper;
        private readonly AdvancedUnstuck _advancedUnstuck;
        private readonly ActiveItemList  _activeItemList;

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

                    if (VNavmesh.Enabled && IsPathGenerating)
                        VNavmesh.Nav.PathfindCancelAll();
                    StopNavigation();
                    CurrentFarNodeLocation   = null;
                    _homeWorldWarning        = false;
                    _diademQueuingInProgress = false;
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.Clear();
                    _diademSpawnAreaLastChecked.Clear();
                    _currentDiademPatrolTarget = null;
                    _diademRecentlyGatheredNodes.Clear();
                    _diademArborCallUsedAt = DateTime.MinValue;
                    _diademArborCallTarget = null;
                    _diademVisitedNodes.Clear();
                    _lastAetherTarget = DateTime.MinValue;
                    _lastNonTimedNodeTerritory = 0;
                    _lastUmbralWeather = 0;
                    _hasGatheredUmbralThisSession = false;
                    _autoRetainerWasEnabledBeforeDiadem = false;
                    
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
                    
                    if (GatherBuddy.Config.CollectableConfig.CollectOnAutogatherDisabled && _disabledBySystem)
                    {
                        GatherBuddy.Log.Debug("[AutoGather] Triggering collectable turn-in after AutoGather ended (system disable)");
                        GatherBuddy.CollectableManager?.Start();
                    }
                    else if (GatherBuddy.CollectableManager?.IsRunning == true)
                    {
                        GatherBuddy.Log.Debug("[AutoGather] Stopping collectable turn-in (user disabled AutoGather)");
                        GatherBuddy.CollectableManager?.Stop();
                    }
                    
                    _disabledBySystem = false;
                }
            else
            {
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
                
                var isInDiademOrFirmament = currentTerritory is 901 or 929 or 939 or 886;
                var wasInDiademOrFirmament = _lastTerritory is 901 or 929 or 939 or 886;
                
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
                
                if (currentTerritory is 901 or 929 or 939)
                {
                    _hasGatheredUmbralThisSession = false;
                    _lastUmbralWeather = 0;
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
                var gatherTarget = _currentGatherTarget!;
                // Mark the node as visited if possible
                var targetNode = Dalamud.Targets.Target ?? Dalamud.Targets.PreviousTarget;
                if (targetNode != null && targetNode.ObjectKind is ObjectKind.GatheringPoint)
                {
                    _activeItemList.MarkVisited(targetNode);
                    var gatherable = gatherTarget.Value.Gatherable;
                    var node = gatherTarget.Value.Node;
                    
                    if (Functions.InTheDiadem())
                    {
                        var isUmbralNode = UmbralNodes.UmbralNodeData.Any(entry => entry.NodeId == targetNode.DataId);
                        
                        if (isUmbralNode)
                        {
                            _hasGatheredUmbralThisSession = true;
                            
                            FarNodesSeenSoFar.Clear();
                            _diademVisitedNodes.Clear();
                            _diademRecentlyGatheredNodes.Clear();
                        }
                        else
                        {
                            _diademRecentlyGatheredNodes.AddLast(targetNode.Position);
                            while (_diademRecentlyGatheredNodes.Count > DiademNodeRespawnWindow)
                                _diademRecentlyGatheredNodes.RemoveFirst();
                                
                            _diademVisitedNodes.AddLast(targetNode.Position);
                            while (_diademVisitedNodes.Count > DiademVisitedNodeTrackingCount)
                                _diademVisitedNodes.RemoveFirst();
                                
                            FarNodesSeenSoFar.Add(targetNode.Position);
                        }
                    }
                    
                    if (gatherable != null && (gatherable.NodeType == NodeType.Regular || gatherable.NodeType == NodeType.Ephemeral)
                        && (VisitedNodes.Last?.Value != targetNode.DataId)
                        && node != null && node.WorldPositions.ContainsKey(targetNode.DataId))
                    {
                        if (!Functions.InTheDiadem())
                            FarNodesSeenSoFar.Clear();
                        VisitedNodes.AddLast(targetNode.DataId);
                        while (VisitedNodes.Count > (node.WorldPositions.Count <= 4 ? 2 : 4))
                            VisitedNodes.RemoveFirst();
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
                AutoStatus = Dalamud.Conditions[ConditionFlag.Gathering] ? "Gathering..." : "Player is busy...";
                return;
            }


            if (FreeInventorySlots == 0)
            {
                if (HasReducibleItems())
                {
                    if (IsGathering)
                        CloseGatheringAddons();
                    else
                        ReduceItems(false);
                }
                else
                {
                    AbortAutoGather("Inventory is full");
                }

                return;
            }

            if (IsGathering && Player.Job == 18 /* FSH */ && _activeItemList.ShouldUpdateWhileFishing())
            {
                var nextAfterRefresh = _activeItemList.GetNextOrDefault(new List<uint>());
                var currentFishId = _currentAutoHookTarget?.Fish?.ItemId ?? 0;
                var currentFishIsTimed = _currentAutoHookTarget?.Time != Time.TimeInterval.Always;
                
                var currentTargetStillAvailable = nextAfterRefresh.Any(g => g.Fish?.ItemId == currentFishId);
                
                var hasNewTimedTarget = nextAfterRefresh.Any(g => g.Fish != null
                    && g.Time != Time.TimeInterval.Always
                    && g.Fish!.ItemId != currentFishId);
                
                var shouldSwitchToNewTimed = !currentFishIsTimed && hasNewTimedTarget;
                var shouldQuitExpiredTimed = currentFishIsTimed && !currentTargetStillAvailable;
                
                if (shouldSwitchToNewTimed || shouldQuitExpiredTimed)
                {
                    var reason = shouldSwitchToNewTimed
                        ? "timed/weather fish available" 
                        : "current timed fish window ended";
                    GatherBuddy.Log.Information($"[AutoGather] {reason} - quitting fishing to pursue new target");
                    
                    if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
                    {
                        AutoHook.SetAutoStartFishing?.Invoke(false);
                    }
                    
                    CleanupAutoHook();
                    QueueQuitFishingTasks();
                    _activeItemList.ForceRefresh();
                    return;
                }
            }

            if (_activeItemList.GetNextOrDefault(new List<uint>()).Any(g => g.Fish != null)
             && !GatherBuddy.Config.AutoGatherConfig.FishDataCollection)
            {
                Communicator.PrintError(
                    "You have fish on your auto-gather list but you have not opted in to fishing data collection. Auto-gather cannot continue. Please enable fishing data collection in your configuration options or remove fish from your auto-gather lists.");
                AbortAutoGather();
                return;
            }

            if (IsGathering)
            {
                // Set the current gather target when entering a node
                if (_currentGatherTarget == null)
                {
                    if (!_activeItemList.IsInitialized)
                        _currentGatherTarget = _activeItemList.GetNextOrDefault([Dalamud.Targets.Target!.DataId]).FirstOrDefault();
                    else
                        _currentGatherTarget = _activeItemList.CurrentOrDefault;
                }

                IEnumerable<GatherTarget> gatherTarget = _currentGatherTarget != null ? new[] { (GatherTarget)_currentGatherTarget } : Array.Empty<GatherTarget>();

                if (!GatherBuddy.Config.AutoGatherConfig.DoGathering)
                    return;

                AutoStatus = "Gathering...";
                StopNavigation();

                var fish = _activeItemList.GetNextOrDefault(new List<uint>()).Where(g => g.Fish != null);
                if (fish.Any() && Player.Job == 18 /* FSH */)
                {
                    var isSpearfishing = fish.First().Fish?.IsSpearFish == true;
                    
                    if (isSpearfishing)
                    {
                        _wasGatheringSpearfish = true;
                        _wasAtShadowNode = _currentGatherTarget?.FishingSpot?.IsShadowNode == true;
                        
                        var currentFishId = fish.First().Fish?.ItemId ?? 0;
                        var targetFishId = _currentAutoHookTarget?.Fish?.ItemId ?? 0;
                        var now = DateTime.Now;
                        
                        if (!_currentAutoHookTarget.HasValue || targetFishId != currentFishId)
                        {
                            SetupAutoHookForFishing(fish.First());
                            _lastAutoHookSetupTime = now;
                            _autoHookSetupComplete = false;
                        }
                        else if (!_autoHookSetupComplete && (now - _lastAutoHookSetupTime).TotalSeconds >= 1.0)
                        {
                            SetupAutoHookForFishing(fish.First());
                            _lastAutoHookSetupTime = now;
                        }
                        return;
                    }
                    
                    if (GatherBuddy.Config.AutoGatherConfig.UseNavigation)
                    {
                        var pathGenerating = IsPathGenerating;
                        var pathing = IsPathing;
                        var unstuckResult = _advancedUnstuck.Check(CurrentDestination, pathGenerating, pathing);
                        if (unstuckResult == AdvancedUnstuckCheckResult.Fail)
                        {
                            StopNavigation();
                            AutoStatus = "Advanced unstuck in progress!";
                            return;
                        }
                        DoFishMovement(fish);
                    }
                    DoFishingTasks(fish);
                    return;
                }

                if (!fish.Any() && Player.Job == 18 /* FSH */)
                {
                    QueueQuitFishingTasks();
                }

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

            switch (_advancedUnstuck.Check(CurrentDestination, isPathGenerating, isPathing))
            {
                case AdvancedUnstuckCheckResult.Pass: break;
                case AdvancedUnstuckCheckResult.Wait: return;
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
                if (SpiritbondMax > 0)
                {
                    GatherBuddy.Log.Debug($"[Materia] Triggering extraction. IsGathering={IsGathering}, SpiritbondMax={SpiritbondMax}");
                    if (IsGathering)
                    {
                        QueueQuitFishingTasks();
                    }

                    DoMateriaExtraction();
                    return;
                }

                if (FreeInventorySlots < 20 && HasReducibleItems())
                {
                    ReduceItems(true);
                    return;
                }
            }

            if (Functions.InTheDiadem())
            {
                TryUseAetherCannon();
                
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
                var wasUmbralWeather = UmbralNodes.IsUmbralWeather(_lastUmbralWeather);
                var weatherChanged = currentWeather != _lastUmbralWeather && _lastUmbralWeather != 0;
                
                var hasUmbralItems = HasUmbralItemsInActiveList();
                var hasNormalDiademItems = _activeItemList.Any(target => target.Gatherable != null && 
                    !UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(target.Gatherable.ItemId)) &&
                    target.Location.Territory.Id is 901 or 929 or 939);
                
                if (weatherChanged && isUmbralWeather && !wasUmbralWeather && hasUmbralItems && hasNormalDiademItems)
                {
                    StopNavigation();
                    GatherBuddy.Log.Information($"[Umbral] Weather changed to umbral ({currentWeather}) - leaving Diadem for clean state");
                    _lastUmbralWeather = currentWeather;
                    LeaveTheDiadem();
                    return;
                }
                
                if (weatherChanged && !isUmbralWeather && wasUmbralWeather && hasUmbralItems && hasNormalDiademItems)
                {
                    StopNavigation();
                    GatherBuddy.Log.Information($"[Umbral] Weather changed to normal ({currentWeather}) - leaving Diadem for clean state");
                    _lastUmbralWeather = currentWeather;
                    LeaveTheDiadem();
                    return;
                }
                
                if (weatherChanged && !isUmbralWeather && wasUmbralWeather && hasUmbralItems && !hasNormalDiademItems)
                {
                    _hasGatheredUmbralThisSession = false;
                    _lastUmbralWeather = currentWeather;
                    GatherBuddy.Log.Information($"[Umbral] Weather changed to normal with only umbral items - leaving Diadem");
                    StopNavigation();
                    LeaveTheDiadem();
                    return;
                }
                
                _lastUmbralWeather = currentWeather;
            }

            var nearbyNodes = Dalamud.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable).Select(o => o.DataId);
            
            var hasNormalDiademItemsInList = _activeItemList.Any(target => target.Gatherable != null && 
                !UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(target.Gatherable.ItemId)) &&
                target.Location.Territory.Id is 901 or 929 or 939);
            
            var next = _activeItemList.GetNextOrDefault(nearbyNodes)
                .Where(target => {
                    if (Functions.InTheDiadem() && _hasGatheredUmbralThisSession && hasNormalDiademItemsInList)
                    {
                        var isUmbralItem = IsUmbralItem(target.Item);
                        if (isUmbralItem)
                        {
                            return false;
                        }
                    }
                    return true;
                })
                .OrderByDescending(nodes => nodes.Item.ItemId);
            
            if (Functions.InTheDiadem() && _hasGatheredUmbralThisSession)
            {
                var hasUmbralItems = HasUmbralItemsInActiveList();
                
                if (hasUmbralItems)
                {
                    GatherBuddy.Log.Information($"[Umbral] Gathered umbral node - leaving Diadem to reset session");
                    StopNavigation();
                    LeaveTheDiadem();
                    return;
                }
            }
            if (!next.Any())
            {
                if (!_activeItemList.HasItemsToGather)
                {
                    AbortAutoGather();
                    return;
                }
                
                var hasUmbralItemsInList = HasUmbralItemsInActiveList();
                if (hasUmbralItemsInList)
                {
                    if (Functions.InTheDiadem())
                    {
                        var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                        var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
                        
                        if (!isUmbralWeather)
                        {
                            AutoStatus = "Waiting in Diadem for umbral weather...";
                            if (!Waiting)
                            {
                                Waiting = true;
                                _plugin.Ipc.AutoGatherWaiting();
                            }
                            return;
                        }
                    }
                    
                    var firstUmbralItem = GetFirstUmbralItemFromActiveList();
                    if (firstUmbralItem.Item != null)
                    {
                        var diademNode = GatherBuddy.GameData.GatheringNodes.Values
                            .FirstOrDefault(node => node.Territory.Id is 901 or 929 or 939);
                            
                        if (diademNode != null)
                        {
                            var syntheticTarget = new GatherTarget(firstUmbralItem.Item, diademNode, Time.TimeInterval.Always, firstUmbralItem.Quantity);
                            next = new[] { syntheticTarget }.OrderByDescending(nodes => nodes.Item.ItemId);
                            AutoStatus = "Traveling to Diadem for umbral items...";
                        }
                    }
                }
                
                if (!next.Any())
                {
                    if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle)
                        if (GoHome())
                            return;

                    if (HasReducibleItems())
                    {
                        if (Player.Job == 18 /* FSH */)
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
                        else
                        {
                            ReduceItems(true);
                        }

                        return;
                    }

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

            if (next.Any(n => n.Item.ItemData.IsCollectable
                 && !CheckCollectablesUnlocked(n.Fish != null ? GatheringType.Fisher : n.Gatherable!.GatheringType.ToGroup())))
            {
                AbortAutoGather();
                return;
            }

            if (RepairIfNeeded())
                return;

            if (!GatherBuddy.Config.AutoGatherConfig.UseNavigation)
            {
                AutoStatus = "Waiting for Gathering Point... (No Nav Mode)";
                return;
            }

            var territoryId = Dalamud.ClientState.TerritoryType;
            var targetTerritoryId = next.First().Node?.Territory.Id ?? next.First().FishingSpot?.Territory.Id ?? 0;
            
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
            if ((territoryId == 478 && (next.First().Node?.Territory.Id == 399 || next.First().FishingSpot?.Territory.Id == 399))
             || (territoryId == 418 && (next.First().Node?.Territory.Id is 901 or 929 or 939 || next.First().FishingSpot?.Territory.Id is 901 or 929 or 939)) && Lifestream.Enabled)
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
                        string name = string.Empty;
                        switch (territoryId)
                        {
                            case 478:
                                var xCoord = next.First().Node?.DefaultXCoord ?? next.First().FishingSpot?.DefaultXCoord ?? 0;
                                var exit = xCoord < 2000 ? 91u : 92u;
                                name = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRow(exit).AethernetName.Value.Name
                                    .ToString();
                                break;
                            case 418:
                                name = Dalamud.GameData.GetExcelSheet<TerritoryType>().GetRow(886).PlaceName.Value.Name.ToString()
                                    .Split(" ")[1];
                                break;
                        }

                        TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                        TaskManager.DelayNext(1000);
                        TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                    }

                    return;
                }
            }

            if (territoryId == 886 && (next.First().Node?.Territory.Id is 901 or 929 or 939 || next.First().FishingSpot?.Territory.Id is 901 or 929 or 939))
            {
                if (JobAsGatheringType == GatheringType.Unknown)
                {
                    var requiredGatheringType = next.First().Location.GatheringType.ToGroup();
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
                
                var dutyNpc                    = Dalamud.Objects.FirstOrDefault(o => o.DataId == 1031694);
                var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                GatherBuddy.Log.Verbose($"Addons: {selectStringAddon}, {talkAddon}, {selectYesNoAddon}, {contentsFinderConfirmAddon}");
                if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                {
                    AutoStatus = "Moving to Diadem NPC...";
                    var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000);
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

            var forcedAetheryte = ForcedAetherytes.ZonesWithoutAetherytes
                .FirstOrDefault(z => z.ZoneId == next.First().Location.Territory.Id);
            if (forcedAetheryte.ZoneId != 0
             && GatherBuddy.GameData.Aetherytes[forcedAetheryte.AetheryteId].Territory.Id == territoryId)
            {
                var needsLifestream = territoryId == 478 || territoryId == 129 || territoryId == 128 || territoryId == 132 || territoryId == 133 || territoryId == 130 || territoryId == 131;
                if (needsLifestream && !Lifestream.Enabled)
                    AutoStatus = $"Install Lifestream or teleport to {next.First().Location.Territory.Name} manually";
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

            //At this point, we are definitely going to gather something, so we may go home after that.
            if (Lifestream.Enabled)
                Lifestream.Abort();
            WentHome = false;
            
            var isTimedNode = next.First().Node?.Times.AlwaysUp() == false;
            if (!isTimedNode && next.First().Location.Territory.Id == territoryId)
            {
                _lastNonTimedNodeTerritory = territoryId;
            }

            
            // fuck Diadem. Covering all zoneIDs for Diadem to gather g2/3/4.
            var isCurrentDiadem = territoryId is 901 or 929 or 939;
            var isTargetDiadem = next.First().Location.Territory.Id is 901 or 929 or 939;
            
            var isInSameCityPair = (territoryId is 128 or 129 && targetTerritoryId is 128 or 129)
                                || (territoryId is 132 or 133 && targetTerritoryId is 132 or 133)
                                || (territoryId is 130 or 131 && targetTerritoryId is 130 or 131);
            
            if (next.First().Location.Territory.Id != territoryId && !(isCurrentDiadem && isTargetDiadem) && !isInSameCityPair)
            {
                if (GatherBuddy.Config.AutoGatherConfig.SortingMethod == AutoGatherConfig.SortingType.Location)
                {
                    var nextIsTimedNode = next.First().Node?.Times.AlwaysUp() == false;
                    
                    if (!nextIsTimedNode)
                    {
                        if (_lastNonTimedNodeTerritory != 0 && _lastNonTimedNodeTerritory != territoryId)
                        {
                            var itemsInPreviousZone = _activeItemList
                                .Where(i => i.Node?.Territory.Id == _lastNonTimedNodeTerritory)
                                .Where(i => i.Node?.Times.AlwaysUp() != false)
                                .ToList();
                            
                            if (itemsInPreviousZone.Any())
                            {
                                var previousZoneItem = itemsInPreviousZone.First();
                                if (!LocationMatchesJob(previousZoneItem.Location))
                                {
                                    if (ChangeGearSet(previousZoneItem.Location.GatheringType.ToGroup(), 2400))
                                    {
                                        return;
                                    }
                                }
                                
                                StopNavigation();
                                if (!MoveToTerritory(previousZoneItem.Location))
                                    AbortAutoGather();
                                return;
                            }
                        }
                        
                        var itemsInCurrentZone = _activeItemList
                            .Where(i => i.Node?.Territory.Id == territoryId)
                            .Where(i => i.Node?.Times.AlwaysUp() != false)
                            .ToList();
                        
                        if (itemsInCurrentZone.Any())
                        {
                            var currentZoneItem = itemsInCurrentZone.First();
                            if (!LocationMatchesJob(currentZoneItem.Location))
                            {
                                if (ChangeGearSet(currentZoneItem.Location.GatheringType.ToGroup(), 2400))
                                {
                                    return;
                                }
                            }
                            else
                            {
                                return;
                            }
                        }
                    }
                }
                
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Functions.InTheDiadem())
                {
                    AutoStatus = "Can not teleport when bound by duty";
                    return;
                }
                else if (Functions.InTheDiadem())
                {
                    // Check if we're waiting for umbral weather - if so, don't leave due to territory mismatch
                    var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                    var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
                    var hasUmbralItems = next.Any(target => target.Gatherable != null && 
                        UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(target.Gatherable.ItemId)));
                    
                    if (!isUmbralWeather && hasUmbralItems)
                    {
                        // We're waiting for umbral weather - don't leave due to territory mismatch
                        // Also reset session flag in case it wasn't reset in DoNodeMovementDiadem
                        _hasGatheredUmbralThisSession = false;
                        AutoStatus = "Waiting in Diadem for next umbral weather...";
                        return;
                    }
                    else
                    {
                        LeaveTheDiadem();
                        return;
                    }
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

            if (!MoveToTerritory(next.First().Location))
                AbortAutoGather();

                // Reset target to pick up closest item after teleport
                next = default;

                return;
            }

            var targetGatheringType = next.First().Location.GatheringType.ToGroup();
            
            var config = MatchConfigPreset(next.First().Gatherable);

            if (DoUseConsumablesWithoutCastTime(config))
                return;
            var firstItem = next.First().Gatherable;
            
            if (firstItem != null && UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(firstItem.ItemId)))
            {
                var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
                if (UmbralNodes.IsUmbralWeather(currentWeather))
                {
                    var umbralWeather = (UmbralNodes.UmbralWeatherType)currentWeather;
                    targetGatheringType = umbralWeather switch
                    {
                        UmbralNodes.UmbralWeatherType.UmbralFlare => GatheringType.Miner,
                        UmbralNodes.UmbralWeatherType.UmbralLevin => GatheringType.Miner,
                        UmbralNodes.UmbralWeatherType.UmbralDuststorms => GatheringType.Botanist,
                        UmbralNodes.UmbralWeatherType.UmbralTempest => GatheringType.Botanist,
                        _ => targetGatheringType
                    };
                }
            }
            
            var shouldSkipJobSwitch = Functions.InTheDiadem() && _hasGatheredUmbralThisSession;
            
            if (shouldSkipJobSwitch)
            {
                GatherBuddy.Log.Information($"[Umbral] Skipping job switch in Diadem after umbral gathering (staying on {JobAsGatheringType})");
            }
            
            if (JobAsGatheringType != targetGatheringType && !shouldSkipJobSwitch)
            {
                if (!ChangeGearSet(targetGatheringType, 2400))
                    AbortAutoGather();
                return;
            }

            if (next.First().Fish != null)
            {
                if (next.First().FishingSpot?.Spearfishing == true)
                {
                    DoNodeMovement(next, config);
                    return;
                }
                
                DoFishMovement(next);
                return;
            }

            
            if (next.First().Gatherable != null)
            {
                DoNodeMovement(next, config);
                return;
            }

            AutoStatus = "Fell out of control loop unexpectedly. Please report this error.";
            return;
        }

        public readonly Dictionary<GatherTarget, (Vector3 Position, Angle Rotation, DateTime Expiration)> FishingSpotData = new();
        private readonly Dictionary<Vector3, DateTime> _fishingSpotDismountAttempts = new();

        private void DoFishMovement(IEnumerable<GatherTarget> next)
        {
            var fish = next.First(ne => ne.Fish != null);
            var territoryId = Dalamud.ClientState.TerritoryType;
            var isCurrentDiadem = territoryId is 901 or 929 or 939;
            var isTargetDiadem = fish.FishingSpot?.Territory.Id is 901 or 929 or 939;
            
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
            
            if (territoryId == 418 && fish.FishingSpot?.Territory.Id is 901 or 929 or 939 && Lifestream.Enabled)
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
                        string name = Dalamud.GameData.GetExcelSheet<TerritoryType>().GetRow(886).PlaceName.Value.Name.ToString()
                            .Split(" ")[1];

                        TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                        TaskManager.DelayNext(1000);
                        TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                    }

                    return;
                }
            }
            
            if (fish.FishingSpot?.Territory.Id != territoryId && !(isCurrentDiadem && isTargetDiadem))
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Functions.InTheDiadem())
                {
                    AutoStatus = "Can not teleport when bound by duty";
                    return;
                }
                else if (Functions.InTheDiadem())
                {
                    LeaveTheDiadem();
                    return;
                }
                
                if (territoryId == 886 && fish.FishingSpot?.Territory.Id is 901 or 929 or 939)
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
                    
                    var dutyNpc                    = Dalamud.Objects.FirstOrDefault(o => o.DataId == 1031694);
                    var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                    var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                    var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                    var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                    
                    if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                    {
                        AutoStatus = "Moving to Diadem NPC...";
                        var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000);
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

            if (IsFishing)
            {
                if (fishingSpotData.Expiration < DateTime.Now)
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
                
                StopNavigation();
                AutoStatus = "Fishing...";
                DoFishingTasks(next);
                return;
            }
            
            if (fishingSpotData.Expiration < DateTime.Now)
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

                if (fishingSpotData.Expiration == DateTime.MaxValue)
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

            if (CurrentDestination != fishingSpotData.Position)
            {
                StopNavigation();
                AutoStatus = "Moving to fishing spot...";
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

        private void DoNodeMovementDiadem(IEnumerable<GatherTarget> next, ConfigPreset config)
        {
            var targetGatheringType = next.First().Location.GatheringType;
            var currentJob = JobAsGatheringType;
            
            var currentWeather = EnhancedCurrentWeather.GetCurrentWeatherId();
            var isUmbralWeather = UmbralNodes.IsUmbralWeather(currentWeather);
            
            var hasUmbralItems = HasUmbralItemsInActiveList();
                
            if (isUmbralWeather && hasUmbralItems)
            {
                var currentUmbralWeather = (UmbralNodes.UmbralWeatherType)currentWeather;
                var relevantUmbralNodes = UmbralNodes.GetNodesForWeatherAndType(currentUmbralWeather, currentJob);
                
                foreach (var nodeId in relevantUmbralNodes)
                {
                    var nodeItems = UmbralNodes.GetItemsForNode(nodeId);
                    var hasNeededItems = GetActiveItemsNeedingGathering()
                        .Any(item => nodeItems.Contains(item.Item.ItemId));
                    
                    if (hasNeededItems && GatherBuddy.GameData.WorldCoords.TryGetValue(nodeId, out var umbralPositions))
                    {
                        var validPositions = umbralPositions
                            .Where(pos => !IsBlacklisted(pos))
                            .OrderBy(pos => Vector3.Distance(Player.Position, pos))
                            .ToList();
                        
                        if (validPositions.Any())
                        {
                            var targetPosition = validPositions.First();
                            var distance = Vector3.Distance(Player.Position, targetPosition);
                            
                            const float CloseEnoughDistance = 50f;
                            
                            if (distance > CloseEnoughDistance)
                            {
                                AutoStatus = $"Rushing to umbral node {nodeId} (Weather: {currentUmbralWeather}, {distance:F0}y)...";
                                
                                if (!Dalamud.Conditions[ConditionFlag.Mounted] && distance >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance)
                                {
                                    if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                                        Navigate(targetPosition, false);
                                    EnqueueMountUp();
                                }
                                else
                                {
                                    Navigate(targetPosition, ShouldFly(targetPosition));
                                }
                                return;
                            }
                            else
                            {
                                // Don't return - let the normal node detection logic below handle finding the actual spawned node
                            }
                        }
                    }
                }
            }
            
            const float RecentlyGatheredDistance = 5f;
            
            var allVisibleNodes = Dalamud.Objects
                .Where(o => o.ObjectKind == ObjectKind.GatheringPoint)
                .Where(o => !IsBlacklisted(o.Position))
                .Where(o => !_diademRecentlyGatheredNodes.Any(recent => Vector3.Distance(recent, o.Position) < RecentlyGatheredDistance))
                .Where(o =>
                {
                    if (!GatherBuddy.GameData.WorldCoords.TryGetValue(o.DataId, out _))
                        return false;
                    
                    var gatheringPoint = Dalamud.GameData.GetExcelSheet<GatheringPoint>()?.GetRow(o.DataId);
                    if (gatheringPoint == null || !gatheringPoint.HasValue)
                        return false;
                    
                    var nodeBase = gatheringPoint.Value.GatheringPointBase;
                    if (nodeBase.RowId == 0)
                        return false;
                    
                    var nodeGatheringType = (GatheringType)nodeBase.Value.GatheringType.RowId;
                    return nodeGatheringType.ToGroup() == currentJob;
                })
                .ToList();
            
            var nonUmbralNodes = allVisibleNodes
                .Where(o => !UmbralNodes.UmbralNodeData.Any(entry => entry.NodeId == o.DataId))
                .OrderBy(o => Vector3.Distance(Player.Position, o.Position))
                .ToList();
                
            if (isUmbralWeather && hasUmbralItems)
            {
                var umbralNodes = allVisibleNodes
                    .Where(o => UmbralNodes.UmbralNodeData.Any(entry => entry.NodeId == o.DataId))
                    .OrderBy(o => Vector3.Distance(Player.Position, o.Position))
                    .ToList();
                
                if (umbralNodes.Any())
                {
                    allVisibleNodes = umbralNodes;
                    AutoStatus = $"Targeting umbral nodes (Weather: {(UmbralNodes.UmbralWeatherType)currentWeather})...";
                }
                else
                {
                    allVisibleNodes = nonUmbralNodes;
                }
            }
            else
            {
                allVisibleNodes = nonUmbralNodes;
            }

            if (ActivateGatheringBuffs(false))
                return;

            const float NearbyNodeDistance = 150f;
            foreach (var visibleNode in allVisibleNodes.Where(node => !node.IsTargetable))
            {
                var nodePosition = visibleNode.Position;
                if (Vector3.Distance(Player.Position, nodePosition) < NearbyNodeDistance)
                {
                    if (!FarNodesSeenSoFar.Contains(nodePosition))
                        FarNodesSeenSoFar.Add(nodePosition);
                }
            }
            
            const int MaxSeenNodes = 100;
            if (FarNodesSeenSoFar.Count > MaxSeenNodes)
                FarNodesSeenSoFar.Clear();

            var targetableNodes = allVisibleNodes.Where(o => o.IsTargetable).ToList();
            
            if (targetableNodes.Any())
            {
                var closestNode = targetableNodes.First();
                var distance = Vector3.Distance(Player.Position, closestNode.Position);
                
                AutoStatus = $"Moving to Diadem node ({distance:F0}y)...";
                
                Gatherable targetItem;
                if (isUmbralWeather && hasUmbralItems && UmbralNodes.UmbralNodeData.Any(entry => entry.NodeId == closestNode.DataId))
                {
                    var currentUmbralWeather = (UmbralNodes.UmbralWeatherType)currentWeather;
                    var matchingUmbralItem = next
                        .Where(target => target.Gatherable != null)
                        .Where(target => UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(target.Gatherable.ItemId) && 
                                                                                entry.Weather == currentUmbralWeather &&
                                                                                UmbralNodes.GetGatheringType(entry.NodeType) == currentJob))
                        .Select(target => target.Gatherable)
                        .FirstOrDefault();
                        
                    if (matchingUmbralItem != null)
                    {
                        targetItem = matchingUmbralItem;
                    }
                    else
                    {
                        targetItem = next.First().Gatherable;
                    }
                }
                else
                {
                    targetItem = next.First().Gatherable;
                }
                
                MoveToCloseNode(closestNode, targetItem, config);
                return;
            }
            
            AutoStatus = "Searching for next Diadem node...";
            
            var currentTerritoryId = Dalamud.ClientState.TerritoryType;
            if (!Functions.InTheDiadem())
            {
                AutoStatus = "Not in Diadem, aborting...";
                return;
            }
            
            var potentialNodePositions = GatherBuddy.GameData.GatheringNodes.Values
                .Where(node => node.Territory.Id == currentTerritoryId)
                .Where(node => node.GatheringType.ToGroup() == currentJob)
                .SelectMany(node => node.WorldPositions.Values.SelectMany(positions => positions))
                .Where(pos => !IsBlacklisted(pos))
                .Where(pos => !_diademVisitedNodes.Any(visited => Vector3.Distance(visited, pos) < DiademNodeProximityThreshold))
                .Where(pos => !FarNodesSeenSoFar.Contains(pos))
                .OrderBy(pos => Vector3.Distance(Player.Position, pos))
                .ToList();
            
            if (potentialNodePositions.Any())
            {
                var targetPosition = potentialNodePositions.First();
                var distance = Vector3.Distance(Player.Position, targetPosition);
                
                AutoStatus = $"Moving to next Diadem node ({distance:F0}y)...";
                
                if (!Dalamud.Conditions[ConditionFlag.Mounted] && distance >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance)
                {
                    if (GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                        Navigate(targetPosition, false);
                    EnqueueMountUp();
                }
                else
                {
                    Navigate(targetPosition, ShouldFly(targetPosition));
                }
            }
            else
            {
                AutoStatus = "No suitable Diadem nodes found, waiting...";
            }
        }

        private void DoNodeMovement(IEnumerable<GatherTarget> next, ConfigPreset config)
        {
            if (Functions.InTheDiadem())
            {
                DoNodeMovementDiadem(next, config);
                return;
            }

            var allPositions = next.Where(n => n.Location.Territory.Id == Player.Territory)
                .SelectMany(ne => (ne.Node?.WorldPositions ?? ne.FishingSpot?.WorldPositions)
                        ?.ExceptBy(VisitedNodes, n => n.Key)
                        .SelectMany(w => w.Value)
                        .Where(v => !IsBlacklisted(v))
                 ?? [])
                .ToHashSet();

            var visibleNodes = Dalamud.Objects
                .Where(o => allPositions.Contains(o.Position))
                .ToList();

            var closestTargetableNode = visibleNodes
                .Where(o => o.IsTargetable)
                .MinBy(o => Vector3.Distance(Player.Position, o.Position));

            var isSpearfishing = next.First().Fish?.IsSpearFish == true;
            if (!isSpearfishing)
            {
                var isTimedNode = next.First().Gatherable?.NodeType is NodeType.Unspoiled or NodeType.Legendary;
                if (ActivateGatheringBuffs(isTimedNode))
                    return;
            }

            if (closestTargetableNode != null)
            {
                AutoStatus = "Moving to node...";
                var targetGather = next.First(ti => 
                    (ti.Node != null && ti.Node.WorldPositions.ContainsKey(closestTargetableNode.DataId)) ||
                    (ti.FishingSpot != null && ti.FishingSpot.WorldPositions.ContainsKey(closestTargetableNode.DataId)));
                
                if (targetGather.Gatherable != null)
                {
                    MoveToCloseNode(closestTargetableNode, targetGather.Gatherable, config);
                }
                else if (targetGather.Fish != null)
                {
                    MoveToCloseSpearfishingNode(closestTargetableNode, targetGather.Fish);
                }
                return;
            }

            AutoStatus = "Moving to far node...";

            if (CurrentDestination != default)
            {
                var currentNode = visibleNodes.FirstOrDefault(o => o.Position == CurrentDestination);
                if (currentNode != null && !currentNode.IsTargetable)
                    GatherBuddy.Log.Verbose($"Far node is not targetable, distance {currentNode.Position.DistanceToPlayer()}.");

                //It takes some time (roundtrip to the server) before a node becomes targetable after it becomes visible,
                //so we need to delay excluding it. But instead of measuring time, we use distance, since character is traveling at a constant speed.
                //Value 50 was determined empirically.
                foreach (var node in allPositions.Where(o => o.DistanceToPlayer() < 50))
                    FarNodesSeenSoFar.Add(node);

                if (CurrentDestination.DistanceToPlayer() < 50)
                {
                    GatherBuddy.Log.Verbose("Far node is not targetable, choosing another");
                }
                else
                {
                    return;
                }
            }

            Vector3 selectedFarNode;

            // only Legendary and Unspoiled show marker
            var timedNode = next.FirstOrDefault(n => n.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8));
            if (ShouldUseFlag && timedNode != default)
            {
                var pos = TimedNodePosition;
                // marker not yet loaded on game
                if (pos == null || timedNode.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8))
                {
                    AutoStatus = "Waiting on flag show up";
                    return;
                }

                selectedFarNode = allPositions
                    .Where(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)) < 10)
                    .OrderBy(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)))
                    .FirstOrDefault();
                if (selectedFarNode == default)
                    selectedFarNode = VNavmesh.Query.Mesh.NearestPoint(new Vector3(pos.Value.X, 0, pos.Value.Y), 10, 10000);
            }
            else
            {
                //Select the closest node
                selectedFarNode = allPositions
                    .Where(fn => !visibleNodes.Select(vn => vn.Position).Contains(fn))
                    .OrderBy(v => Vector3.Distance(Player.Position, v))
                    .FirstOrDefault(n => !FarNodesSeenSoFar.Contains(n));

                if (selectedFarNode == default)
                {
                    FarNodesSeenSoFar.Clear();
                    GatherBuddy.Log.Verbose($"Selected node was null and far node filters have been cleared");
                    return;
                }
            }

            MoveToFarNode(selectedFarNode);
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
            if (Functions.InTheDiadem())
            {
                LeaveTheDiadem();
                return;
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
        
        private bool HasUmbralItemsInActiveList()
        {
            return _activeItemList.GetType()
                .GetField("_listsManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_activeItemList) is AutoGatherListsManager listsManager
                && listsManager.ActiveItems.Any(item => UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(item.Item.ItemId)));
        }
        
        private (Gatherable Item, uint Quantity) GetFirstUmbralItemFromActiveList()
        {
            if (_activeItemList.GetType()
                .GetField("_listsManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_activeItemList) is not AutoGatherListsManager listsManager)
                return (null, 0);
                
            return listsManager.ActiveItems
                .Where(item => item.Item.GetInventoryCount() < item.Quantity)
                .FirstOrDefault(item => UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(item.Item.ItemId)));
        }
        
        private IEnumerable<(Gatherable Item, uint Quantity)> GetActiveItemsNeedingGathering()
        {
            if (_activeItemList.GetType()
                .GetField("_listsManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_activeItemList) is not AutoGatherListsManager listsManager)
                return Enumerable.Empty<(Gatherable, uint)>();
                
            return listsManager.ActiveItems
                .Where(item => item.Item.GetInventoryCount() < item.Quantity); // Only items that need gathering
        }
        
        private bool IsUmbralItem(IGatherable item)
        {
            return UmbralNodes.UmbralNodeData.Any(entry => entry.ItemIds.Contains(item.ItemId));
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
                    
                    var nextItems = _activeItemList.GetNextOrDefault(new List<uint>());
                    if (nextItems.Any())
                    {
                        var nextItem = nextItems.First();
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
            Dalamud.Chat.CheckMessageHandled -= OnMessageHandled;
            //Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
        }
    }
}
