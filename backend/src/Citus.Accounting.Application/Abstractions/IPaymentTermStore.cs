namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company payment-term catalog. Backs the Settings → Payment Terms
/// surface and the per-vendor (and later per-customer) Payment Term
/// pickers. V1 is intentionally minimal: identity, code, name, net days,
/// active flag. Discount terms (e.g. 2/10 net 30) and proximo schedules
/// land in a later batch — net_days alone is enough to drive bill due
/// dates.
/// </summary>
public sealed record PaymentTermRecord(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    int NetDays,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PaymentTermUpsertInput(
    string Code,
    string Name,
    int NetDays,
    bool IsActive);

public interface IPaymentTermStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentTermRecord>> ListAsync(
        Guid companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> GetByIdAsync(
        Guid companyId,
        Guid paymentTermId,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord> CreateAsync(
        Guid companyId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> UpdateAsync(
        Guid companyId,
        Guid paymentTermId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> SetActiveAsync(
        Guid companyId,
        Guid paymentTermId,
        bool isActive,
        CancellationToken cancellationToken);
}
