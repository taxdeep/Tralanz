using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

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
    private readonly HttpClient _httpClient;
    private readonly ILogger<BusinessWriteFlowClient> _logger;

    public BusinessWriteFlowClient(
        CustomerClient customers,
        VendorClient vendors,
        HttpClient httpClient,
        ILogger<BusinessWriteFlowClient> logger)
    {
        _customers = customers ?? throw new ArgumentNullException(nameof(customers));
        _vendors = vendors ?? throw new ArgumentNullException(nameof(vendors));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WriteFlowResult> PostManualJournalAsync(ManualJournalDraft draft, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            date = draft.Date,
            transactionCurrencyCode = draft.TransactionCurrencyCode,
            exchangeRate = draft.ExchangeRate,
            description = draft.Description,
            displayNumber = draft.DisplayNumber,
            lines = draft.Lines.Select(line => new
            {
                accountId = line.AccountId,
                description = line.Description,
                debit = line.Debit,
                credit = line.Credit,
            }).ToArray(),
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "accounting/manual-journals/save-and-post",
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ManualJournalErrorBody>(cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error?.Message ?? $"Could not post the journal entry (HTTP {(int)response.StatusCode}).",
                    Operation: nameof(PostManualJournalAsync),
                    DraftEcho: draft);
            }

            var body = await response.Content.ReadFromJsonAsync<ManualJournalSaveAndPostResponse>(cancellationToken);
            return new WriteFlowResult(
                Succeeded: true,
                Message: $"Journal entry {body?.JournalDisplayNumber ?? "(unknown)"} posted.",
                Operation: nameof(PostManualJournalAsync),
                DraftEcho: body ?? (object)draft);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual journal save+post call failed.");
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to post the journal entry. Please retry.",
                Operation: nameof(PostManualJournalAsync),
                DraftEcho: draft);
        }
    }

    private sealed record ManualJournalSaveAndPostResponse(
        Guid DocumentId,
        string DocumentNumber,
        Guid JournalEntryId,
        string JournalDisplayNumber);

    private sealed record ManualJournalErrorBody(string? ErrorCode, string? Message);

    /// <summary>
    /// Two-step invoice post — mirrors <see cref="PostReceivePaymentAsync"/>:
    ///   1. <c>POST /accounting/invoices/drafts</c> persists the draft and
    ///      reserves the entity / invoice display numbers; returns documentId.
    ///   2. <c>POST /accounting/invoices/{documentId}/post</c> runs the
    ///      posting engine (writes the JE, opens the AR row) and the
    ///      auto-COGS bridge for any linked sales-issues (M3 iter 3).
    ///
    /// The returned <see cref="WriteFlowResult.Message"/> composes the
    /// invoice display number with the auto-COGS outcomes:
    ///   • all succeeded:  "Invoice INV-0042 posted. COGS auto-posted: JE-0007."
    ///   • partial fail:   "Invoice INV-0042 posted. COGS auto-posted: JE-0007.
    ///                      1 sales-issue(s) need manual COGS post (see workbench)."
    ///   • no linked SI:   "Invoice INV-0042 posted."
    ///
    /// Step-1 success + Step-2 failure leaves a draft sitting in the DB with
    /// <c>DocumentId</c> populated on the result so the page can deep-link
    /// the operator to the draft for retry / inspection.
    /// </summary>
    public async Task<WriteFlowResult> PostInvoiceAsync(InvoiceDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.CompanyId.Value is null)
        {
            return new WriteFlowResult(false, "Active company is required.", nameof(PostInvoiceAsync), draft);
        }
        if (draft.UserId.Value is null)
        {
            return new WriteFlowResult(false, "Active user is required.", nameof(PostInvoiceAsync), draft);
        }
        if (draft.CustomerId is null || draft.CustomerId == Guid.Empty)
        {
            return new WriteFlowResult(false, "Customer is required.", nameof(PostInvoiceAsync), draft);
        }
        if (draft.Lines.Count == 0)
        {
            return new WriteFlowResult(false, "At least one line is required.", nameof(PostInvoiceAsync), draft);
        }

        var savePayload = BuildInvoiceDraftPayload(draft);

        Guid documentId;
        string displayNumber;
        try
        {
            using var saveResponse = await _httpClient.PostAsJsonAsync(
                "accounting/invoices/drafts",
                savePayload,
                cancellationToken);

            if (!saveResponse.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(saveResponse, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Could not save the invoice draft (HTTP {(int)saveResponse.StatusCode}).",
                    Operation: nameof(PostInvoiceAsync),
                    DraftEcho: draft);
            }

            var saved = await saveResponse.Content.ReadFromJsonAsync<InvoiceSaveDraftResponse>(cancellationToken);
            if (saved is null || saved.DocumentId == Guid.Empty)
            {
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: "Server saved the draft but did not return a document id.",
                    Operation: nameof(PostInvoiceAsync),
                    DraftEcho: draft);
            }

            documentId = saved.DocumentId;
            displayNumber = saved.DisplayNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invoice draft save call failed.");
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to save the invoice draft. Please retry.",
                Operation: nameof(PostInvoiceAsync),
                DraftEcho: draft);
        }

        var postPayload = new
        {
            companyId = draft.CompanyId,
            userId = draft.UserId,
            acceptedFxSnapshotId = (Guid?)null,
            idempotencyKey = (string?)null,
        };

        try
        {
            using var postResponse = await _httpClient.PostAsJsonAsync(
                $"accounting/invoices/{documentId:D}/post",
                postPayload,
                cancellationToken);

            if (!postResponse.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(postResponse, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: $"Saved draft as {displayNumber} but posting failed: {error ?? $"HTTP {(int)postResponse.StatusCode}"}. The draft remains for retry.",
                    Operation: nameof(PostInvoiceAsync),
                    DraftEcho: draft)
                {
                    DocumentId = documentId,
                };
            }

            var posted = await postResponse.Content.ReadFromJsonAsync<InvoicePostResponse>(cancellationToken);
            return new WriteFlowResult(
                Succeeded: true,
                Message: ComposePostInvoiceMessage(displayNumber, posted),
                Operation: nameof(PostInvoiceAsync),
                DraftEcho: posted ?? (object)draft)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invoice post call failed.");
            return new WriteFlowResult(
                Succeeded: false,
                Message: $"Saved draft as {displayNumber} but could not reach the server to post. Please retry from the draft.",
                Operation: nameof(PostInvoiceAsync),
                DraftEcho: draft)
            {
                DocumentId = documentId,
            };
        }
    }

    /// <summary>
    /// Post an already-saved invoice draft (draft → posted) WITHOUT
    /// re-saving its lines. Backs the invoice detail page's Post action.
    /// Hits only step 2 of the two-step flow
    /// (<c>POST /invoices/{id}/post</c>) — the draft must already exist.
    /// </summary>
    public async Task<WriteFlowResult> PostExistingInvoiceDraftAsync(
        Guid documentId,
        CompanyId companyId,
        UserId userId,
        string displayNumber,
        CancellationToken cancellationToken = default)
    {
        var postPayload = new
        {
            companyId,
            userId,
            acceptedFxSnapshotId = (Guid?)null,
            idempotencyKey = (string?)null,
        };

        try
        {
            using var postResponse = await _httpClient.PostAsJsonAsync(
                $"accounting/invoices/{documentId:D}/post",
                postPayload,
                cancellationToken);

            if (!postResponse.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(postResponse, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Posting failed (HTTP {(int)postResponse.StatusCode}).",
                    Operation: nameof(PostExistingInvoiceDraftAsync),
                    DraftEcho: (object)documentId)
                {
                    DocumentId = documentId,
                };
            }

            var posted = await postResponse.Content.ReadFromJsonAsync<InvoicePostResponse>(cancellationToken);
            return new WriteFlowResult(
                Succeeded: true,
                Message: ComposePostInvoiceMessage(displayNumber, posted),
                Operation: nameof(PostExistingInvoiceDraftAsync),
                DraftEcho: posted ?? (object)documentId)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invoice post call failed for {DocumentId}.", documentId);
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to post the invoice. Please retry.",
                Operation: nameof(PostExistingInvoiceDraftAsync),
                DraftEcho: (object)documentId)
            {
                DocumentId = documentId,
            };
        }
    }

    /// <summary>
    /// Post an existing bill draft straight from the detail page (draft →
    /// posted) through the v2 engine path — the posting engine's JE writer
    /// flips the bill draft to posted directly, so no separate submit step is
    /// needed. Mirror of <see cref="PostExistingInvoiceDraftAsync"/>.
    /// </summary>
    public async Task<WriteFlowResult> PostExistingBillDraftAsync(
        Guid documentId,
        CompanyId companyId,
        UserId userId,
        string displayNumber,
        CancellationToken cancellationToken = default)
    {
        var postPayload = new
        {
            companyId,
            userId,
            acceptedFxSnapshotId = (Guid?)null,
            idempotencyKey = (string?)null,
        };

        try
        {
            using var postResponse = await _httpClient.PostAsJsonAsync(
                $"accounting/bills/{documentId:D}/post",
                postPayload,
                cancellationToken);

            if (!postResponse.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(postResponse, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Posting failed (HTTP {(int)postResponse.StatusCode}).",
                    Operation: nameof(PostExistingBillDraftAsync),
                    DraftEcho: (object)documentId)
                {
                    DocumentId = documentId,
                };
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: $"Bill {displayNumber} posted.",
                Operation: nameof(PostExistingBillDraftAsync),
                DraftEcho: (object)documentId)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bill post call failed for {DocumentId}.", documentId);
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to post the bill. Please retry.",
                Operation: nameof(PostExistingBillDraftAsync),
                DraftEcho: (object)documentId)
            {
                DocumentId = documentId,
            };
        }
    }

    /// <summary>
    /// Builds the JSON body shared by the invoice draft save (POST
    /// /invoices/drafts) and update (PUT /invoices/drafts/{id}) calls —
    /// a single source of truth so the two paths never drift (a per-line
    /// field missing from one copy once silently dropped tax on save).
    /// Callers must validate CompanyId / UserId / CustomerId / Lines first.
    /// </summary>
    private static object BuildInvoiceDraftPayload(InvoiceDraft draft) => new
    {
        companyId = draft.CompanyId,
        userId = draft.UserId,
        customerId = draft.CustomerId!.Value,
        invoiceDate = draft.DocumentDate,
        dueDate = draft.DueDate ?? draft.DocumentDate,
        transactionCurrencyCode = string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) ? "USD" : draft.TransactionCurrencyCode.Trim().ToUpperInvariant(),
        baseCurrencyCode = string.IsNullOrWhiteSpace(draft.BaseCurrencyCode)
            ? (string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) ? "USD" : draft.TransactionCurrencyCode.Trim().ToUpperInvariant())
            : draft.BaseCurrencyCode.Trim().ToUpperInvariant(),
        fxSnapshotId = (Guid?)null,
        fxRate = draft.FxRate,
        fxEffectiveDate = (DateOnly?)null,
        fxSource = (string?)null,
        memo = draft.Memo,
        invoiceNumber = string.IsNullOrWhiteSpace(draft.InvoiceNumber) ? null : draft.InvoiceNumber.Trim(),
        billingAddress = string.IsNullOrWhiteSpace(draft.BillingAddress) ? null : draft.BillingAddress.Trim(),
        shippingAddress = string.IsNullOrWhiteSpace(draft.ShippingAddress) ? null : draft.ShippingAddress.Trim(),
        lines = draft.Lines.Select(l => new
        {
            lineNumber = l.LineNumber,
            revenueAccountId = l.RevenueAccountId,
            description = l.Description,
            quantity = l.Quantity,
            unitPrice = l.UnitPrice,
            taxCodeId = l.TaxCodeId,
            taxCodeSetId = l.TaxCodeSetId,
            taxAmount = l.TaxAmount,
            taskId = l.TaskId,
        }).ToArray(),
        customerPoNumber = string.IsNullOrWhiteSpace(draft.CustomerPoNumber) ? null : draft.CustomerPoNumber.Trim(),
        salesOrderId = draft.SalesOrderId,
    };

    /// <summary>
    /// Update an existing invoice DRAFT in place (PUT /invoices/drafts/{id})
    /// WITHOUT posting it — backs the invoice editor's Edit mode. The
    /// invoice stays a draft; the operator posts later from the detail
    /// page. ExpectedUpdatedAt is omitted (null) so v1 opts out of the
    /// optimistic-concurrency check.
    /// </summary>
    public async Task<WriteFlowResult> UpdateInvoiceDraftAsync(
        Guid documentId,
        InvoiceDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (draft.CompanyId.Value is null)
        {
            return new WriteFlowResult(false, "Active company is required.", nameof(UpdateInvoiceDraftAsync), draft);
        }
        if (draft.UserId.Value is null)
        {
            return new WriteFlowResult(false, "Active user is required.", nameof(UpdateInvoiceDraftAsync), draft);
        }
        if (draft.CustomerId is null || draft.CustomerId == Guid.Empty)
        {
            return new WriteFlowResult(false, "Customer is required.", nameof(UpdateInvoiceDraftAsync), draft);
        }
        if (draft.Lines.Count == 0)
        {
            return new WriteFlowResult(false, "At least one line is required.", nameof(UpdateInvoiceDraftAsync), draft);
        }

        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"accounting/invoices/drafts/{documentId:D}",
                BuildInvoiceDraftPayload(draft),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(response, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Could not save the invoice draft (HTTP {(int)response.StatusCode}).",
                    Operation: nameof(UpdateInvoiceDraftAsync),
                    DraftEcho: draft)
                {
                    DocumentId = documentId,
                };
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: "Draft saved.",
                Operation: nameof(UpdateInvoiceDraftAsync),
                DraftEcho: draft)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invoice draft update call failed for {DocumentId}.", documentId);
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to save the invoice draft. Please retry.",
                Operation: nameof(UpdateInvoiceDraftAsync),
                DraftEcho: draft);
        }
    }

    private static string ComposePostInvoiceMessage(string displayNumber, InvoicePostResponse? posted)
    {
        if (posted is null)
        {
            return $"Invoice {displayNumber} posted.";
        }

        var parts = new List<string>(4) { $"Invoice {displayNumber} posted." };

        // M3 iter 3: COGS auto-trigger outcomes.
        var auto = posted.AutoPostedCogs ?? Array.Empty<InvoiceAutoCogsOutcomeDto>();
        if (auto.Count > 0)
        {
            var succeeded = auto.Where(o => o.Succeeded).ToArray();
            var failed = auto.Where(o => !o.Succeeded).ToArray();
            if (succeeded.Length > 0)
            {
                var labels = succeeded
                    .Select(o => string.IsNullOrWhiteSpace(o.JournalEntryDisplayNumber) ? "(unnamed JE)" : o.JournalEntryDisplayNumber)
                    .ToArray();
                parts.Add(succeeded.Length == 1
                    ? $"COGS auto-posted: {labels[0]}."
                    : $"COGS auto-posted: {string.Join(", ", labels)}.");
            }
            if (failed.Length > 0)
            {
                parts.Add(failed.Length == 1
                    ? "1 sales-issue needs manual COGS post (see workbench)."
                    : $"{failed.Length} sales-issues need manual COGS post (see workbench).");
            }
        }

        // M5 iter 4: customer-deposit pro-rata application outcome.
        var dep = posted.AppliedCustomerDeposits;
        if (dep is not null)
        {
            if (!string.IsNullOrWhiteSpace(dep.ErrorMessage))
            {
                parts.Add($"Deposit application failed: {dep.ErrorMessage}");
            }
            else if (dep.TotalAppliedBase > 0m && dep.Slices is { Count: > 0 })
            {
                var sliceLabels = dep.Slices
                    .Select(s => $"{s.CustomerDepositDisplayNumber} ({s.AppliedAmountBase:N2})")
                    .ToArray();
                parts.Add(dep.Slices.Count == 1
                    ? $"Deposit applied: {sliceLabels[0]}."
                    : $"Deposits applied: {string.Join(", ", sliceLabels)}.");
            }
        }

        // M6 iter 3: drop-ship COGS auto-recognition outcome. Field is null
        // when the invoice carried no drop-ship lines (most invoices).
        var dropShip = posted.DropShipCogs;
        if (dropShip is not null)
        {
            if (!string.IsNullOrWhiteSpace(dropShip.ErrorMessage))
            {
                parts.Add($"Drop-ship COGS post failed: {dropShip.ErrorMessage}");
            }
            else if (dropShip.JournalEntryId is not null && dropShip.TotalAmountBase > 0m)
            {
                var label = string.IsNullOrWhiteSpace(dropShip.JournalEntryDisplayNumber)
                    ? "(unnamed JE)"
                    : dropShip.JournalEntryDisplayNumber;
                parts.Add(dropShip.AlreadyPosted
                    ? $"Drop-ship COGS already posted: {label}."
                    : $"Drop-ship COGS auto-posted: {label} ({dropShip.TotalAmountBase:N2}).");
            }
        }

        return string.Join(" ", parts);
    }

    private static async Task<string?> ReadAccountingErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<AccountingErrorBody>(cancellationToken);
            return string.IsNullOrWhiteSpace(error?.Message) ? null : error!.Message;
        }
        catch
        {
            return null;
        }
    }

    private sealed record InvoiceSaveDraftResponse(
        Guid DocumentId,
        string EntityNumber,
        string DisplayNumber,
        string Status);

    private sealed record BillSaveDraftResponse(
        Guid DocumentId,
        string EntityNumber,
        string DisplayNumber,
        string Status);

    private sealed record InvoicePostResponse(
        Guid JournalEntryId,
        string JournalEntryDisplayNumber,
        string Status,
        DateTimeOffset PostedAt,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<InvoiceAutoCogsOutcomeDto>? AutoPostedCogs,
        InvoiceDepositApplicationOutcomeDto? AppliedCustomerDeposits,
        InvoiceDropShipCogsOutcomeDto? DropShipCogs);

    /// <summary>Wire-shape mirror of M6 iter 3's <c>InvoiceDropShipCogsOutcome</c>.</summary>
    private sealed record InvoiceDropShipCogsOutcomeDto(
        Guid? JournalEntryId,
        string? JournalEntryDisplayNumber,
        bool AlreadyPosted,
        decimal TotalAmountBase,
        string? ErrorMessage);

    /// <summary>Wire-shape mirror of <c>InvoiceAutoCogsOutcome</c>.</summary>
    private sealed record InvoiceAutoCogsOutcomeDto(
        Guid SalesIssueDocumentId,
        Guid? JournalEntryId,
        string? JournalEntryDisplayNumber,
        bool AlreadyPosted,
        bool Succeeded,
        string? ErrorMessage);

    /// <summary>Wire-shape mirror of M5 iter 4's <c>InvoiceDepositApplicationOutcome</c>.</summary>
    private sealed record InvoiceDepositApplicationOutcomeDto(
        Guid? JournalEntryId,
        string? JournalEntryDisplayNumber,
        decimal TotalAppliedBase,
        IReadOnlyList<InvoiceDepositApplicationSliceDto> Slices,
        string? ErrorMessage);

    private sealed record InvoiceDepositApplicationSliceDto(
        Guid CustomerDepositId,
        string CustomerDepositDisplayNumber,
        decimal AppliedAmountBase,
        bool DepositFullyClosed);

    private sealed record AccountingErrorBody(string? Code, string? Message);

    public Task<WriteFlowResult> PostBillAsync(BillDraft draft, CancellationToken cancellationToken = default) =>
        Pending(nameof(PostBillAsync), draft);

    /// <summary>
    /// Builds the JSON body shared by the bill draft save (POST
    /// /bills/drafts) and update (PUT /bills/drafts/{id}) calls — mirror of
    /// <see cref="BuildInvoiceDraftPayload"/>. JSON keys are camelCase to
    /// match <c>SaveBillDraftHttpRequest</c> / <c>SaveBillDraftLineHttpRequest</c>.
    /// Callers must validate CompanyId / UserId / VendorId / Lines first.
    /// </summary>
    private static object BuildBillDraftPayload(BillDraft draft) => new
    {
        companyId = draft.CompanyId,
        userId = draft.UserId,
        vendorId = draft.VendorId!.Value,
        billDate = draft.DocumentDate,
        dueDate = draft.DueDate ?? draft.DocumentDate,
        transactionCurrencyCode = string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) ? "USD" : draft.TransactionCurrencyCode.Trim().ToUpperInvariant(),
        baseCurrencyCode = string.IsNullOrWhiteSpace(draft.BaseCurrencyCode)
            ? (string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) ? "USD" : draft.TransactionCurrencyCode.Trim().ToUpperInvariant())
            : draft.BaseCurrencyCode.Trim().ToUpperInvariant(),
        fxSnapshotId = (Guid?)null,
        fxRate = draft.FxRate,
        fxEffectiveDate = (DateOnly?)null,
        fxSource = (string?)null,
        memo = draft.Memo,
        billNumber = string.IsNullOrWhiteSpace(draft.BillNumber) ? null : draft.BillNumber.Trim(),
        paymentTermId = draft.PaymentTermId,
        sourcePurchaseOrderId = draft.SourcePurchaseOrderId,
        sourcePurchaseOrderNumber = string.IsNullOrWhiteSpace(draft.SourcePurchaseOrderNumber) ? null : draft.SourcePurchaseOrderNumber.Trim(),
        lines = draft.Lines.Select(l => new
        {
            lineNumber = l.LineNumber,
            expenseAccountId = l.ExpenseAccountId,
            description = l.Description,
            lineAmount = l.LineAmount,
            taxCodeId = l.TaxCodeId,
            taxCodeSetId = l.TaxCodeSetId,
            taxAmount = l.TaxAmount,
            isTaxRecoverable = l.IsTaxRecoverable,
            itemId = l.ItemId,
            warehouseId = l.WarehouseId,
            uomCode = l.UomCode,
            quantity = l.Quantity,
            unitCost = l.UnitCost,
            purchaseOrderId = l.PurchaseOrderId,
            purchaseOrderLineNumber = l.PurchaseOrderLineNumber,
            taskId = l.TaskId,
        }).ToArray(),
    };

    /// <summary>
    /// Save a NEW bill draft through the v2 engine path (POST /bills/drafts)
    /// — mirror of the save-draft half of <see cref="PostInvoiceAsync"/>.
    /// The bill page has no Post action, so there is no second step: the
    /// draft is created (running the purchase-tax engine + snapshot write)
    /// and the operator stays on the draft.
    /// </summary>
    public async Task<WriteFlowResult> PostBillDraftAsync(BillDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.CompanyId.Value is null)
        {
            return new WriteFlowResult(false, "Active company is required.", nameof(PostBillDraftAsync), draft);
        }
        if (draft.UserId.Value is null)
        {
            return new WriteFlowResult(false, "Active user is required.", nameof(PostBillDraftAsync), draft);
        }
        if (draft.VendorId is null || draft.VendorId == Guid.Empty)
        {
            return new WriteFlowResult(false, "Vendor is required.", nameof(PostBillDraftAsync), draft);
        }
        if (draft.Lines.Count == 0)
        {
            return new WriteFlowResult(false, "At least one line is required.", nameof(PostBillDraftAsync), draft);
        }

        try
        {
            using var saveResponse = await _httpClient.PostAsJsonAsync(
                "accounting/bills/drafts",
                BuildBillDraftPayload(draft),
                cancellationToken);

            if (!saveResponse.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(saveResponse, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Could not save the bill draft (HTTP {(int)saveResponse.StatusCode}).",
                    Operation: nameof(PostBillDraftAsync),
                    DraftEcho: draft);
            }

            var saved = await saveResponse.Content.ReadFromJsonAsync<BillSaveDraftResponse>(cancellationToken);
            if (saved is null || saved.DocumentId == Guid.Empty)
            {
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: "Server saved the draft but did not return a document id.",
                    Operation: nameof(PostBillDraftAsync),
                    DraftEcho: draft);
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: $"Bill {saved.DisplayNumber} saved.",
                Operation: nameof(PostBillDraftAsync),
                DraftEcho: draft)
            {
                DocumentId = saved.DocumentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bill draft save call failed.");
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to save the bill draft. Please retry.",
                Operation: nameof(PostBillDraftAsync),
                DraftEcho: draft);
        }
    }

    /// <summary>
    /// Update an existing bill DRAFT in place (PUT /bills/drafts/{id})
    /// through the v2 engine path — mirror of
    /// <see cref="UpdateInvoiceDraftAsync"/>.
    /// </summary>
    public async Task<WriteFlowResult> UpdateBillDraftAsync(
        Guid documentId,
        BillDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (draft.CompanyId.Value is null)
        {
            return new WriteFlowResult(false, "Active company is required.", nameof(UpdateBillDraftAsync), draft);
        }
        if (draft.UserId.Value is null)
        {
            return new WriteFlowResult(false, "Active user is required.", nameof(UpdateBillDraftAsync), draft);
        }
        if (draft.VendorId is null || draft.VendorId == Guid.Empty)
        {
            return new WriteFlowResult(false, "Vendor is required.", nameof(UpdateBillDraftAsync), draft);
        }
        if (draft.Lines.Count == 0)
        {
            return new WriteFlowResult(false, "At least one line is required.", nameof(UpdateBillDraftAsync), draft);
        }

        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"accounting/bills/drafts/{documentId:D}",
                BuildBillDraftPayload(draft),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadAccountingErrorAsync(response, cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error ?? $"Could not save the bill draft (HTTP {(int)response.StatusCode}).",
                    Operation: nameof(UpdateBillDraftAsync),
                    DraftEcho: draft)
                {
                    DocumentId = documentId,
                };
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: "Draft saved.",
                Operation: nameof(UpdateBillDraftAsync),
                DraftEcho: draft)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bill draft update call failed for {DocumentId}.", documentId);
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to save the bill draft. Please retry.",
                Operation: nameof(UpdateBillDraftAsync),
                DraftEcho: draft);
        }
    }

    /// <summary>
    /// Sales receipt = invoice + receive-payment in one shot. The cash
    /// (or bank / credit-card / wallet) account picked as <c>DepositTo</c>
    /// is debited the gross amount; the line revenue accounts and the
    /// tax-payable account are credited. No AR open item is created —
    /// the customer doesn't owe anything because they paid at the point
    /// of sale. Endpoint at <c>/accounting/sales-receipts/save-and-post</c>
    /// returns 501 until the matching repository ships;
    /// <see cref="PostPendingAsync"/> translates that to an
    /// <c>IsStubbed</c> result the page already knows how to render.
    /// </summary>
    public Task<WriteFlowResult> PostSalesReceiptAsync(SalesReceiptDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/sales-receipts/save-and-post", nameof(PostSalesReceiptAsync), draft, cancellationToken);

    /// <summary>
    /// Credit memo — the AR-side reversal of an invoice. Posting credits
    /// AR (reducing what the customer owes) and debits the line revenue
    /// accounts plus tax-payable. If linked to a specific invoice the
    /// backend posts an apply against that invoice's open item so the
    /// receivable trims directly; if standalone it lives as a credit
    /// balance on the customer that can be applied against future
    /// invoices in Receive Payment.
    /// </summary>
    public Task<WriteFlowResult> PostCreditMemoAsync(CreditMemoDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/credit-memos/save-and-post", nameof(PostCreditMemoAsync), draft, cancellationToken);

    /// <summary>
    /// Refund receipt — the cash-side reversal of a sales receipt.
    /// Posting credits the deposit account (money leaves the bank /
    /// wallet) and debits revenue + tax-payable. No AR is touched —
    /// the original sale was cash-in-hand and so is the refund.
    /// </summary>
    public Task<WriteFlowResult> PostRefundReceiptAsync(RefundReceiptDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/refund-receipts/save-and-post", nameof(PostRefundReceiptAsync), draft, cancellationToken);

    /// <summary>
    /// Vendor credit — the AP-side mirror of a credit memo. Posting
    /// debits AP (reducing what we owe the vendor) and credits the line
    /// expense / asset accounts plus the input-tax (ITC) row. Optional
    /// apply-against-bill links it directly to a posted bill so the
    /// payable trims; standalone leaves it as a credit on the vendor.
    /// </summary>
    public Task<WriteFlowResult> PostVendorCreditAsync(VendorCreditDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/vendor-credits/save-and-post", nameof(PostVendorCreditAsync), draft, cancellationToken);

    /// <summary>
    /// Internal account transfer (operating → savings, USD wallet →
    /// CAD wallet, etc.). Single-line journal: debit the destination
    /// account, credit the source. Multi-currency transfers carry an
    /// FX rate so the base-currency amounts stay tied to the same
    /// transaction snapshot.
    /// </summary>
    public Task<WriteFlowResult> PostBankTransferAsync(BankTransferDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/bank-transfers/save-and-post", nameof(PostBankTransferAsync), draft, cancellationToken);

    /// <summary>
    /// Bank deposit — bundles multiple cash-in items (sales receipts,
    /// receive-payment cash, etc.) sitting in an Undeposited Funds
    /// holding account into a single bank-statement-shaped entry that
    /// will reconcile against one bank line later. Posting debits the
    /// destination bank account and credits the holding account for
    /// the total of selected items.
    /// </summary>
    public Task<WriteFlowResult> PostBankDepositAsync(BankDepositDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/bank-deposits/save-and-post", nameof(PostBankDepositAsync), draft, cancellationToken);

    /// <summary>
    /// Tax return — period close for sales-tax (GST/HST/PST/VAT) and
    /// matching input credits (ITCs). Posting moves the period's
    /// collected-tax balance and ITC balance off their respective
    /// accrual accounts and onto a single net-payable (or refundable)
    /// row that becomes a Pay Bills / Receive Payment target. The
    /// filing snapshot itself is immutable once posted — corrections
    /// happen via a follow-on adjustment return.
    /// </summary>
    public Task<WriteFlowResult> PostTaxReturnAsync(TaxReturnDraft draft, CancellationToken cancellationToken = default) =>
        PostPendingAsync("accounting/tax-returns/save-and-post", nameof(PostTaxReturnAsync), draft, cancellationToken);

    /// <summary>
    /// Single round-tripper for the seven V1-pending write flows. POSTs
    /// the draft as JSON to the backend's save-and-post endpoint and
    /// translates the response into a WriteFlowResult:
    ///   • 200 OK             → real round-trip succeeded (future state)
    ///   • 400 Bad Request    → server-side validation failed; surface
    ///                          the error message
    ///   • 501 Not Implemented→ endpoint accepted the payload shape but
    ///                          the matching repository / posting
    ///                          fragment hasn't shipped yet → IsStubbed
    ///   • everything else    → network or transport failure
    ///
    /// Pages render the stubbed branch via WriteFlowResultAlert in a
    /// Warning tone; the operator never sees a green "posted" toast for
    /// a flow that didn't actually touch the GL.
    /// </summary>
    private async Task<WriteFlowResult> PostPendingAsync(
        string requestUri,
        string operation,
        object draft,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(requestUri, draft, cancellationToken);

            if ((int)response.StatusCode == 501)
            {
                var body = await response.Content.ReadFromJsonAsync<PendingImplementationBody>(cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: body?.Message ?? PendingMessage,
                    Operation: operation,
                    DraftEcho: draft)
                {
                    IsStubbed = true,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: $"Server returned {(int)response.StatusCode}: {Truncate(body, 240)}",
                    Operation: operation,
                    DraftEcho: draft);
            }

            // Successful round-trip — try to surface the documentId
            // from the response body so Create pages can redirect to
            // the new doc's detail view. All seven save-and-post
            // endpoints currently return { documentId, ... }; if a
            // future endpoint omits it, the redirect simply falls
            // back to the section overview.
            Guid? documentId = null;
            try
            {
                var body = await response.Content.ReadFromJsonAsync<SaveAndPostSuccessBody>(cancellationToken);
                if (body?.DocumentId is { } id && id != Guid.Empty)
                {
                    documentId = id;
                }
            }
            catch
            {
                // Response wasn't the expected shape — leave DocumentId
                // null and the page will use its fallback redirect.
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: $"{operation} round-trip succeeded.",
                Operation: operation,
                DraftEcho: draft)
            {
                DocumentId = documentId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Operation} HTTP call failed.", operation);
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server. Please retry.",
                Operation: operation,
                DraftEcho: draft);
        }
    }

    private sealed record PendingImplementationBody(
        string? Status,
        string? Operation,
        string? Message,
        string? NextStep);

    private sealed record SaveAndPostSuccessBody(
        Guid? DocumentId);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    public async Task<WriteFlowResult> PostReceivePaymentAsync(ReceivePaymentDraft draft, CancellationToken cancellationToken = default)
    {
        // Two-step server flow:
        //   1. POST /receive-payments/prepare  → saves the draft + reserves AR
        //      open-item amounts, returns a document id.
        //   2. POST /receive-payments/{id}/post → runs the posting engine,
        //      writes the JE rows, settles the open items.
        // We collapse them into one client call so the form doesn't have to
        // know about the document id round-trip.
        var preparePayload = new
        {
            companyId = draft.CompanyId,
            userId = draft.UserId,
            customerId = draft.CustomerId ?? Guid.Empty,
            bankAccountId = draft.BankAccountId,
            paymentDate = draft.Date,
            acceptedFxSnapshotId = (Guid?)null,
            memo = draft.Memo,
            lines = draft.Applications.Select(a => new
            {
                targetOpenItemId = a.OpenItemId,
                appliedAmountTx = a.AppliedAmount,
            }).ToArray(),
        };

        try
        {
            using var prepareResponse = await _httpClient.PostAsJsonAsync(
                "accounting/receive-payments/prepare",
                preparePayload,
                cancellationToken);

            if (!prepareResponse.IsSuccessStatusCode)
            {
                var error = await prepareResponse.Content.ReadFromJsonAsync<ReceivePaymentErrorBody>(cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error?.Message ?? $"Could not prepare the payment (HTTP {(int)prepareResponse.StatusCode}).",
                    Operation: nameof(PostReceivePaymentAsync),
                    DraftEcho: draft);
            }

            var prepared = await prepareResponse.Content.ReadFromJsonAsync<ReceivePaymentPreparedBody>(cancellationToken);
            if (prepared is null || prepared.DocumentId == Guid.Empty)
            {
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: "Server prepared the payment but did not return a document id.",
                    Operation: nameof(PostReceivePaymentAsync),
                    DraftEcho: draft);
            }

            var postPayload = new
            {
                companyId = draft.CompanyId,
                userId = draft.UserId,
                acceptedFxSnapshotId = (Guid?)null,
                idempotencyKey = (string?)null,
            };

            using var postResponse = await _httpClient.PostAsJsonAsync(
                $"accounting/receive-payments/{prepared.DocumentId:D}/post",
                postPayload,
                cancellationToken);

            if (!postResponse.IsSuccessStatusCode)
            {
                var error = await postResponse.Content.ReadFromJsonAsync<ReceivePaymentErrorBody>(cancellationToken);
                return new WriteFlowResult(
                    Succeeded: false,
                    Message: error?.Message ?? $"Prepared the payment but posting failed (HTTP {(int)postResponse.StatusCode}). Document id: {prepared.DocumentId:D}.",
                    Operation: nameof(PostReceivePaymentAsync),
                    DraftEcho: draft);
            }

            return new WriteFlowResult(
                Succeeded: true,
                Message: $"Payment {prepared.DisplayNumber} posted.",
                Operation: nameof(PostReceivePaymentAsync),
                DraftEcho: prepared);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receive payment prepare/post call failed.");
            return new WriteFlowResult(
                Succeeded: false,
                Message: "Could not reach the server to post the payment. Please retry.",
                Operation: nameof(PostReceivePaymentAsync),
                DraftEcho: draft);
        }
    }

    private sealed record ReceivePaymentPreparedBody(
        Guid DocumentId,
        string EntityNumber,
        string DisplayNumber,
        int PreparedLineCount,
        decimal TotalAmount,
        string Status);

    private sealed record ReceivePaymentErrorBody(string? Message);

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
            DraftEcho: payload)
        {
            IsStubbed = true,
        });
}

