namespace Citus.Accounting.Application.Queries;

/// <summary>
/// Reporting basis for P&amp;L-style reports. Accrual (the GL's native basis,
/// recognised at posting date) or Cash (invoice/bill revenue and expense
/// shifted to the date their payment lands, proportional to the amount paid).
/// </summary>
public enum AccountingBasis
{
    Accrual = 0,
    Cash = 1,
}

public static class AccountingBasisExtensions
{
    public static AccountingBasis ParseBasis(string? raw) =>
        string.Equals(raw?.Trim(), "cash", StringComparison.OrdinalIgnoreCase)
            ? AccountingBasis.Cash
            : AccountingBasis.Accrual;

    public static string ToWireValue(this AccountingBasis basis) =>
        basis == AccountingBasis.Cash ? "cash" : "accrual";
}
