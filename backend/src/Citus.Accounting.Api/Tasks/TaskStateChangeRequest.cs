namespace Citus.Accounting.Api.Tasks;

/// <summary>
/// Optional body for state-transition endpoints
/// (<c>/tasks/{id}/complete</c>, <c>/cancel</c>). When the caller
/// supplies a reason it lands in <c>task_state_transitions.reason</c>
/// for the audit trail; null is allowed.
/// </summary>
public sealed record TaskStateChangeRequest(string? Reason);
