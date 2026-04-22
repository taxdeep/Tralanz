namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryAvailabilityLedgerDrillDown(
    InventoryAvailabilityFilter AppliedFilter,
    string? ItemDisplayText,
    string? WarehouseDisplayText,
    IReadOnlyList<InventoryItemAvailabilitySummary> MatchingBalances,
    IReadOnlyList<InventoryLedgerEntrySummary> LedgerEntries);
