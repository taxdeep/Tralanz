namespace Citus.Business.Blazor.Services;

/// <summary>
/// Placeholder client for the business write flows (manual journal posting,
/// invoice / bill / receive-payment / pay-bill creation, customer + vendor
/// master data). The Application layer already has the matching CQRS
/// handlers (PostManualJournalCommandHandler, PostInvoiceCommandHandler,
/// PostReceivePaymentCommandHandler, etc.) — what's missing is the HTTP
/// surface on Citus.Accounting.Api. Pages that consume this client get a
/// structured "endpoint pending" response so they can render the full form,
/// run client-side validation, and frame the submit button correctly until
/// the backend route lands.
///
/// When real endpoints arrive, replace each stub with an HttpClient call
/// against the corresponding /accounting/...&#47;post route.
/// </summary>
public sealed class BusinessWriteFlowClient
{
    private const string PendingMessage =
        "This write flow's HTTP endpoint is not wired yet on Citus.Accounting.Api. " +
        "The form is fully validated and the payload is ready; once the backend " +
        "route is published, this stub will be replaced with the real call.";

    private readonly CustomerClient _customers;
    private readonly VendorClient _vendors;

    public BusinessWriteFlowClient(CustomerClient customers, VendorClient vendors)
    {
        _customers = customers ?? throw new ArgumentNullException(nameof(customers));
        _vendors = vendors ?? throw new ArgumentNullException(nameof(vendors));
    }

    public Task<WriteFlowResult> PostManualJournalAsync(ManualJournalDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostManualJournalAsync), draft);

    public Task<WriteFlowResult> PostInvoiceAsync(InvoiceDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostInvoiceAsync), draft);

    public Task<WriteFlowResult> PostBillAsync(BillDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostBillAsync), draft);

    public Task<WriteFlowResult> PostReceivePaymentAsync(ReceivePaymentDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostReceivePaymentAsync), draft);

    public Task<WriteFlowResult> PostPayBillAsync(PayBillDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostPayBillAsync), draft);

    public async Task<WriteFlowResult> SaveCustomerAsync(CounterpartyDraft draft, CancellationToken cancellationToken = default)
    {
        var payload = new CustomerUpsertPayload(
            DisplayName: draft.DisplayName,
            DefaultCurrencyCode: draft.PreferredCurrencyCode,
            Email: draft.Email,
            Phone: draft.Phone,
            AddressLine: draft.AddressLine,
            City: draft.City,
            ProvinceState: draft.ProvinceState,
            PostalCode: draft.PostalCode,
            Country: draft.Country,
            TaxId: draft.TaxId,
            Notes: draft.Notes,
            PaymentTermId: draft.PaymentTermId);

        var outcome = draft.CustomerId is { } existingId
            ? await _customers.UpdateAsync(existingId, payload, cancellationToken)
            : await _customers.CreateAsync(payload, cancellationToken);

        return outcome.Succeeded
            ? new WriteFlowResult(
                Succeeded: true,
                Message: $"Customer {outcome.Saved!.EntityNumber} saved.",
                Operation: nameof(SaveCustomerAsync),
                DraftEcho: outcome.Saved)
            : new WriteFlowResult(
                Succeeded: false,
                Message: outcome.ErrorMessage ?? "Could not save the customer.",
                Operation: nameof(SaveCustomerAsync),
                DraftEcho: draft);
    }

    public async Task<WriteFlowResult> SaveVendorAsync(CounterpartyDraft draft, CancellationToken cancellationToken = default)
    {
        var payload = new VendorUpsertPayload(
            DisplayName: draft.DisplayName,
            DefaultCurrencyCode: draft.PreferredCurrencyCode,
            Email: draft.Email,
            Phone: draft.Phone,
            AddressLine: draft.AddressLine,
            City: draft.City,
            ProvinceState: draft.ProvinceState,
            PostalCode: draft.PostalCode,
            Country: draft.Country,
            TaxId: draft.TaxId,
            Notes: draft.Notes,
            PaymentTermId: draft.PaymentTermId);

        var outcome = draft.VendorId is { } existingId
            ? await _vendors.UpdateAsync(existingId, payload, cancellationToken)
            : await _vendors.CreateAsync(payload, cancellationToken);

        return outcome.Succeeded
            ? new WriteFlowResult(
                Succeeded: true,
                Message: $"Vendor {outcome.Saved!.EntityNumber} saved.",
                Operation: nameof(SaveVendorAsync),
                DraftEcho: outcome.Saved)
            : new WriteFlowResult(
                Succeeded: false,
                Message: outcome.ErrorMessage ?? "Could not save the vendor.",
                Operation: nameof(SaveVendorAsync),
                DraftEcho: draft);
    }

    private static Task<WriteFlowResult> Pending(string operation, object payload) =>
        Task.FromResult(new WriteFlowResult(
            Succeeded: false,
            Message: PendingMessage,
            Operation: operation,
            DraftEcho: payload));
}

