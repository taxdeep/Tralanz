namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventorySalesIssuePostRequest(
    Guid CompanyId,
    Guid UserId,
    Guid CustomerId,
    DateOnly PostingDate,
    string? SourceModule,
    Guid? SourceDocumentId,
    string? SourceDocumentNumber,
    string? Memo,
    IReadOnlyList<InventorySalesIssueLineInput> Lines);
