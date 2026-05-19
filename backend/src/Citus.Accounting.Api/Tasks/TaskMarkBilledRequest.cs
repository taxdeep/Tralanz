namespace Citus.Accounting.Api.Tasks;

/// <summary>
/// Body for <c>POST /accounting/tasks/billing/mark-billed</c>.
/// <see cref="CustomerId"/> is optional; when supplied the coordinator
/// verifies every linked task targets that customer (a single invoice
/// cannot bill across customers).
/// </summary>
public sealed record TaskMarkBilledRequest(
    Guid InvoiceId,
    Guid? CustomerId,
    IReadOnlyList<Guid> TaskIds);