public sealed record WriteFlowResult(
    bool Succeeded,
    string Message,
    string Operation,
    object DraftEcho);

public sealed record ManualJournalDraft
{
    public DateOnly Date { get; init; }
    /// <summary>
    /// Display number the user wants for this journal. Pre-filled from
    /// <c>GET /accounting/journal-entries/next-number</c> on form load so
    /// the operator sees what the system would assign. Editable —
    /// the backend will honor the override if it doesn't collide with an
    /// existing journal in the active company; if the user clears the field,
    /// the backend falls back to <c>ReserveNextDisplayNumberAsync</c>.
    /// </summary>
    public string DisplayNumber { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public decimal? ExchangeRate { get; init; }
    public IReadOnlyList<ManualJournalLineDraft> Lines { get; init; } = Array.Empty<ManualJournalLineDraft>();
}

public sealed record ManualJournalLineDraft
{
    public string AccountCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
}

public sealed record InvoiceDraft
{
    public DateOnly DocumentDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    /// <summary>
    /// Per-document FX rate (transaction → company base currency). Pre-filled
    /// with the recommended D-1 close rate from <c>fx_rates_daily</c> /
    /// frankfurter, but the user can override (credit-card statement rate,
    /// hand-negotiated conversion, etc.). Null when the transaction
    /// currency equals the base currency.
    /// </summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<DocumentLineDraft> Lines { get; init; } = Array.Empty<DocumentLineDraft>();
}

public sealed record BillDraft
{
    public DateOnly DocumentDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? VendorId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    /// <summary>Per-document FX rate (transaction → base). Same semantics as <see cref="InvoiceDraft.FxRate"/>.</summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<DocumentLineDraft> Lines { get; init; } = Array.Empty<DocumentLineDraft>();
}

public sealed record ReceivePaymentDraft
{
    public DateOnly Date { get; init; }
    public Guid? CustomerId { get; init; }
    public decimal Amount { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    /// <summary>Per-payment FX rate (transaction → base). Used when the customer pays in a non-base currency; bank deposits at a different rate than the originating invoice carry that delta into FX gain/loss.</summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<SettlementApplicationDraft> Applications { get; init; } = Array.Empty<SettlementApplicationDraft>();
}

public sealed record PayBillDraft
{
    public DateOnly Date { get; init; }
    public Guid? VendorId { get; init; }
    public decimal Amount { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    /// <summary>Per-payment FX rate (transaction → base). Mirrors <see cref="ReceivePaymentDraft.FxRate"/>; matters most for credit-card / bank-converted payments where the bank's rate differs from the bill's posted rate.</summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<SettlementApplicationDraft> Applications { get; init; } = Array.Empty<SettlementApplicationDraft>();
}

public sealed record DocumentLineDraft
{
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string TaxCode { get; init; } = string.Empty;
}

public sealed record SettlementApplicationDraft
{
    public Guid OpenItemId { get; init; }
    public decimal AppliedAmount { get; init; }
    public string DocumentDisplayNumber { get; init; } = string.Empty;
}

public sealed record CounterpartyDraft
{
    public string DisplayName { get; init; } = string.Empty;
    public string EntityNumber { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string ProvinceState { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string TaxId { get; init; } = string.Empty;
    public string PreferredCurrencyCode { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    /// <summary>
    /// Selected payment term (vendor side only — customer side ignores it
    /// for now). <c>null</c> means "no preferred term"; the bill flow
    /// falls back to the company default.
    /// </summary>
    public Guid? PaymentTermId { get; init; }
    /// <summary>
    /// When set, <see cref="BusinessWriteFlowClient.SaveVendorAsync"/>
    /// routes to PUT /accounting/vendors/{id} instead of POST. The Vendor
    /// profile page passes the existing vendor id when saving edits;
    /// the create form leaves this <c>null</c>.
    /// </summary>
    public Guid? VendorId { get; init; }
    /// <summary>
    /// Customer-side equivalent of <see cref="VendorId"/>. When set,
    /// <see cref="BusinessWriteFlowClient.SaveCustomerAsync"/> routes to
    /// PUT /accounting/customers/{id}.
    /// </summary>
    public Guid? CustomerId { get; init; }
}
