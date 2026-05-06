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
    CompanyId CompanyId,
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
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid paymentTermId,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord> CreateAsync(
        CompanyId companyId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> UpdateAsync(
        CompanyId companyId,
        Guid paymentTermId,
        PaymentTermUpsertInput input,
        CancellationToken cancellationToken);

    Task<PaymentTermRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid paymentTermId,
        bool isActive,
        CancellationToken cancellationToken);
}
