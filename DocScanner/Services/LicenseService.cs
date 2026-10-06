using DocScanner.Core.Licensing;
using Plugin.InAppBilling;

namespace DocScanner.Services;

/// <summary>
/// <see cref="ILicenseService"/> backed by Google Play Billing (managed, non-consumable product), via
/// the <c>Plugin.InAppBilling</c> library (MIT). This is the ONLY place in the app that talks to Play
/// Billing; everything else asks <see cref="ILicenseService"/>, which is why the trial rule itself
/// (<see cref="TrialPolicy"/>) has no dependency on this class and is unit-tested without it.
///
/// The exports-used counter and the last-known Pro flag are cached locally (<see cref="Preferences"/>)
/// so the app has an instant, offline answer to "can I export" on every screen; <see cref="RefreshAsync"/>
/// then corrects that cache from Play's own purchase record, which is the actual source of truth. See
/// MOBILE-STATUS.md, section "License &amp; khuyến mãi", for why a custom license server was not built.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>Product id of the one-time "remove the trial limit" unlock. MUST match the managed
    /// product created in Play Console &gt; Monetize &gt; Products &gt; In-app products exactly, or Play
    /// will report it as unknown and <see cref="PurchaseProAsync"/> will fail.</summary>
    public const string ProProductId = "doc_scanner_pro_upgrade_guidid_20260930_1131_101_01051989";

    private const string ExportsUsedKey = "license_exports_used";
    private const string CachedIsProKey = "license_is_pro_cached";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseService()
    {
        State = TrialPolicy.Evaluate(Preferences.Default.Get(CachedIsProKey, false), Preferences.Default.Get(ExportsUsedKey, 0));
    }

    public LicenseState State { get; private set; }
    public string? ProPriceText { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await LockedRefreshAsync();
        Changed?.Invoke();
    }

    public async Task<PurchaseOutcome> PurchaseProAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play. Kiểm tra mạng rồi thử lại.";
                Changed?.Invoke();
                return PurchaseOutcome.Error;
            }

            InAppBillingPurchase? purchase;
            try
            {
                purchase = await billing.PurchaseAsync(ProProductId, ItemType.InAppPurchase);
            }
            finally
            {
                await billing.DisconnectAsync();
            }

            if (purchase == null) return PurchaseOutcome.Cancelled; // the user closed Play's sheet
            await LockedRefreshAsync(); // picks up the new purchase, acknowledges it, updates State
            Changed?.Invoke();
            return PurchaseOutcome.Purchased;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.UserCancelled)
        {
            return PurchaseOutcome.Cancelled;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.AlreadyOwned)
        {
            await LockedRefreshAsync();
            Changed?.Invoke();
            return PurchaseOutcome.AlreadyOwned;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Changed?.Invoke();
            return PurchaseOutcome.Error;
        }
    }

    public async Task<PurchaseOutcome> RestoreAsync()
    {
        bool wasPro = State.IsPro;
        await RefreshAsync();
        if (LastError != null) return PurchaseOutcome.Error;
        return State.IsPro ? (wasPro ? PurchaseOutcome.AlreadyOwned : PurchaseOutcome.Purchased) : PurchaseOutcome.Cancelled;
    }

    public void RecordExport()
    {
        if (State.IsPro) return; // Pro never spends free uses; also keeps the counter meaningful if downgraded somehow
        int used = Preferences.Default.Get(ExportsUsedKey, 0) + 1;
        Preferences.Default.Set(ExportsUsedKey, used);
        State = State with { ExportsUsed = used };
        Changed?.Invoke();
    }

    /// <summary>Runs <see cref="RefreshLockedAsync"/> under <see cref="_gate"/>, so a concurrent call from
    /// another screen (a resume-triggered <see cref="RefreshAsync"/> landing at the same moment as a
    /// purchase, say) waits its turn instead of hitting Play twice at once.</summary>
    private async Task LockedRefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshLockedAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play.";
                return;
            }
            try
            {
                IEnumerable<InAppBillingPurchase>? purchases = await billing.GetPurchasesAsync(ItemType.InAppPurchase);
                InAppBillingPurchase? owned = purchases?.FirstOrDefault(p => p.ProductId == ProProductId && p.State == PurchaseState.Purchased);
                if (owned is { IsAcknowledged: false })
                {
                    // Required within 3 days of purchase or Play auto-refunds it; harmless to repeat.
                    try { await billing.FinalizePurchaseOfProductAsync([ProProductId]); }
                    catch (Exception) { /* the purchase still counts as owned even if acknowledging failed here; retried next refresh */ }
                }
                ApplyPro(owned != null);

                IEnumerable<InAppBillingProduct>? products = await billing.GetProductInfoAsync(ItemType.InAppPurchase, [ProProductId]);
                ProPriceText = products?.FirstOrDefault(p => p.ProductId == ProProductId)?.LocalizedPrice;
                LastError = null;
            }
            finally
            {
                await billing.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void ApplyPro(bool isPro)
    {
        Preferences.Default.Set(CachedIsProKey, isPro);
        State = State with { IsPro = isPro };
    }
}
