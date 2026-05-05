using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Posting;

/// <summary>
/// No-op implementation. Used by integration tests that hand-build
/// <see cref="DefaultPostingEngine"/> without DI and don't care about
/// the M7 iter 2 period-policy enforcement (those tests focus on the
/// posting fragments + journal writer, not the date-validation gate).
/// Production DI binds the real
/// <see cref="PostgresPostingPeriodPolicyValidator"/>.
/// </summary>
public sealed class NullPostingPeriodPolicyValidator : IPostingPeriodPolicyValidator
{
    public Task ValidateAsync(
        CompanyId companyId,
        DateOnly effectiveDate,
        string sourceType,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
