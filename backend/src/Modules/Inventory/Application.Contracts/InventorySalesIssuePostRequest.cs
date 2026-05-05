namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventorySalesIssuePostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly PostingDate,
    string? SourceModule,
    Guid? SourceDocumentId,
    string? SourceDocumentNumber,
    string? Memo,
    IReadOnlyList<InventorySalesIssueLineInput> Lines);
