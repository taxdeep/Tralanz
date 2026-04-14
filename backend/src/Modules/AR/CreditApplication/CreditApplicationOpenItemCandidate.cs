namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationOpenItemCandidate(
    Guid OpenItemId,
    Guid CustomerId,
    string SourceType,
    Guid SourceId,
    string DisplayNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal OriginalAmountTx,
    decimal OpenAmountTx,
    decimal OpenAmountBase,
    string BalanceSide,
    string Status);
