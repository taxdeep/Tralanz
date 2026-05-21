using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// P0-2 (C2): mirror of <see cref="PostSalesIssueCogsCommandHandler"/> for
/// the invoice-reverse compensation leg. Reads the same consumption rows
/// the forward post used and posts a Dr Inventory / Cr COGS journal entry
/// at identical per-account amounts, so the GL Inventory Asset balance
/// reconciles after invoice reverse.
///
/// Idempotent at the journal layer: a re-run on the same sales-issue
/// returns the previously-posted reverse JE instead of double-posting.
/// Soft-handles the case where the forward COGS JE was never posted
/// (PostInvoiceCommandHandler.TryAutoPostCogsAsync soft-failed) by
/// returning a result with <see cref="PostSalesIssueCogsReverseCommandResult.ForwardNotPosted"/>
/// = true — no GL effect needed.
/// </summary>
public sealed class PostSalesIssueCogsReverseCommandHandler
{
    private readonly ISalesIssueCogsPostingRepository _postingRepository;
    private readonly IPostingEngine _postingEngine;
    private readonly IUnitOfWork _unitOfWork;

    public PostSalesIssueCogsReverseCommandHandler(
        ISalesIssueCogsPostingRepository postingRepository,
        IPostingEngine postingEngine,
        IUnitOfWork unitOfWork)
    {
        _postingRepository = postingRepository ?? throw new ArgumentNullException(nameof(postingRepository));
        _postingEngine = postingEngine ?? throw new ArgumentNullException(nameof(postingEngine));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public Task<PostSalesIssueCogsReverseCommandResult> HandleAsync(
        PostSalesIssueCogsReverseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var preparation = await _postingRepository.PrepareReversePostingDocumentAsync(
                command.CompanyId,
                command.UserId,
                command.SalesIssueDocumentId,
                ct);

            // Idempotent re-run: a reverse JE already exists for this
            // sales-issue. Surface its identifiers so the orchestrator
            // can include them in the audit payload.
            if (preparation.Document is null &&
                preparation.ExistingReverseJournalEntryId is { } existingReverseId &&
                existingReverseId != Guid.Empty)
            {
                return new PostSalesIssueCogsReverseCommandResult(
                    SalesIssueDocumentId: command.SalesIssueDocumentId,
                    JournalEntryId: existingReverseId,
                    JournalEntryDisplayNumber: preparation.ExistingReverseJournalEntryDisplayNumber ?? string.Empty,
                    AlreadyReversed: true,
                    ForwardNotPosted: false,
                    TotalAmountBase: 0m);
            }

            // No forward JE was ever posted — nothing to compensate on the
            // GL side. Inventory subledger reverse still runs upstream.
            if (preparation.Document is null && preparation.ForwardNotPosted)
            {
                return new PostSalesIssueCogsReverseCommandResult(
                    SalesIssueDocumentId: command.SalesIssueDocumentId,
                    JournalEntryId: null,
                    JournalEntryDisplayNumber: null,
                    AlreadyReversed: false,
                    ForwardNotPosted: true,
                    TotalAmountBase: 0m);
            }

            if (preparation.Document is null)
            {
                throw new InvalidOperationException(
                    "Reverse posting preparation returned no document and no existing reverse journal entry — cannot proceed.");
            }

            var document = preparation.Document;

            var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"sales-issue-cogs-reverse:{command.CompanyId.Value}:{command.SalesIssueDocumentId}"
                : command.IdempotencyKey.Trim();

            var postingContext = new PostingContext(
                command.CompanyId,
                command.UserId,
                document.BaseCurrencyCode,
                AcceptedFxSnapshotId: null,
                idempotencyKey,
                DateTimeOffset.UtcNow);

            var result = await _postingEngine.PostAsync(document, postingContext, ct);

            return new PostSalesIssueCogsReverseCommandResult(
                SalesIssueDocumentId: command.SalesIssueDocumentId,
                JournalEntryId: result.JournalEntryId,
                JournalEntryDisplayNumber: result.JournalEntryDisplayNumber,
                AlreadyReversed: false,
                ForwardNotPosted: false,
                TotalAmountBase: document.TotalAmountBase);
        }, cancellationToken);
    }
}
