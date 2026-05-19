namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Single authority for "can this AR / AP line legally attribute itself
/// to this task?" Called by line-write paths (invoice / bill / expense
/// / PO line creators in a follow-up batch) right before persistence
/// so the link can never settle on a task that doesn't belong to the
/// company or that's already past the point of useful attribution.
///
/// Rules enforced:
/// <list type="bullet">
///   <item>The task exists and belongs to the given company (cross-
///     company links are rejected even if the GUID happens to exist
///     elsewhere).</item>
///   <item>The task is in <c>open</c> or <c>completed</c> status —
///     <c>billed</c> tasks have already passed through AR, so adding
///     more cost/revenue lines under them would silently corrupt the
///     billed-margin read model; <c>canceled</c> tasks are terminal.</item>
/// </list>
///
/// Throws <see cref="InvalidOperationException"/> with a user-facing
/// message on every failure. Returns silently on success.
/// </summary>
public interface ITaskLineLinkValidator
{
    Task ValidateAsync(CompanyId companyId, Guid taskId, CancellationToken cancellationToken);
}
