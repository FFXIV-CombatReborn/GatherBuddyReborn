using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Collectables.Data;
using GatherBuddy.Config;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.AutoGather.Collectables;

enum CollectableState
{
    Idle,
    CheckingInventory,
    NavigatingToTurnInNpc,
    OpeningTurnInWindow,
    SelectingJob,
    SelectingItem,
    SubmittingItem,
    CheckingOvercapDialog,
    WaitingForSubmit,
    CheckingForMore,
    ClosingTurnInWindow,
    StartingPurchaseList,
    WaitingForPurchaseList,
    ReturningHome,
    Completed,
    Error,
}

public enum CollectableRunSource
{
    Manual,
    AutoGather,
    VulcanQueue,
}

public unsafe class CollectableManager : IDisposable
{
    private readonly Configuration _config;
    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly CollectableWindowHandler _windowHandler;

    public event Action? OnFinishCollecting;
    public event Action<string>? OnError;

    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Idle";
    public CollectableRunSource CurrentRunSource { get; private set; } = CollectableRunSource.Manual;

    private CollectableState _state = CollectableState.Idle;
    private Queue<CollectableTurnInItem> _turnInQueue = new();
    private readonly Queue<Guid> _pendingPurchaseListIds = new();
    private VendorNpc? _turnInVendor;
    private VendorNpcLocation? _turnInLocation;
    private Guid? _activePurchaseListId;
    private uint _currentItemId;
    private int _currentJobId = -1;
    private DateTime _lastAction = DateTime.MinValue;
    private DateTime _stateStartTime = DateTime.MinValue;
    private readonly TimeSpan _actionDelay = TimeSpan.FromMilliseconds(400);
    private bool _overcapInterrupted;
    private bool _purchaseAttemptedForOvercap;
    private bool _lastOvercapPurchaseHitScripReserveLimit;
    private bool _returnHomeAfterCompletion;
    private bool _homeReturnStarted;
    private bool _runContainsGatheringCollectables;
    private bool _runContainsCraftingCollectables;
    private string? _lastErrorText;
    private string? _completionMessage;
    private CollectableState _nextStateAfterWindowClose = CollectableState.Idle;
    private string? _statusAfterWindowClose;

    public CollectableManager(IFramework framework, ICondition condition, Configuration config)
    {
        _config = config;
        _framework = framework;
        _condition = condition;
        _windowHandler = new CollectableWindowHandler();
    }

    public bool Start(CollectableRunSource source = CollectableRunSource.Manual, bool returnHomeAfterCompletion = false)
    {
        if (IsRunning)
        {
            GatherBuddy.Log.Debug("[CollectableManager] Collectables run already active");
            return false;
        }

        if (!CollectableTurnInRequirements.IsAvailable)
        {
            StatusText = CollectableTurnInRequirements.UnavailableStatusText;
            GatherBuddy.Log.Debug("[CollectableManager] Blocked collectables start because neither AllaganTools nor AllaganItemSearch is loaded");
            return false;
        }

        CollectableInventoryHelper.InitializeAsync();
        if (!CollectableInventoryHelper.IsTurnInItemMetadataReady)
        {
            StatusText = CollectableInventoryHelper.IsTurnInItemMetadataLoading
                ? "Collectables item data is still loading."
                : "Collectables item data is unavailable.";
            return false;
        }

        var availableItems = CollectableInventoryHelper.GetTurnInItems();
        if (availableItems.Count == 0)
        {
            StatusText = "No collectables are ready for turn-in.";
            return false;
        }

        var route = CollectableTurnInRouteResolver.ResolvePreferredRoute(_config.CollectableConfig.PreferredTurnInRoute);
        if (route == null)
        {
            StatusText = CollectableTurnInRouteResolver.HasLookupData
                ? "Collectables route locations are still loading."
                : "Collectables route data is unavailable.";
            return false;
        }

        _turnInVendor = route.Vendor;
        _turnInLocation = route.Location;
        _config.CollectableConfig.PreferredTurnInRoute = CollectableTurnInRouteResolver.ToPreference(route);
        _config.Save();

        _turnInQueue = new Queue<CollectableTurnInItem>(availableItems);
        UpdateRunCollectableTypes(availableItems);
        _pendingPurchaseListIds.Clear();
        _activePurchaseListId = null;
        _currentItemId = 0;
        _currentJobId = -1;
        _lastAction = DateTime.MinValue;
        _stateStartTime = DateTime.UtcNow;
        _overcapInterrupted = false;
        _purchaseAttemptedForOvercap = false;
        _lastOvercapPurchaseHitScripReserveLimit = false;
        _returnHomeAfterCompletion = returnHomeAfterCompletion;
        _homeReturnStarted = false;
        _lastErrorText = null;
        _completionMessage = null;
        CurrentRunSource = source;
        IsRunning = true;
        _state = CollectableState.CheckingInventory;
        StatusText = $"Starting collectables run via {DescribeSource(source)} at {route.DisplayName}.";

        GatherBuddy.Log.Information($"[CollectableManager] Starting collectables run via {DescribeSource(source)} using {route.DisplayName}");
        _framework.Update += OnUpdate;
        return true;
    }

