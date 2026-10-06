namespace DocScanner.Core.Licensing;

public enum PurchaseOutcome
{
    /// <summary>Play confirmed the purchase; <see cref="ILicenseService.State"/> already reflects it.</summary>
    Purchased,

    /// <summary>The user closed the Play purchase sheet without buying.</summary>
    Cancelled,

    /// <summary>Play reports it is already owned (e.g. bought on another device with the same account);
    /// entitlement was applied same as <see cref="Purchased"/>.</summary>
    AlreadyOwned,

    /// <summary>Billing is unreachable, or Play returned an error. <see cref="ILicenseService.LastError"/>
    /// has the detail for a dialog.</summary>
    Error,
}

/// <summary>
/// The single source of truth for "is this install allowed to export a PDF right now", and the one
/// door through which a purchase happens. Implemented once against Google Play Billing (the only
/// distribution channel, per owner's decision); <see cref="LicenseState"/> and <see cref="TrialPolicy"/>
/// stay engine-agnostic so the trial rule itself is tested without any billing SDK.
///
/// There is deliberately no server and no license key: Play's purchase record for the "pro_upgrade"
/// managed product IS the license. A refund or chargeback makes Play stop returning that purchase, and
/// <see cref="RefreshAsync"/> (called at every app start and whenever the app is resumed) picks that up
/// and revokes Pro automatically -- see MOBILE-STATUS.md section "License &amp; khuyến mãi" for why this
/// was chosen over a custom license-key server.
/// </summary>
public interface ILicenseService
{
    /// <summary>Current trial / Pro status. Starts as a locally-cached guess (instant, no network) and is
    /// corrected by <see cref="RefreshAsync"/> once Play answers; <see cref="Changed"/> fires on every change.</summary>
    LicenseState State { get; }

    /// <summary>Price of the Pro unlock as Play formats it for the user's country (e.g. "79.000 ₫"), once
    /// known; null before the first successful <see cref="RefreshAsync"/> or if Play could not be reached.</summary>
    string? ProPriceText { get; }

    /// <summary>Detail of the last <see cref="PurchaseOutcome.Error"/>, for a dialog. Cleared on success.</summary>
    string? LastError { get; }

    /// <summary>Raised (on a background thread) whenever <see cref="State"/>, <see cref="ProPriceText"/> or
    /// <see cref="LastError"/> changes.</summary>
    event Action? Changed;

    /// <summary>Connects to Play, re-reads owned purchases and the product's price. Safe to call often
    /// (e.g. on every app resume); a call already in flight is awaited rather than repeated.</summary>
    Task RefreshAsync();

    /// <summary>Shows Play's purchase sheet for the Pro unlock. Awaits Play's own confirmation before
    /// returning <see cref="PurchaseOutcome.Purchased"/>, so <see cref="State"/> is already updated by
    /// the time the caller sees success.</summary>
    Task<PurchaseOutcome> PurchaseProAsync();

    /// <summary>Re-checks Play for a purchase made elsewhere (another device, or before a reinstall) and
    /// applies it. Same effect as <see cref="RefreshAsync"/>; kept as a separate, user-facing action
    /// ("Khôi phục giao dịch") because Play's own guidelines call for one.</summary>
    Task<PurchaseOutcome> RestoreAsync();

    /// <summary>Counts one free export against the trial. No-op once <see cref="LicenseState.IsPro"/> is
    /// true. Call only after an export has actually finished -- a cancelled or failed export must not
    /// spend a free use.</summary>
    void RecordExport();
}
