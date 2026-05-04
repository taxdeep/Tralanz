namespace Citus.Accounting.Api;

/// <summary>
/// Wire shape for <c>PUT /accounting/warehouses/{id}</c> — rename /
/// re-describe an existing warehouse. WarehouseCode is included for
/// completeness even though V1 inventory locks the default
/// "MAIN" code; future ERP tier may let operators rename codes too.
/// </summary>
public sealed record WarehouseRenameHttpRequest(
    string? WarehouseCode,
    string Name,
    string? Description);