public sealed record WriteFlowResult(
    bool Succeeded,
    string Message,
    string Operation,
    object DraftEcho)
{
    /// <summary>
    /// True when this result came from a Pending stub — i.e. the form
    /// is fully validated and the payload is well-shaped, but the
    /// backend HTTP route does not exist yet. Pages use this to render
    /// a Warning-tone alert (vs. the Info tone for real "draft saved /
    /// posted" responses) so an operator never mistakes a simulation
    /// for a real GL movement.
    /// </summary>
    public bool IsStubbed { get; init; }

    /// <summary>
    /// Document id of the just-created/posted document, surfaced from
    /// the success response body (200 OK with <c>documentId</c> field).
    /// Create pages use this to redirect to the detail page after a
    /// successful post.
    /// </summary>
    public Guid? DocumentId { get; init; }
}

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
    /// <summary>
    /// Account-id resolved by the AccountPicker. Required — the backend
    /// looks up each line's account by id (not code) so a tampered request
    /// can't reference rows from another company by guessing codes.
    /// </summary>
    public Guid AccountId { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
}

public sealed record InvoiceDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    /// <summary>Company base currency snapshot at submit time — server uses this to validate FX direction.</summary>
    public string BaseCurrencyCode { get; init; } = string.Empty;
    /// <summary>
    /// Per-document FX rate (transaction → company base currency). Pre-filled
    /// with the recommended D-1 close rate from <c>fx_rates_daily</c> /
    /// frankfurter, but the user can override (credit-card statement rate,
    /// hand-negotiated conversion, etc.). Null when the transaction
    /// currency equals the base currency.
    /// </summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    /// <summary>
    /// User-supplied invoice number (free-form). Defaults to the peeked next
    /// auto number on a new invoice; the server uses it instead of the
    /// INV-###### auto sequence when non-blank, and ignores it on update.
    /// </summary>
    public string? InvoiceNumber { get; init; }
    /// <summary>Free-text billing / shipping address shown on the invoice Header.</summary>
    public string? BillingAddress { get; init; }
    public string? ShippingAddress { get; init; }
    /// <summary>
    /// Customer's own purchase-order reference. Carried into the wire shape
    /// so when the create endpoint lands the value is already in the payload.
    /// </summary>
    public string CustomerPoNumber { get; init; } = string.Empty;
    /// <summary>
    /// Back-link to the Sales Order this invoice was created from (when
    /// applicable). Drives auto-COGS-post linkage and the printed banner.
    /// </summary>
    public Guid? SalesOrderId { get; init; }
    public IReadOnlyList<InvoiceLineDraft> Lines { get; init; } = Array.Empty<InvoiceLineDraft>();
}

