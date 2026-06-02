using Citus.Modules.Inventory.Domain.Shared;

namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class CompanyUomSummary(
    CompanyId CompanyId,
    string UomCode,
    string Name,
    string Category,
    int DecimalPlaces,
    bool IsActive,
    bool IsSystem);

public sealed record class CompanyUomUpsertRequest(
    CompanyId CompanyId,
    string UomCode,
    string Name,
    string? Category,
    int DecimalPlaces,
    bool IsActive);
