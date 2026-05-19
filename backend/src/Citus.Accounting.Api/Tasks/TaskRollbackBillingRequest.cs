namespace Citus.Accounting.Api.Tasks;

/// <summary>
/// Body for <c>POST /accounting/tasks/billing/rollback</c>. Sent by
/// the AR void path (or a manual unbill) to flip every task billed
/// by the given invoice back to <c>Completed</c>.
/// </summary>
public sealed record TaskRollbackBillingRequest(
    Guid InvoiceId,
    string? Reason);
