using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PrepareSettlementDraftCommandHandlerTests
{
    [Fact]
    public async Task PrepareReceivePaymentDraft_MapsRepositoryResult()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var repository = new FakeReceivePaymentRepository();
        var handler = new PrepareReceivePaymentDraftCommandHandler(repository, new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PrepareReceivePaymentDraftCommand(
                companyId,
                userId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new DateOnly(2026, 4, 12),
                null,
                "Draft receive payment",
                [new SettlementDraftLine(Guid.NewGuid(), 125m)]),
            CancellationToken.None);

        Assert.Equal(repository.PreparedResult.DocumentId, result.DocumentId);
        Assert.Equal(repository.PreparedResult.DisplayNumber, result.DisplayNumber);
        Assert.Equal(125m, result.TotalAmount);
        Assert.Single(repository.CapturedRequest!.Lines);
    }

    [Fact]
    public async Task PreparePayBillDraft_MapsRepositoryResult()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var repository = new FakePayBillRepository();
        var handler = new PreparePayBillDraftCommandHandler(repository, new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PreparePayBillDraftCommand(
                companyId,
                userId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new DateOnly(2026, 4, 12),
                null,
                "Draft pay bill",
                [new SettlementDraftLine(Guid.NewGuid(), 95m)]),
            CancellationToken.None);

        Assert.Equal(repository.PreparedResult.DocumentId, result.DocumentId);
        Assert.Equal(repository.PreparedResult.DisplayNumber, result.DisplayNumber);
        Assert.Equal(95m, result.TotalAmount);
        Assert.Single(repository.CapturedRequest!.Lines);
    }

    private sealed class FakeReceivePaymentRepository : IReceivePaymentDocumentRepository
    {
        public SettlementDraftPreparationResult PreparedResult { get; } =
            new(Guid.NewGuid(), "EN2026000001", "RCP-000001", 1, 125m, "draft");

        public ReceivePaymentDraftPreparation? CapturedRequest { get; private set; }

        public Task<ReceivePaymentDocument?> GetForPostingAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReceivePaymentDocument?>(null);

        public Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenReceivableCandidatesAsync(
            CompanyId companyId,
            Guid customerId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementOpenItemCandidate>>([]);

        public Task<SettlementDraftPreparationResult> PrepareDraftAsync(
            ReceivePaymentDraftPreparation request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(PreparedResult);
        }
    }

    private sealed class FakePayBillRepository : IPayBillDocumentRepository
    {
        public SettlementDraftPreparationResult PreparedResult { get; } =
            new(Guid.NewGuid(), "EN2026000002", "PB-000001", 1, 95m, "draft");

        public PayBillDraftPreparation? CapturedRequest { get; private set; }

        public Task<PayBillDocument?> GetForPostingAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PayBillDocument?>(null);

        public Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenPayableCandidatesAsync(
            CompanyId companyId,
            Guid vendorId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementOpenItemCandidate>>([]);

        public Task<SettlementDraftPreparationResult> PrepareDraftAsync(
            PayBillDraftPreparation request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(PreparedResult);
        }
    }

    private sealed class ImmediateUnitOfWork : IUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken) =>
            action(cancellationToken);
    }
}
