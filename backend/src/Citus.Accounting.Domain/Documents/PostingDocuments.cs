using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Domain.Documents;

public interface IPostingDocument
{
    Guid Id { get; }
    CompanyId CompanyId { get; }
    EntityNumber EntityNumber { get; }
    string SourceType { get; }
    string Status { get; }
    DateOnly DocumentDate { get; }
    CurrencyCode TransactionCurrencyCode { get; }
    CurrencyCode BaseCurrencyCode { get; }
    IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public interface IPostingDocumentLine
{
    int LineNumber { get; }
    string Description { get; }
}

public interface IOpenItemDocument
{
    Guid PartyId { get; }
    DateOnly? DueDate { get; }
    decimal TotalAmount { get; }
}

public interface ISettlementDocument
{
    Guid BankAccountId { get; }
}

public sealed record ManualJournalDocumentLine : IPostingDocumentLine
{
    public ManualJournalDocumentLine(int lineNumber, Guid accountId, string description, decimal txDebit, decimal txCredit)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (txDebit < 0 || txCredit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(txDebit), "Amounts cannot be negative.");
        }

        if ((txDebit > 0m && txCredit > 0m) || (txDebit == 0m && txCredit == 0m))
        {
            throw new InvalidOperationException("Manual journal line must contain exactly one debit or one credit.");
        }

        LineNumber = lineNumber;
        AccountId = accountId;
        Description = description.Trim();
        TxDebit = txDebit;
        TxCredit = txCredit;
    }

    public int LineNumber { get; }

    public Guid AccountId { get; }

    public string Description { get; }

    public decimal TxDebit { get; }

    public decimal TxCredit { get; }
}

public sealed class ManualJournalDocument : IPostingDocument
{
    public ManualJournalDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<ManualJournalDocumentLine> lines,
        string? memo = null)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Manual journal document must contain at least one line.");
        }

        JournalLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "manual_journal";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public string? Memo { get; }

    public IReadOnlyList<ManualJournalDocumentLine> JournalLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    public bool IsBalancedInTransactionCurrency()
    {
        var totalDebit = JournalLines.Sum(static line => line.TxDebit);
        var totalCredit = JournalLines.Sum(static line => line.TxCredit);
        return totalDebit == totalCredit;
    }
}

/// <summary>
/// S5: one posted tax-snapshot row attached to a posting-document line.
/// This is a LOCAL Citus.Accounting.Domain read-model (deliberately not the
/// SalesTax module's record, to avoid a Domain→SalesTax dependency). It is
/// populated by the per-document <c>GetForPostingAsync</c> from
/// <c>document_line_sales_tax_snapshots</c>; the posting fragment builder
/// (S5.2) reads it to route each tax leg to its snapshotted GL account. The
/// list is empty when the document was saved with SalesTaxV2 off — the
/// builder then falls back to the single <c>line.TaxAmount</c> path.
/// </summary>
public sealed record DocumentLineTaxSnapshot(
    int Sequence,
    string Leg,
    string RegimeType,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    Guid? PayableAccountId,
    Guid? RecoverableAccountId,
    Guid? NonRecoverableAccountId,
    // The Tax Rule's code at compute time (e.g. GST, PST-BC). Lets read
    // surfaces show the per-rule tax breakdown without re-deriving it.
    string Code = "");

public sealed record InvoiceDocumentLine : IPostingDocumentLine
{
    public InvoiceDocumentLine(
        int lineNumber,
        Guid revenueAccountId,
        string description,
        decimal quantity,
        decimal unitPrice,
        decimal lineAmount,
        decimal taxAmount,
        Guid? payableTaxAccountId,
        Guid? taxCodeId = null,
        Guid? itemId = null,
        Guid? warehouseId = null,
        string? uomCode = null,
        // Optional Task back-link, surfaced on read so the credit-note
        // create page can propagate the source line's task_id when
        // pre-filling from an invoice. Persisted in invoice_lines.task_id;
        // the posting engine ignores this field.
        Guid? taskId = null,
        // S5: per-line tax snapshots (empty when saved with the flag off).
        IReadOnlyList<DocumentLineTaxSnapshot>? taxSnapshots = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (revenueAccountId == Guid.Empty)
        {
            throw new ArgumentException("Revenue account id is required.", nameof(revenueAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (unitPrice < 0m || lineAmount < 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Invoice amounts cannot be negative.");
        }

        // A single line-level payable account only fits a single-rule tax.
        // A multi-rule Tax Code (e.g. GST + PST) splits into several legs
        // whose payable accounts live on the per-leg tax snapshots, so the
        // line-level account is legitimately null there. Require a payable
        // account ONLY when the line bears tax but has no snapshots to carry it.
        if (taxAmount > 0m && payableTaxAccountId is null && (taxSnapshots is null || taxSnapshots.Count == 0))
        {
            throw new InvalidOperationException("Tax-bearing invoice lines must resolve to a payable tax account (directly or via tax snapshots).");
        }

        LineNumber = lineNumber;
        RevenueAccountId = revenueAccountId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        PayableTaxAccountId = payableTaxAccountId;
        TaxCodeId = taxCodeId;
        ItemId = itemId;
        WarehouseId = warehouseId;
        UomCode = string.IsNullOrWhiteSpace(uomCode) ? null : uomCode.Trim().ToUpperInvariant();
        TaskId = taskId;
        TaxSnapshots = taxSnapshots ?? Array.Empty<DocumentLineTaxSnapshot>();
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }

    public Guid? ItemId { get; }

    public Guid? WarehouseId { get; }

    public string? UomCode { get; }

    public Guid? TaskId { get; }

    public IReadOnlyList<DocumentLineTaxSnapshot> TaxSnapshots { get; }
}

public sealed class InvoiceDocument : IPostingDocument, IOpenItemDocument
{
    public InvoiceDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        DateOnly dueDate,
        Guid customerId,
        Guid receivableAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<InvoiceDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null,
        string? customerPoNumber = null,
        Guid? salesOrderId = null,
        string? billingAddress = null,
        string? shippingAddress = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Receivable account id is required.", nameof(receivableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        DueDate = dueDate;
        PartyId = customerId;
        ReceivableAccountId = receivableAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        CustomerPoNumber = string.IsNullOrWhiteSpace(customerPoNumber) ? null : customerPoNumber.Trim();
        SalesOrderId = salesOrderId;
        BillingAddress = string.IsNullOrWhiteSpace(billingAddress) ? null : billingAddress.Trim();
        ShippingAddress = string.IsNullOrWhiteSpace(shippingAddress) ? null : shippingAddress.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Invoice document must contain at least one line.");
        }

        InvoiceLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "invoice";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public DateOnly? DueDate { get; }

    public Guid PartyId { get; }

    public Guid ReceivableAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    /// <summary>
    /// Customer's own purchase-order reference (their procurement system's
    /// PO number). Carried through from Sales Order if invoiced from one;
    /// editable on the invoice itself for late-arriving PO updates. Optional —
    /// retail / B2C invoices typically leave it null.
    /// </summary>
    public string? CustomerPoNumber { get; }

    /// <summary>Free-text billing / shipping address surfaced on the invoice
    /// Header. Metadata only — not used in the posting fragments.</summary>
    public string? BillingAddress { get; }

    public string? ShippingAddress { get; }

    /// <summary>
    /// Back-link to the Sales Order this invoice was created from, when
    /// applicable. Null for direct-create invoices. Drives the auto-COGS
    /// trigger (find the SalesIssue tied to the SO) and the printed
    /// "From SO# XYZ" banner on the customer-facing invoice.
    /// </summary>
    public Guid? SalesOrderId { get; }

    public IReadOnlyList<InvoiceDocumentLine> InvoiceLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public sealed record CreditNoteDocumentLine : IPostingDocumentLine
{
    public CreditNoteDocumentLine(
        int lineNumber,
        Guid revenueAccountId,
        string description,
        decimal quantity,
        decimal unitPrice,
        decimal lineAmount,
        decimal taxAmount,
        Guid? payableTaxAccountId,
        Guid? taxCodeId = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (revenueAccountId == Guid.Empty)
        {
            throw new ArgumentException("Revenue account id is required.", nameof(revenueAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (unitPrice < 0m || lineAmount < 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Credit note amounts cannot be negative.");
        }

        if (taxAmount > 0m && payableTaxAccountId is null)
        {
            throw new InvalidOperationException("Tax-bearing credit note lines must resolve to a payable tax account.");
        }

        LineNumber = lineNumber;
        RevenueAccountId = revenueAccountId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        PayableTaxAccountId = payableTaxAccountId;
        TaxCodeId = taxCodeId;
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }
}

public sealed class CreditNoteDocument : IPostingDocument, IOpenItemDocument
{
    public CreditNoteDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        DateOnly dueDate,
        Guid customerId,
        Guid receivableAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<CreditNoteDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null,
        string? customerPoNumber = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Receivable account id is required.", nameof(receivableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        DueDate = dueDate;
        PartyId = customerId;
        ReceivableAccountId = receivableAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        CustomerPoNumber = string.IsNullOrWhiteSpace(customerPoNumber) ? null : customerPoNumber.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Credit note document must contain at least one line.");
        }

        CreditNoteLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "credit_note";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public DateOnly? DueDate { get; }

    public Guid PartyId { get; }

    public Guid ReceivableAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    /// <summary>Customer's own purchase-order reference (typically copied from the original invoice). Optional.</summary>
    public string? CustomerPoNumber { get; }

    public IReadOnlyList<CreditNoteDocumentLine> CreditNoteLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public sealed record BillDocumentLine : IPostingDocumentLine
{
    public BillDocumentLine(
        int lineNumber,
        Guid expenseAccountId,
        string description,
        decimal lineAmount,
        decimal taxAmount,
        bool isTaxRecoverable,
        Guid? recoverableTaxAccountId,
        Guid? taxCodeId = null,
        Guid? itemId = null,
        Guid? warehouseId = null,
        string? uomCode = null,
        decimal? quantity = null,
        decimal? unitCost = null,
        Guid? purchaseOrderId = null,
        int? purchaseOrderLineNumber = null,
        // S5/B2: per-line tax snapshots (empty when saved with the flag off).
        // A multi-rule Tax Code splits purchase tax into per-rule recoverable
        // (ITC) and non-recoverable portions whose accounts live on these
        // snapshots; the posting fragment builder reads them to emit one ITC
        // leg per recoverable rule and folds non-recoverable into the expense.
        IReadOnlyList<DocumentLineTaxSnapshot>? taxSnapshots = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (expenseAccountId == Guid.Empty)
        {
            throw new ArgumentException("Expense account id is required.", nameof(expenseAccountId));
        }

        // Description is optional on a bill line (many vendor invoices carry
        // only a category + amount); the JE line description falls back to a
        // placeholder when blank.

        if (lineAmount <= 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(lineAmount), "Bill line amounts must be positive and tax cannot be negative.");
        }

        // A single line-level recoverable account only fits a single-rule
        // tax. A multi-rule Tax Code (e.g. GST + PST) splits into per-rule
        // recoverable (ITC) / non-recoverable legs whose accounts live on the
        // per-leg tax snapshots, so the line-level recoverable account is
        // legitimately null there. Require it ONLY when the line bears
        // recoverable tax but has no snapshots to carry the per-rule accounts.
        if (taxAmount > 0m && isTaxRecoverable && recoverableTaxAccountId is null
            && (taxSnapshots is null || taxSnapshots.Count == 0))
        {
            throw new InvalidOperationException("Recoverable tax bill lines must resolve to a recoverable tax account (directly or via tax snapshots).");
        }

        // Stock-receipt-grade lines need the full bundle: item + warehouse +
        // uom + quantity + unit cost. Drop-ship lines carry only itemId
        // (used by the M6 aging workbench for clearing-account matching) and
        // intentionally have no warehouse / qty / cost, since the item never
        // enters our inventory. So the all-or-nothing rule keys off
        // warehouseId / uomCode / quantity / unitCost — itemId alone is fine.
        var hasInventorySemantics =
            warehouseId.HasValue ||
            !string.IsNullOrWhiteSpace(uomCode) ||
            quantity.HasValue ||
            unitCost.HasValue;

        if (hasInventorySemantics)
        {
            if (!itemId.HasValue || itemId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require an item id.");
            }

            if (!warehouseId.HasValue || warehouseId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a warehouse id.");
            }

            if (string.IsNullOrWhiteSpace(uomCode))
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a UOM code.");
            }

            if (!quantity.HasValue || quantity.Value <= 0m)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a positive quantity.");
            }

            if (!unitCost.HasValue || unitCost.Value < 0m)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a non-negative unit cost.");
            }
        }

        var hasPurchaseOrderAnchor = purchaseOrderId.HasValue || purchaseOrderLineNumber.HasValue;
        if (hasPurchaseOrderAnchor)
        {
            if (!purchaseOrderId.HasValue || purchaseOrderId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("PO-anchored bill lines require a purchase order id.");
            }

            if (!purchaseOrderLineNumber.HasValue || purchaseOrderLineNumber.Value <= 0)
            {
                throw new InvalidOperationException("PO-anchored bill lines require a positive purchase order line number.");
            }

            if (!quantity.HasValue || quantity.Value <= 0m)
            {
                throw new InvalidOperationException("PO-anchored bill lines require a positive quantity.");
            }
        }

        LineNumber = lineNumber;
        ExpenseAccountId = expenseAccountId;
        Description = description?.Trim() ?? string.Empty;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        IsTaxRecoverable = isTaxRecoverable;
        RecoverableTaxAccountId = recoverableTaxAccountId;
        TaxCodeId = taxCodeId;
        ItemId = itemId;
        WarehouseId = warehouseId;
        UomCode = string.IsNullOrWhiteSpace(uomCode) ? null : uomCode.Trim().ToUpperInvariant();
        Quantity = quantity;
        UnitCost = unitCost;
        PurchaseOrderId = purchaseOrderId;
        PurchaseOrderLineNumber = purchaseOrderLineNumber;
        TaxSnapshots = taxSnapshots ?? Array.Empty<DocumentLineTaxSnapshot>();
    }

    public int LineNumber { get; }

    public Guid ExpenseAccountId { get; }

    public string Description { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public bool IsTaxRecoverable { get; }

    public Guid? RecoverableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }

    public Guid? ItemId { get; }

    public Guid? WarehouseId { get; }

    public string? UomCode { get; }

    public decimal? Quantity { get; }

    public decimal? UnitCost { get; }

    public Guid? PurchaseOrderId { get; }

    public int? PurchaseOrderLineNumber { get; }

    public IReadOnlyList<DocumentLineTaxSnapshot> TaxSnapshots { get; }
}

public sealed class BillDocument : IPostingDocument, IOpenItemDocument
{
    public BillDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        DateOnly dueDate,
        Guid vendorId,
        Guid payableAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<BillDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Payable account id is required.", nameof(payableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        DueDate = dueDate;
        PartyId = vendorId;
        PayableAccountId = payableAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Bill document must contain at least one line.");
        }

        BillLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "bill";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public DateOnly? DueDate { get; }

    public Guid PartyId { get; }

    public Guid PayableAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<BillDocumentLine> BillLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public static class ReceiptDocumentStatuses
{
    public const string Draft = "draft";
    public const string Posted = "posted";

    public static string Normalize(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? Draft
            : status.Trim().ToLowerInvariant();

        return normalized switch
        {
            Draft => Draft,
            Posted => Posted,
            _ => throw new InvalidOperationException("Receipt documents only support draft or posted status in the current phase.")
        };
    }

    public static bool CanEdit(string status) =>
        string.Equals(Normalize(status), Draft, StringComparison.Ordinal);

    public static bool CanPost(string status) =>
        string.Equals(Normalize(status), Draft, StringComparison.Ordinal);
}

public sealed record ReceiptDocumentLine
{
    public ReceiptDocumentLine(
        int lineNumber,
        Guid itemId,
        decimal quantity,
        string uomCode,
        string? trackingCaptureHome = null,
        Guid? purchaseOrderId = null,
        int? purchaseOrderLineNumber = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Receipt quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(uomCode))
        {
            throw new ArgumentException("UOM code is required.", nameof(uomCode));
        }

        var hasPurchaseOrderAnchor = purchaseOrderId.HasValue || purchaseOrderLineNumber.HasValue;
        if (hasPurchaseOrderAnchor)
        {
            if (!purchaseOrderId.HasValue || purchaseOrderId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("PO-anchored receipt lines require a purchase order id.");
            }

            if (!purchaseOrderLineNumber.HasValue || purchaseOrderLineNumber.Value <= 0)
            {
                throw new InvalidOperationException("PO-anchored receipt lines require a positive purchase order line number.");
            }
        }

        LineNumber = lineNumber;
        ItemId = itemId;
        Quantity = quantity;
        UomCode = uomCode.Trim().ToUpperInvariant();
        TrackingCaptureHome = string.IsNullOrWhiteSpace(trackingCaptureHome) ? null : trackingCaptureHome.Trim();
        PurchaseOrderId = purchaseOrderId;
        PurchaseOrderLineNumber = purchaseOrderLineNumber;
    }

    public int LineNumber { get; }

    public Guid ItemId { get; }

    public decimal Quantity { get; }

    public string UomCode { get; }

    public string? TrackingCaptureHome { get; }

    public Guid? PurchaseOrderId { get; }

    public int? PurchaseOrderLineNumber { get; }
}

public static class PurchaseOrderDocumentStatuses
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Issued = "issued";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static string Normalize(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? Draft
            : status.Trim().ToLowerInvariant();

        return normalized switch
        {
            Draft => Draft,
            Approved => Approved,
            Issued => Issued,
            Closed => Closed,
            Cancelled => Cancelled,
            _ => throw new InvalidOperationException("Purchase orders only support draft, approved, issued, closed, or cancelled status in the current phase.")
        };
    }

