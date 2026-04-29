using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Wraps the two endpoints that drive Expense & Bills → Overview:
/// cash-outflow band (10 past + current + 3 forecast months) and
/// expense-over-time chart (accrual-basis cost per month, bills +
/// expenses combined). Mirror of <see cref="SalesOverviewClient"/>.
/// Vendor list + balances are read through the existing VendorClient
/// and ApAgingClient.
/// </summary>
public sealed class ExpenseOverviewClient(HttpClient httpClient, ILogger<ExpenseOverviewClient> logger)
{
    public async Task<ExpenseCashOutflowSummary?> GetCashOutflowAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/expense/cash-outflow?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Expense cash-outflow unavailable: company not provisioned in accounting core.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ExpenseCashOutflowSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load expense cash outflow.");
            return null;
        }
    }

    public async Task<ExpenseOverTimeSummary?> GetExpenseOverTimeAsync(
        Guid companyId,
        DateOnly fromDate,
        DateOnly toDate,
        bool compareToPreviousYear,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/expense/over-time" +
            $"?companyId={companyId:D}" +
            $"&fromDate={fromDate:yyyy-MM-dd}" +
            $"&toDate={toDate:yyyy-MM-dd}" +
            $"&compareToPreviousYear={(compareToPreviousYear ? "true" : "false")}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Expense-over-time unavailable: company not provisioned in accounting core.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ExpenseOverTimeSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load expense over time.");
            return null;
        }
    }
}