/// <summary>
/// Wire-shape line for <see cref="InvoiceDraft"/>. Mirror of
/// <see cref="SalesReceiptLineDraft"/> — sends the resolved
/// <c>RevenueAccountId</c> + <c>TaxCodeId</c> Guids the picker already
/// holds, so the server doesn't have to re-resolve from a code lookup.
/// </summary>
public sealed record InvoiceLineDraft
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }

    /// <summary>
    /// Per-line back-link to the Task this line bills. Set when the
    /// invoice was opened via "Bill this task" — the server persists
    /// it on invoice_lines.task_id, and the post handler aggregates
    /// distinct task_ids to flip source tasks Completed -> Billed.
    /// </summary>
    public Guid? TaskId { get; init; }
    // R2: tax_code_sets.id — a Tax Code bundle selected on this line.
    public Guid? TaxCodeSetId { get; init; }
}

/// <summary>
/// Wire shape for the cash-in-hand sale flow. Distinct from
/// <see cref="InvoiceDraft"/> in three ways: there is no <c>DueDate</c>
/// (the customer pays now), the form carries an explicit
/// <see cref="DepositToAccountId"/> (the cash / bank / credit-card /
/// wallet account that receives the money), and the
/// <see cref="PaymentMethod"/> + <see cref="ReferenceNo"/> are mandatory
/// metadata for the bank reconciliation step downstream.
///
/// The line shape is <see cref="SalesReceiptLineDraft"/>, which carries
/// the resolved <c>RevenueAccountId</c> + <c>TaxCodeId</c> Guids
/// directly (the frontend pickers already have them). Sending Guids
/// rather than display codes is the same convention the Invoice
/// repository uses and removes a server-side <c>code → id</c> lookup
/// that's redundant when the client already holds the id.
/// </summary>
public sealed record SalesReceiptDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid? CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;

    /// <summary>Company base currency at the time of save — server uses this to validate FX direction without an extra round-trip.</summary>
    public string BaseCurrencyCode { get; init; } = string.Empty;

    /// <summary>Per-document FX rate (transaction → base). Same semantics as <see cref="InvoiceDraft.FxRate"/>.</summary>
    public decimal? FxRate { get; init; }

    /// <summary>Asset account that receives the cash. Picked from the
    /// <c>AccountPicker</c> with <c>AllowedRootTypes="asset"</c>.</summary>
    public Guid? DepositToAccountId { get; init; }
    public string DepositToAccountCode { get; init; } = string.Empty;

    /// <summary>cash / cheque / credit_card / wire / eft / direct_deposit / other.</summary>
    public string PaymentMethod { get; init; } = string.Empty;

    /// <summary>Cheque #, wire trace, EFT reference, etc. Conditionally required by <see cref="PaymentMethod"/>.</summary>
    public string ReferenceNo { get; init; } = string.Empty;

    public string Memo { get; init; } = string.Empty;
    /// <summary>Customer's own purchase-order reference. Optional. Carried into the saved sales-receipt for downstream search + customer-portal display.</summary>
    public string CustomerPoNumber { get; init; } = string.Empty;
    public IReadOnlyList<SalesReceiptLineDraft> Lines { get; init; } = Array.Empty<SalesReceiptLineDraft>();
}