    public static bool CanEdit(string status) =>
        string.Equals(Normalize(status), Draft, StringComparison.Ordinal);

    public static bool CanApprove(string status) =>
        string.Equals(Normalize(status), Draft, StringComparison.Ordinal);

    public static bool CanIssue(string status) =>
        string.Equals(Normalize(status), Approved, StringComparison.Ordinal);

    public static bool CanReopenForAmendment(string status)
    {
        var normalized = Normalize(status);
        return normalized is Approved or Issued;
    }

    public static bool CanClose(string status) =>
        string.Equals(Normalize(status), Issued, StringComparison.Ordinal);

    public static bool CanCancel(string status)
    {
        var normalized = Normalize(status);
        return normalized is Draft or Approved or Issued;
    }
}

public sealed record PurchaseOrderDocumentLine
{
    public PurchaseOrderDocumentLine(
        int lineNumber,
        Guid itemId,
        decimal orderedQuantity,
        string uomCode,
        string? description = null,
        decimal? unitCost = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }

        if (orderedQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(orderedQuantity), "PO ordered quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(uomCode))
        {
            throw new ArgumentException("UOM code is required.", nameof(uomCode));
        }

        if (unitCost.HasValue && unitCost.Value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCost), "PO unit cost cannot be negative.");
        }

        LineNumber = lineNumber;
        ItemId = itemId;
        OrderedQuantity = orderedQuantity;
        UomCode = uomCode.Trim().ToUpperInvariant();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UnitCost = unitCost;
    }

    public int LineNumber { get; }

    public Guid ItemId { get; }

    public decimal OrderedQuantity { get; }

    public string UomCode { get; }

    public string? Description { get; }

    public decimal? UnitCost { get; }
}

public sealed class PurchaseOrderDocument
{
    public PurchaseOrderDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid vendorId,
        DateOnly orderDate,
        IEnumerable<PurchaseOrderDocumentLine> lines,
        DateOnly? expectedDate = null,
        string? vendorReference = null,
        string? memo = null,
        DateTimeOffset? approvedAt = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? closedAt = null,
        DateTimeOffset? cancelledAt = null,
        DateTimeOffset? amendmentStartedAt = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = PurchaseOrderDocumentStatuses.Normalize(status);
        VendorId = vendorId;
        OrderDate = orderDate;
        ExpectedDate = expectedDate;
        VendorReference = string.IsNullOrWhiteSpace(vendorReference) ? null : vendorReference.Trim();
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        ApprovedAt = approvedAt;
        IssuedAt = issuedAt;
        ClosedAt = closedAt;
        CancelledAt = cancelledAt;
        AmendmentStartedAt = amendmentStartedAt;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Purchase order must contain at least one line.");
        }

        PurchaseOrderLines = Array.AsReadOnly(materializedLines);
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "purchase_order";

    public string Status { get; }

    public Guid VendorId { get; }

    public DateOnly OrderDate { get; }

    public DateOnly? ExpectedDate { get; }

    public string? VendorReference { get; }

    public string? Memo { get; }

    public DateTimeOffset? ApprovedAt { get; }

    public DateTimeOffset? IssuedAt { get; }

    public DateTimeOffset? ClosedAt { get; }

    public DateTimeOffset? CancelledAt { get; }

    public DateTimeOffset? AmendmentStartedAt { get; }

    public IReadOnlyList<PurchaseOrderDocumentLine> PurchaseOrderLines { get; }
}

