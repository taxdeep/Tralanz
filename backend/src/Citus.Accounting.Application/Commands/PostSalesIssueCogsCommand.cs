using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PostSalesIssueCogsCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid SalesIssueDocumentId,
    string? IdempotencyKey = null);

public sealed record PostSalesIssueCogsCommandResult(
    Guid SalesIssueDocumentId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyPosted,
    decimal TotalAmountBase);