    public void Stop()
    {
        if (!IsRunning && _state == CollectableState.Idle)
            return;

        GatherBuddy.Log.Information("[CollectableManager] Stopping collectables run");
        CleanupCurrentRun(stopActivePurchaseList: true);
        StatusText = "Collectables run stopped.";
    }

    public void ClearStatus()
    {
        if (!IsRunning)
            StatusText = "Idle";
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (!IsRunning)
                return;

            switch (_state)
            {
                case CollectableState.CheckingInventory:
                    CheckInventory();
                    break;
                case CollectableState.NavigatingToTurnInNpc:
                    UpdateTurnInNavigation();
                    break;
                case CollectableState.OpeningTurnInWindow:
                    OpenTurnInWindow();
                    break;
                case CollectableState.SelectingJob:
                    SelectJob();
                    break;
                case CollectableState.SelectingItem:
                    SelectItem();
                    break;
                case CollectableState.SubmittingItem:
                    SubmitItem();
                    break;
                case CollectableState.CheckingOvercapDialog:
                    CheckOvercapDialog();
                    break;
                case CollectableState.WaitingForSubmit:
                    WaitForSubmit();
                    break;
                case CollectableState.CheckingForMore:
                    CheckForMore();
                    break;
                case CollectableState.ClosingTurnInWindow:
                    CloseTurnInWindow();
                    break;
                case CollectableState.StartingPurchaseList:
                    StartPurchaseList();
                    break;
                case CollectableState.WaitingForPurchaseList:
                    WaitForPurchaseList();
                    break;
                case CollectableState.ReturningHome:
                    ReturnHome();
                    break;
                case CollectableState.Completed:
                    Complete();
                    break;
                case CollectableState.Error:
                    HandleError();
                    break;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CollectableManager] Error in collectables run: {ex}");
            Fail("An unexpected error occurred during collectables automation.");
        }
    }

    private void CheckInventory()
    {
        CollectableInventoryHelper.InitializeAsync();
        if (!CollectableInventoryHelper.IsTurnInItemMetadataReady)
        {
            StatusText = CollectableInventoryHelper.IsTurnInItemMetadataLoading
                ? "Collectables item data is still loading."
                : "Collectables item data is unavailable.";
            return;
        }
        var route = CollectableTurnInRouteResolver.ResolvePreferredRoute(_config.CollectableConfig.PreferredTurnInRoute);
        if (route == null)
        {
            StatusText = CollectableTurnInRouteResolver.HasLookupData
                ? "Collectables route locations are still loading."
                : "Collectables route data is unavailable.";
            return;
        }

        _turnInVendor = route.Vendor;
        _turnInLocation = route.Location;
        _config.CollectableConfig.PreferredTurnInRoute = CollectableTurnInRouteResolver.ToPreference(route);
        _config.Save();

        var items = CollectableInventoryHelper.GetTurnInItems();
        _turnInQueue = new Queue<CollectableTurnInItem>(items);
        UpdateRunCollectableTypes(items);
        _currentItemId = 0;
        _currentJobId = -1;

        if (_turnInQueue.Count == 0)
        {
            _completionMessage = "No collectables were available for turn-in.";
            _state = CollectableState.Completed;
            return;
        }

        VendorInteractionHelper.ResetShopSelectionState(_turnInVendor);
        GatherBuddy.VendorNavigator.StartNavigation(_turnInLocation);
        _stateStartTime = DateTime.UtcNow;
        StatusText = $"Navigating to {_turnInVendor.Name} for collectables turn-ins.";
        _state = CollectableState.NavigatingToTurnInNpc;
    }

    private void UpdateTurnInNavigation()
    {
        if (_turnInVendor == null || _turnInLocation == null)
        {
            Fail("No collectables turn-in route is configured.");
            return;
        }

        if (GatherBuddy.VendorNavigator.IsFailed)
        {
            Fail($"Failed to navigate to {_turnInVendor.Name} for collectables turn-ins.");
            return;
        }

        if (!GatherBuddy.VendorNavigator.IsReadyToPurchase)
            return;

        _stateStartTime = DateTime.UtcNow;
        _lastAction = DateTime.MinValue;
        StatusText = $"Opening {_turnInVendor.Name}'s collectables menu.";
        _state = CollectableState.OpeningTurnInWindow;
    }

    private void OpenTurnInWindow()
    {
        if (_turnInVendor == null || _turnInLocation == null)
        {
            Fail("No collectables turn-in route is configured.");
            return;
        }

        if (_windowHandler.IsReady)
        {
            _stateStartTime = DateTime.UtcNow;
            _lastAction = DateTime.MinValue;
            StatusText = "Selecting collectables turn-in job.";
            _state = CollectableState.SelectingJob;
            return;
        }

        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        if (VendorInteractionHelper.TryClickTalk())
        {
            _lastAction = DateTime.UtcNow;
            return;
        }

        if (VendorInteractionHelper.TrySelectShopOption(_turnInVendor, out var selectionError))
        {
            _lastAction = DateTime.UtcNow;
            return;
        }

        if (selectionError != null)
        {
            Fail(selectionError);
            return;
        }

        if (VendorInteractionHelper.TryInteractWithTarget(_turnInLocation))
        {
            _lastAction = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime) > TimeSpan.FromSeconds(15))
            Fail($"Timed out opening {_turnInVendor.Name}'s collectables menu.");
    }

    private void SelectJob()
    {
        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        if (_turnInQueue.Count == 0)
        {
            BeginCompletionFlow();
            return;
        }

        var next = _turnInQueue.Peek();
        if (_currentJobId != next.JobId)
        {
            _windowHandler.SelectJob((uint)next.JobId);
            _currentJobId = next.JobId;
            _currentItemId = 0;
            _lastAction = DateTime.UtcNow;
            return;
        }

        _state = CollectableState.SelectingItem;
    }

    private void SelectItem()
    {
        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        var next = _turnInQueue.Peek();
        if (_currentItemId != next.ItemId)
        {
            _windowHandler.SelectItemById(next.ItemId);
            _currentItemId = next.ItemId;
            _lastAction = DateTime.UtcNow;
            StatusText = $"Selecting {next.ItemName} for turn-in.";
            return;
        }

        _state = CollectableState.SubmittingItem;
    }

    private void SubmitItem()
    {
        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        var next = _turnInQueue.Peek();
        StatusText = $"Turning in {next.ItemName}.";
        _windowHandler.SubmitItem();
        _lastAction = DateTime.UtcNow;
        _stateStartTime = DateTime.UtcNow;
        _state = CollectableState.CheckingOvercapDialog;
    }

    private void CheckOvercapDialog()
    {
        if (GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno>("SelectYesno", out var addon)
         && GenericHelpers.IsAddonReady((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon))
        {
            Callback.Fire((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, true, 1);
            _lastAction = DateTime.UtcNow;
            _currentItemId = 0;
            _overcapInterrupted = true;
            GatherBuddy.Log.Warning("[CollectableManager] Scrip cap detected during collectables turn-in");

            if (_purchaseAttemptedForOvercap)
            {
                DisableAutoTurnInAndFail(_lastOvercapPurchaseHitScripReserveLimit
                    ? "Collectables are still blocked by the scrip cap after running the purchase list. The configured scrip reserve prevented spending enough scrips to continue."
                    : "Collectables are still blocked by the scrip cap after running the purchase list.");
                return;
            }

            if (!HasConfiguredPurchaseList())
            {
                DisableAutoTurnInAndFail("Scrip cap reached while turning in collectables, but no collectables purchase list is configured.");
                return;
            }

            TransitionAfterClosingTurnInWindow(CollectableState.StartingPurchaseList, "Scrip cap reached, running the collectables purchase list.");
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime) > TimeSpan.FromMilliseconds(500))
            _state = CollectableState.WaitingForSubmit;
    }

    private void WaitForSubmit()
    {
        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        if (_turnInQueue.Count == 0)
        {
            BeginCompletionFlow();
            return;
        }

        var current = _turnInQueue.Dequeue();
        var remainingCount = current.Count - 1;
        if (remainingCount > 0)
            _turnInQueue = new Queue<CollectableTurnInItem>(new[] { current with { Count = remainingCount } }.Concat(_turnInQueue));

        _purchaseAttemptedForOvercap = false;
        _lastOvercapPurchaseHitScripReserveLimit = false;
        _currentItemId = 0;
        _lastAction = DateTime.UtcNow;
        StatusText = $"Turned in {current.ItemName}.";
        _state = CollectableState.CheckingForMore;
    }

    private void CheckForMore()
    {
        if ((DateTime.UtcNow - _lastAction) < _actionDelay)
            return;

        if (_turnInQueue.Count > 0)
        {
            _state = CollectableState.SelectingJob;
            return;
        }

        if (ShouldRunPurchaseList())
        {
            TransitionAfterClosingTurnInWindow(CollectableState.StartingPurchaseList, "Running the collectables purchase list.");
            return;
        }

        BeginCompletionFlow();
    }

    private VendorPurchaseConstraints? GetPurchaseConstraints()
    {
        var reserveScripAmount = Math.Clamp(_config.CollectableConfig.ReserveScripAmount, 0, 4000);
        return reserveScripAmount > 0
            ? new VendorPurchaseConstraints((uint)reserveScripAmount)
            : null;
    }

    private void CloseTurnInWindow()
    {
        if (!_windowHandler.IsReady)
        {
            var nextState = _nextStateAfterWindowClose;
            var nextStatus = _statusAfterWindowClose;
            _nextStateAfterWindowClose = CollectableState.Idle;
            _statusAfterWindowClose = null;
            _stateStartTime = DateTime.UtcNow;
            _lastAction = DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(nextStatus))
                StatusText = nextStatus;
            _state = nextState;
            return;
        }

        if ((DateTime.UtcNow - _lastAction) >= _actionDelay)
        {
            _windowHandler.CloseWindow();
            _lastAction = DateTime.UtcNow;
        }

        if ((DateTime.UtcNow - _stateStartTime) > TimeSpan.FromSeconds(10))
            Fail("Timed out closing the collectables turn-in window.");
    }

    private void StartPurchaseList()
    {
        if (_activePurchaseListId == null)
        {
            QueuePendingPurchaseLists();
            if (_pendingPurchaseListIds.Count == 0)
            {
                if (_overcapInterrupted)
                    DisableAutoTurnInAndFail("Scrip cap reached while turning in collectables, but no collectables purchase list is configured.");
                else
                    BeginCompletionFlow();
                return;
            }

            _activePurchaseListId = _pendingPurchaseListIds.Dequeue();
        }

        var purchaseListId = _activePurchaseListId.Value;
        if (purchaseListId == Guid.Empty)
        {
            if (_overcapInterrupted)
                DisableAutoTurnInAndFail("Scrip cap reached while turning in collectables, but no collectables purchase list is configured.");
            else
                BeginCompletionFlow();
            return;
        }

        var startResult = GatherBuddy.VendorBuyListManager.Start(purchaseListId, GetPurchaseConstraints());
        if (!string.IsNullOrWhiteSpace(GatherBuddy.VendorBuyListManager.StatusText))
            StatusText = GatherBuddy.VendorBuyListManager.StatusText;

        switch (startResult)
        {
            case VendorBuyListManager.StartResult.Started:
            case VendorBuyListManager.StartResult.AlreadyRunning:
            case VendorBuyListManager.StartResult.WaitingForPreviousInteraction:
                if (_overcapInterrupted)
                    _purchaseAttemptedForOvercap = true;

                if (GatherBuddy.VendorBuyListManager.IsBusy)
                {
                    _stateStartTime = DateTime.UtcNow;
                    _state = CollectableState.WaitingForPurchaseList;
                    return;
                }

                HandlePurchaseListCompletion();
                return;
            case VendorBuyListManager.StartResult.VendorDataLoading:
            case VendorBuyListManager.StartResult.LocationDataLoading:
            case VendorBuyListManager.StartResult.AnotherPurchaseRunning:
                return;
            case VendorBuyListManager.StartResult.AutomationUnavailable:
                if (_overcapInterrupted)
                {
                    DisableAutoTurnInAndFail("Scrip cap reached while turning in collectables, but vendor automation is unavailable because neither Allagan Tools nor Allagan Item Search is installed and enabled.");
                    return;
                }
                _activePurchaseListId = null;
                AdvancePurchaseListsOrComplete();
                return;
            case VendorBuyListManager.StartResult.Empty:
            case VendorBuyListManager.StartResult.NoPendingEntries:
                if (_overcapInterrupted)
                {
                    DisableAutoTurnInAndFail($"Scrip cap reached while turning in collectables, but purchase list '{GetPurchaseListName(purchaseListId)}' has no pending items.");
                    return;
                }
                _activePurchaseListId = null;
                AdvancePurchaseListsOrComplete();
                return;
            case VendorBuyListManager.StartResult.NoList:
                if (_overcapInterrupted)
                    DisableAutoTurnInAndFail("The configured collectables purchase list is unavailable.");
                else
                {
                    _activePurchaseListId = null;
                    AdvancePurchaseListsOrComplete();
                }
                return;
        }
    }

    private void WaitForPurchaseList()
    {
        if (!string.IsNullOrWhiteSpace(GatherBuddy.VendorBuyListManager.StatusText))
            StatusText = GatherBuddy.VendorBuyListManager.StatusText;

        if (GatherBuddy.VendorBuyListManager.IsBusy)
            return;

        HandlePurchaseListCompletion();
    }

    private void HandlePurchaseListCompletion()
    {
        _activePurchaseListId = null;
        if (_overcapInterrupted)
        {
            _lastOvercapPurchaseHitScripReserveLimit = GatherBuddy.VendorBuyListManager.LastRunHitScripReserveLimit;
            _overcapInterrupted = false;
            _currentItemId = 0;
            _currentJobId = -1;
            _pendingPurchaseListIds.Clear();
            StatusText = "Resuming collectables turn-ins after the purchase list.";
            _state = CollectableState.CheckingInventory;
            return;
        }
        AdvancePurchaseListsOrComplete();
    }

    private void ReturnHome()
    {
        if (!_homeReturnStarted)
        {
            if (Lifestream.Enabled && Lifestream.IsBusy())
                return;

            if (!HomeNavigationHelper.TryStartReturnHome(out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    GatherBuddy.Log.Warning($"[CollectableManager] {error}");

                _completionMessage = string.IsNullOrWhiteSpace(error)
                    ? "Collectables turn-in complete."
                    : $"Collectables turn-in complete without home return: {error}";
                _state = CollectableState.Completed;
                return;
            }

            _homeReturnStarted = true;
            _lastAction = DateTime.UtcNow;
            StatusText = "Returning home after collectables turn-ins.";
            return;
        }

        if (!HomeNavigationHelper.IsReturnComplete())
            return;

        _completionMessage = "Collectables turn-in complete.";
        _state = CollectableState.Completed;
    }

    private void BeginCompletionFlow()
    {
        if (_returnHomeAfterCompletion && HomeNavigationHelper.ShouldReturnHomeAfterCollectables())
        {
            _homeReturnStarted = false;
            TransitionAfterClosingTurnInWindow(CollectableState.ReturningHome, "Returning home after collectables turn-ins.");
            return;
        }

        _completionMessage = "Collectables turn-in complete.";
        TransitionAfterClosingTurnInWindow(CollectableState.Completed, _completionMessage);
    }

    private void Complete()
    {
        _completionMessage ??= "Collectables turn-in complete.";
        GatherBuddy.Log.Information($"[CollectableManager] {_completionMessage}");
        CleanupCurrentRun(stopActivePurchaseList: false);
        StatusText = _completionMessage;
        OnFinishCollecting?.Invoke();
    }

    private void HandleError()
    {
        var errorMessage = _lastErrorText ?? "An error occurred during collectables automation.";
        GatherBuddy.Log.Error($"[CollectableManager] {errorMessage}");
        CleanupCurrentRun(stopActivePurchaseList: true);
        StatusText = errorMessage;
        OnError?.Invoke(errorMessage);
    }

    private void Fail(string message)
    {
        _lastErrorText = message;
        StatusText = message;
        _state = CollectableState.Error;
    }

    private void DisableAutoTurnInAndFail(string message)
    {
        var hardFailMessage = $"{message} Auto turn-in collectables has been disabled to prevent repeated failures. Adjust the collectables reserve or purchase list, then re-enable auto turn-ins.";
        var shouldSave = false;
        if (_config.CollectableConfig.AutoTurnInCollectables)
        {
            _config.CollectableConfig.AutoTurnInCollectables = false;
            shouldSave = true;
        }

        if (!string.Equals(_config.CollectableConfig.AutoTurnInHardFailReason, hardFailMessage, StringComparison.Ordinal))
        {
            _config.CollectableConfig.AutoTurnInHardFailReason = hardFailMessage;
            shouldSave = true;
        }

        if (shouldSave)
            _config.Save();

        Communicator.PrintError($"[GatherBuddyReborn] {hardFailMessage}");
        Fail(hardFailMessage);
    }

    private void TransitionAfterClosingTurnInWindow(CollectableState nextState, string nextStatus)
    {
        if (!_windowHandler.IsReady)
        {
            _stateStartTime = DateTime.UtcNow;
            _lastAction = DateTime.MinValue;
            StatusText = nextStatus;
            _state = nextState;
            return;
        }

        _nextStateAfterWindowClose = nextState;
        _statusAfterWindowClose = nextStatus;
        _stateStartTime = DateTime.UtcNow;
        _lastAction = DateTime.MinValue;
        StatusText = "Closing the collectables turn-in window.";
        _state = CollectableState.ClosingTurnInWindow;
    }

    private void CleanupCurrentRun(bool stopActivePurchaseList)
    {
        _framework.Update -= OnUpdate;
        if (_turnInVendor != null)
            VendorInteractionHelper.ResetShopSelectionState(_turnInVendor);

        if (_windowHandler.IsReady)
            _windowHandler.CloseWindow();

        if (stopActivePurchaseList
         && _activePurchaseListId.HasValue
         && GatherBuddy.VendorBuyListManager.IsBusy
         && GatherBuddy.VendorBuyListManager.RunningListId == _activePurchaseListId.Value)
        {
            GatherBuddy.VendorBuyListManager.Stop();
        }
        else if (_activePurchaseListId == null && (GatherBuddy.VendorNavigator.IsActive || GatherBuddy.VendorNavigator.IsReadyToPurchase))
        {
            GatherBuddy.VendorNavigator.Stop();
        }

        IsRunning = false;
        _state = CollectableState.Idle;
        _turnInQueue.Clear();
        _pendingPurchaseListIds.Clear();
        _turnInVendor = null;
        _turnInLocation = null;
        _activePurchaseListId = null;
        _currentItemId = 0;
        _currentJobId = -1;
        _lastAction = DateTime.MinValue;
        _stateStartTime = DateTime.MinValue;
        _overcapInterrupted = false;
        _purchaseAttemptedForOvercap = false;
        _lastOvercapPurchaseHitScripReserveLimit = false;
        _returnHomeAfterCompletion = false;
        _homeReturnStarted = false;
        _runContainsGatheringCollectables = false;
        _runContainsCraftingCollectables = false;
        _completionMessage = null;
        _lastErrorText = null;
        _nextStateAfterWindowClose = CollectableState.Idle;
        _statusAfterWindowClose = null;
    }

    private bool HasConfiguredPurchaseList()
        => GetConfiguredPurchaseListIds(_overcapInterrupted).Count > 0;

    private bool ShouldRunPurchaseList()
        => _config.CollectableConfig.BuyAfterEachCollect && HasConfiguredPurchaseList();

    private void UpdateRunCollectableTypes(IReadOnlyCollection<CollectableTurnInItem> items)
    {
        _runContainsGatheringCollectables = false;
        _runContainsCraftingCollectables = false;

        foreach (var item in items)
        {
            if (IsGatheringCollectable(item))
                _runContainsGatheringCollectables = true;
            else
                _runContainsCraftingCollectables = true;
        }
    }

    private void QueuePendingPurchaseLists()
    {
        _pendingPurchaseListIds.Clear();
        foreach (var purchaseListId in GetConfiguredPurchaseListIds(_overcapInterrupted))
            _pendingPurchaseListIds.Enqueue(purchaseListId);
    }

    private List<Guid> GetConfiguredPurchaseListIds(bool prioritizeCurrentTurnInType)
    {
        var purchaseListIds = new List<Guid>();

        switch (CurrentRunSource)
        {
            case CollectableRunSource.AutoGather:
                AddPurchaseListIdIfConfigured(purchaseListIds, _config.CollectableConfig.GatheringPurchaseListId);
                break;
            case CollectableRunSource.VulcanQueue:
                AddPurchaseListIdIfConfigured(purchaseListIds, _config.CollectableConfig.CraftingPurchaseListId);
                break;
            default:
                if (prioritizeCurrentTurnInType && TryGetCurrentTurnInPurchaseListId(out var currentPurchaseListId))
                {
                    AddPurchaseListIdIfConfigured(purchaseListIds, currentPurchaseListId);
                    break;
                }

                if (_runContainsGatheringCollectables)
                    AddPurchaseListIdIfConfigured(purchaseListIds, _config.CollectableConfig.GatheringPurchaseListId);

                if (_runContainsCraftingCollectables)
                    AddPurchaseListIdIfConfigured(purchaseListIds, _config.CollectableConfig.CraftingPurchaseListId);
                break;
        }

        return purchaseListIds;
    }

    private void AdvancePurchaseListsOrComplete()
    {
        if (_pendingPurchaseListIds.Count > 0)
        {
            var nextPurchaseListId = _pendingPurchaseListIds.Peek();
            StatusText = $"Running collectables purchase list '{GetPurchaseListName(nextPurchaseListId)}'.";
            _stateStartTime = DateTime.UtcNow;
            _state = CollectableState.StartingPurchaseList;
            return;
        }

        BeginCompletionFlow();
    }

    private bool TryGetCurrentTurnInPurchaseListId(out Guid purchaseListId)
    {
        purchaseListId = Guid.Empty;
        if (_turnInQueue.Count == 0)
            return false;

        var currentTurnInItem = _turnInQueue.Peek();
        purchaseListId = IsGatheringCollectable(currentTurnInItem)
            ? _config.CollectableConfig.GatheringPurchaseListId
            : _config.CollectableConfig.CraftingPurchaseListId;
        return purchaseListId != Guid.Empty;
    }

    private static void AddPurchaseListIdIfConfigured(ICollection<Guid> purchaseListIds, Guid purchaseListId)
    {
        if (purchaseListId != Guid.Empty && !purchaseListIds.Contains(purchaseListId))
            purchaseListIds.Add(purchaseListId);
    }

    private static bool IsGatheringCollectable(CollectableTurnInItem item)
        => item.JobId >= 8;

    private string GetPurchaseListName(Guid purchaseListId)
        => GatherBuddy.VendorBuyListManager.Lists.FirstOrDefault(list => list.Id == purchaseListId)?.Name ?? purchaseListId.ToString();

    private static string DescribeSource(CollectableRunSource source)
        => source switch
        {
            CollectableRunSource.AutoGather => "Auto-Gather",
            CollectableRunSource.VulcanQueue => "Vulcan queue",
            _ => "manual mode",
        };

    public void Dispose()
        => Stop();
}