public sealed record SalesReceiptLineDraft
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
}

/// <summary>
/// Wire shape for the customer-side credit memo (AR reversal). Carries
/// the same line shape as <see cref="InvoiceDraft"/>; the optional
/// <see cref="AppliedToInvoiceId"/> and <see cref="AppliedToInvoiceNumber"/>
/// are populated when the operator opens this credit memo as "credit
/// against invoice X" — backend uses that linkage to settle the AR open
/// item directly. Standalone credit memos leave both fields null and
/// produce a dangling negative-AR row that can be applied later via
/// Receive Payment.
///
/// Backend reuses the existing <c>credit_notes</c> table; the
/// "credit memo" name is the QBO-flavoured operator label, the GL
/// artifact stays a credit_note. Line shape carries
/// RevenueAccountId / TaxCodeId Guids (the frontend pickers already
/// hold the ids — no server-side code → id resolution needed).
/// </summary>
public sealed record CreditMemoDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid? CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }

    /// <summary>Optional reference to an existing invoice that this credit cancels in part or whole.</summary>
    public Guid? AppliedToInvoiceId { get; init; }
    public string AppliedToInvoiceNumber { get; init; } = string.Empty;

    /// <summary>Operator-visible note on why credit was issued (RMA #, return reason, etc.).</summary>
    public string Reason { get; init; } = string.Empty;

    public string Memo { get; init; } = string.Empty;
    /// <summary>Customer's own purchase-order reference (typically copied from the original invoice). Optional.</summary>
    public string CustomerPoNumber { get; init; } = string.Empty;
    public IReadOnlyList<CreditMemoLineDraft> Lines { get; init; } = Array.Empty<CreditMemoLineDraft>();
}

