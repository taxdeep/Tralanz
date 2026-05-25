namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-company Units of Measure (UOM). Backs the inventory-item edit
/// form's UOM picker and drives qty input rules on Task / Invoice /
/// Bill line grids — the operator-meaningful field is
/// <see cref="DecimalPrecision"/> (0 = integer-only "each / case",
/// 2 = "hour / day / kg / m / L", 4-6 reserved for high-precision).
///
/// V1 is read-only: the 8 default UOMs are seeded per company by the
/// 2026-05-25-uom-foundation migration + a trigger that catches every
/// newly-created company. Operator-managed CRUD lands in a later batch
/// (Settings → UOM).
/// </summary>
public sealed record UomRecord(
    Guid Id,
    CompanyId CompanyId,
    string Code,
    string Name,
    int DecimalPrecision,
    string? Category,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IUomStore
{
    Task<IReadOnlyList<UomRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken);
}
