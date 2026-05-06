using System.Net;
using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// Wraps the two endpoints that drive the Sales & Get Paid → Overview
/// page: cash-flow band (10 past + current + 3 forecast months) and
/// income-over-time chart (accrual-basis revenue per month). Customer
/// list + balances are read through the existing CustomerClient and
/// ArAgingClient — this client only owns the new aggregations.
/// </summary>
public sealed class SalesOverviewClient(HttpClient httpClient, ILogger<SalesOverviewClient> logger)
{
    public async Task<SalesCashFlowSummary?> GetCashFlowAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/sales/cash-flow?companyId={companyId:D}&asOfDate={asOfDate:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Sales cash-flow unavailable: company not provisioned in accounting core.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SalesCashFlowSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load sales cash flow.");
            return null;
        }
    }

    public async Task<IncomeOverTimeSummary?> GetIncomeOverTimeAsync(
        CompanyId companyId,
        DateOnly fromDate,
        DateOnly toDate,
        bool compareToPreviousYear,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/sales/income-over-time" +
            $"?companyId={companyId:D}" +
            $"&fromDate={fromDate:yyyy-MM-dd}" +
            $"&toDate={toDate:yyyy-MM-dd}" +
            $"&compareToPreviousYear={(compareToPreviousYear ? "true" : "false")}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Income-over-time unavailable: company not provisioned in accounting core.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IncomeOverTimeSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load income over time.");
            return null;
        }
    }
}
