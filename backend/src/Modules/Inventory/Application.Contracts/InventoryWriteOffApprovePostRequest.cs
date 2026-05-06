namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWriteOffApprovePostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid DocumentId);
