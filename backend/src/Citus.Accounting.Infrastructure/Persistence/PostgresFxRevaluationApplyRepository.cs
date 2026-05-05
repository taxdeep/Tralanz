using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresFxRevaluationApplyRepository : IFxRevaluationApplyRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresFxRevaluationApplyRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task ApplyAsync(
        FxRevaluationDocument document,
        UserId appliedByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using (var existingCommand = scope.CreateCommand(
                         """
                         select applied_at
                         from fx_revaluation_batch_lines
                         where company_id = @company_id
                           and fx_revaluation_batch_id = @batch_id
                         order by line_number asc
                         limit 1;
                         """))
        {
            existingCommand.Parameters.AddWithValue("company_id", document.CompanyId);
            existingCommand.Parameters.AddWithValue("batch_id", document.Id);

            var appliedAt = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (appliedAt is not null && appliedAt != DBNull.Value)
            {
                return;
            }
        }

        foreach (var line in document.RevaluationLines)
        {
            var (tableName, partyColumn) = line.TargetOpenItemType switch
            {
                "ar_open_item" => ("ar_open_items", "customer_id"),
                "ap_open_item" => ("ap_open_items", "vendor_id"),
                _ => throw new InvalidOperationException(
                    $"FX revaluation line target type '{line.TargetOpenItemType}' is not supported.")
            };

            await using var updateOpenItemCommand = scope.CreateCommand(
                $"""
                update {tableName}
                set open_amount_base = @open_amount_base,
                    updated_at = now()
                where company_id = @company_id
                  and id = @target_open_item_id
                  and {partyColumn} = @party_id
                  and status in ('open', 'partially_applied');
                """);

            updateOpenItemCommand.Parameters.AddWithValue("open_amount_base", line.RevaluedAmountBase);
            updateOpenItemCommand.Parameters.AddWithValue("company_id", document.CompanyId);
            updateOpenItemCommand.Parameters.AddWithValue("target_open_item_id", line.TargetOpenItemId);
            updateOpenItemCommand.Parameters.AddWithValue("party_id", line.PartyId);

            var affectedRows = await updateOpenItemCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    $"FX revaluation apply step could not update target open item '{line.TargetOpenItemId}'.");
            }
        }

        await using var applyMarkerCommand = scope.CreateCommand(
            """
            update fx_revaluation_batch_lines
            set applied_at = now(),
                updated_at = now()
            where company_id = @company_id
              and fx_revaluation_batch_id = @batch_id
              and applied_at is null;
            """);

        applyMarkerCommand.Parameters.AddWithValue("company_id", document.CompanyId);
        applyMarkerCommand.Parameters.AddWithValue("batch_id", document.Id);
        await applyMarkerCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
