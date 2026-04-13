using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Journal;
using Citus.Accounting.Domain.Posting;

namespace Citus.Accounting.Application.Abstractions;

public interface IPostingEngine
{
    Task<PostingResult> PostAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken);
}

public interface IPostingValidator
{
    Task ValidateAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken);
}

public interface ITaxEngine
{
    Task<TaxComputationResult> CalculateAsync(
        IPostingDocument document,
        CancellationToken cancellationToken);
}

public interface IFxResolutionService
{
    Task<FxResolutionResult> ResolveAsync(
        FxResolutionRequest request,
        CancellationToken cancellationToken);
}

public interface IPostingFragmentBuilder
{
    Task<IReadOnlyList<PostingFragment>> BuildAsync(
        IPostingDocument document,
        TaxComputationResult taxResult,
        FxResolutionResult fxResult,
        CancellationToken cancellationToken);
}

public interface IJournalAggregator
{
    JournalEntryDraft Aggregate(
        IPostingDocument document,
        IReadOnlyList<PostingFragment> fragments,
        FxResolutionResult fxResult);
}

public interface IJournalEntryWriter
{
    Task<JournalEntryWriteResult> WriteAsync(
        JournalEntryDraft draft,
        PostingContext context,
        CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}

public sealed record JournalEntryWriteResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber);
