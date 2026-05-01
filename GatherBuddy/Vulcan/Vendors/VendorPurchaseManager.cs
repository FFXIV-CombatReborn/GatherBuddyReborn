using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorPurchaseManager : IDisposable
{
    private enum State
    {
        Idle,
        WaitingForNavigation,
        OpeningShop,
        PurchasingItem,
        ConfirmingPurchase,
        WaitingForPurchaseComplete,
    }

    public enum CompletionState
    {
        Completed,
        PartiallyCompleted,
        Failed,
        Cancelled,
    }

    private sealed record VendorPurchaseRequest(
        uint              ItemId,
        string            ItemName,
        uint              Cost,
        uint              CurrencyItemId,
        string            CurrencyName,
        VendorCurrencyGroup CurrencyGroup,
        uint              Quantity,
        VendorNpc         Vendor,
        VendorNpcLocation Location,
        VendorShopType    ShopType
    );

    public sealed record PurchaseResult(
        CompletionState State,
        uint            ItemId,
        string          ItemName,
        uint            RequestedQuantity,
        uint            CompletedQuantity,
        VendorNpc       Vendor,
        string          Message
    );

    private static readonly TimeSpan ActionThrottle       = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InteractionCooldown  = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShopOpenTimeout      = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConfirmTimeout       = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PurchaseTimeout      = TimeSpan.FromSeconds(10);
    private const           int      MaxInteractionTries  = 5;
    private const           uint     MaxPurchaseBatchSize = 99;

    private State                  _state = State.Idle;
    private VendorPurchaseRequest? _request;
    private DateTime               _stateStartTime = DateTime.MinValue;
    private DateTime               _lastActionTime = DateTime.MinValue;
    private int                    _interactionAttempts;
    private int                    _ownedCountBeforePurchase;
    private uint                   _completedQuantity;
    private uint                   _currentBatchQuantity;
    private string                 _statusText = string.Empty;
    private bool                   _inclusionPageSelected;
    private bool                   _inclusionSubPageSelected;
    private bool                   _gcRankSelected;
    private bool                   _gcCategorySelected;
    private bool                   _grandCompanyQuantityPrepared;

    public event Action<PurchaseResult>? PurchaseFinished;

    public bool IsRunning
        => _state != State.Idle && _request != null;

    public string StatusText
        => _statusText;

    public bool IsRunningFor(VendorShopEntry entry, VendorNpc npc)
        => _request != null
        && _request.ItemId == entry.ItemId
        && _request.ShopType == entry.ShopType
        && VendorPreferenceHelper.MatchesVendor(_request.Vendor, npc);

    private static bool IsDirectSpecialShopPurchaseSupported(VendorShopEntry entry, VendorNpc vendor)
        => entry.Group is VendorCurrencyGroup.Tomestones or VendorCurrencyGroup.BicolorGemstones
        && vendor.MenuShopType == VendorMenuShopType.SpecialShop
        && vendor.ShopItemIndex >= 0
        && vendor.SourceShopId != 0;

    private static bool IsGrandCompanyPurchaseSupported(VendorNpc vendor)
        => vendor.MenuShopType == VendorMenuShopType.GrandCompanyShop
        && vendor.GcRankIndex >= 0
        && vendor.GcCategoryIndex >= 0;

    public static bool IsPurchaseSupported(VendorShopEntry entry, VendorNpc vendor)
        => entry.ShopType switch
        {
            VendorShopType.GilShop => vendor.MenuShopType == VendorMenuShopType.GilShop,
            VendorShopType.SpecialCurrency =>
                (vendor.MenuShopType == VendorMenuShopType.InclusionShop
                && vendor.InclusionPageIndex >= 0
                && vendor.ShopItemIndex >= 0
                && vendor.SourceShopId != 0
                || IsDirectSpecialShopPurchaseSupported(entry, vendor)),
            VendorShopType.GrandCompanySeals => IsGrandCompanyPurchaseSupported(vendor),
            _ => false,
        };

    public void StartPurchase(VendorShopEntry entry, VendorNpc vendor, VendorNpcLocation location, uint quantity, bool continueCurrentVendorInteraction = false)
    {
        if (VendorDevExclusions.IsExcluded(vendor))
        {
            GatherBuddy.Log.Warning($"[VendorPurchaseManager] Ignoring purchase request for dev-excluded vendor {vendor.Name} ({vendor.NpcId}/{vendor.MenuShopType}/{vendor.ShopId}/{vendor.SourceShopId}:{vendor.ShopItemIndex}, gc={vendor.GcRankIndex}/{vendor.GcCategoryIndex})");
            return;
        }
        if (!IsPurchaseSupported(entry, vendor))
        {
            GatherBuddy.Log.Warning($"[VendorPurchaseManager] Unsupported purchase request for {entry.ItemName}: shopType={entry.ShopType}, menuShopType={vendor.MenuShopType}, shopId={vendor.ShopId}, sourceShopId={vendor.SourceShopId}, shopItemIndex={vendor.ShopItemIndex}, gcRankIndex={vendor.GcRankIndex}, gcCategoryIndex={vendor.GcCategoryIndex}");
            return;
        }

        Stop();

        var requestedQuantity = quantity == 0 ? 1u : quantity;
        VendorInteractionHelper.ResetShopSelectionState(vendor);
        _request = new VendorPurchaseRequest(entry.ItemId, entry.ItemName, entry.Cost, entry.CurrencyItemId, entry.CurrencyName, entry.Group, requestedQuantity, vendor,
            location, entry.ShopType);
        _interactionAttempts = 0;
        _ownedCountBeforePurchase = CountItemOnCharacter(entry.ItemId);
        _completedQuantity = 0;
        _currentBatchQuantity = 0;
        _inclusionPageSelected = false;
        _inclusionSubPageSelected = false;
        _gcRankSelected = false;
        _gcCategorySelected = false;
        _grandCompanyQuantityPrepared = false;
        _lastActionTime = DateTime.MinValue;
        _stateStartTime = DateTime.UtcNow;
        _statusText = continueCurrentVendorInteraction
            ? $"Continuing purchase of {requestedQuantity:N0}x {entry.ItemName} from {vendor.Name}"
            : $"Navigating to {vendor.Name} for {requestedQuantity:N0}x {entry.ItemName}";
        _state = continueCurrentVendorInteraction
            ? State.OpeningShop
            : State.WaitingForNavigation;

        YesAlready.Lock();
        if (continueCurrentVendorInteraction)
            return;
        else
            GatherBuddy.Log.Information($"[VendorPurchaseManager] Starting {entry.ShopType} purchase of {requestedQuantity:N0}x {entry.ItemName} from {vendor.Name} (menu={vendor.MenuShopType}, shop={vendor.ShopId}, source={vendor.SourceShopId}, itemIndex={vendor.ShopItemIndex}, gc={vendor.GcRankIndex}/{vendor.GcCategoryIndex})");
        GatherBuddy.VendorNavigator.StartNavigation(location, continueCurrentVendorInteraction);
    }

    public void Update()
    {
        if (_request == null || _state == State.Idle)
            return;

        try
        {
            switch (_state)
            {
                case State.WaitingForNavigation:       UpdateWaitingForNavigation();       break;
                case State.OpeningShop:                UpdateOpeningShop();                break;
                case State.PurchasingItem:             UpdatePurchasingItem();             break;
                case State.ConfirmingPurchase:         UpdateConfirmingPurchase();         break;
                case State.WaitingForPurchaseComplete: UpdateWaitingForPurchaseComplete(); break;
            }
        }
        catch (Exception ex)
        {
            Fail($"Vendor purchase failed for {_request.ItemName}: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_request == null)
        {
            ResetState();
            return;
        }

        var result = new PurchaseResult(
            CompletionState.Cancelled,
            _request.ItemId,
            _request.ItemName,
            _request.Quantity,
            _completedQuantity,
            _request.Vendor,
            $"Cancelled purchase of {_request.ItemName} from {_request.Vendor.Name}.");
        ResetState();
        PurchaseFinished?.Invoke(result);
    }

    public void Dispose()
        => Stop();

    private uint GetRemainingQuantity()
        => _request == null || _request.Quantity <= _completedQuantity
            ? 0
            : _request.Quantity - _completedQuantity;

    private static bool RequiresSinglePurchaseBatch(uint itemId)
        => VendorShopResolver.HousingItemIds.Contains(itemId);

    private uint GetDesiredBatchQuantity(uint remainingQuantity)
    {
        if (_request == null || remainingQuantity == 0)
            return 0;

        var requiresSinglePurchaseBatch = RequiresSinglePurchaseBatch(_request.ItemId);
        return Math.Min(remainingQuantity, requiresSinglePurchaseBatch ? 1u : MaxPurchaseBatchSize);
    }

    private bool TryPrepareBatchQuantity(uint remainingQuantity)
    {
        if (_request == null)
            return false;

        _currentBatchQuantity = 0;
        var desiredBatchQuantity = GetDesiredBatchQuantity(remainingQuantity);
        if (desiredBatchQuantity == 0)
            return false;

        if (_request.Cost == 0)
        {
            _currentBatchQuantity = desiredBatchQuantity;
            return true;
        }

        var availability = VendorCurrencyAvailabilityResolver.Resolve(_request.CurrencyGroup, _request.CurrencyItemId, _request.CurrencyName);
        var affordableBatchQuantity = Math.Min(desiredBatchQuantity, availability.AvailableAmount / _request.Cost);
        if (affordableBatchQuantity == 0)
        {
            HandleCurrencyExhaustion(availability);
            return false;
        }

        if (affordableBatchQuantity < desiredBatchQuantity)
            GatherBuddy.Log.Warning(
                $"[VendorPurchaseManager] Only {availability.AvailableAmount:N0} {availability.CurrencyName} are available for {_request.ItemName}; capping the current batch from {desiredBatchQuantity:N0} to {affordableBatchQuantity:N0} (cost {_request.Cost:N0} each, source={availability.Source}).");

        _currentBatchQuantity = affordableBatchQuantity;
        return true;
    }

    private void HandleCurrencyExhaustion(VendorCurrencyAvailability availability)
    {
        if (_request == null)
            return;

        var remainingQuantity = GetRemainingQuantity();
        var message = _completedQuantity > 0
            ? $"Purchased {_completedQuantity:N0}/{_request.Quantity:N0}x {_request.ItemName} from {_request.Vendor.Name}. Could not afford the remaining {remainingQuantity:N0}x with {availability.AvailableAmount:N0} {availability.CurrencyName} remaining (cost {_request.Cost:N0} each)."
            : $"Not enough {availability.CurrencyName} to buy {_request.ItemName} from {_request.Vendor.Name}. Need {_request.Cost:N0} for 1x and only {availability.AvailableAmount:N0} are available.";

        if (_completedQuantity > 0)
        {
            CompletePartially(message);
            return;
        }

        Fail(message);
    }

    private void BeginNextBatch()
    {
        if (_request == null)
            return;

        _currentBatchQuantity = 0;
        _interactionAttempts = 0;
        _inclusionPageSelected = false;
        _inclusionSubPageSelected = false;
        _gcRankSelected = false;
        _gcCategorySelected = false;
        _grandCompanyQuantityPrepared = false;
        _state = State.OpeningShop;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.MinValue;
        _statusText = $"Preparing next batch of {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
    }

    private bool TryAdvancePurchaseProgress()
    {
        if (_request == null || _currentBatchQuantity == 0)
            return false;

        var currentCount = CountItemOnCharacter(_request.ItemId);
        var batchIncrease = currentCount - _ownedCountBeforePurchase;
        if (batchIncrease <= 0)
            return false;

        var purchasedThisBatch = (uint)batchIncrease;
        purchasedThisBatch = Math.Min(purchasedThisBatch, Math.Min(_currentBatchQuantity, GetRemainingQuantity()));
        if (purchasedThisBatch == 0)
            return false;

        _completedQuantity += purchasedThisBatch;

        if (_completedQuantity >= _request.Quantity)
        {
            Complete($"Purchased {_completedQuantity:N0}x {_request.ItemName} from {_request.Vendor.Name}.");
            return true;
        }

        BeginNextBatch();
        return true;
    }

    private void UpdateWaitingForNavigation()
    {
        if (_request == null)
            return;

        var navigator = GatherBuddy.VendorNavigator;
        if (navigator.IsFailed)
        {
            Fail($"Failed to navigate to {_request.Vendor.Name} for {_request.ItemName}.");
            return;
        }

        if (!navigator.IsReadyToPurchase)
            return;

        _state = State.OpeningShop;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.MinValue;
        _interactionAttempts = 0;
        _statusText = $"Opening {_request.Vendor.Name}'s shop";
    }

    private unsafe void UpdateOpeningShop()
    {
        if (_request == null || !GenericHelpers.IsScreenReady())
            return;

        if (_request.ShopType == VendorShopType.GilShop
         && GenericHelpers.TryGetAddonByName("Shop", out AtkUnitBase* gilShop)
         && gilShop->IsVisible)
        {
            _state = State.PurchasingItem;
            _stateStartTime = DateTime.UtcNow;
            _lastActionTime = DateTime.MinValue;
            _statusText = $"Selecting {_request.ItemName} in the gil shop ({_completedQuantity:N0}/{_request.Quantity:N0})";
            return;
        }

        if (_request.ShopType == VendorShopType.SpecialCurrency
         && GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* inclusionShop)
         && inclusionShop->IsVisible)
        {
            _state = State.PurchasingItem;
            _stateStartTime = DateTime.UtcNow;
            _lastActionTime = DateTime.MinValue;
            _statusText = $"Selecting {_request.ItemName} in the inclusion shop ({_completedQuantity:N0}/{_request.Quantity:N0})";
            return;
        }

        if (_request.ShopType == VendorShopType.GrandCompanySeals
         && GenericHelpers.TryGetAddonByName("GrandCompanyExchange", out AtkUnitBase* grandCompanyExchange)
         && grandCompanyExchange->IsVisible)
        {
            _state = State.PurchasingItem;
            _stateStartTime = DateTime.UtcNow;
            _lastActionTime = DateTime.MinValue;
            _statusText = $"Selecting {_request.ItemName} in the grand-company shop ({_completedQuantity:N0}/{_request.Quantity:N0})";
            return;
        }

        if (_request.ShopType == VendorShopType.SpecialCurrency
         && _request.Vendor.MenuShopType == VendorMenuShopType.SpecialShop)
        {
            if (GenericHelpers.TryGetAddonByName("ShopExchangeItem", out AtkUnitBase* itemExchange) && itemExchange->IsVisible)
            {
                _state = State.PurchasingItem;
                _stateStartTime = DateTime.UtcNow;
                _lastActionTime = DateTime.MinValue;
                _statusText = $"Selecting {_request.ItemName} in the direct special shop ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }

            if (GenericHelpers.TryGetAddonByName("ShopExchangeCurrency", out AtkUnitBase* currencyExchange) && currencyExchange->IsVisible)
            {
                _state = State.PurchasingItem;
                _stateStartTime = DateTime.UtcNow;
                _lastActionTime = DateTime.MinValue;
                _statusText = $"Selecting {_request.ItemName} in the direct special shop ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }
        }

        if ((DateTime.UtcNow - _lastActionTime) >= ActionThrottle)
        {
            if (VendorInteractionHelper.TryClickTalk())
            {
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Advancing dialogue with {_request.Vendor.Name}";
                return;
            }

            if (VendorInteractionHelper.TrySelectShopOption(_request.Vendor, out var selectionError))
            {
                _lastActionTime = DateTime.UtcNow;
                _stateStartTime = DateTime.UtcNow;
                _statusText = $"Selecting {_request.ItemName}'s vendor route";
                return;
            }

            if (selectionError != null)
            {
                Fail(selectionError);
                return;
            }
        }

        if (_interactionAttempts == 0 || (DateTime.UtcNow - _lastActionTime) >= InteractionCooldown)
        {
            if (AttemptVendorInteraction("Opening shop"))
                return;
        }

        if ((DateTime.UtcNow - _stateStartTime) > ShopOpenTimeout)
        {
            if (_interactionAttempts >= MaxInteractionTries)
            {
                Fail($"Timed out opening {_request.Vendor.Name}'s {DescribeShopLabel(_request)} for {_request.ItemName}.");
                return;
            }

            AttemptVendorInteraction("Retrying shop interaction");
            _stateStartTime = DateTime.UtcNow;
        }
    }

    private unsafe void UpdatePurchasingItem()
    {
        if (_request == null)
            return;

        var remainingQuantity = GetRemainingQuantity();
        if (remainingQuantity == 0)
        {
            Complete($"Purchased {_request.Quantity:N0}x {_request.ItemName} from {_request.Vendor.Name}.");
            return;
        }

        if (_request.ShopType == VendorShopType.SpecialCurrency)
        {
            UpdatePurchasingSpecialCurrencyItem();
            return;
        }

        if (_request.ShopType == VendorShopType.GrandCompanySeals)
        {
            UpdatePurchasingGrandCompanyItem();
            return;
        }

        if (!GenericHelpers.TryGetAddonByName("Shop", out AtkUnitBase* shop))
        {
            if ((DateTime.UtcNow - _stateStartTime) > ShopOpenTimeout)
                Fail($"The gil shop closed before {_request.ItemName} could be selected.");
            return;
        }

        if ((DateTime.UtcNow - _lastActionTime) < ActionThrottle)
            return;

        var reader = new VendorGilShopReader(shop);
        var targetItem = reader.Items.FirstOrDefault(item => item.ItemId == _request.ItemId);
        if (targetItem == null)
        {
            Fail($"Could not find {_request.ItemName} in {_request.Vendor.Name}'s gil shop.");
            return;
        }
        if (!TryPrepareBatchQuantity(remainingQuantity))
            return;
        _ownedCountBeforePurchase = CountItemOnCharacter(_request.ItemId);
        Callback.Fire(shop, true, 0, targetItem.Index, _currentBatchQuantity, 0);

        _state = State.ConfirmingPurchase;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.UtcNow;
        _statusText = $"Confirming purchase of {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
    }

    private unsafe void UpdatePurchasingDirectSpecialShopItem()
    {
        if (_request == null)
            return;

        if (_request.Vendor.ShopItemIndex < 0)
        {
            Fail($"No special-shop item index is available for {_request.ItemName}.");
            return;
        }

        var activeAddonName = string.Empty;
        if (GenericHelpers.TryGetAddonByName("ShopExchangeItem", out AtkUnitBase* itemShop) && itemShop->IsVisible)
            activeAddonName = "ShopExchangeItem";
        else if (GenericHelpers.TryGetAddonByName("ShopExchangeCurrency", out AtkUnitBase* currencyShop) && currencyShop->IsVisible)
            activeAddonName = "ShopExchangeCurrency";

        if (activeAddonName.Length == 0)
        {
            if ((DateTime.UtcNow - _stateStartTime) > ShopOpenTimeout)
                Fail($"The direct special shop closed before {_request.ItemName} could be selected.");
            return;
        }

        if ((DateTime.UtcNow - _lastActionTime) < ActionThrottle)
            return;

        var remainingQuantity = GetRemainingQuantity();
        if (!TryPrepareBatchQuantity(remainingQuantity))
            return;
        _ownedCountBeforePurchase = CountItemOnCharacter(_request.ItemId);
        if (!VendorInteractionHelper.TrySelectSpecialShopItem(_request.Vendor.ShopItemIndex, _request.ItemId, _currentBatchQuantity, out var itemError))
        {
            if (itemError != null)
                Fail(itemError);
            return;
        }

        _state = State.ConfirmingPurchase;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.UtcNow;
        _statusText = $"Confirming purchase of {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
    }

    private unsafe void UpdatePurchasingSpecialCurrencyItem()
    {
        if (_request == null)
            return;
        if (_request.Vendor.MenuShopType == VendorMenuShopType.SpecialShop)
        {
            UpdatePurchasingDirectSpecialShopItem();
            return;
        }

        if (_request.Vendor.ShopItemIndex < 0)
        {
            Fail($"No inclusion-shop item index is available for {_request.ItemName}.");
            return;
        }

        if (!GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* shop) || !shop->IsVisible)
        {
            if ((DateTime.UtcNow - _stateStartTime) > ShopOpenTimeout)
                Fail($"The inclusion shop closed before {_request.ItemName} could be selected.");
            return;
        }

        if ((DateTime.UtcNow - _lastActionTime) < ActionThrottle)
            return;

        if (!_inclusionPageSelected && _request.Vendor.InclusionPageIndex >= 0)
        {
            if (VendorInteractionHelper.TrySelectInclusionPage(_request.Vendor.InclusionPageIndex, out var pageError))
            {
                _inclusionPageSelected = true;
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Selecting {_request.ItemName}'s inclusion page ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }

            if (pageError != null)
            {
                Fail(pageError);
                return;
            }
        }

        if (!_inclusionSubPageSelected && _request.Vendor.InclusionSubPageIndex > 0)
        {
            if (VendorInteractionHelper.TrySelectInclusionSubPage(_request.Vendor.InclusionSubPageIndex, out var subPageError))
            {
                _inclusionSubPageSelected = true;
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Selecting {_request.ItemName}'s inclusion subpage ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }

            if (subPageError != null)
            {
                Fail(subPageError);
                return;
            }
        }

        var remainingQuantity = GetRemainingQuantity();
        if (!TryPrepareBatchQuantity(remainingQuantity))
            return;
        _ownedCountBeforePurchase = CountItemOnCharacter(_request.ItemId);
        if (!VendorInteractionHelper.TrySelectInclusionShopItem(_request.Vendor.ShopItemIndex, _request.ItemId, _currentBatchQuantity, out var itemError))
        {
            if (itemError != null)
                Fail(itemError);
            return;
        }

        _state = State.ConfirmingPurchase;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.UtcNow;
        _statusText = $"Confirming purchase of {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
    }

    private unsafe void UpdatePurchasingGrandCompanyItem()
    {
        if (_request == null)
            return;

        if (!GenericHelpers.TryGetAddonByName("GrandCompanyExchange", out AtkUnitBase* shop) || !shop->IsVisible)
        {
            if ((DateTime.UtcNow - _stateStartTime) > ShopOpenTimeout)
                Fail($"The grand-company shop closed before {_request.ItemName} could be selected.");
            return;
        }

        if ((DateTime.UtcNow - _lastActionTime) < ActionThrottle)
            return;

        if (!_gcRankSelected)
        {
            if (VendorInteractionHelper.IsGrandCompanyRankTabSelected(_request.Vendor.GcRankIndex, out var rankError))
            {
                _gcRankSelected = true;
            }
            else if (rankError != null)
            {
                Fail(rankError);
                return;
            }
            else if (VendorInteractionHelper.TrySelectGrandCompanyRankTab(_request.Vendor.GcRankIndex, out rankError))
            {
                _gcRankSelected = true;
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Selecting {_request.ItemName}'s GC rank tab ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }
            else if (rankError != null)
            {
                Fail(rankError);
                return;
            }
            else
            {
                return;
            }
        }

        if (!_gcCategorySelected)
        {
            if (VendorInteractionHelper.IsGrandCompanyCategoryTabSelected(_request.Vendor.GcCategoryIndex, out var categoryError))
            {
                _gcCategorySelected = true;
            }
            else if (categoryError != null)
            {
                Fail(categoryError);
                return;
            }
            else if (VendorInteractionHelper.TrySelectGrandCompanyCategoryTab(_request.Vendor.GcCategoryIndex, out categoryError))
            {
                _gcCategorySelected = true;
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Selecting {_request.ItemName}'s GC category tab ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }
            else if (categoryError != null)
            {
                Fail(categoryError);
                return;
            }
            else
            {
                return;
            }
        }

        var currentGrandCompanyId = GetCurrentGrandCompanyId();
        if (currentGrandCompanyId == 0)
        {
            Fail("Could not determine the current Grand Company for the requested shop.");
            return;
        }

        var currentSealCurrencyItemId = GetGrandCompanySealCurrencyItemId(currentGrandCompanyId);
        if (_request.CurrencyItemId != 0 && currentSealCurrencyItemId != 0 && _request.CurrencyItemId != currentSealCurrencyItemId)
        {
            Fail($"{_request.ItemName} is not sold by the current Grand Company.");
            return;
        }
        var remainingQuantity = GetRemainingQuantity();
        if (!TryPrepareBatchQuantity(remainingQuantity))
            return;

        _ownedCountBeforePurchase = CountItemOnCharacter(_request.ItemId);
        if (!VendorInteractionHelper.TrySelectGrandCompanyItem(_request.ItemId, _currentBatchQuantity, GetCurrentGrandCompanyRank(currentGrandCompanyId),
                out var selectedQuantity, out var opensCurrencyExchange, out var itemError))
        {
            if (itemError != null)
                Fail(itemError);
            return;
        }

        _currentBatchQuantity = selectedQuantity;
        _grandCompanyQuantityPrepared = !opensCurrencyExchange;
        _state = State.ConfirmingPurchase;
        _stateStartTime = DateTime.UtcNow;
        _lastActionTime = DateTime.UtcNow;
        _statusText = opensCurrencyExchange
            ? $"Setting quantity for {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})"
            : $"Confirming purchase of {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
    }

    private void UpdateConfirmingPurchase()
    {
        if (_request == null)
            return;

        if (_request.ShopType == VendorShopType.GrandCompanySeals && !_grandCompanyQuantityPrepared)
        {
            if ((DateTime.UtcNow - _lastActionTime) < ActionThrottle)
                return;

            if (VendorInteractionHelper.TrySetGrandCompanyExchangeQuantity(_currentBatchQuantity, out var quantityError))
            {
                _grandCompanyQuantityPrepared = true;
                _lastActionTime = DateTime.UtcNow;
                _statusText = $"Confirming purchase of {_currentBatchQuantity:N0}x {_request.ItemName} ({_completedQuantity:N0}/{_request.Quantity:N0})";
                return;
            }

            if (quantityError != null)
            {
                Fail(quantityError);
                return;
            }
        }

        if ((DateTime.UtcNow - _lastActionTime) >= ActionThrottle && VendorInteractionHelper.TryConfirmPurchase())
        {
            _state = State.WaitingForPurchaseComplete;
            _stateStartTime = DateTime.UtcNow;
            _lastActionTime = DateTime.UtcNow;
            _statusText = $"Waiting for {_currentBatchQuantity:N0}x {_request.ItemName} to reach inventory or armoury ({_completedQuantity:N0}/{_request.Quantity:N0})";
            return;
        }

        if (TryAdvancePurchaseProgress())
            return;

        if ((DateTime.UtcNow - _stateStartTime) > ConfirmTimeout)
            Fail($"Timed out waiting for the {_request.ItemName} purchase confirmation.");
    }

    private void UpdateWaitingForPurchaseComplete()
    {
        if (_request == null)
            return;

        if (TryAdvancePurchaseProgress())
            return;

        var shouldAttemptReconfirm = (DateTime.UtcNow - _lastActionTime) >= ActionThrottle;
        var reConfirmed = shouldAttemptReconfirm
            && (_request.ShopType == VendorShopType.GrandCompanySeals
                ? VendorInteractionHelper.TryConfirmYesNo()
                : VendorInteractionHelper.TryConfirmPurchase());
        if (reConfirmed)
        {
            _lastActionTime = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _stateStartTime) > PurchaseTimeout)
            Fail($"Timed out waiting for {_request.ItemName} to be added to inventory or armoury.");
    }

    private bool AttemptVendorInteraction(string reason)
    {
        if (_request == null)
            return false;

        _interactionAttempts++;
        _lastActionTime = DateTime.UtcNow;

        if (!VendorInteractionHelper.TryInteractWithTarget(_request.Location))
            return false;

        _statusText = $"{reason} with {_request.Vendor.Name}";
        return true;
    }

    private void Complete(string message)
    {
        if (_request == null)
            return;

        GatherBuddy.Log.Information($"[VendorPurchaseManager] {message}");
        Communicator.Print($"[GatherBuddyReborn] {message}");
        var result = new PurchaseResult(
            CompletionState.Completed,
            _request.ItemId,
            _request.ItemName,
            _request.Quantity,
            _completedQuantity,
            _request.Vendor,
            message);
        ResetState();
        PurchaseFinished?.Invoke(result);
    }

    private void CompletePartially(string message)
    {
        if (_request == null)
            return;

        GatherBuddy.Log.Error($"[VendorPurchaseManager] {message}");
        Communicator.PrintError($"[GatherBuddyReborn] {message}");
        var result = new PurchaseResult(
            CompletionState.PartiallyCompleted,
            _request.ItemId,
            _request.ItemName,
            _request.Quantity,
            _completedQuantity,
            _request.Vendor,
            message);
        ResetState();
        PurchaseFinished?.Invoke(result);
    }

    private void Fail(string message)
    {
        if (_request == null)
            return;

        GatherBuddy.Log.Error($"[VendorPurchaseManager] {message}");
        Communicator.PrintError($"[GatherBuddyReborn] {message}");
        var result = new PurchaseResult(
            CompletionState.Failed,
            _request.ItemId,
            _request.ItemName,
            _request.Quantity,
            _completedQuantity,
            _request.Vendor,
            message);
        ResetState();
        PurchaseFinished?.Invoke(result);
    }

    private void ResetState()
    {
        if (_request != null)
            VendorInteractionHelper.ResetShopSelectionState(_request.Vendor);
        GatherBuddy.VendorNavigator.Stop();
        YesAlready.Unlock();

        _state = State.Idle;
        _request = null;
        _stateStartTime = DateTime.MinValue;
        _lastActionTime = DateTime.MinValue;
        _interactionAttempts = 0;
        _ownedCountBeforePurchase = 0;
        _completedQuantity = 0;
        _currentBatchQuantity = 0;
        _inclusionPageSelected = false;
        _inclusionSubPageSelected = false;
        _gcRankSelected = false;
        _gcCategorySelected = false;
        _grandCompanyQuantityPrepared = false;
        _statusText = string.Empty;
    }

    private static string DescribeShopLabel(VendorPurchaseRequest request)
        => request.ShopType switch
        {
            VendorShopType.GilShop         => "gil shop",
            VendorShopType.GrandCompanySeals => "grand-company shop",
            VendorShopType.SpecialCurrency => request.Vendor.MenuShopType switch
            {
                VendorMenuShopType.InclusionShop => "inclusion shop",
                VendorMenuShopType.SpecialShop   => "special shop",
                _                                => "special-currency shop",
            },
            _                              => "shop",
        };

    private static unsafe byte GetCurrentGrandCompanyId()
    {
        var playerState = PlayerState.Instance();
        return playerState == null ? (byte)0 : playerState->GrandCompany;
    }

    private static unsafe uint GetCurrentGrandCompanyRank(byte grandCompanyId)
    {
        if (grandCompanyId == 0)
            return 0;

        var playerState = PlayerState.Instance();
        return playerState == null || playerState->GrandCompany != grandCompanyId
            ? 0u
            : playerState->GetGrandCompanyRank();
    }

    private static uint GetGrandCompanySealCurrencyItemId(uint grandCompanyId)
        => grandCompanyId switch
        {
            1 => 20u,
            2 => 21u,
            3 => 22u,
            _ => 0u,
        };

    private static int CountItemOnCharacter(uint itemId)
        => ItemHelper.GetInventoryAndArmoryItemCount(itemId);
}
