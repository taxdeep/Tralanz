using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
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
        var clientRequestId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new PrepareReceivePaymentDraftCommand(
                companyId,
                userId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new DateOnly(2026, 4, 12),
                null,
                "Draft receive payment",
                [new SettlementDraftLine(Guid.NewGuid(), 125m)],
                ClientRequestId: clientRequestId),
            CancellationToken.None);

        Assert.Equal(repository.PreparedResult.DocumentId, result.DocumentId);
        Assert.Equal(repository.PreparedResult.DisplayNumber, result.DisplayNumber);
        Assert.Equal(125m, result.TotalAmount);
        Assert.Single(repository.CapturedRequest!.Lines);
        Assert.Equal(clientRequestId, repository.CapturedRequest.ClientRequestId);
    }

    [Fact]
    public async Task PreparePayBillDraft_MapsRepositoryResult()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var repository = new FakePayBillRepository();
        var handler = new PreparePayBillDraftCommandHandler(repository, new ImmediateUnitOfWork());
        var clientRequestId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new PreparePayBillDraftCommand(
                companyId,
                userId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new DateOnly(2026, 4, 12),
                null,
                "Draft pay bill",
                [new SettlementDraftLine(Guid.NewGuid(), 95m)],
                clientRequestId),
            CancellationToken.None);

        Assert.Equal(repository.PreparedResult.DocumentId, result.DocumentId);
        Assert.Equal(repository.PreparedResult.DisplayNumber, result.DisplayNumber);
        Assert.Equal(95m, result.TotalAmount);
        Assert.Single(repository.CapturedRequest!.Lines);
        Assert.Equal(clientRequestId, repository.CapturedRequest.ClientRequestId);
    }

    [Fact]
    public async Task PostReceivePayment_WhenAlreadyPosted_ReturnsExistingJournalWithoutReReadingOpenItems()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var documentId = Guid.NewGuid();
        var existing = new PostedSettlementPostingResult(
            Guid.NewGuid(),
            "JE-000123",
            DateTimeOffset.UtcNow);
        var repository = new FakeReceivePaymentRepository { ExistingPostedResult = existing };
        var handler = new PostReceivePaymentCommandHandler(
            repository,
            new ThrowingPostingEngine(),
            new ThrowingSettlementApplicationRepository(),
            new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PostReceivePaymentCommand(
                companyId,
                documentId,
                UserId.FromOrdinal(1),
                null,
                null),
            CancellationToken.None);

        Assert.Equal(existing.JournalEntryId, result.JournalEntryId);
        Assert.Equal(existing.JournalEntryDisplayNumber, result.JournalEntryDisplayNumber);
        Assert.False(repository.GetForPostingCalled);
    }

    [Fact]
    public async Task PostPayBill_WhenAlreadyPosted_ReturnsExistingJournalWithoutReReadingOpenItems()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var documentId = Guid.NewGuid();
        var existing = new PostedSettlementPostingResult(
            Guid.NewGuid(),
            "JE-000124",
            DateTimeOffset.UtcNow);
        var repository = new FakePayBillRepository { ExistingPostedResult = existing };
        var handler = new PostPayBillCommandHandler(
            repository,
            new ThrowingPostingEngine(),
            new ThrowingSettlementApplicationRepository(),
            new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PostPayBillCommand(
                companyId,
                documentId,
                UserId.FromOrdinal(1),
                null,
                null),
            CancellationToken.None);

        Assert.Equal(existing.JournalEntryId, result.JournalEntryId);
        Assert.Equal(existing.JournalEntryDisplayNumber, result.JournalEntryDisplayNumber);
        Assert.False(repository.GetForPostingCalled);
    }

    private sealed class FakeReceivePaymentRepository : IReceivePaymentDocumentRepository
    {
        public SettlementDraftPreparationResult PreparedResult { get; } =
            new(Guid.NewGuid(), "EN2026000001", "RCP-000001", 1, 125m, "draft");

        public ReceivePaymentDraftPreparation? CapturedRequest { get; private set; }
        public PostedSettlementPostingResult? ExistingPostedResult { get; init; }
        public bool GetForPostingCalled { get; private set; }

        public Task<ReceivePaymentDocument?> GetForPostingAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken)
        {
            GetForPostingCalled = true;
            return Task.FromResult<ReceivePaymentDocument?>(null);
        }

        public Task<PostedSettlementPostingResult?> GetPostedResultAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ExistingPostedResult);

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

        public Task<CustomerDepositParkingResult?> ParkExtraDepositAsync(
            ReceivePaymentDocument document,
            UserId createdByUserId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CustomerDepositParkingResult?>(null);
    }

    private sealed class FakePayBillRepository : IPayBillDocumentRepository
    {
        public SettlementDraftPreparationResult PreparedResult { get; } =
            new(Guid.NewGuid(), "EN2026000002", "PB-000001", 1, 95m, "draft");

        public PayBillDraftPreparation? CapturedRequest { get; private set; }
        public PostedSettlementPostingResult? ExistingPostedResult { get; init; }
        public bool GetForPostingCalled { get; private set; }

        public Task<PayBillDocument?> GetForPostingAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken)
        {
            GetForPostingCalled = true;
            return Task.FromResult<PayBillDocument?>(null);
        }

        public Task<PostedSettlementPostingResult?> GetPostedResultAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ExistingPostedResult);

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

    private sealed class ThrowingPostingEngine : IPostingEngine
    {
        public Task<PostingResult> PostAsync(
            IPostingDocument document,
            PostingContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Posting engine should not run for already-posted settlement documents.");
    }

    private sealed class ThrowingSettlementApplicationRepository : ISettlementApplicationRepository
    {
        public Task ApplyReceivePaymentAsync(
            ReceivePaymentDocument document,
            UserId createdByUserId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Settlement application should not run for already-posted receive payments.");

        public Task ApplyCreditApplicationAsync(
            CreditApplicationDocument document,
            UserId createdByUserId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyPayBillAsync(
            PayBillDocument document,
            UserId createdByUserId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Settlement application should not run for already-posted pay bills.");

        public Task ApplyVendorCreditApplicationAsync(
            VendorCreditApplicationDocument document,
            UserId createdByUserId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OpenItemApplicationDrillDown>> ListApplicationsAsync(
            CompanyId companyId,
            string targetOpenItemType,
            Guid targetOpenItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenItemApplicationDrillDown>>([]);
    }
}