public sealed class ReceiptDocument
{
    public ReceiptDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid vendorId,
        Guid warehouseId,
        DateOnly receiptDate,
        IEnumerable<ReceiptDocumentLine> lines,
        string? vendorReference = null,
        string? sourceReference = null,
        string? memo = null,
        DateTimeOffset? postedAt = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        if (warehouseId == Guid.Empty)
        {
            throw new ArgumentException("Warehouse id is required.", nameof(warehouseId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = ReceiptDocumentStatuses.Normalize(status);
        VendorId = vendorId;
        WarehouseId = warehouseId;
        ReceiptDate = receiptDate;
        VendorReference = string.IsNullOrWhiteSpace(vendorReference) ? null : vendorReference.Trim();
        SourceReference = string.IsNullOrWhiteSpace(sourceReference) ? null : sourceReference.Trim();
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        PostedAt = postedAt;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Receipt document must contain at least one line.");
        }

        ReceiptLines = Array.AsReadOnly(materializedLines);
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "receipt";

    public string Status { get; }

    public Guid VendorId { get; }

    public Guid WarehouseId { get; }

    public DateOnly ReceiptDate { get; }

    public string? VendorReference { get; }

    public string? SourceReference { get; }

    public string? Memo { get; }

    public DateTimeOffset? PostedAt { get; }

    public IReadOnlyList<ReceiptDocumentLine> ReceiptLines { get; }
}

public sealed record ReceiptGrIrPostingDocumentLine : IPostingDocumentLine
{
    public ReceiptGrIrPostingDocumentLine(
        int lineNumber,
        Guid bridgeLineId,
        Guid inventoryAssetAccountId,
        Guid grIrClearingAccountId,
        string description,
        decimal amountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (bridgeLineId == Guid.Empty)
        {
            throw new ArgumentException("Bridge line id is required.", nameof(bridgeLineId));
        }

        if (inventoryAssetAccountId == Guid.Empty)
        {
            throw new ArgumentException("Inventory asset account id is required.", nameof(inventoryAssetAccountId));
        }

        if (grIrClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("GR/IR clearing account id is required.", nameof(grIrClearingAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (amountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountBase), "GR/IR line amount must be positive.");
        }

        LineNumber = lineNumber;
        BridgeLineId = bridgeLineId;
        InventoryAssetAccountId = inventoryAssetAccountId;
        GrIrClearingAccountId = grIrClearingAccountId;
        Description = description.Trim();
        AmountBase = Math.Round(amountBase, 6, MidpointRounding.ToEven);
    }

    public int LineNumber { get; }

    public Guid BridgeLineId { get; }

    public Guid InventoryAssetAccountId { get; }

    public Guid GrIrClearingAccountId { get; }

    public string Description { get; }

    public decimal AmountBase { get; }
}

public sealed class ReceiptGrIrPostingDocument : IPostingDocument
{
    public ReceiptGrIrPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid receiptDocumentId,
        DateOnly documentDate,
        CurrencyCode baseCurrencyCode,
        Guid grIrClearingAccountId,
        IEnumerable<ReceiptGrIrPostingDocumentLine> lines)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        if (grIrClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("GR/IR clearing account id is required.", nameof(grIrClearingAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        ReceiptDocumentId = receiptDocumentId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;
        GrIrClearingAccountId = grIrClearingAccountId;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Receipt GR/IR posting document must contain at least one line.");
        }

        GrIrLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "receipt_grir_bridge_posting";

    public string Status { get; }

    public Guid ReceiptDocumentId { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public Guid GrIrClearingAccountId { get; }

    public IReadOnlyList<ReceiptGrIrPostingDocumentLine> GrIrLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    public decimal TotalAmountBase => GrIrLines.Sum(static line => line.AmountBase);
}

public sealed record ReceiptGrIrSettlementPostingDocumentLine : IPostingDocumentLine
{
    public ReceiptGrIrSettlementPostingDocumentLine(
        int lineNumber,
        Guid settlementLineId,
        Guid settlementBatchLineId,
        Guid grIrClearingAccountId,
        Guid billOffsetAccountId,
        Guid? ppvAccountId,
        string description,
        decimal grIrAmountBase,
        decimal billAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (settlementLineId == Guid.Empty)
        {
            throw new ArgumentException("Settlement line id is required.", nameof(settlementLineId));
        }

        if (settlementBatchLineId == Guid.Empty)
        {
            throw new ArgumentException("Settlement batch line id is required.", nameof(settlementBatchLineId));
        }

        if (grIrClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("GR/IR clearing account id is required.", nameof(grIrClearingAccountId));
        }

        if (billOffsetAccountId == Guid.Empty)
        {
            throw new ArgumentException("Bill offset account id is required.", nameof(billOffsetAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (grIrAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(grIrAmountBase), "GR/IR amount must be positive.");
        }

        if (billAmountBase < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(billAmountBase), "Bill-side amount must be non-negative.");
        }

        var rounded = Math.Round(billAmountBase - grIrAmountBase, 6, MidpointRounding.ToEven);
        if (Math.Abs(rounded) > 0m && ppvAccountId is null)
        {
            // Caller (settlement repo) must resolve the PPV account from the
            // CoA when it knows there is a non-zero variance to post. Refusing
            // to construct the line is safer than silently dropping the
            // variance and creating an unbalanced journal.
            throw new InvalidOperationException(
                "PPV account id is required when bill amount differs from GR/IR amount.");
        }

        LineNumber = lineNumber;
        SettlementLineId = settlementLineId;
        SettlementBatchLineId = settlementBatchLineId;
        GrIrClearingAccountId = grIrClearingAccountId;
        BillOffsetAccountId = billOffsetAccountId;
        PpvAccountId = ppvAccountId;
        Description = description.Trim();
        GrIrAmountBase = Math.Round(grIrAmountBase, 6, MidpointRounding.ToEven);
        BillAmountBase = Math.Round(billAmountBase, 6, MidpointRounding.ToEven);
    }

    public int LineNumber { get; }

    public Guid SettlementLineId { get; }

    public Guid SettlementBatchLineId { get; }

    public Guid GrIrClearingAccountId { get; }

    public Guid BillOffsetAccountId { get; }

    /// <summary>
    /// Purchase Price Variance account id. Required when
    /// <see cref="BillAmountBase"/> differs from <see cref="GrIrAmountBase"/>;
    /// optional (null) when the two match exactly. Resolved by the settlement
    /// repository from the Chart of Accounts via SystemRole
    /// <c>purchase_price_variance</c>.
    /// </summary>
    public Guid? PpvAccountId { get; }

    public string Description { get; }

    /// <summary>
    /// GR/IR-side amount: equals what the receipt-side GR/IR posting parked on
    /// the clearing account for this slice. The settlement journal debits the
    /// GR/IR clearing for this amount to bring it back to zero.
    /// </summary>
    public decimal GrIrAmountBase { get; }

    /// <summary>
    /// Bill-side amount: the proportional vendor invoice amount for this
    /// settled quantity (settled_qty / bill_line_qty × bill_line_amount × fx).
    /// The settlement journal credits the bill's expense account for this
    /// amount to fully reverse the bill's standalone Dr Expense line. Zero
    /// when the bill line quantity basis is missing — in that case caller
    /// passes <see cref="GrIrAmountBase"/> here so the journal still balances
    /// (no variance booked, refresh in workbench later).
    /// </summary>
    public decimal BillAmountBase { get; }

    /// <summary>
    /// Signed PPV: positive = unfavorable (bill cost more than expected),
    /// negative = favorable (bill cost less). Computed once at construction
    /// from <see cref="BillAmountBase"/> minus <see cref="GrIrAmountBase"/>.
    /// </summary>
    public decimal VarianceAmountBase => BillAmountBase - GrIrAmountBase;
}

public sealed class ReceiptGrIrSettlementPostingDocument : IPostingDocument
{
    public ReceiptGrIrSettlementPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid receiptDocumentId,
        Guid settlementBatchId,
        DateOnly documentDate,
        CurrencyCode baseCurrencyCode,
        IEnumerable<ReceiptGrIrSettlementPostingDocumentLine> lines)
    {
        if (receiptDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Receipt document id is required.", nameof(receiptDocumentId));
        }

        if (settlementBatchId == Guid.Empty)
        {
            throw new ArgumentException("Settlement batch id is required.", nameof(settlementBatchId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        ReceiptDocumentId = receiptDocumentId;
        SettlementBatchId = settlementBatchId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Receipt GR/IR settlement posting document must contain at least one line.");
        }

        SettlementLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "receipt_grir_ap_settlement_posting";

    public string Status { get; }

    public Guid ReceiptDocumentId { get; }

    public Guid SettlementBatchId { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public IReadOnlyList<ReceiptGrIrSettlementPostingDocumentLine> SettlementLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    public decimal TotalAmountBase => SettlementLines.Sum(static line => line.BillAmountBase);
}

public sealed record VendorCreditDocumentLine : IPostingDocumentLine
{
    public VendorCreditDocumentLine(
        int lineNumber,
        Guid expenseAccountId,
        string description,
        decimal lineAmount,
        decimal taxAmount,
        bool isTaxRecoverable,
        Guid? recoverableTaxAccountId,
        Guid? taxCodeId = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (expenseAccountId == Guid.Empty)
        {
            throw new ArgumentException("Expense account id is required.", nameof(expenseAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (lineAmount <= 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(lineAmount), "Vendor credit line amounts must be positive and tax cannot be negative.");
        }

        if (taxAmount > 0m && isTaxRecoverable && recoverableTaxAccountId is null)
        {
            throw new InvalidOperationException("Recoverable tax vendor credit lines must resolve to a recoverable tax account.");
        }

        LineNumber = lineNumber;
        ExpenseAccountId = expenseAccountId;
        Description = description.Trim();
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        IsTaxRecoverable = isTaxRecoverable;
        RecoverableTaxAccountId = recoverableTaxAccountId;
        TaxCodeId = taxCodeId;
    }

    public int LineNumber { get; }

    public Guid ExpenseAccountId { get; }

    public string Description { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public bool IsTaxRecoverable { get; }

    public Guid? RecoverableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }
}

public sealed class VendorCreditDocument : IPostingDocument, IOpenItemDocument
{
    public VendorCreditDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        DateOnly dueDate,
        Guid vendorId,
        Guid payableAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<VendorCreditDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Payable account id is required.", nameof(payableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        DueDate = dueDate;
        PartyId = vendorId;
        PayableAccountId = payableAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Vendor credit document must contain at least one line.");
        }

        VendorCreditLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "vendor_credit";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public DateOnly? DueDate { get; }

    public Guid PartyId { get; }

    public Guid PayableAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<VendorCreditDocumentLine> VendorCreditLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public sealed record CreditApplicationDocumentLine : IPostingDocumentLine
{
    public CreditApplicationDocumentLine(
        int lineNumber,
        Guid sourceCreditArOpenItemId,
        Guid targetInvoiceArOpenItemId,
        string description,
        decimal appliedAmount,
        decimal sourceCarryingAmountBase,
        decimal targetCarryingAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (sourceCreditArOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Source credit AR open item id is required.", nameof(sourceCreditArOpenItemId));
        }

        if (targetInvoiceArOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target invoice AR open item id is required.", nameof(targetInvoiceArOpenItemId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (appliedAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Applied amount must be greater than zero.");
        }

        if (sourceCarryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceCarryingAmountBase), "Source carrying amount must be greater than zero.");
        }

        if (targetCarryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCarryingAmountBase), "Target carrying amount must be greater than zero.");
        }

        LineNumber = lineNumber;
        SourceCreditArOpenItemId = sourceCreditArOpenItemId;
        TargetInvoiceArOpenItemId = targetInvoiceArOpenItemId;
        Description = description.Trim();
        AppliedAmount = appliedAmount;
        SourceCarryingAmountBase = sourceCarryingAmountBase;
        TargetCarryingAmountBase = targetCarryingAmountBase;
    }

    public int LineNumber { get; }

    public Guid SourceCreditArOpenItemId { get; }

    public Guid TargetInvoiceArOpenItemId { get; }

    public string Description { get; }

    public decimal AppliedAmount { get; }

    public decimal SourceCarryingAmountBase { get; }

    public decimal TargetCarryingAmountBase { get; }

    public decimal RealizedFxAmountBase =>
        Math.Round(SourceCarryingAmountBase - TargetCarryingAmountBase, 6, MidpointRounding.ToEven);
}

public sealed class CreditApplicationDocument : IPostingDocument
{
    public CreditApplicationDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        Guid customerId,
        Guid receivableAccountId,
        Guid? realizedFxGainAccountId,
        Guid? realizedFxLossAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<CreditApplicationDocumentLine> lines,
        decimal totalAmount,
        string? memo = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Receivable account id is required.", nameof(receivableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        PartyId = customerId;
        ReceivableAccountId = receivableAccountId;
        RealizedFxGainAccountId = NormalizeOptionalAccountId(realizedFxGainAccountId, nameof(realizedFxGainAccountId));
        RealizedFxLossAccountId = NormalizeOptionalAccountId(realizedFxLossAccountId, nameof(realizedFxLossAccountId));
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Credit application document must contain at least one line.");
        }

        ApplicationLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "credit_application";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid PartyId { get; }

    public Guid ReceivableAccountId { get; }

    public Guid? RealizedFxGainAccountId { get; }

    public Guid? RealizedFxLossAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<CreditApplicationDocumentLine> ApplicationLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static Guid? NormalizeOptionalAccountId(Guid? accountId, string paramName)
    {
        if (!accountId.HasValue)
        {
            return null;
        }

        if (accountId.Value == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty when supplied.", paramName);
        }

        return accountId.Value;
    }
}

public sealed record VendorCreditApplicationDocumentLine : IPostingDocumentLine
{
    public VendorCreditApplicationDocumentLine(
        int lineNumber,
        Guid sourceVendorCreditApOpenItemId,
        Guid targetBillApOpenItemId,
        string description,
        decimal appliedAmount,
        decimal sourceCarryingAmountBase,
        decimal targetCarryingAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (sourceVendorCreditApOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Source vendor credit AP open item id is required.", nameof(sourceVendorCreditApOpenItemId));
        }

        if (targetBillApOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target bill AP open item id is required.", nameof(targetBillApOpenItemId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (appliedAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Applied amount must be greater than zero.");
        }

        if (sourceCarryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceCarryingAmountBase), "Source carrying amount must be greater than zero.");
        }

        if (targetCarryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCarryingAmountBase), "Target carrying amount must be greater than zero.");
        }

        LineNumber = lineNumber;
        SourceVendorCreditApOpenItemId = sourceVendorCreditApOpenItemId;
        TargetBillApOpenItemId = targetBillApOpenItemId;
        Description = description.Trim();
        AppliedAmount = appliedAmount;
        SourceCarryingAmountBase = sourceCarryingAmountBase;
        TargetCarryingAmountBase = targetCarryingAmountBase;
    }

    public int LineNumber { get; }

    public Guid SourceVendorCreditApOpenItemId { get; }

    public Guid TargetBillApOpenItemId { get; }

    public string Description { get; }

    public decimal AppliedAmount { get; }

    public decimal SourceCarryingAmountBase { get; }

    public decimal TargetCarryingAmountBase { get; }

    public decimal RealizedFxAmountBase =>
        Math.Round(SourceCarryingAmountBase - TargetCarryingAmountBase, 6, MidpointRounding.ToEven);
}