public sealed record CreditMemoLineDraft
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }

    /// <summary>
    /// Optional Task back-link. Set when the credit memo was pre-filled
    /// from a "Credit invoice" flow that propagates the source invoice
    /// line's task_id. After the credit note posts, the server-side
    /// hook rolls every linked task back to Completed.
    /// </summary>
    public Guid? TaskId { get; init; }
}

/// <summary>
/// Refund receipt — money out, mirror of <see cref="SalesReceiptDraft"/>.
/// The deposit account becomes the source of funds (bank credited) and
/// the revenue / tax accrual rows are debited. Cheque / wire / EFT
/// reference is mandatory the same way it is on the original sale so
/// downstream bank-rec can match the outflow.
/// </summary>
public sealed record RefundReceiptDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid? CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? RefundFromAccountId { get; init; }
    public string RefundFromAccountCode { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    /// <summary>Customer's own purchase-order reference. Optional.</summary>
    public string CustomerPoNumber { get; init; } = string.Empty;
    public IReadOnlyList<RefundReceiptLineDraft> Lines { get; init; } = Array.Empty<RefundReceiptLineDraft>();
}

public sealed record RefundReceiptLineDraft
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
}

/// <summary>
/// Vendor credit (AP credit memo). Mirror of <see cref="CreditMemoDraft"/>
/// but on the purchase side: vendor refund / return-to-vendor / volume
/// rebate / dispute resolution. The optional <see cref="AppliedToBillId"/>
/// link settles a posted bill's AP open item directly.
///
/// Backend reuses the existing <c>vendor_credits</c> table; the line
/// shape sends ExpenseAccountId Guids the picker already holds.
/// </summary>
public sealed record VendorCreditDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid? VendorId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? AppliedToBillId { get; init; }
    public string AppliedToBillNumber { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<VendorCreditLineDraft> Lines { get; init; } = Array.Empty<VendorCreditLineDraft>();
}

