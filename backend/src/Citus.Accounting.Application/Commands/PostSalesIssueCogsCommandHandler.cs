using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// M3 entry point — turns the inventory engine's already-emitted
/// <c>inventory_layer_consumptions</c> rows for a posted sales-issue
/// into a Dr COGS / Cr Inventory journal entry. Mirror of
/// <see cref="PostReceiptGrIrCommandHandler"/> for the outbound leg.
///
/// Idempotency is journal-layer: re-running on the same sales-issue
/// returns the existing JE rather than double-posting.
/// </summary>
public sealed class PostSalesIssueCogsCommandHandler
{
    private readonly ISalesIssueCogsPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostSalesIssueCogsCommandHandler(
        ISalesIssueCogsPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostSalesIssueCogsCommandResult> HandleAsync(
        PostSalesIssueCogsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PreparePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.SalesIssueDocumentId,
                ct);

            // Idempotent re-run: bridge already saw a posted JE for this
            // sales-issue. Surface the same result the original post would
            // have given so callers can replay safely.
            if (preparation.Document is null)
            {
                if (preparation.ExistingJournalEntryId is null ||
                    preparation.ExistingJournalEntryId.Value == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "Posting repository returned no document and no existing journal entry — cannot proceed.");
                }
                return new PostSalesIssueCogsCommandResult(
                    SalesIssueDocumentId: command.SalesIssueDocumentId,
                    JournalEntryId: preparation.ExistingJournalEntryId.Value,
                    JournalEntryDisplayNumber: preparation.ExistingJournalEntryDisplayNumber ?? string.Empty,
                    AlreadyPosted: true,
                    TotalAmountBase: 0m);
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"sales-issue-cogs:{command.CompanyId.Value}:{command.SalesIssueDocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostSalesIssueCogsCommandResult(
                SalesIssueDocumentId: command.SalesIssueDocumentId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AlreadyPosted: false,
                TotalAmountBase: document.TotalAmountBase);
        }, cancellationToken);
    }
}
