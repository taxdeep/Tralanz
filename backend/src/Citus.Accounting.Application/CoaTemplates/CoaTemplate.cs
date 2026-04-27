namespace Citus.Accounting.Application.CoaTemplates;

/// <summary>
/// A code-defined chart-of-accounts starter template. Templates are
/// versioned by <see cref="Key"/> + <see cref="Version"/> and shipped as
/// static C# data so they round-trip with the source tree (no template
/// migration). Application is additive — codes that already exist on
/// the target company are skipped, so applying the same template
/// twice is a no-op for the second pass.
/// </summary>
public sealed record CoaTemplate(
    string Key,
    string Version,
    string Name,
    string Description,
    string Country,
    int AccountCodeLength,
    IReadOnlyList<CoaTemplateAccount> Accounts);

/// <summary>
/// One row of a CoA template. Mirrors the columns the Posting Engine
/// cares about; <see cref="SystemRole"/> aligns with the keys
/// <c>PostgresAccountLookup.TryResolveActiveAccountIdAsync</c> resolves
/// (e.g. <c>accounts_receivable</c>, <c>accounts_payable</c>,
/// <c>fx_revaluation</c>, <c>retained_earnings</c>). Setting that
/// field marks the row as a system account, which the maintenance UI
/// then renders as read-only.
/// </summary>
public sealed record CoaTemplateAccount(
    string Code,
    string Name,
    string RootType,
    string? DetailType = null,
    bool AllowManualPosting = true,
    string? SystemKey = null,
    string? SystemRole = null);

public sealed record CoaSeedSummary(
    string TemplateKey,
    string TemplateVersion,
    int CreatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<CoaSeedAccountResult> Results);

public sealed record CoaSeedAccountResult(
    string Code,
    string Name,
    CoaSeedOutcome Outcome,
    string? ErrorMessage = null);

public enum CoaSeedOutcome
{
    Created,
    SkippedExisting,
    Failed,
}