public sealed record VendorCreditLineDraft
{
    public int LineNumber { get; init; }
    public Guid ExpenseAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal LineAmount { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
}

/// <summary>
/// Internal account transfer. Single source → single destination.
/// Same-currency transfers carry no FX rate. Cross-currency transfers
/// (USD operating → CAD savings, etc.) take the conversion rate the
/// bank actually applied — defaulting to D-1 close from
/// <c>fx_rates_daily</c> with manual override, same convention as
/// invoice / bill.
///
/// ── BACKEND VALIDATION CONTRACT ─────────────────────────────────
///   Same-currency:
///     • <c>FxRate</c> MUST be null.
///     • <c>FromCurrencyCode</c> == <c>ToCurrencyCode</c>.
///     • Posted JE: Cr From = Amount, Dr To = Amount.
///
///   Cross-currency:
///     • <c>FxRate</c> MUST be &gt; 0.
///     • Backend recomputes <c>derivedToAmount = Amount * FxRate</c>
///       and asserts it matches the operator's expectation within a
///       tolerance (recommended: 1 cent of the destination currency,
///       OR 0.5% — whichever is wider, to absorb rounding on the
///       operator's side). UI shows derivedToAmount in the sidebar
///       so any mismatch is visible before posting.
///     • Posted JE base-currency rows: convert each side using its
///       own currency's snapshot rate from
///       <c>company_fx_rate_snapshots</c>; never use the operator's
///       per-document FX for the GL base-currency math (only for
///       the displayed transaction-currency amounts).
/// ────────────────────────────────────────────────────────────────
/// </summary>
public sealed record BankTransferDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }

    /// <summary>Account funds leave (credited).</summary>
    public Guid? FromAccountId { get; init; }
    public string FromAccountCode { get; init; } = string.Empty;
    public string FromCurrencyCode { get; init; } = string.Empty;

    /// <summary>Account funds land in (debited).</summary>
    public Guid? ToAccountId { get; init; }
    public string ToAccountCode { get; init; } = string.Empty;
    public string ToCurrencyCode { get; init; } = string.Empty;

    /// <summary>Amount in the source account's currency.</summary>
    public decimal Amount { get; init; }

    /// <summary>FX rate when source and destination currencies differ.</summary>
    public decimal? FxRate { get; init; }

    public string ReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
}

