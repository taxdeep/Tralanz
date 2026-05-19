namespace Modules.AP.Expenses;

/// <summary>
/// Per-company Expense (cash outflow) document surface for the
/// Tralanz Books AP page. Owns the <c>expenses</c> +
/// <c>expense_lines</c> tables.
///
/// Expense models a payment that has already happened — it creates
/// no AP open item and (eventually) writes a journal entry directly
/// when the document is created. Status lifecycle is intentionally
/// minimal:
///
///   Posted ──Void──▶ Voided
///
/// There is no Draft state. If the user wants to record an obligation
/// they have not paid yet, the right document is Bill, not Expense.
///
/// V1 (framework only) keeps the JE write deferred — same approach
/// the Bill / PO modules took. <c>posted_journal_entry_id</c> is
/// nullable and stays NULL in V1; the JE pipeline wires in alongside
/// Bill posting when the GL integration batch ships.
/// </summary>
public interface IExpenseStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpenseSummary>> ListAsync(
        CompanyId companyId,
        ExpenseListFilter filter,
        CancellationToken cancellationToken);

    Task<ExpenseRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid expenseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the expense in <see cref="ExpenseStatus.Posted"/>
    /// state. The store calls back into the connection to look up
    /// the company's base currency + the payment account's
    /// detail_type for cross-validation against
    /// <paramref name="input"/>.PaymentMethod.
    /// </summary>
    Task<ExpenseRecord> CreateAsync(
        CompanyId companyId,
        UserId createdByUserId,
        ExpenseUpsertInput input,
        CancellationToken cancellationToken);

    Task<ExpenseRecord?> VoidAsync(
        CompanyId companyId,
        Guid expenseId,
        CancellationToken cancellationToken);
}

public sealed record ExpenseListFilter(
    string? Status,
    Guid? PayeeId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record ExpenseSummary(
    Guid Id,
    CompanyId CompanyId,
    string ExpenseNumber,
    string Status,
    string PayeeKind,
    Guid? PayeeId,
    string PayeeDisplayName,
    Guid PaymentAccountId,
    string PaymentAccountLabel,
    string PaymentMethod,
    DateOnly PaymentDate,
    string TransactionCurrencyCode,
    decimal TotalAmount,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ExpenseRecord(
    Guid Id,
    CompanyId CompanyId,
    string ExpenseNumber,
    string Status,
    string PayeeKind,
    Guid? PayeeId,
    string PayeeNameFreeform,
    Guid PaymentAccountId,
    string PaymentAccountLabel,
    string PaymentMethod,
    string? ChequeNumber,
    string? RefNo,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal FxRate,
    string FxSource,
    DateOnly PaymentDate,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    string? InternalNote,
    Guid? PostedJournalEntryId,
    DateTimeOffset? VoidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExpenseLineRecord> Lines);

public sealed record ExpenseLineRecord(
    Guid Id,
    Guid ExpenseId,
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    decimal LineTotal,
    // Optional Task back-link, persisted in expense_lines.task_id. Read
    // path surfaces it so the edit page can pre-fill the per-line
    // TaskPicker; write path persists it after the validator clears.
    Guid? TaskId = null);

public sealed record ExpenseUpsertInput(
    string PayeeKind,
    Guid? PayeeId,
    string PayeeNameFreeform,
    Guid PaymentAccountId,
    string PaymentMethod,
    string? ChequeNumber,
    string? RefNo,
    string TransactionCurrencyCode,
    decimal? FxRate,
    DateOnly PaymentDate,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    string TaxMode,
    string? DiscountKind,
    decimal? DiscountValue,
    string? Memo,
    string? InternalNote,
    IReadOnlyList<ExpenseLineInput> Lines);

public sealed record ExpenseLineInput(
    int Sequence,
    DateOnly? ServiceDate,
    Guid? ItemId,
    Guid ExpenseAccountId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? TaxCodeId,
    // See ExpenseLineRecord.TaskId.
    Guid? TaskId = null);

public static class ExpenseStatus
{
    public const string Posted = "posted";
    public const string Voided = "voided";

    public static bool IsValid(string? status) => status is Posted or Voided;
}

public static class ExpensePayeeKind
{
    public const string Vendor = "vendor";
    public const string Employee = "employee";
    public const string Other = "other";

    public static bool IsValid(string? kind) => kind is Vendor or Employee or Other;
}

public static class ExpensePaymentMethod
{
    public const string Cash = "cash";
    public const string Cheque = "cheque";
    public const string CreditCard = "credit_card";
    public const string Wire = "wire";
    public const string DirectDeposit = "direct_deposit";
    public const string Eft = "eft";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        new[] { Cash, Cheque, CreditCard, Wire, DirectDeposit, Eft, Other };

    public static bool IsValid(string? method) =>
        method is Cash or Cheque or CreditCard or Wire or DirectDeposit or Eft or Other;

    /// <summary>
    /// Returns null if the (method, cheque#, ref#) combo is valid.
    /// Returns a user-facing error message otherwise. Cash needs
    /// nothing extra; cheque needs cheque_number; wire / direct
    /// deposit / EFT need ref_no; credit_card / other accept either
    /// or none.
    /// </summary>
    public static string? ValidateReferenceFields(
        string method,
        string? chequeNumber,
        string? refNo)
    {
        switch (method)
        {
            case Cash:
                return null;
            case Cheque:
                return string.IsNullOrWhiteSpace(chequeNumber)
                    ? "Cheque number is required when payment method is Cheque."
                    : null;
            case Wire:
            case DirectDeposit:
            case Eft:
                return string.IsNullOrWhiteSpace(refNo)
                    ? "Reference number is required when payment method is Wire, Direct Deposit, or EFT."
                    : null;
            case CreditCard:
            case Other:
                return null;
            default:
                return $"Unknown payment method '{method}'.";
        }
    }

    /// <summary>
    /// Cross-validates the chosen payment method against the
    /// payment account's detail_type. cheque / wire / direct deposit
    /// / EFT must come from a bank account; credit_card must come
    /// from a credit_card account; cash works from cash or bank;
    /// other accepts any.
    /// </summary>
    public static string? ValidateAgainstAccountDetailType(
        string method,
        string? accountDetailType)
    {
        var detail = accountDetailType ?? string.Empty;
        switch (method)
        {
            case Cash:
                return detail is "cash" or "bank"
                    ? null
                    : "Cash payments must come from a Cash or Bank account.";
            case Cheque:
            case Wire:
            case DirectDeposit:
            case Eft:
                return detail == "bank"
                    ? null
                    : $"{HumanizeMethod(method)} payments must come from a Bank account.";
            case CreditCard:
                return detail == "credit_card"
                    ? null
                    : "Credit-card payments must come from a Credit Card account.";
            case Other:
                return null;
            default:
                return $"Unknown payment method '{method}'.";
        }
    }

    private static string HumanizeMethod(string method) => method switch
    {
        Cash => "Cash",
        Cheque => "Cheque",
        CreditCard => "Credit-card",
        Wire => "Wire",
        DirectDeposit => "Direct-deposit",
        Eft => "EFT",
        Other => "Other",
        _ => method
    };
}

public static class ExpenseTaxMode
{
    public const string Exclusive = "exclusive";
    public const string Inclusive = "inclusive";

    public static bool IsValid(string? mode) => mode is Exclusive or Inclusive;
}
