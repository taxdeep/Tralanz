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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        Guid? payableTaxAccountId)
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

        if (taxAmount > 0m && payableTaxAccountId is null)
        {
            throw new InvalidOperationException("Tax-bearing invoice lines must resolve to a payable tax account.");
        }

        LineNumber = lineNumber;
        RevenueAccountId = revenueAccountId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        PayableTaxAccountId = payableTaxAccountId;
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        Guid? payableTaxAccountId)
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
    }

    public int LineNumber { get; }

    public Guid RevenueAccountId { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public Guid? PayableTaxAccountId { get; }
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        Guid? recoverableTaxAccountId)
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
            throw new ArgumentOutOfRangeException(nameof(lineAmount), "Bill line amounts must be positive and tax cannot be negative.");
        }

        if (taxAmount > 0m && isTaxRecoverable && recoverableTaxAccountId is null)
        {
            throw new InvalidOperationException("Recoverable tax bill lines must resolve to a recoverable tax account.");
        }

        LineNumber = lineNumber;
        ExpenseAccountId = expenseAccountId;
        Description = description.Trim();
        LineAmount = lineAmount;
        TaxAmount = taxAmount;
        IsTaxRecoverable = isTaxRecoverable;
        RecoverableTaxAccountId = recoverableTaxAccountId;
    }

    public int LineNumber { get; }

    public Guid ExpenseAccountId { get; }

    public string Description { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public bool IsTaxRecoverable { get; }

    public Guid? RecoverableTaxAccountId { get; }
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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

public sealed record VendorCreditDocumentLine : IPostingDocumentLine
{
    public VendorCreditDocumentLine(
        int lineNumber,
        Guid expenseAccountId,
        string description,
        decimal lineAmount,
        decimal taxAmount,
        bool isTaxRecoverable,
        Guid? recoverableTaxAccountId)
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
    }

    public int LineNumber { get; }

    public Guid ExpenseAccountId { get; }

    public string Description { get; }

    public decimal LineAmount { get; }

    public decimal TaxAmount { get; }

    public bool IsTaxRecoverable { get; }

    public Guid? RecoverableTaxAccountId { get; }
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
        EntityNumber = entityNumber ?? throw new ArgumentNullException(nameof(entityNumber));
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
