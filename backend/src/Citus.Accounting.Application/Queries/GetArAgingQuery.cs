using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

public sealed record class GetArAgingQuery(
    CompanyId CompanyId,
    DateOnly AsOfDate);

public sealed record class ArAgingOpenItemAmount
{
    public Guid OpenItemId { get; init; }

    public Guid CustomerId { get; init; }

    public string CustomerEntityNumber { get; init; } = string.Empty;

    public string CustomerDisplayName { get; init; } = string.Empty;

    public bool CustomerIsActive { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string DisplayNumber { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public int DaysPastDue { get; init; }

    public string AgingBucket { get; init; } = "current";

    public string DocumentCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string BalanceSide { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal OriginalAmountTx { get; init; }

    public decimal OriginalAmountBase { get; init; }

    public decimal OpenAmountTx { get; init; }

    public decimal OpenAmountBase { get; init; }

    public decimal SignedOpenAmountTx { get; init; }

    public decimal SignedOpenAmountBase { get; init; }

    public bool IsOverdue => DaysPastDue > 0;

    public bool HasOutstandingBalance => SignedOpenAmountBase != 0m || SignedOpenAmountTx != 0m;

    public static ArAgingOpenItemAmount Create(
        Guid openItemId,
        Guid customerId,
        string customerEntityNumber,
        string customerDisplayName,
        bool customerIsActive,
        string sourceType,
        Guid sourceDocumentId,
        string displayNumber,
        DateOnly documentDate,
        DateOnly? dueDate,
        string documentCurrencyCode,
        string baseCurrencyCode,
        string balanceSide,
        string status,
        decimal originalAmountTx,
        decimal originalAmountBase,
        decimal openAmountTx,
        decimal openAmountBase,
        DateOnly asOfDate)
    {
        originalAmountTx = Round6(originalAmountTx);
        originalAmountBase = Round6(originalAmountBase);
        openAmountTx = Round6(openAmountTx);
        openAmountBase = Round6(openAmountBase);

        var normalizedBalanceSide = balanceSide.Trim().ToLowerInvariant();
        var sign = normalizedBalanceSide == "credit" ? -1m : 1m;
        var daysPastDue = CalculateDaysPastDue(dueDate, asOfDate);

        return new ArAgingOpenItemAmount
        {
            OpenItemId = openItemId,
            CustomerId = customerId,
            CustomerEntityNumber = customerEntityNumber.Trim(),
            CustomerDisplayName = customerDisplayName.Trim(),
            CustomerIsActive = customerIsActive,
            SourceType = sourceType.Trim(),
            SourceDocumentId = sourceDocumentId,
            DisplayNumber = displayNumber.Trim(),
            DocumentDate = documentDate,
            DueDate = dueDate,
            DaysPastDue = daysPastDue,
            AgingBucket = ResolveBucket(daysPastDue),
            DocumentCurrencyCode = documentCurrencyCode.Trim().ToUpperInvariant(),
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            BalanceSide = normalizedBalanceSide,
            Status = status.Trim(),
            OriginalAmountTx = originalAmountTx,
            OriginalAmountBase = originalAmountBase,
            OpenAmountTx = openAmountTx,
            OpenAmountBase = openAmountBase,
            SignedOpenAmountTx = Round6(openAmountTx * sign),
            SignedOpenAmountBase = Round6(openAmountBase * sign)
        };
    }

    private static int CalculateDaysPastDue(DateOnly? dueDate, DateOnly asOfDate)
    {
        if (!dueDate.HasValue || dueDate.Value >= asOfDate)
        {
            return 0;
        }

        return asOfDate.DayNumber - dueDate.Value.DayNumber;
    }

    private static string ResolveBucket(int daysPastDue) =>
        daysPastDue switch
        {
            <= 0 => "current",
            <= 30 => "1_30",
            <= 60 => "31_60",
            <= 90 => "61_90",
            _ => "over_90"
        };

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class ArAgingCustomerBalance
{
    public Guid CustomerId { get; init; }

    public string CustomerEntityNumber { get; init; } = string.Empty;

    public string CustomerDisplayName { get; init; } = string.Empty;

    public bool CustomerIsActive { get; init; }

    public int OpenItemCount { get; init; }

    public DateOnly? OldestDueDate { get; init; }

    public decimal CurrentAmountBase { get; init; }

    public decimal Days1To30AmountBase { get; init; }

    public decimal Days31To60AmountBase { get; init; }

    public decimal Days61To90AmountBase { get; init; }

    public decimal DaysOver90AmountBase { get; init; }

    public decimal TotalOverdueAmountBase { get; init; }

    public decimal TotalOutstandingAmountBase { get; init; }

    public IReadOnlyList<ArAgingOpenItemAmount> OpenItems { get; init; } = Array.Empty<ArAgingOpenItemAmount>();

    public static ArAgingCustomerBalance Create(
        Guid customerId,
        string customerEntityNumber,
        string customerDisplayName,
        bool customerIsActive,
        IEnumerable<ArAgingOpenItemAmount> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var orderedRows = rows
            .Where(static row => row.HasOutstandingBalance)
            .OrderBy(static row => row.DueDate)
            .ThenBy(static row => row.DocumentDate)
            .ThenBy(static row => row.DisplayNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var current = SumBucket(orderedRows, "current");
        var days1To30 = SumBucket(orderedRows, "1_30");
        var days31To60 = SumBucket(orderedRows, "31_60");
        var days61To90 = SumBucket(orderedRows, "61_90");
        var daysOver90 = SumBucket(orderedRows, "over_90");

        return new ArAgingCustomerBalance
        {
            CustomerId = customerId,
            CustomerEntityNumber = customerEntityNumber.Trim(),
            CustomerDisplayName = customerDisplayName.Trim(),
            CustomerIsActive = customerIsActive,
            OpenItemCount = orderedRows.Length,
            OldestDueDate = orderedRows
                .Where(static row => row.DueDate.HasValue)
                .Select(static row => row.DueDate)
                .Min(),
            CurrentAmountBase = current,
            Days1To30AmountBase = days1To30,
            Days31To60AmountBase = days31To60,
            Days61To90AmountBase = days61To90,
            DaysOver90AmountBase = daysOver90,
            TotalOverdueAmountBase = Round6(days1To30 + days31To60 + days61To90 + daysOver90),
            TotalOutstandingAmountBase = Round6(orderedRows.Sum(static row => row.SignedOpenAmountBase)),
            OpenItems = orderedRows
        };
    }

    private static decimal SumBucket(IEnumerable<ArAgingOpenItemAmount> rows, string bucket) =>
        Round6(rows.Where(row => row.AgingBucket == bucket).Sum(static row => row.SignedOpenAmountBase));

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class ArAgingReport
{
    public Guid CompanyId { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int CustomerCount { get; init; }

    public int OpenItemCount { get; init; }

    public decimal CurrentAmountBase { get; init; }

    public decimal Days1To30AmountBase { get; init; }

    public decimal Days31To60AmountBase { get; init; }

    public decimal Days61To90AmountBase { get; init; }

    public decimal DaysOver90AmountBase { get; init; }

    public decimal TotalOverdueAmountBase { get; init; }

    public decimal TotalOutstandingAmountBase { get; init; }

    public IReadOnlyList<ArAgingCustomerBalance> CustomerRows { get; init; } = Array.Empty<ArAgingCustomerBalance>();

    public IReadOnlyList<ArAgingOpenItemAmount> DetailRows { get; init; } = Array.Empty<ArAgingOpenItemAmount>();

    public static ArAgingReport Create(
        Guid companyId,
        DateOnly asOfDate,
        string baseCurrencyCode,
        IEnumerable<ArAgingOpenItemAmount> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var detailRows = rows
            .Where(static row => row.HasOutstandingBalance)
            .OrderBy(static row => row.CustomerDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.CustomerEntityNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.DueDate)
            .ThenBy(static row => row.DocumentDate)
            .ThenBy(static row => row.DisplayNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var customerRows = detailRows
            .GroupBy(
                static row => new
                {
                    row.CustomerId,
                    row.CustomerEntityNumber,
                    row.CustomerDisplayName,
                    row.CustomerIsActive
                })
            .Select(
                static group => ArAgingCustomerBalance.Create(
                    group.Key.CustomerId,
                    group.Key.CustomerEntityNumber,
                    group.Key.CustomerDisplayName,
                    group.Key.CustomerIsActive,
                    group))
            .OrderBy(static row => row.CustomerDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.CustomerEntityNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var current = Round6(customerRows.Sum(static row => row.CurrentAmountBase));
        var days1To30 = Round6(customerRows.Sum(static row => row.Days1To30AmountBase));
        var days31To60 = Round6(customerRows.Sum(static row => row.Days31To60AmountBase));
        var days61To90 = Round6(customerRows.Sum(static row => row.Days61To90AmountBase));
        var daysOver90 = Round6(customerRows.Sum(static row => row.DaysOver90AmountBase));

        return new ArAgingReport
        {
            CompanyId = companyId,
            AsOfDate = asOfDate,
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            CustomerCount = customerRows.Length,
            OpenItemCount = detailRows.Length,
            CurrentAmountBase = current,
            Days1To30AmountBase = days1To30,
            Days31To60AmountBase = days31To60,
            Days61To90AmountBase = days61To90,
            DaysOver90AmountBase = daysOver90,
            TotalOverdueAmountBase = Round6(days1To30 + days31To60 + days61To90 + daysOver90),
            TotalOutstandingAmountBase = Round6(customerRows.Sum(static row => row.TotalOutstandingAmountBase)),
            CustomerRows = customerRows,
            DetailRows = detailRows
        };
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
