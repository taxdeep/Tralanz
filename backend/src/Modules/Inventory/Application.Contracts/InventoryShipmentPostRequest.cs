namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryShipmentPostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly PostingDate,
    string? CarrierName,
    string? TrackingNumber,
    string? ShippingSlipNumber,
    string? SourceModule,
    Guid? SourceDocumentId,
    string? SourceDocumentNumber,
    string? Memo,
    IReadOnlyList<InventoryShipmentLineInput> Lines);