public sealed class VendorCreditApplicationDocument : IPostingDocument
{
    public VendorCreditApplicationDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        Guid vendorId,
        Guid payableAccountId,
        Guid? realizedFxGainAccountId,
        Guid? realizedFxLossAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<VendorCreditApplicationDocumentLine> lines,
        decimal totalAmount,
        string? memo = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Payable account id is required.", nameof(payableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        PartyId = vendorId;
        PayableAccountId = payableAccountId;
        RealizedFxGainAccountId = NormalizeOptionalAccountId(realizedFxGainAccountId, nameof(realizedFxGainAccountId));
        RealizedFxLossAccountId = NormalizeOptionalAccountId(realizedFxLossAccountId, nameof(realizedFxLossAccountId));
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Vendor credit application document must contain at least one line.");
        }

        ApplicationLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "vendor_credit_application";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid PartyId { get; }

    public Guid PayableAccountId { get; }

    public Guid? RealizedFxGainAccountId { get; }

    public Guid? RealizedFxLossAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<VendorCreditApplicationDocumentLine> ApplicationLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static Guid? NormalizeOptionalAccountId(Guid? accountId, string paramName)
    {
        if (!accountId.HasValue)
        {
            return null;
        }

        if (accountId.Value == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty when supplied.", paramName);
        }

        return accountId.Value;
    }
}

public sealed record ReceivePaymentDocumentLine : IPostingDocumentLine
{
    public ReceivePaymentDocumentLine(
        int lineNumber,
        Guid targetArOpenItemId,
        string description,
        decimal appliedAmount,
        decimal appliedAmountBase,
        decimal carryingAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (targetArOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target AR open item id is required.", nameof(targetArOpenItemId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (appliedAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Applied amount must be greater than zero.");
        }

        if (appliedAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmountBase), "Applied base amount must be greater than zero.");
        }

        if (carryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(carryingAmountBase), "Carrying base amount must be greater than zero.");
        }

        LineNumber = lineNumber;
        TargetArOpenItemId = targetArOpenItemId;
        Description = description.Trim();
        AppliedAmount = appliedAmount;
        AppliedAmountBase = appliedAmountBase;
        CarryingAmountBase = carryingAmountBase;
    }

    public int LineNumber { get; }

    public Guid TargetArOpenItemId { get; }

    public string Description { get; }

    public decimal AppliedAmount { get; }

    public decimal AppliedAmountBase { get; }

    public decimal CarryingAmountBase { get; }
}

public sealed class ReceivePaymentDocument : IPostingDocument, ISettlementDocument
{
    public ReceivePaymentDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        Guid customerId,
        Guid bankAccountId,
        Guid receivableAccountId,
        Guid? realizedFxGainAccountId,
        Guid? realizedFxLossAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<ReceivePaymentDocumentLine> lines,
        decimal totalAmount,
        string? memo = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (bankAccountId == Guid.Empty)
        {
            throw new ArgumentException("Bank account id is required.", nameof(bankAccountId));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Receivable account id is required.", nameof(receivableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        PartyId = customerId;
        BankAccountId = bankAccountId;
        ReceivableAccountId = receivableAccountId;
        RealizedFxGainAccountId = NormalizeOptionalAccountId(realizedFxGainAccountId, nameof(realizedFxGainAccountId));
        RealizedFxLossAccountId = NormalizeOptionalAccountId(realizedFxLossAccountId, nameof(realizedFxLossAccountId));
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Receive payment document must contain at least one line.");
        }

        PaymentLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "receive_payment";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid PartyId { get; }

    public Guid BankAccountId { get; }

    public Guid ReceivableAccountId { get; }

    public Guid? RealizedFxGainAccountId { get; }

    public Guid? RealizedFxLossAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<ReceivePaymentDocumentLine> PaymentLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static Guid? NormalizeOptionalAccountId(Guid? accountId, string paramName)
    {
        if (!accountId.HasValue)
        {
            return null;
        }

        if (accountId.Value == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty when supplied.", paramName);
        }

        return accountId.Value;
    }
}

public sealed record PayBillDocumentLine : IPostingDocumentLine
{
    public PayBillDocumentLine(
        int lineNumber,
        Guid targetApOpenItemId,
        string description,
        decimal appliedAmount,
        decimal appliedAmountBase,
        decimal carryingAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (targetApOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target AP open item id is required.", nameof(targetApOpenItemId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (appliedAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Applied amount must be greater than zero.");
        }

        if (appliedAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmountBase), "Applied base amount must be greater than zero.");
        }

        if (carryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(carryingAmountBase), "Carrying base amount must be greater than zero.");
        }

        LineNumber = lineNumber;
        TargetApOpenItemId = targetApOpenItemId;
        Description = description.Trim();
        AppliedAmount = appliedAmount;
        AppliedAmountBase = appliedAmountBase;
        CarryingAmountBase = carryingAmountBase;
    }

    public int LineNumber { get; }

    public Guid TargetApOpenItemId { get; }

    public string Description { get; }

    public decimal AppliedAmount { get; }

    public decimal AppliedAmountBase { get; }

    public decimal CarryingAmountBase { get; }
}

public sealed class PayBillDocument : IPostingDocument, ISettlementDocument
{
    public PayBillDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly documentDate,
        Guid vendorId,
        Guid bankAccountId,
        Guid payableAccountId,
        Guid? realizedFxGainAccountId,
        Guid? realizedFxLossAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<PayBillDocumentLine> lines,
        decimal totalAmount,
        string? memo = null)
    {
        if (vendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(vendorId));
        }

        if (bankAccountId == Guid.Empty)
        {
            throw new ArgumentException("Bank account id is required.", nameof(bankAccountId));
        }

        if (payableAccountId == Guid.Empty)
        {
            throw new ArgumentException("Payable account id is required.", nameof(payableAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        PartyId = vendorId;
        BankAccountId = bankAccountId;
        PayableAccountId = payableAccountId;
        RealizedFxGainAccountId = NormalizeOptionalAccountId(realizedFxGainAccountId, nameof(realizedFxGainAccountId));
        RealizedFxLossAccountId = NormalizeOptionalAccountId(realizedFxLossAccountId, nameof(realizedFxLossAccountId));
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Pay bill document must contain at least one line.");
        }

        PaymentLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "pay_bill";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid PartyId { get; }

    public Guid BankAccountId { get; }

    public Guid PayableAccountId { get; }

    public Guid? RealizedFxGainAccountId { get; }

    public Guid? RealizedFxLossAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    public IReadOnlyList<PayBillDocumentLine> PaymentLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static Guid? NormalizeOptionalAccountId(Guid? accountId, string paramName)
    {
        if (!accountId.HasValue)
        {
            return null;
        }

        if (accountId.Value == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty when supplied.", paramName);
        }

        return accountId.Value;
    }
}

public sealed record OpenItemAdjustmentDocumentLine : IPostingDocumentLine
{
    public OpenItemAdjustmentDocumentLine(
        int lineNumber,
        Guid targetOpenItemId,
        string targetOpenItemType,
        string targetBalanceSide,
        Guid controlAccountId,
        Guid offsetAccountId,
        Guid partyId,
        string description,
        decimal adjustmentAmountTx,
        decimal adjustmentAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (targetOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target open item id is required.", nameof(targetOpenItemId));
        }

        var normalizedOpenItemType = string.IsNullOrWhiteSpace(targetOpenItemType)
            ? throw new ArgumentException("Target open item type is required.", nameof(targetOpenItemType))
            : targetOpenItemType.Trim().ToLowerInvariant();
        if (normalizedOpenItemType is not ("ar_open_item" or "ap_open_item"))
        {
            throw new InvalidOperationException("Open item adjustment target type is not supported.");
        }

        var normalizedBalanceSide = string.IsNullOrWhiteSpace(targetBalanceSide)
            ? throw new ArgumentException("Target balance side is required.", nameof(targetBalanceSide))
            : targetBalanceSide.Trim().ToLowerInvariant();
        if (normalizedBalanceSide is not ("debit" or "credit"))
        {
            throw new InvalidOperationException("Open item adjustment balance side is not supported.");
        }

        if (controlAccountId == Guid.Empty)
        {
            throw new ArgumentException("Control account id is required.", nameof(controlAccountId));
        }

        if (offsetAccountId == Guid.Empty)
        {
            throw new ArgumentException("Offset account id is required.", nameof(offsetAccountId));
        }

        if (partyId == Guid.Empty)
        {
            throw new ArgumentException("Party id is required.", nameof(partyId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (adjustmentAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustmentAmountTx), "Adjustment transaction amount must be greater than zero.");
        }

        if (adjustmentAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustmentAmountBase), "Adjustment base amount must be greater than zero.");
        }

        LineNumber = lineNumber;
        TargetOpenItemId = targetOpenItemId;
        TargetOpenItemType = normalizedOpenItemType;
        TargetBalanceSide = normalizedBalanceSide;
        ControlAccountId = controlAccountId;
        OffsetAccountId = offsetAccountId;
        PartyId = partyId;
        Description = description.Trim();
        AdjustmentAmountTx = adjustmentAmountTx;
        AdjustmentAmountBase = adjustmentAmountBase;
    }

    public int LineNumber { get; }

    public Guid TargetOpenItemId { get; }

    public string TargetOpenItemType { get; }

    public string TargetBalanceSide { get; }

    public Guid ControlAccountId { get; }

    public Guid OffsetAccountId { get; }

    public Guid PartyId { get; }

    public string Description { get; }

    public decimal AdjustmentAmountTx { get; }

    public decimal AdjustmentAmountBase { get; }

    public string ControlRole => TargetOpenItemType == "ar_open_item" ? "accounts_receivable" : "accounts_payable";

    public bool ReducesDebitBalance => TargetBalanceSide == "debit";
}

public sealed class OpenItemAdjustmentDocument : IPostingDocument
{
    public OpenItemAdjustmentDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string sourceType,
        string status,
        DateOnly documentDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        string adjustmentType,
        IEnumerable<OpenItemAdjustmentDocumentLine> lines,
        string? memo = null)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        SourceType = NormalizeSourceType(sourceType);
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = documentDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        AdjustmentType = NormalizeAdjustmentType(adjustmentType);
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Open item adjustment document must contain at least one line.");
        }

        AdjustmentLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType { get; }

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public string AdjustmentType { get; }

    public string? Memo { get; }

    public IReadOnlyList<OpenItemAdjustmentDocumentLine> AdjustmentLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static string NormalizeSourceType(string sourceType)
    {
        var normalized = string.IsNullOrWhiteSpace(sourceType)
            ? throw new ArgumentException("Source type is required.", nameof(sourceType))
            : sourceType.Trim().ToLowerInvariant();

        return normalized is "ar_open_item_adjustment" or "ap_open_item_adjustment"
            ? normalized
            : throw new InvalidOperationException("Open item adjustment source type is not supported.");
    }

    private static string NormalizeAdjustmentType(string adjustmentType)
    {
        var normalized = string.IsNullOrWhiteSpace(adjustmentType)
            ? throw new ArgumentException("Adjustment type is required.", nameof(adjustmentType))
            : adjustmentType.Trim().ToLowerInvariant();

        return normalized is "write_off" or "small_balance_adjustment"
            ? normalized
            : throw new InvalidOperationException("Open item adjustment type is not supported.");
    }
}

public sealed record FxRevaluationDocumentLine : IPostingDocumentLine
{
    public FxRevaluationDocumentLine(
        int lineNumber,
        string targetOpenItemType,
        Guid targetOpenItemId,
        string targetBalanceSide,
        Guid targetControlAccountId,
        Guid offsetAccountId,
        Guid partyId,
        string description,
        decimal openAmountTx,
        decimal carryingAmountBase,
        decimal revaluedAmountBase,
        decimal unrealizedAmountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (string.IsNullOrWhiteSpace(targetOpenItemType))
        {
            throw new ArgumentException("Target open item type is required.", nameof(targetOpenItemType));
        }

        if (targetOpenItemId == Guid.Empty)
        {
            throw new ArgumentException("Target open item id is required.", nameof(targetOpenItemId));
        }

        if (targetControlAccountId == Guid.Empty)
        {
            throw new ArgumentException("Target control account id is required.", nameof(targetControlAccountId));
        }

        if (string.IsNullOrWhiteSpace(targetBalanceSide))
        {
            throw new ArgumentException("Target balance side is required.", nameof(targetBalanceSide));
        }

        if (offsetAccountId == Guid.Empty)
        {
            throw new ArgumentException("Offset account id is required.", nameof(offsetAccountId));
        }

        if (partyId == Guid.Empty)
        {
            throw new ArgumentException("Party id is required.", nameof(partyId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (openAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openAmountTx), "Open amount must be greater than zero.");
        }

        if (carryingAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(carryingAmountBase), "Carrying amount must be greater than zero.");
        }

        if (revaluedAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(revaluedAmountBase), "Revalued amount must be greater than zero.");
        }

