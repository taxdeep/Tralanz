using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M7 iter 4: pre-close dashboard. Each soft-block check returns a
/// count + a one-line summary. The dashboard surfaces them so an
/// owner / book-governance user can resolve (or override) each
/// before transitioning the period through closing → closed.
///
/// V1 surfaces the 3 checks the plan calls out:
///   - GR/IR bridge rows aged &gt; 90 days
///   - Drop-ship Clearing residuals aged &gt; 90 days
///   - SO backorder lines aged &gt; 30 days
///
/// Each is a soft block — non-zero count means "look at this before
/// you close" but doesn't enforce a hard refusal at this layer.
/// </summary>
public interface IYearEndPreCloseChecksReader
{
    Task<YearEndPreCloseChecks> ReadAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);
}

public sealed record YearEndPreCloseChecks(
    YearEndPreCloseCheck GrIrAged,
    YearEndPreCloseCheck DropShipClearingAged,
    YearEndPreCloseCheck SalesOrderBackorderAged);

public sealed record YearEndPreCloseCheck(
    string Title,
    string Description,
    int Count,
    string ResolutionHint);
