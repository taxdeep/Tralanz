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
    Guid CompanyId,
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
    DateTimeOffset UpdatedAt);

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

public sealed record AccountUpsertInput(
    string Code,
    string Name,
    string RootType,
    string? DetailType,
    string? CurrencyCode,
    bool AllowManualPosting,
    bool IsActive);

public interface IAccountStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountRecord>> ListAsync(
        Guid companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<AccountRecord?> GetByIdAsync(
        Guid companyId,
        Guid accountId,
        CancellationToken cancellationToken);

    Task<AccountRecord> CreateAsync(
        Guid companyId,
        AccountUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates user-editable fields. Refuses to modify rows where
    /// <c>is_system = true</c>; callers should hide the edit affordance
    /// for those rows in the UI.
    /// </summary>
    Task<AccountRecord?> UpdateAsync(
        Guid companyId,
        Guid accountId,
        AccountUpsertInput input,
        CancellationToken cancellationToken);

    Task<AccountRecord?> SetActiveAsync(
        Guid companyId,
        Guid accountId,
        bool isActive,
        CancellationToken cancellationToken);
}
