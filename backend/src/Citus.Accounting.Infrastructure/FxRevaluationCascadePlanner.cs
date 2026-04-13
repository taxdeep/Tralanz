using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Infrastructure;

internal static class FxRevaluationCascadePlanner
{
    public static FxRevaluationCascadeUnwindPlanResult BuildPlan(
        Guid requestedDocumentId,
        string requestedDisplayNumber,
        IReadOnlyList<ActiveRevaluationBatch> activeBatches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedDisplayNumber);
        ArgumentNullException.ThrowIfNull(activeBatches);

        if (activeBatches.Count == 0)
        {
            throw new InvalidOperationException(
                $"FX revaluation batch {requestedDisplayNumber} has no active revaluation chain left to unwind.");
        }

        var requestedBatch = activeBatches.FirstOrDefault(batch => batch.DocumentId == requestedDocumentId);
        if (requestedBatch is null)
        {
            throw new InvalidOperationException(
                $"FX revaluation batch {requestedDisplayNumber} is no longer active in the revaluation chain and cannot prepare cascade unwind.");
        }

        var nextBatch = activeBatches[0];
        var steps = activeBatches
            .Select(batch => new FxRevaluationCascadeUnwindPlanStep(
                batch.DocumentId,
                batch.DisplayNumber,
                batch.RevaluationDate,
                batch.PostedAt,
                batch.DocumentId == requestedDocumentId,
                batch.DocumentId == nextBatch.DocumentId))
            .ToArray();

        return new FxRevaluationCascadeUnwindPlanResult(
            requestedDocumentId,
            requestedDisplayNumber,
            nextBatch.DocumentId,
            nextBatch.DisplayNumber,
            nextBatch.DocumentId == requestedDocumentId,
            steps);
    }

    internal sealed record ActiveRevaluationBatch(
        Guid DocumentId,
        string DisplayNumber,
        DateOnly RevaluationDate,
        DateTimeOffset PostedAt);
}
