using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Generates and operates the Action Center task list. Generation is
/// deterministic: registered providers each emit drafts; this service
/// dedupes by fingerprint and inserts new ones. Existing rows for the
/// same fingerprint are left alone so user-visible status is preserved.
/// </summary>
public sealed class ActionCenterTaskService : IActionCenterTaskService
{
    private readonly IActionCenterTaskStore _store;
    private readonly IActionCenterTaskEventStore _events;
    private readonly IAiJobRunStore _jobRuns;
    private readonly IEnumerable<IActionCenterTaskProvider> _providers;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<ActionCenterTaskService> _logger;

    public ActionCenterTaskService(
        IActionCenterTaskStore store,
        IActionCenterTaskEventStore events,
        IAiJobRunStore jobRuns,
        IEnumerable<IActionCenterTaskProvider> providers,
        UnityAiFeatureFlagAccessor flags,
        ILogger<ActionCenterTaskService> logger)
    {
        _store = store;
        _events = events;
        _jobRuns = jobRuns;
        _providers = providers;
        _flags = flags;
        _logger = logger;
    }

    public async Task<ActionCenterGenerationResult> RegenerateAsync(
        CompanyId companyId,
        UserId? userId,
        CancellationToken cancellationToken)
    {
        if (!_flags.ActionCenterEnabled)
        {
            return new ActionCenterGenerationResult(0, 0, 0, new[] { "action center disabled by feature flag" });
        }

        var asOf = DateTimeOffset.UtcNow;
        var jobRunId = await _jobRuns.StartAsync(
            companyId,
            AiJobType.ActionCenterGeneration,
            AiJobRunTriggerType.Manual,
            userId,
            null, null,
            inputSummaryJson: null,
            cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        var deduped = 0;
        var warnings = new List<string>();

        try
        {
            foreach (var provider in _providers)
            {
                IReadOnlyList<ActionCenterTaskDraft> drafts;
                try
                {
                    drafts = await provider.GenerateAsync(companyId, userId, asOf, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "action center provider {Provider} failed for company {CompanyId}", provider.ProviderName, companyId);
                    warnings.Add($"{provider.ProviderName}: {ex.Message}");
                    continue;
                }

                foreach (var draft in drafts)
                {
                    var existing = await _store.GetByFingerprintAsync(companyId, draft.Fingerprint, cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        deduped++;
                        continue;
                    }

                    var record = new ActionCenterTaskRecord(
                        Id: Guid.NewGuid(),
                        CompanyId: draft.CompanyId,
                        AssignedUserId: draft.AssignedUserId,
                        TaskType: draft.TaskType,
                        SourceEngine: draft.SourceEngine,
                        SourceType: draft.SourceType,
                        SourceObjectId: draft.SourceObjectId,
                        Title: draft.Title,
                        Description: draft.Description,
                        Reason: draft.Reason,
                        EvidenceJson: draft.EvidenceJson,
                        Priority: draft.Priority,
                        DueDate: draft.DueDate,
                        ActionUrl: draft.ActionUrl,
                        Status: ActionCenterTaskStatus.Open,
                        Fingerprint: draft.Fingerprint,
                        AiGenerated: draft.AiGenerated,
                        Confidence: draft.Confidence,
                        CreatedAt: asOf,
                        UpdatedAt: asOf,
                        CompletedAt: null,
                        DismissedAt: null,
                        SnoozedUntil: null);

                    var newId = await _store.InsertAsync(record, cancellationToken).ConfigureAwait(false);
                    await _events.RecordAsync(
                        companyId, newId, userId,
                        ActionCenterTaskEventType.Created,
                        metadataJson: null,
                        asOf, cancellationToken).ConfigureAwait(false);

                    inserted++;
                }
            }

            await _jobRuns.CompleteAsync(
                jobRunId,
                warnings.Count == 0 ? AiJobRunStatus.Succeeded : AiJobRunStatus.Partial,
                outputSummaryJson: System.Text.Json.JsonSerializer.Serialize(new { inserted, deduped, warnings = warnings.Count }),
                errorMessage: null,
                warningsJson: warnings.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(warnings),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _jobRuns.CompleteAsync(jobRunId, AiJobRunStatus.Failed, null, ex.Message, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new ActionCenterGenerationResult(inserted, 0, deduped, warnings);
    }

    public Task<IReadOnlyList<ActionCenterTaskRecord>> GetTasksAsync(
        CompanyId companyId,
        UserId? assignedUserId,
        IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken)
        => _store.GetTasksAsync(companyId, assignedUserId, statuses, cancellationToken);

    public async Task<ActionCenterTaskRecord?> StartAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken)
        => await TransitionAsync(companyId, taskId, actorUserId, ActionCenterTaskStatus.InProgress, ActionCenterTaskEventType.Started, cancellationToken).ConfigureAwait(false);

    public async Task<ActionCenterTaskRecord?> CompleteAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken)
        => await TransitionAsync(companyId, taskId, actorUserId, ActionCenterTaskStatus.Done, ActionCenterTaskEventType.Completed, cancellationToken).ConfigureAwait(false);

    public async Task<ActionCenterTaskRecord?> DismissAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, CancellationToken cancellationToken)
        => await TransitionAsync(companyId, taskId, actorUserId, ActionCenterTaskStatus.Dismissed, ActionCenterTaskEventType.Dismissed, cancellationToken).ConfigureAwait(false);

    public async Task<ActionCenterTaskRecord?> SnoozeAsync(CompanyId companyId, Guid taskId, UserId? actorUserId, DateTimeOffset until, CancellationToken cancellationToken)
        => await TransitionAsync(companyId, taskId, actorUserId, ActionCenterTaskStatus.Snoozed, ActionCenterTaskEventType.Snoozed, cancellationToken, snoozedUntil: until).ConfigureAwait(false);

    private async Task<ActionCenterTaskRecord?> TransitionAsync(
        CompanyId companyId,
        Guid taskId,
        UserId? actorUserId,
        string newStatus,
        string eventType,
        CancellationToken cancellationToken,
        DateTimeOffset? snoozedUntil = null)
    {
        var existing = await _store.GetByIdAsync(companyId, taskId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = string.Equals(newStatus, ActionCenterTaskStatus.Done, StringComparison.OrdinalIgnoreCase) ? now : existing.CompletedAt;
        DateTimeOffset? dismissedAt = string.Equals(newStatus, ActionCenterTaskStatus.Dismissed, StringComparison.OrdinalIgnoreCase) ? now : existing.DismissedAt;

        await _store.UpdateStatusAsync(
            companyId, taskId, newStatus,
            completedAt, dismissedAt, snoozedUntil ?? existing.SnoozedUntil,
            now, cancellationToken).ConfigureAwait(false);

        await _events.RecordAsync(companyId, taskId, actorUserId, eventType, metadataJson: null, now, cancellationToken).ConfigureAwait(false);

        return existing with
        {
            Status = newStatus,
            CompletedAt = completedAt,
            DismissedAt = dismissedAt,
            SnoozedUntil = snoozedUntil ?? existing.SnoozedUntil,
            UpdatedAt = now,
        };
    }
}
