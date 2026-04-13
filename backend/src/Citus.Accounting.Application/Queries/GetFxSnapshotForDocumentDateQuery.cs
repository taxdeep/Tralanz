using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Application.Queries;

public sealed record GetFxSnapshotForDocumentDateQuery(
    CompanyId CompanyId,
    CurrencyCode BaseCurrencyCode,
    CurrencyCode QuoteCurrencyCode,
    DateOnly RequestedDate,
    Guid? AcceptedSnapshotId);

public sealed class GetFxSnapshotForDocumentDateQueryHandler
{
    private readonly IFxSnapshotRepository _repository;

    public GetFxSnapshotForDocumentDateQueryHandler(IFxSnapshotRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<FxSnapshotRef?> HandleAsync(
        GetFxSnapshotForDocumentDateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return _repository.FindAcceptedSnapshotAsync(
            query.CompanyId,
            query.BaseCurrencyCode,
            query.QuoteCurrencyCode,
            query.RequestedDate,
            query.AcceptedSnapshotId,
            cancellationToken);
    }
}
