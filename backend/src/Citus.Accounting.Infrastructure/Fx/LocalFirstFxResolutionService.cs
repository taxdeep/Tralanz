using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Infrastructure.Fx;

public sealed class LocalFirstFxResolutionService : IFxResolutionService
{
    private readonly IFxSnapshotRepository _repository;

    public LocalFirstFxResolutionService(IFxSnapshotRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<FxResolutionResult> ResolveAsync(
        FxResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.BaseCurrencyCode == request.QuoteCurrencyCode)
        {
            var identitySnapshot = new FxSnapshotRef(
                Guid.Empty,
                request.BaseCurrencyCode,
                request.QuoteCurrencyCode,
                1m,
                request.RequestedDate,
                request.RequestedDate,
                "identity");

            return new FxResolutionResult(identitySnapshot, new[] { "Identity FX snapshot applied." });
        }

        var snapshot = await _repository.FindAcceptedSnapshotAsync(
            request.CompanyId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            request.AcceptedSnapshotId,
            cancellationToken);

        if (snapshot is null)
        {
            throw new InvalidOperationException("No acceptable local FX snapshot was found for the requested document date.");
        }

        return new FxResolutionResult(snapshot, new[] { "Local FX snapshot applied." });
    }
}