        if (unrealizedAmountBase == 0m)
        {
            throw new InvalidOperationException("Revaluation line must produce a non-zero unrealized FX delta.");
        }

        if (Math.Round(revaluedAmountBase - carryingAmountBase, 2, MidpointRounding.ToEven) !=
            Math.Round(unrealizedAmountBase, 2, MidpointRounding.ToEven))
        {
            throw new InvalidOperationException("Revaluation line delta does not reconcile to its carrying and revalued amounts.");
        }

        var normalizedTargetType = targetOpenItemType.Trim().ToLowerInvariant();
        if (normalizedTargetType is not ("ar_open_item" or "ap_open_item"))
        {
            throw new InvalidOperationException("Revaluation line target open item type is not supported.");
        }

        var normalizedBalanceSide = targetBalanceSide.Trim().ToLowerInvariant();
        if (normalizedBalanceSide is not ("debit" or "credit"))
        {
            throw new InvalidOperationException("Revaluation line target balance side is not supported.");
        }

        LineNumber = lineNumber;
        TargetOpenItemType = normalizedTargetType;
        TargetOpenItemId = targetOpenItemId;
        TargetBalanceSide = normalizedBalanceSide;
        TargetControlAccountId = targetControlAccountId;
        OffsetAccountId = offsetAccountId;
        PartyId = partyId;
        Description = description.Trim();
        OpenAmountTx = openAmountTx;
        CarryingAmountBase = carryingAmountBase;
        RevaluedAmountBase = revaluedAmountBase;
        UnrealizedAmountBase = unrealizedAmountBase;
    }

    public int LineNumber { get; }

    public string TargetOpenItemType { get; }

    public Guid TargetOpenItemId { get; }

    public string TargetBalanceSide { get; }

    public Guid TargetControlAccountId { get; }

    public Guid OffsetAccountId { get; }

    public Guid PartyId { get; }

    public string Description { get; }

    public decimal OpenAmountTx { get; }

    public decimal CarryingAmountBase { get; }

    public decimal RevaluedAmountBase { get; }

    public decimal UnrealizedAmountBase { get; }

    public string ControlRole => TargetOpenItemType == "ar_open_item" ? "accounts_receivable" : "accounts_payable";

    public bool IsDebitBalance => TargetBalanceSide == "debit";
}