/// <summary>
/// Bank deposit slip — multiple Undeposited-Funds items rolled into
/// one bank-line entry. Each application reduces the holding account
/// by its share; the total goes to the destination bank account.
/// </summary>
public sealed record BankDepositDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid? DepositToAccountId { get; init; }
    public string DepositToAccountCode { get; init; } = string.Empty;
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public IReadOnlyList<BankDepositItemDraft> Items { get; init; } = Array.Empty<BankDepositItemDraft>();
}

public sealed record BankDepositItemDraft
{
    /// <summary>Source receipt / payment id sitting in Undeposited Funds.</summary>
    public Guid SourceItemId { get; init; }
    public string SourceItemDisplayNumber { get; init; } = string.Empty;
    public string PayerName { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

/// <summary>
/// Tax return draft — the period the operator wants to file plus any
/// manual adjustments to the auto-calculated boxes (Quebec QST, HST
/// recapture, prior-period corrections). Posting the return locks the
/// period and emits the net JE.
///
/// ── GL CONTRACT (binding for backend implementations) ─────────────
///   Net = CollectedAmount − InputCreditsAmount + AdjustmentsAmount
///
///   Net &gt; 0  →  the operator owes the regulator
///                 Dr Tax Payable (output-tax accrual)   = CollectedAmount
///                 Cr Tax Receivable (ITC accrual)       = InputCreditsAmount
///                 Dr Tax Adjustments (signed)           = AdjustmentsAmount
///                 Cr Tax Filing Liability               = Net
///
///   Net &lt; 0  →  the regulator owes the operator (refund)
///                 Same accrual clearings, but the |Net| lands on the
///                 Tax Filing Receivable side instead of the liability.
///
///   Net = 0  →  no settlement row; period still locks.
///
///   The Tax Filing Liability / Receivable row is the open item that
///   becomes a Pay Bills (owe) or Receive Payment (refund) target on
///   the cash side. Its account id comes from company_settings's
///   tax_filing_liability_account / tax_filing_receivable_account
///   columns; until those settings ship, the backend should fall back
///   to the regime-specific tax-payable account.
/// ─────────────────────────────────────────────────────────────────
///
/// Frontend computes Net live for sidebar preview. Backend MUST use
/// the same arithmetic — operator sees Net before posting and any
/// drift between the displayed value and the posted JE breaks trust.
/// </summary>
public sealed record TaxReturnDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }

