using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresCustomerDepositReader : ICustomerDepositReader
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresCustomerDepositReader(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<SalesOrderCustomerDepositSummary> GetForSalesOrderAsync(
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        if (salesOrderId == Guid.Empty)
        {
            throw new ArgumentException("Sales-order id is required.", nameof(salesOrderId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // JOIN deposits → ar_open_items so applied = original - open. The
        // applied figure is always derivable; we don't need a separate
        // "applied_amount" column on customer_deposits because the open
        // item is the system of record for the running balance.
        await using var command = scope.CreateCommand(
            """
            select
              cd.id,
              cd.display_number,
              cd.deposit_date,
              cd.original_amount_base,
              coalesce(oi.open_amount_base, 0) as open_amount_base,
              cd.status,
              cd.posted_at
            from customer_deposits cd
            left join ar_open_items oi
              on oi.company_id = cd.company_id
             and oi.source_type = 'customer_deposit'
             and oi.source_id = cd.id
            where cd.company_id = @company_id
              and cd.source_sales_order_id = @so_id
            order by cd.deposit_date asc, cd.created_at asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("so_id", salesOrderId);

        var deposits = new List<CustomerDepositRow>();
        decimal totalOriginal = 0m;
        decimal totalOpen = 0m;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var original = reader.GetDecimal(3);
            var open = reader.GetDecimal(4);
            var applied = Math.Round(original - open, 6, MidpointRounding.ToEven);
            deposits.Add(new CustomerDepositRow(
                Id: reader.GetGuid(0),
                DisplayNumber: reader.GetString(1),
                DepositDate: reader.GetFieldValue<DateOnly>(2),
                OriginalAmountBase: original,
                AppliedAmountBase: applied,
                OpenAmountBase: open,
                Status: reader.GetString(5),
                PostedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
            totalOriginal += original;
            totalOpen += open;
        }

        var totalApplied = Math.Round(totalOriginal - totalOpen, 6, MidpointRounding.ToEven);
        return new SalesOrderCustomerDepositSummary(
            SalesOrderId: salesOrderId,
            TotalOriginalBase: totalOriginal,
            TotalAppliedBase: totalApplied,
            TotalOpenBase: totalOpen,
            Deposits: deposits);
    }
}
