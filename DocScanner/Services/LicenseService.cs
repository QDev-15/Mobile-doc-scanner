using DocScanner.Core.Licensing;
using Plugin.InAppBilling;

namespace DocScanner.Services;

/// <summary>
/// <see cref="ILicenseService"/> backed by Google Play Billing (managed, non-consumable product), via
/// the <c>Plugin.InAppBilling</c> library (MIT). This is the ONLY place in the app that talks to Play
/// Billing; everything else asks <see cref="ILicenseService"/>, which is why the trial rule itself
/// (<see cref="TrialPolicy"/>) has no dependency on this class and is unit-tested without it.
///
/// The exports-used counter is cached locally (<see cref="Preferences"/>) for an instant, offline answer
/// to "can I export" on every screen. The last-known Pro flag is cached in <see cref="SecureStorage"/>
/// instead (Keystore-backed, unlike plain Preferences, which any SharedPreferences-editor app can rewrite
/// on a rooted device): it is read asynchronously right at startup, and <see cref="RefreshAsync"/> then
/// corrects it from Play's own purchase record, which is the actual source of truth. See
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

    // True once a real Play Billing answer (ApplyProAsync, from RefreshAsync/PurchaseProAsync) has landed: that is
    // always the authoritative source, so the slower, merely-cached SecureStorage read (LoadCachedProAsync, fired
    // once from the constructor) must not clobber it if it resolves afterwards -- App.OnStart() kicks off its own
    // RefreshAsync right after construction, with no ordering between the two otherwise.
    private bool _appliedFromBilling;

    public LicenseService()
    {
        // Pro starts false until LoadCachedProAsync resolves (SecureStorage has no synchronous read); it is a local
        // Keystore read with no network, so that normally lands within a frame, well before anyone can act on it.
        State = TrialPolicy.Evaluate(false, Preferences.Default.Get(ExportsUsedKey, 0));
        Preferences.Default.Remove(CachedIsProKey); // migrated off the old plaintext copy (see class remarks)
        _ = LoadCachedProAsync();
    }

    private async Task LoadCachedProAsync()
    {
        bool isPro = false;
        try
        {
            string? raw = await SecureStorage.Default.GetAsync(CachedIsProKey);
            bool.TryParse(raw, out isPro);
        }
        catch (Exception)
        {
            // Some OEM Keystores throw on first access; treat that as "not Pro" rather than crash startup.
        }
        if (_appliedFromBilling) return; // a real RefreshAsync/PurchaseProAsync already answered first; don't overwrite it with the stale cache
        State = State with { IsPro = isPro };
        try { Changed?.Invoke(); }
        catch (Exception)
        {
            // This runs fire-and-forget from the constructor (nobody awaits LoadCachedProAsync): an exception here
            // would otherwise fault an unobserved Task this early in app startup instead of surfacing anywhere.
        }
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
                await ApplyProAsync(owned != null);

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

    private async Task ApplyProAsync(bool isPro)
    {
        try { await SecureStorage.Default.SetAsync(CachedIsProKey, isPro.ToString()); }
        catch (Exception)
        {
            // Same OEM-Keystore caveat as LoadCachedProAsync: the in-memory State below is still correct for
            // this session either way, only the offline-startup cache write is best-effort.
        }
        _appliedFromBilling = true;
        State = State with { IsPro = isPro };
    }
}
