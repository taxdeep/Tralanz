using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

public sealed record PostInvoiceDropShipCogsCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid InvoiceDocumentId,
    string? IdempotencyKey = null);

public sealed record PostInvoiceDropShipCogsCommandResult(
    Guid InvoiceDocumentId,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    bool AlreadyPosted,
    bool NoOp,
    decimal TotalAmountBase);