public sealed class FxRevaluationDocument : IPostingDocument
{
    public FxRevaluationDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly revaluationDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef fxSnapshot,
        Guid unrealizedFxGainAccountId,
        Guid unrealizedFxLossAccountId,
        IEnumerable<FxRevaluationDocumentLine> lines,
        string? memo = null,
        string? batchKind = null,
        Guid? reversalOfDocumentId = null,
        Guid? bookId = null,
        string? bookCode = null,
        string? accountingStandard = null,
        string? revaluationProfile = null,
        string? fxRoundingPolicy = null)
    {
        if (fxSnapshot is null)
        {
            throw new ArgumentNullException(nameof(fxSnapshot));
        }

        if (transactionCurrencyCode == baseCurrencyCode)
        {
            throw new InvalidOperationException("FX revaluation document must target a foreign currency.");
        }

        if (unrealizedFxGainAccountId == Guid.Empty)
        {
            throw new ArgumentException("Unrealized FX gain account id is required.", nameof(unrealizedFxGainAccountId));
        }

        if (unrealizedFxLossAccountId == Guid.Empty)
        {
            throw new ArgumentException("Unrealized FX loss account id is required.", nameof(unrealizedFxLossAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        BatchKind = NormalizeBatchKind(batchKind);
        ReversalOfDocumentId = reversalOfDocumentId;
        DocumentDate = revaluationDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        UnrealizedFxGainAccountId = unrealizedFxGainAccountId;
        UnrealizedFxLossAccountId = unrealizedFxLossAccountId;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        BookId = bookId;
        BookCode = string.IsNullOrWhiteSpace(bookCode) ? null : bookCode.Trim();
        AccountingStandard = string.IsNullOrWhiteSpace(accountingStandard) ? null : accountingStandard.Trim();
        RevaluationProfile = string.IsNullOrWhiteSpace(revaluationProfile) ? null : revaluationProfile.Trim();
        FxRoundingPolicy = string.IsNullOrWhiteSpace(fxRoundingPolicy) ? null : fxRoundingPolicy.Trim();

        if (BatchKind == "revaluation" && ReversalOfDocumentId.HasValue)
        {
            throw new InvalidOperationException("Base FX revaluation documents cannot point at a reversal source batch.");
        }

        if (BatchKind == "next_period_unwind" && !ReversalOfDocumentId.HasValue)
        {
            throw new InvalidOperationException("Next-period unwind documents must reference the posted FX revaluation batch they reverse.");
        }

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("FX revaluation document must contain at least one line.");
        }

        RevaluationLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "fx_revaluation";

    public string Status { get; }

    public string BatchKind { get; }

    public Guid? ReversalOfDocumentId { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef FxSnapshot { get; }

    public Guid? BookId { get; }

    public string? BookCode { get; }

    public string? AccountingStandard { get; }

    public string? RevaluationProfile { get; }

    public string? FxRoundingPolicy { get; }

    public Guid UnrealizedFxGainAccountId { get; }

    public Guid UnrealizedFxLossAccountId { get; }

    public string? Memo { get; }

    public IReadOnlyList<FxRevaluationDocumentLine> RevaluationLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    private static string NormalizeBatchKind(string? batchKind)
    {
        var normalized = string.IsNullOrWhiteSpace(batchKind)
            ? "revaluation"
            : batchKind.Trim().ToLowerInvariant();

        return normalized is "revaluation" or "next_period_unwind"
            ? normalized
            : throw new InvalidOperationException("FX revaluation document batch kind is not supported.");
    }
}

// ========================================================================
// Sales Receipt — cash-in-hand sale.
//
// Posts a single journal in one shot:
//   Dr DepositToAccountId      = TotalAmount      (cash flowing in)
//   Cr line.RevenueAccountId   = LineAmount       (per line)
//   Cr line.PayableTaxAccountId = TaxAmount       (per tax-bearing line)
//
// Differs from InvoiceDocument in two structural ways:
//   1. No IOpenItemDocument — cash settled at point of sale; no AR row.
//   2. No DueDate — customer doesn't owe anything afterward.
// Lines reuse the same shape as InvoiceDocumentLine (item + revenue
// account + tax) because the operator UX is identical to invoice line
// entry; only the GL polarity at post time differs.
// ========================================================================
public sealed record SalesReceiptDocumentLine : IPostingDocumentLine
{
    public SalesReceiptDocumentLine(
        int lineNumber,
        Guid revenueAccountId,
        string description,
        decimal quantity,
        decimal unitPrice,
        decimal lineAmount,
        decimal taxAmount,
        Guid? payableTaxAccountId,
        Guid? taxCodeId = null,
        Guid? itemId = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (revenueAccountId == Guid.Empty)
        {
            throw new ArgumentException("Revenue account id is required.", nameof(revenueAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (unitPrice < 0m || lineAmount < 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Sales receipt amounts cannot be negative.");
        }

        if (taxAmount > 0m && payableTaxAccountId is null)
        {
            throw new InvalidOperationException("Tax-bearing sales receipt lines must resolve to a payable tax account.");
        }

        LineNumber = lineNumber;
        RevenueAccountId = revenueAccountId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        PayableTaxAccountId = payableTaxAccountId;
        TaxCodeId = taxCodeId;
        ItemId = itemId;
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }

    public Guid? ItemId { get; }
}

public sealed class SalesReceiptDocument : IPostingDocument
{
    public SalesReceiptDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly receiptDate,
        Guid customerId,
        Guid depositToAccountId,
        string paymentMethod,
        string? referenceNo,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<SalesReceiptDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null,
        string? customerPoNumber = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (depositToAccountId == Guid.Empty)
        {
            throw new ArgumentException("Deposit-to account id is required.", nameof(depositToAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = receiptDate;
        CustomerId = customerId;
        DepositToAccountId = depositToAccountId;
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "cash" : paymentMethod.Trim().ToLowerInvariant();
        ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        CustomerPoNumber = string.IsNullOrWhiteSpace(customerPoNumber) ? null : customerPoNumber.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Sales receipt document must contain at least one line.");
        }

        ReceiptLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "sales_receipt";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid CustomerId { get; }

    public Guid DepositToAccountId { get; }

    public string PaymentMethod { get; }

    public string? ReferenceNo { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    /// <summary>Customer's own purchase-order reference (their procurement / AP system's ref). Optional.</summary>
    public string? CustomerPoNumber { get; }

    public IReadOnlyList<SalesReceiptDocumentLine> ReceiptLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

// ========================================================================
// Refund Receipt — cash-out customer refund.
//
// Polarity flip of SalesReceiptDocument:
//   Cr RefundFromAccountId      = TotalAmount      (cash flowing out)
//   Dr line.RevenueAccountId    = LineAmount       (per line)
//   Dr line.PayableTaxAccountId = TaxAmount        (per tax-bearing line)
//
// No IOpenItemDocument (cash refund — no AR open item is created or
// touched). Same line shape as SalesReceiptDocumentLine; the only
// header-level addition is the Reason field, which travels via memo
// at persistence time.
// ========================================================================
public sealed record RefundReceiptDocumentLine : IPostingDocumentLine
{
    public RefundReceiptDocumentLine(
        int lineNumber,
        Guid revenueAccountId,
        string description,
        decimal quantity,
        decimal unitPrice,
        decimal lineAmount,
        decimal taxAmount,
        Guid? payableTaxAccountId,
        Guid? taxCodeId = null,
        Guid? itemId = null)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }

        if (revenueAccountId == Guid.Empty)
        {
            throw new ArgumentException("Revenue account id is required.", nameof(revenueAccountId));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (unitPrice < 0m || lineAmount < 0m || taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Refund receipt amounts cannot be negative.");
        }

        if (taxAmount > 0m && payableTaxAccountId is null)
        {
            throw new InvalidOperationException("Tax-bearing refund receipt lines must resolve to a payable tax account.");
        }

        LineNumber = lineNumber;
        RevenueAccountId = revenueAccountId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        PayableTaxAccountId = payableTaxAccountId;
        TaxCodeId = taxCodeId;
        ItemId = itemId;
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }

    public Guid? TaxCodeId { get; }

    public Guid? ItemId { get; }
}

public sealed class RefundReceiptDocument : IPostingDocument
{
    public RefundReceiptDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly refundDate,
        Guid customerId,
        Guid refundFromAccountId,
        string paymentMethod,
        string? referenceNo,
        string? reason,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        IEnumerable<RefundReceiptDocumentLine> lines,
        decimal subtotalAmount,
        decimal taxAmount,
        decimal totalAmount,
        string? memo = null,
        string? customerPoNumber = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (refundFromAccountId == Guid.Empty)
        {
            throw new ArgumentException("Refund-from account id is required.", nameof(refundFromAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = refundDate;
        CustomerId = customerId;
        RefundFromAccountId = refundFromAccountId;
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "cash" : paymentMethod.Trim().ToLowerInvariant();
        ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        SubtotalAmount = subtotalAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        CustomerPoNumber = string.IsNullOrWhiteSpace(customerPoNumber) ? null : customerPoNumber.Trim();

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Refund receipt document must contain at least one line.");
        }

        ReceiptLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "refund_receipt";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid CustomerId { get; }

    public Guid RefundFromAccountId { get; }

    public string PaymentMethod { get; }

    public string? ReferenceNo { get; }

    public string? Reason { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public decimal SubtotalAmount { get; }

    public decimal TaxAmount { get; }

    public decimal TotalAmount { get; }

    public string? Memo { get; }

    /// <summary>Customer's own purchase-order reference. Optional.</summary>
    public string? CustomerPoNumber { get; }

    public IReadOnlyList<RefundReceiptDocumentLine> ReceiptLines { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

// ========================================================================
// Bank Transfer — internal asset → asset movement.
//
// Two-fragment journal:
//   Cr FromAccountId  = Amount             (in from-account currency)
//   Dr ToAccountId    = Amount * FxRate    (in to-account currency)
//
// Same-currency transfers carry FxRate = null and Amount lands on
// both sides identically. Cross-currency transfers carry FxRate > 0;
// the engine still uses each account's snapshot rate for the
// base-currency rows of the JE so the audit trail uses
// fx_rates_daily-grade rates rather than the bank's rate. The
// operator's per-document FxRate only sets the destination
// transaction-currency amount.
//
// No lines, no IOpenItemDocument, no party. The single virtual
// "transfer line" is just the [from, to, amount] tuple on the
// document header.
// ========================================================================
public sealed class BankTransferDocument : IPostingDocument
{
    public BankTransferDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly transferDate,
        Guid fromAccountId,
        CurrencyCode fromCurrencyCode,
        Guid toAccountId,
        CurrencyCode toCurrencyCode,
        decimal amount,
        decimal? fxRate,
        FxSnapshotRef? fxSnapshot,
        string? referenceNo,
        string? memo)
    {
        if (fromAccountId == Guid.Empty)
        {
            throw new ArgumentException("From-account id is required.", nameof(fromAccountId));
        }
        if (toAccountId == Guid.Empty)
        {
            throw new ArgumentException("To-account id is required.", nameof(toAccountId));
        }
        if (fromAccountId == toAccountId)
        {
            throw new InvalidOperationException("Bank transfer source and destination accounts must differ.");
        }
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        var sameCurrency = fromCurrencyCode == toCurrencyCode;
        if (sameCurrency && fxRate is not null)
        {
            throw new InvalidOperationException("Same-currency transfers must carry no FX rate.");
        }
        if (!sameCurrency && (fxRate is null || fxRate.Value <= 0m))
        {
            throw new InvalidOperationException("Cross-currency transfers must carry a positive FX rate.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = transferDate;
        FromAccountId = fromAccountId;
        FromCurrencyCode = fromCurrencyCode ?? throw new ArgumentNullException(nameof(fromCurrencyCode));
        ToAccountId = toAccountId;
        ToCurrencyCode = toCurrencyCode ?? throw new ArgumentNullException(nameof(toCurrencyCode));
        Amount = amount;
        FxRate = fxRate;
        FxSnapshot = fxSnapshot;
        // Transfer's "transaction currency" is the from-side; the
        // PostingEngine's per-row FX is what handles the multi-
        // currency wiring downstream.
        TransactionCurrencyCode = fromCurrencyCode;
        // Base currency comes from the FxSnapshot when present,
        // otherwise we treat the from-side as base (same-currency
        // transfer where Amount maps 1:1 to base).
        BaseCurrencyCode = fxSnapshot?.BaseCurrencyCode ?? fromCurrencyCode;
        ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        Lines = Array.Empty<IPostingDocumentLine>();
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "bank_transfer";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid FromAccountId { get; }

    public CurrencyCode FromCurrencyCode { get; }

    public Guid ToAccountId { get; }

    public CurrencyCode ToCurrencyCode { get; }

    public decimal Amount { get; }

    public decimal? FxRate { get; }

    public FxSnapshotRef? FxSnapshot { get; }

    public string? ReferenceNo { get; }

    public string? Memo { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

// ========================================================================
// Bank Deposit — group N Undeposited-Funds items into one bank-line
// posting. Each item carries the source-receipt reference and the
// amount; the holding account loses each item's share, the bank
// account gains the total.
//
// V1 simplification: the holding account is implicit (every item
// must already be sitting in the same Undeposited-Funds account on
// the books — the future iteration where items can come from
// different holding accounts requires per-item account resolution).
// ========================================================================
public sealed record BankDepositItemDocumentLine : IPostingDocumentLine
{
    public BankDepositItemDocumentLine(
        int lineNumber,
        string sourceItemKind,
        Guid? sourceItemId,
        string sourceItemDisplayNumber,
        string? payerName,
        string? paymentMethod,
        string? referenceNo,
        decimal amount)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }
        if (string.IsNullOrWhiteSpace(sourceItemKind))
        {
            throw new ArgumentException("Source item kind is required.", nameof(sourceItemKind));
        }
        if (string.IsNullOrWhiteSpace(sourceItemDisplayNumber))
        {
            throw new ArgumentException("Source item display number is required.", nameof(sourceItemDisplayNumber));
        }
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Item amount must be positive.");
        }

        LineNumber = lineNumber;
        SourceItemKind = sourceItemKind.Trim().ToLowerInvariant();
        SourceItemId = sourceItemId;
        SourceItemDisplayNumber = sourceItemDisplayNumber.Trim();
        PayerName = string.IsNullOrWhiteSpace(payerName) ? null : payerName.Trim();
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? null : paymentMethod.Trim().ToLowerInvariant();
        ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        Amount = amount;
        Description = $"Deposit item {sourceItemDisplayNumber}";
    }

    public int LineNumber { get; }

    public string SourceItemKind { get; }

    public Guid? SourceItemId { get; }

    public string SourceItemDisplayNumber { get; }

    public string? PayerName { get; }

    public string? PaymentMethod { get; }

    public string? ReferenceNo { get; }

    public decimal Amount { get; }

    public string Description { get; }
}

public sealed class BankDepositDocument : IPostingDocument
{
    public BankDepositDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        DateOnly depositDate,
        Guid depositToAccountId,
        Guid undepositedFundsAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        decimal totalAmount,
        string? referenceNo,
        string? memo,
        IEnumerable<BankDepositItemDocumentLine> items)
    {
        if (depositToAccountId == Guid.Empty)
        {
            throw new ArgumentException("Deposit-to account id is required.", nameof(depositToAccountId));
        }
        if (undepositedFundsAccountId == Guid.Empty)
        {
            throw new ArgumentException("Undeposited-funds account id is required.", nameof(undepositedFundsAccountId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        DocumentDate = depositDate;
        DepositToAccountId = depositToAccountId;
        UndepositedFundsAccountId = undepositedFundsAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        TotalAmount = totalAmount;
        ReferenceNo = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        var materializedItems = items?.ToArray() ?? throw new ArgumentNullException(nameof(items));
        if (materializedItems.Length == 0)
        {
            throw new InvalidOperationException("Bank deposit document must contain at least one item.");
        }

        Items = Array.AsReadOnly(materializedItems);
        Lines = Array.AsReadOnly(materializedItems.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "bank_deposit";

    public string Status { get; }

    public DateOnly DocumentDate { get; }

    public Guid DepositToAccountId { get; }

    public Guid UndepositedFundsAccountId { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public decimal TotalAmount { get; }

    public string? ReferenceNo { get; }

    public string? Memo { get; }

    public IReadOnlyList<BankDepositItemDocumentLine> Items { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    /// <summary>
    /// Bank deposit always-1 FX rate placeholder. Same-currency only
    /// for V1 (the deposit-to and undeposited-funds accounts must be
    /// in the same currency); cross-currency banking sits in
    /// BankTransfer.
    /// </summary>
    public FxSnapshotRef? FxSnapshot => null;
}

// ========================================================================
// Tax Return — period close for a sales-tax regime.
//
//   Net = CollectedAmount - InputCreditsAmount + AdjustmentsAmount
//
//   Net > 0  →  Dr TaxPayableAccount         = CollectedAmount
//               Cr TaxReceivableAccount      = InputCreditsAmount
//               Dr/Cr TaxAdjustmentsAccount  = signed AdjustmentsAmount
//               Cr TaxFilingLiabilityAccount = Net
//
//   Net < 0  →  same accrual clearings, |Net| lands on
//               TaxFilingReceivableAccount (Dr) instead.
//
// Single header, no lines, no party. Tax payable / receivable /
// filing-liability account ids are resolved by the repository at
// GetForPostingAsync time from the company chart by canonical code
// (V1 hard-codes the codes; future iteration moves them to
// company_settings columns).
// ========================================================================
public sealed class TaxReturnDocument : IPostingDocument
{
    public TaxReturnDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        string taxRegime,
        string filingFrequency,
        DateOnly periodStart,
        DateOnly periodEnd,
        CurrencyCode baseCurrencyCode,
        decimal collectedAmount,
        decimal inputCreditsAmount,
        decimal adjustmentsAmount,
        decimal netAmount,
        string? adjustmentsNote,
        string? regulatorReferenceNo,
        Guid taxPayableAccountId,
        Guid taxReceivableAccountId,
        Guid taxAdjustmentsAccountId,
        Guid taxFilingLiabilityAccountId,
        Guid taxFilingReceivableAccountId,
        string? memo)
    {
        if (string.IsNullOrWhiteSpace(taxRegime))
        {
            throw new ArgumentException("Tax regime is required.", nameof(taxRegime));
        }
        if (periodEnd < periodStart)
        {
            throw new InvalidOperationException("Tax return period end must be on or after period start.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        TaxRegime = taxRegime.Trim();
        FilingFrequency = string.IsNullOrWhiteSpace(filingFrequency) ? "quarterly" : filingFrequency.Trim().ToLowerInvariant();
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        DocumentDate = periodEnd; // For the engine's date-driven FX
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        TransactionCurrencyCode = baseCurrencyCode; // Tax returns settle in base
        CollectedAmount = collectedAmount;
        InputCreditsAmount = inputCreditsAmount;
        AdjustmentsAmount = adjustmentsAmount;
        NetAmount = netAmount;
        AdjustmentsNote = string.IsNullOrWhiteSpace(adjustmentsNote) ? null : adjustmentsNote.Trim();
        RegulatorReferenceNo = string.IsNullOrWhiteSpace(regulatorReferenceNo) ? null : regulatorReferenceNo.Trim();
        TaxPayableAccountId = taxPayableAccountId;
        TaxReceivableAccountId = taxReceivableAccountId;
        TaxAdjustmentsAccountId = taxAdjustmentsAccountId;
        TaxFilingLiabilityAccountId = taxFilingLiabilityAccountId;
        TaxFilingReceivableAccountId = taxFilingReceivableAccountId;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        Lines = Array.Empty<IPostingDocumentLine>();
    }

    public Guid Id { get; }

    public CompanyId CompanyId { get; }

    public EntityNumber EntityNumber { get; }

    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "tax_return";

    public string Status { get; }

    public string TaxRegime { get; }

    public string FilingFrequency { get; }

    public DateOnly PeriodStart { get; }

    public DateOnly PeriodEnd { get; }

    public DateOnly DocumentDate { get; }

    public CurrencyCode TransactionCurrencyCode { get; }

    public CurrencyCode BaseCurrencyCode { get; }

    public decimal CollectedAmount { get; }

    public decimal InputCreditsAmount { get; }

    public decimal AdjustmentsAmount { get; }

    public decimal NetAmount { get; }

    public string? AdjustmentsNote { get; }

    public string? RegulatorReferenceNo { get; }

    public Guid TaxPayableAccountId { get; }

    public Guid TaxReceivableAccountId { get; }

    public Guid TaxAdjustmentsAccountId { get; }

    public Guid TaxFilingLiabilityAccountId { get; }

    public Guid TaxFilingReceivableAccountId { get; }

    public string? Memo { get; }

    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    /// <summary>
    /// Tax returns settle in the company's base currency — no FX
    /// snapshot needed. Net == base by construction.
    /// </summary>
    public FxSnapshotRef? FxSnapshot => null;
}

/// <summary>
/// One per-item slice of a sales-issue COGS posting. Each line carries
/// the resolved item-level COGS + Inventory Asset accounts plus the
/// total base-currency cost (already rolled up across cost layers and
/// pre-frozen at receipt FX rate by the inventory engine).
///
/// Consuming layer FX context is intentionally NOT carried on the line:
/// the inventory engine has already converted layer cost to base; the
/// posting engine just journalises in base. Re-derivation isn't allowed
/// because that would let drifting spot rates re-cost historical sales.
/// </summary>
public sealed record SalesIssueCogsPostingDocumentLine : IPostingDocumentLine
{
    public SalesIssueCogsPostingDocumentLine(
        int lineNumber,
        Guid itemId,
        Guid cogsAccountId,
        Guid inventoryAssetAccountId,
        string description,
        decimal amountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }
        if (cogsAccountId == Guid.Empty)
        {
            throw new ArgumentException("COGS account id is required.", nameof(cogsAccountId));
        }
        if (inventoryAssetAccountId == Guid.Empty)
        {
            throw new ArgumentException("Inventory asset account id is required.", nameof(inventoryAssetAccountId));
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }
        if (amountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountBase), "Sales-issue COGS line amount must be positive.");
        }

        LineNumber = lineNumber;
        ItemId = itemId;
        CogsAccountId = cogsAccountId;
        InventoryAssetAccountId = inventoryAssetAccountId;
        Description = description.Trim();
        AmountBase = Math.Round(amountBase, 6, MidpointRounding.ToEven);
    }

    public int LineNumber { get; }
    public Guid ItemId { get; }
    public Guid CogsAccountId { get; }
    public Guid InventoryAssetAccountId { get; }
    public string Description { get; }
    public decimal AmountBase { get; }
}

/// <summary>
/// Posting document for the M3 sales-issue → COGS bridge. Mirrors
/// <see cref="ReceiptGrIrPostingDocument"/>:
///   - one document per sales_issue (the source_id on the produced JE)
///   - one line per item rolled up across whatever cost layers were
///     consumed for that item on this issue
///   - SourceType is the JE's source_type column, which is also the
///     idempotency key — re-posting the same sales_issue lands on the
///     existing JE rather than double-posting
///   - base currency only (cost layers are pre-converted; see line
///     class XML doc for why FX context isn't carried)
/// </summary>
public sealed class SalesIssueCogsPostingDocument : IPostingDocument
{
    public SalesIssueCogsPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid salesIssueDocumentId,
        DateOnly documentDate,
        CurrencyCode baseCurrencyCode,
        IEnumerable<SalesIssueCogsPostingDocumentLine> lines,
        bool isReverse = false)
    {
        if (salesIssueDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Sales-issue document id is required.", nameof(salesIssueDocumentId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        SalesIssueDocumentId = salesIssueDocumentId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;
        IsReverse = isReverse;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Sales-issue COGS posting document must contain at least one line.");
        }

        CogsLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }

    /// <summary>
    /// Stamped on the produced journal_entries.source_type. The
    /// command handler probes for an existing JE with this source_type
    /// + source_id = SalesIssueDocumentId to enforce idempotency.
    /// P0-2 (C2): the reverse path uses a distinct source_type so the
    /// forward and reverse JEs coexist on the same sales-issue, each
    /// idempotent on its own.
    /// </summary>
    public string SourceType => IsReverse ? "sales_issue_cogs_reverse" : "sales_issue_cogs";

    public string Status { get; }
    public Guid SalesIssueDocumentId { get; }
    public DateOnly DocumentDate { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public IReadOnlyList<SalesIssueCogsPostingDocumentLine> CogsLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
    public decimal TotalAmountBase => CogsLines.Sum(static line => line.AmountBase);

    /// <summary>
    /// P0-2 (C2): when true the fragment builder posts the compensating
    /// JE for an invoice-reverse, swapping the forward Dr COGS / Cr
    /// Inventory into Dr Inventory / Cr COGS at identical per-account
    /// amounts. Set by <c>ISalesIssueCogsReversePostingRepository</c>;
    /// the forward command handler always passes false.
    /// </summary>
    public bool IsReverse { get; }

    /// <summary>
    /// Cost layers are already in base; no FX step happens at posting.
    /// </summary>
    public FxSnapshotRef? FxSnapshot => null;
}

/// <summary>
/// M6 iter 3: Invoice → Drop-ship COGS recognition. Sister of
/// <see cref="SalesIssueCogsPostingDocument"/> but for drop-ship items —
/// we never receive or ship them, so the M3 cost-layer path doesn't
/// apply. Cost basis is the item's <c>default_purchase_price</c> × line
/// qty (V1 simplification — see commit message for trade-off vs
/// per-bill-line lookup or per-line cost capture). One line per item
/// (rolled up across multiple invoice lines for the same item).
///
/// GL math (per item line):
///   Dr  COGS                           qty × default_purchase_price
///   Cr  Drop-ship Clearing             qty × default_purchase_price
///
/// SourceType is <c>invoice_drop_ship_cogs</c>; SourceId is the
/// invoice id, so the journal-layer idempotency probe lands on this
/// JE rather than double-posting.
/// </summary>
public sealed record InvoiceDropShipCogsPostingDocumentLine : IPostingDocumentLine
{
    public InvoiceDropShipCogsPostingDocumentLine(
        int lineNumber,
        Guid itemId,
        Guid cogsAccountId,
        Guid dropShipClearingAccountId,
        string description,
        decimal amountBase)
    {
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        }
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }
        if (cogsAccountId == Guid.Empty)
        {
            throw new ArgumentException("COGS account id is required.", nameof(cogsAccountId));
        }
        if (dropShipClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("Drop-ship clearing account id is required.", nameof(dropShipClearingAccountId));
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }
        if (amountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountBase), "Drop-ship COGS line amount must be positive.");
        }

        LineNumber = lineNumber;
        ItemId = itemId;
        CogsAccountId = cogsAccountId;
        DropShipClearingAccountId = dropShipClearingAccountId;
        Description = description.Trim();
        AmountBase = Math.Round(amountBase, 6, MidpointRounding.ToEven);
    }

    public int LineNumber { get; }
    public Guid ItemId { get; }
    public Guid CogsAccountId { get; }
    public Guid DropShipClearingAccountId { get; }
    public string Description { get; }
    public decimal AmountBase { get; }
}

public sealed class InvoiceDropShipCogsPostingDocument : IPostingDocument
{
    public InvoiceDropShipCogsPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        string status,
        Guid invoiceDocumentId,
        DateOnly documentDate,
        CurrencyCode baseCurrencyCode,
        IEnumerable<InvoiceDropShipCogsPostingDocumentLine> lines)
    {
        if (invoiceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        Status = string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();
        InvoiceDocumentId = invoiceDocumentId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;

        var materializedLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materializedLines.Length == 0)
        {
            throw new InvalidOperationException("Invoice drop-ship COGS document must contain at least one line.");
        }

        CogsLines = Array.AsReadOnly(materializedLines);
        Lines = Array.AsReadOnly(materializedLines.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "invoice_drop_ship_cogs";

    public string Status { get; }
    public Guid InvoiceDocumentId { get; }
    public DateOnly DocumentDate { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public IReadOnlyList<InvoiceDropShipCogsPostingDocumentLine> CogsLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
    public decimal TotalAmountBase => CogsLines.Sum(static line => line.AmountBase);

    public FxSnapshotRef? FxSnapshot => null;
}

/// <summary>
/// M6 iter 4: Drop-ship Clearing write-off. Operator-driven cleanup of
/// a residual on the Drop-ship Clearing account for a given item — the
/// gap between SUM(posted bill clearing debits) and SUM(posted invoice
/// COGS clearing credits). The write-off books the gap to the Purchase
/// Price Variance account so the clearing returns to zero for that
/// item.
///
/// GL math (sign-aware):
///   net &gt; 0 (over-billed):  Dr PPV  / Cr Drop-ship Clearing
///   net &lt; 0 (under-billed): Dr Drop-ship Clearing / Cr PPV
///
/// SourceType is <c>drop_ship_clearing_writeoff</c>; SourceId is the
/// document Id (a fresh GUID per write-off) so each write-off lands on
/// its own JE and the operator can write off the same item again later
/// if more activity reopens the clearing.
/// </summary>
public sealed class DropShipClearingWriteOffDocument : IPostingDocument
{
    public DropShipClearingWriteOffDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        DateOnly documentDate,
        Guid itemId,
        string itemCode,
        Guid dropShipClearingAccountId,
        Guid varianceAccountId,
        decimal netClearingAmountBase,
        CurrencyCode baseCurrencyCode,
        string? memo)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }
        if (string.IsNullOrWhiteSpace(itemCode))
        {
            throw new ArgumentException("Item code is required.", nameof(itemCode));
        }
        if (dropShipClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("Drop-ship clearing account id is required.", nameof(dropShipClearingAccountId));
        }
        if (varianceAccountId == Guid.Empty)
        {
            throw new ArgumentException("Variance account id is required.", nameof(varianceAccountId));
        }
        if (netClearingAmountBase == 0m)
        {
            throw new InvalidOperationException("Drop-ship clearing write-off requires a non-zero net amount.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        DocumentDate = documentDate;
        ItemId = itemId;
        ItemCode = itemCode.Trim();
        DropShipClearingAccountId = dropShipClearingAccountId;
        VarianceAccountId = varianceAccountId;
        NetClearingAmountBase = Math.Round(netClearingAmountBase, 6, MidpointRounding.ToEven);
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        Lines = Array.AsReadOnly(Array.Empty<IPostingDocumentLine>());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }
    public string SourceType => "drop_ship_clearing_writeoff";
    public string Status => "draft";
    public DateOnly DocumentDate { get; }
    public Guid ItemId { get; }
    public string ItemCode { get; }
    public Guid DropShipClearingAccountId { get; }
    public Guid VarianceAccountId { get; }
    public decimal NetClearingAmountBase { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public string? Memo { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
    public FxSnapshotRef? FxSnapshot => null;
}

/// <summary>
/// M5 iter 4: Customer Deposit → Invoice application. After an invoice
/// posts, any open customer_deposits for the same SO get pro-rata applied
/// against it: the deposit's liability balance is debited, the invoice's
/// AR open item is credited, and a settlement_applications row links
/// the two open items. Net economic effect: the customer's deposit
/// payment converts into invoice settlement; AR shows only the unpaid
/// remainder; the Customer Deposit liability shrinks by the applied amount.
///
/// GL math (per applied line):
///   Dr  Customer Deposit (24700)   AppliedAmountBase
///   Cr  Accounts Receivable        AppliedAmountBase
///
/// SourceType is 'customer_deposit_application'; SourceId is the
/// document Id (a fresh GUID per application JE) so re-running on the
/// same invoice + deposit pair stays journal-layer idempotent.
/// </summary>
public sealed record CustomerDepositApplicationDocumentLine : IPostingDocumentLine
{
    public CustomerDepositApplicationDocumentLine(
        int lineNumber,
        Guid sourceCustomerDepositId,
        Guid sourceCustomerDepositArOpenItemId,
        Guid targetInvoiceArOpenItemId,
        string description,
        decimal appliedAmountBase)
    {
        if (lineNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Line number must be positive.");
        if (sourceCustomerDepositId == Guid.Empty)
            throw new ArgumentException("Source customer-deposit id is required.", nameof(sourceCustomerDepositId));
        if (sourceCustomerDepositArOpenItemId == Guid.Empty)
            throw new ArgumentException("Source AR open item id is required.", nameof(sourceCustomerDepositArOpenItemId));
        if (targetInvoiceArOpenItemId == Guid.Empty)
            throw new ArgumentException("Target invoice AR open item id is required.", nameof(targetInvoiceArOpenItemId));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));
        if (appliedAmountBase <= 0m)
            throw new ArgumentOutOfRangeException(nameof(appliedAmountBase), "Applied amount must be positive.");

        LineNumber = lineNumber;
        SourceCustomerDepositId = sourceCustomerDepositId;
        SourceCustomerDepositArOpenItemId = sourceCustomerDepositArOpenItemId;
        TargetInvoiceArOpenItemId = targetInvoiceArOpenItemId;
        Description = description.Trim();
        AppliedAmountBase = Math.Round(appliedAmountBase, 6, MidpointRounding.ToEven);
    }

    public int LineNumber { get; }
    public Guid SourceCustomerDepositId { get; }
    public Guid SourceCustomerDepositArOpenItemId { get; }
    public Guid TargetInvoiceArOpenItemId { get; }
    public string Description { get; }
    public decimal AppliedAmountBase { get; }
}

public sealed class CustomerDepositApplicationDocument : IPostingDocument
{
    public CustomerDepositApplicationDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        DateOnly documentDate,
        Guid customerId,
        Guid receivableAccountId,
        Guid customerDepositAccountId,
        CurrencyCode baseCurrencyCode,
        IEnumerable<CustomerDepositApplicationDocumentLine> lines,
        Guid invoiceDocumentId)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        if (receivableAccountId == Guid.Empty)
            throw new ArgumentException("Receivable account id is required.", nameof(receivableAccountId));
        if (customerDepositAccountId == Guid.Empty)
            throw new ArgumentException("Customer deposit account id is required.", nameof(customerDepositAccountId));
        if (invoiceDocumentId == Guid.Empty)
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        DocumentDate = documentDate;
        PartyId = customerId;
        ReceivableAccountId = receivableAccountId;
        CustomerDepositAccountId = customerDepositAccountId;
        TransactionCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode;
        InvoiceDocumentId = invoiceDocumentId;
        FxSnapshot = null;

        var materialized = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materialized.Length == 0)
            throw new InvalidOperationException("Customer deposit application must contain at least one line.");

        ApplicationLines = Array.AsReadOnly(materialized);
        Lines = Array.AsReadOnly(materialized.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }
    public string SourceType => "customer_deposit_application";
    public string Status => "draft";
    public DateOnly DocumentDate { get; }
    public Guid PartyId { get; }
    public Guid ReceivableAccountId { get; }
    public Guid CustomerDepositAccountId { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public FxSnapshotRef? FxSnapshot { get; }
    public Guid InvoiceDocumentId { get; }
    public IReadOnlyList<CustomerDepositApplicationDocumentLine> ApplicationLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    public decimal TotalAmountBase => ApplicationLines.Sum(static line => line.AppliedAmountBase);
}

/// <summary>
/// M5 iter 3: standalone Customer Deposit posting. The customer pays
/// against an open / confirmed Sales Order (no invoice exists yet);
/// the cash lands as a liability on the Customer Deposit account
/// (system_role=customer_deposit, code 24700) instead of touching AR.
///
/// GL math:
///   Dr  Bank (deposit_to_account_id)            AmountBase
///   Cr  Customer Deposit (customer_deposit acct) AmountBase
///
/// Persistence side-effects (handled by the repository before the
/// engine post): a customer_deposits row is inserted (status='open',
/// source_sales_order_id set), and a matching ar_open_items row is
/// inserted with source_type='customer_deposit', balance_side='credit'
/// — that row drives M5 iter 4's pro-rata clearing on shipment / invoice.
///
/// Idempotency: SourceType='customer_deposit', SourceId=Id (the deposit
/// row id), so re-posting on the same deposit is journal-layer safe.
/// </summary>
public sealed class CustomerDepositPostingDocument : IPostingDocument
{
    public CustomerDepositPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        DateOnly documentDate,
        Guid customerId,
        Guid depositToAccountId,
        Guid customerDepositAccountId,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        FxSnapshotRef? fxSnapshot,
        decimal amountTx,
        decimal amountBase,
        string? memo = null)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }
        if (depositToAccountId == Guid.Empty)
        {
            throw new ArgumentException("Deposit-to account id is required.", nameof(depositToAccountId));
        }
        if (customerDepositAccountId == Guid.Empty)
        {
            throw new ArgumentException("Customer Deposit account id is required.", nameof(customerDepositAccountId));
        }
        if (amountTx <= 0m || amountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountTx), "Customer deposit amounts must be positive.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        DocumentDate = documentDate;
        CustomerId = customerId;
        DepositToAccountId = depositToAccountId;
        CustomerDepositAccountId = customerDepositAccountId;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxSnapshot = fxSnapshot;
        AmountTx = Math.Round(amountTx, 6, MidpointRounding.ToEven);
        AmountBase = Math.Round(amountBase, 6, MidpointRounding.ToEven);
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

        Lines = Array.Empty<IPostingDocumentLine>();
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }
    public string SourceType => "customer_deposit";
    public string Status => "draft";
    public DateOnly DocumentDate { get; }
    public Guid CustomerId { get; }
    public Guid DepositToAccountId { get; }
    public Guid CustomerDepositAccountId { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public FxSnapshotRef? FxSnapshot { get; }
    public decimal AmountTx { get; }
    public decimal AmountBase { get; }
    public string? Memo { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

/// <summary>
/// H1: compensating posting document for voiding a posted Expense.
/// Carries pre-flipped reverse fragments — the repository reads the
/// original Expense JE's lines and swaps Dr↔Cr (and tx ↔ tx) on each
/// before building this document. The fragment builder then maps each
/// line straight to a PostingFragment without further transformation.
///
/// Rationale: the forward Expense post is currently hand-rolled SQL
/// inside PostgreSqlExpenseStore (a broader H-level concern, deferred).
/// The Void path was previously also hand-rolled SQL (the audit's H1
/// specific finding) — routing it through the Posting Engine here
/// gives:
///   - EnsureJournalInvariants (Dr=Cr in TX and base);
///   - PostgresJournalEntryWriter's ambient-tx guard (PR #25);
///   - uniform idempotency via (source_type='expense_void', source_id);
///   - uniform audit + ledger writer.
///
/// The fragments are pre-built (not derived from line semantics) so
/// the engine treats this document as a self-describing reverse. This
/// is a pragmatic choice: building from line semantics would require
/// re-deriving the original expense allocations from a partial reverse
/// view, and re-deriving is exactly what the audit flagged as
/// drift-prone in the hand-rolled SQL.
/// </summary>
public sealed class ExpenseVoidPostingDocument : IPostingDocument
{
    public ExpenseVoidPostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        Guid expenseId,
        string expenseNumber,
        DateOnly documentDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        decimal fxRate,
        IEnumerable<ExpenseVoidPostingDocumentLine> lines)
    {
        if (expenseId == Guid.Empty)
        {
            throw new ArgumentException("Expense id is required.", nameof(expenseId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        ExpenseId = expenseId;
        ExpenseNumber = expenseNumber?.Trim() ?? string.Empty;
        DocumentDate = documentDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxRate = fxRate;

        var materialized = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materialized.Length == 0)
        {
            throw new InvalidOperationException("Expense-void document must carry at least one line.");
        }

        VoidLines = Array.AsReadOnly(materialized);
        Lines = Array.AsReadOnly(materialized.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }

    /// <summary>
    /// Stamped on the produced journal_entries.source_type. The
    /// repository probes for an existing JE with this source_type +
    /// source_id = ExpenseId to enforce idempotency on retry.
    /// </summary>
    public string SourceType => "expense_void";

    public string Status => "draft";
    public Guid ExpenseId { get; }
    public string ExpenseNumber { get; }
    public DateOnly DocumentDate { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public decimal FxRate { get; }
    public IReadOnlyList<ExpenseVoidPostingDocumentLine> VoidLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    /// <summary>
    /// The original Expense never went through an FX snapshot lookup —
    /// FxRate / FxSource were captured at post-time. The reverse JE
    /// uses the SAME rate (no fresh FX resolution); identity rate when
    /// the transaction currency equals base.
    /// </summary>
    public FxSnapshotRef? FxSnapshot => null;
}

/// <summary>
/// One leg of an expense-void compensation JE. Amounts are ALREADY
/// flipped relative to the forward Expense post — the repository
/// reads the original journal_entry_lines and constructs each line
/// with debit/credit swapped (and tx_debit/tx_credit swapped).
/// </summary>
public sealed record ExpenseVoidPostingDocumentLine(
    int LineNumber,
    Guid AccountId,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string Description,
    string PostingRole,
    string? ControlRole = null,
    Guid? PartyId = null,
    int? SourceLineNumber = null) : IPostingDocumentLine;

/// <summary>
/// Reverse-of-a-posted-invoice compensation JE. Mirrors
/// <see cref="ExpenseVoidPostingDocument"/>: the repository reads the
/// original invoice JE (source_type='invoice') + its lines and pre-flips
/// each Dr/Cr, so the engine just emits them as fragments. Reverses every
/// original leg — AR, revenue, and each per-rule sales-tax leg — for free.
/// Stamps source_type='invoice_reversal' (idempotency probe key with
/// source_id = InvoiceId).
/// </summary>
public sealed class InvoiceReversePostingDocument : IPostingDocument
{
    public InvoiceReversePostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        Guid invoiceId,
        DateOnly documentDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        decimal fxRate,
        IEnumerable<InvoiceReversePostingDocumentLine> lines)
    {
        if (invoiceId == Guid.Empty)
        {
            throw new ArgumentException("Invoice id is required.", nameof(invoiceId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        InvoiceId = invoiceId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxRate = fxRate;

        var materialized = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materialized.Length == 0)
        {
            throw new InvalidOperationException("Invoice-reverse document must carry at least one line.");
        }

        ReverseLines = Array.AsReadOnly(materialized);
        Lines = Array.AsReadOnly(materialized.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "invoice_reversal";

    public string Status => "draft";
    public Guid InvoiceId { get; }
    public DateOnly DocumentDate { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public decimal FxRate { get; }
    public IReadOnlyList<InvoiceReversePostingDocumentLine> ReverseLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    // The reverse JE reuses the original invoice's CAPTURED rate (read from
    // the original JE's exchange_rate) instead of resolving a fresh FX
    // snapshot — exactly how InvoiceDocument hands the engine a Guid.Empty
    // FxSnapshotRef for a manual/foreign rate, so no local-snapshot lookup
    // runs. Null (identity) when the transaction currency equals base.
    public FxSnapshotRef? FxSnapshot =>
        TransactionCurrencyCode == BaseCurrencyCode && FxRate == 1m
            ? null
            : new FxSnapshotRef(
                Guid.Empty,
                BaseCurrencyCode,
                TransactionCurrencyCode,
                FxRate,
                DocumentDate,
                DocumentDate,
                "reverse_captured");
}

/// <summary>
/// One leg of an invoice-reverse compensation JE — amounts ALREADY flipped
/// relative to the forward invoice post (Dr/Cr and TxDr/TxCr swapped).
/// </summary>
public sealed record InvoiceReversePostingDocumentLine(
    int LineNumber,
    Guid AccountId,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string Description,
    string PostingRole,
    string? ControlRole = null,
    Guid? PartyId = null,
    int? SourceLineNumber = null) : IPostingDocumentLine;

public sealed class BillReversePostingDocument : IPostingDocument
{
    public BillReversePostingDocument(
        Guid id,
        CompanyId companyId,
        EntityNumber entityNumber,
        DocumentNumber displayNumber,
        Guid billId,
        DateOnly documentDate,
        CurrencyCode transactionCurrencyCode,
        CurrencyCode baseCurrencyCode,
        decimal fxRate,
        IEnumerable<BillReversePostingDocumentLine> lines)
    {
        if (billId == Guid.Empty)
        {
            throw new ArgumentException("Bill id is required.", nameof(billId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        CompanyId = companyId;
        EntityNumber = entityNumber;
        DisplayNumber = displayNumber ?? throw new ArgumentNullException(nameof(displayNumber));
        BillId = billId;
        DocumentDate = documentDate;
        TransactionCurrencyCode = transactionCurrencyCode ?? throw new ArgumentNullException(nameof(transactionCurrencyCode));
        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        FxRate = fxRate;

        var materialized = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materialized.Length == 0)
        {
            throw new InvalidOperationException("Bill-reverse document must carry at least one line.");
        }

        ReverseLines = Array.AsReadOnly(materialized);
        Lines = Array.AsReadOnly(materialized.Cast<IPostingDocumentLine>().ToArray());
    }

    public Guid Id { get; }
    public CompanyId CompanyId { get; }
    public EntityNumber EntityNumber { get; }
    public DocumentNumber DisplayNumber { get; }

    public string SourceType => "bill_reversal";

    public string Status => "draft";
    public Guid BillId { get; }
    public DateOnly DocumentDate { get; }
    public CurrencyCode TransactionCurrencyCode { get; }
    public CurrencyCode BaseCurrencyCode { get; }
    public decimal FxRate { get; }
    public IReadOnlyList<BillReversePostingDocumentLine> ReverseLines { get; }
    public IReadOnlyList<IPostingDocumentLine> Lines { get; }

    // Reuse the bill's CAPTURED rate (read from the original JE's
    // exchange_rate) instead of resolving a fresh FX snapshot — exactly how
    // InvoiceReversePostingDocument hands the engine a Guid.Empty FxSnapshotRef
    // for a manual/foreign rate, so no local-snapshot lookup runs. Null
    // (identity) when the transaction currency equals base.
    public FxSnapshotRef? FxSnapshot =>
        TransactionCurrencyCode == BaseCurrencyCode && FxRate == 1m
            ? null
            : new FxSnapshotRef(
                Guid.Empty,
                BaseCurrencyCode,
                TransactionCurrencyCode,
                FxRate,
                DocumentDate,
                DocumentDate,
                "reverse_captured");
}

/// <summary>
/// One leg of a bill-reverse compensation JE — amounts ALREADY flipped
/// relative to the forward bill post (Dr/Cr and TxDr/TxCr swapped).
/// </summary>
public sealed record BillReversePostingDocumentLine(
    int LineNumber,
    Guid AccountId,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string Description,
    string PostingRole,
    string? ControlRole = null,
    Guid? PartyId = null,
    int? SourceLineNumber = null) : IPostingDocumentLine;