    /// <summary>e.g. "GST_HST", "QST", "PST_BC", "VAT_UK".</summary>
    public string TaxRegime { get; init; } = string.Empty;

    /// <summary>Filing frequency: monthly / quarterly / annual. Drives default period bounds + due date.</summary>
    public string FilingFrequency { get; init; } = string.Empty;

    /// <summary>Company base currency at the time of save — server stores it on tax_returns.base_currency_code.</summary>
    public string BaseCurrencyCode { get; init; } = string.Empty;

    /// <summary>Auto-totalled output tax (sales-tax collected) in transaction currency.</summary>
    public decimal CollectedAmount { get; init; }

    /// <summary>Auto-totalled input-tax credits (sales-tax paid on purchases).</summary>
    public decimal InputCreditsAmount { get; init; }

    /// <summary>Operator override for prior-period corrections / regulator adjustments.</summary>
    public decimal AdjustmentsAmount { get; init; }
    public string AdjustmentsNote { get; init; } = string.Empty;

    /// <summary>Reference number from the regulator portal once filed (CRA confirmation #, Revenu Quebec ref, etc.).</summary>
    public string RegulatorReferenceNo { get; init; } = string.Empty;

    public string Memo { get; init; } = string.Empty;
}

public sealed record BillDraft
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? VendorId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    /// <summary>Per-document FX rate (transaction → base). Same semantics as <see cref="InvoiceDraft.FxRate"/>.</summary>
    public decimal? FxRate { get; init; }
    public string Memo { get; init; } = string.Empty;
    /// <summary>Supplier's own invoice number (AP reference). Stored as the bill's display number; v2 auto-numbers only when this is blank.</summary>
    public string BillNumber { get; init; } = string.Empty;
    public Guid? PaymentTermId { get; init; }
    public Guid? SourcePurchaseOrderId { get; init; }
    public string SourcePurchaseOrderNumber { get; init; } = string.Empty;
    public IReadOnlyList<BillLineDraft> Lines { get; init; } = Array.Empty<BillLineDraft>();
}

public sealed record BillLineDraft
{
    public int LineNumber { get; init; }
    public Guid ExpenseAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal LineAmount { get; init; }
    public Guid? TaxCodeId { get; init; }
    public Guid? TaxCodeSetId { get; init; }
    public decimal TaxAmount { get; init; }
    public bool IsTaxRecoverable { get; init; }
    public Guid? ItemId { get; init; }
    public Guid? WarehouseId { get; init; }
    public string? UomCode { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitCost { get; init; }
    public Guid? PurchaseOrderId { get; init; }
    public int? PurchaseOrderLineNumber { get; init; }
    public Guid? TaskId { get; init; }
}

public sealed record ReceivePaymentDraft
{
    /// <summary>Active company id. The page reads this from
    /// <c>BusinessShellState.ActiveCompany.Id</c>; the API endpoint
    /// expects it in the prepare-draft request body.</summary>
    public CompanyId CompanyId { get; init; }

    /// <summary>The acting user id from the same source.</summary>
    public UserId UserId { get; init; }

    /// <summary>Asset account that receives the cash. Picked from the
    /// page's "Deposit to (Bank)" dropdown, filtered by detail_type
    /// ∈ {bank, cash, credit_card}.</summary>
    public Guid BankAccountId { get; init; }

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
