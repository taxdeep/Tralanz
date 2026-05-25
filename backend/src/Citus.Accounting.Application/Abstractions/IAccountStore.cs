namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company chart of accounts. Backs the Settings → Chart of Accounts
/// page and supplies <see cref="Citus.Modules.UnitySearch"/>'s account
/// projection (<c>SeedAccountDocumentsAsync</c> reads the same table on
/// projection refresh, so newly-created accounts surface in pickers
/// automatically once the per-company refresh window rolls over).
///
/// V1 surfaces the columns the maintenance UI exposes today: code,
/// name, root_type, detail_type, currency_code, allow_manual_posting,
/// is_active. Internal control fields (entity_number, system_key,
/// system_role, is_system, is_system_default) stay server-side; the
/// store fills them with safe defaults on insert and refuses to
/// modify them on update.
/// </summary>
public sealed record AccountRecord(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive,
    bool IsSystem,
    bool IsSystemDefault,
    string? SystemKey,
    string? SystemRole,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Batch C: nullable self-reference. NULL = top-level account.
    Guid? ParentAccountId = null,
    // Batch D: when set, the account is locked — financial-truth
    // fields (code/name/root_type/detail_type/currency_code/
    // allow_manual_posting) cannot be modified until unlocked.
    DateTimeOffset? LockedAt = null,
    string? LockedByUserId = null);

public static class AccountRootType
{
    public const string Asset = "asset";
    public const string Liability = "liability";
    public const string Equity = "equity";
    public const string Revenue = "revenue";
    public const string CostOfSales = "cost_of_sales";
    public const string Expense = "expense";

    public static readonly IReadOnlyList<string> AllValues =
        new[] { Asset, Liability, Equity, Revenue, CostOfSales, Expense };

    public static bool IsValid(string? value) =>
        value is Asset or Liability or Equity or Revenue or CostOfSales or Expense;
}

/// <summary>
/// The <c>detail_type</c> values that mark an account as eligible to
/// be a payment source on an Expense. Tralanz Books surfaces the
/// Payment Account picker grouped by these three buckets so cheque /
/// wire / EFT / direct-deposit / credit-card workflows can be
/// validated against the picked account's category.
/// </summary>
public static class PaymentAccountDetailType
{
    public const string Bank = "bank";
    public const string Cash = "cash";
    public const string CreditCard = "credit_card";

    public static readonly IReadOnlyList<string> All = new[] { Bank, Cash, CreditCard };

    public static bool IsPaymentEligible(string? detailType) =>
        detailType is Bank or Cash or CreditCard;
}

public sealed record AccountUpsertInput(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive,
    // Batch C: nullable self-reference. Same root_type is recommended
    // (UI enforces) but not required (store accepts cross-root).
    Guid? ParentAccountId = null);

/// <summary>
/// Batch D: Lock / unlock toggle. Separate from AccountUpsertInput
/// because (a) it's intentionally not part of the routine
/// edit-the-account flow (operators must consciously choose to
/// lock/unlock), and (b) the actor user id is required for the
/// audit_log row.
/// </summary>
public sealed record AccountLockInput(
    bool Lock,
    UserId? ActorUserId);

/// <summary>
/// Account-creation payload used by template seeders. Carries the
/// system-account fields that the user-facing maintenance UI never
/// touches (system_key, system_role, is_system, is_system_default).
/// </summary>
public sealed record AccountSeedInput(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive,
    bool IsSystem,
    bool IsSystemDefault,
    string? SystemKey,
    string? SystemRole);

public interface IAccountStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<AccountRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken);

    Task<AccountRecord> CreateAsync(
        CompanyId companyId,
        AccountUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates user-editable fields. Refuses to modify rows where
    /// <c>is_system = true</c>; callers should hide the edit affordance
    /// for those rows in the UI.
    /// </summary>
    Task<AccountRecord?> UpdateAsync(
        CompanyId companyId,
        Guid accountId,
        AccountUpsertInput input,
        CancellationToken cancellationToken);

    Task<AccountRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid accountId,
        bool isActive,
        CancellationToken cancellationToken);

    /// <summary>
    /// Batch D: lock or unlock the account. While locked, financial-
    /// truth fields (code/name/root_type/detail_type/currency_code/
    /// allow_manual_posting) cannot be modified via UpdateAsync —
    /// UpdateAsync raises InvalidOperationException until the
    /// operator un-locks. Both lock and unlock are audited (action
    /// 'account_locked' / 'account_unlocked'). Re-locking an already-
    /// locked account or unlocking an already-unlocked one is a no-
    /// op and returns the row unchanged.
    /// </summary>
    Task<AccountRecord?> SetLockAsync(
        CompanyId companyId,
        Guid accountId,
        AccountLockInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a system / template account, including the protected
    /// fields (<c>is_system</c>, <c>system_key</c>, <c>system_role</c>)
    /// that the user-facing CRUD methods never touch. Returns
    /// <c>null</c> when a row with the same <c>code</c> already exists
    /// for the company so the caller can record a "skipped" outcome
    /// instead of failing the whole seed.
    /// </summary>
    Task<AccountRecord?> SeedSystemAccountAsync(
        CompanyId companyId,
        AccountSeedInput input,
        CancellationToken cancellationToken);
}
