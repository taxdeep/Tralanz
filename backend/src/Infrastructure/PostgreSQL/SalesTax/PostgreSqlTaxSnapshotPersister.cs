// PostgreSqlTaxSnapshotPersister — writes engine output to
// document_line_sales_tax_snapshots.
//
// Designed to be invoked from inside the host repository's save-draft
// transaction so the snapshot rows commit (or roll back) atomically
// with the line inserts. Re-saves replace prior snapshot rows via
// the (document_type, document_id, line_id, sequence, leg) unique
// constraint + DO UPDATE upsert.
//
// One BatchCommand per line; component rows go in a single
// parameterized INSERT to keep the round-trip count flat.

using System.Data;
using Citus.Modules.SalesTax.Application.Contracts;
using Citus.Modules.SalesTax.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.SalesTax;

public sealed class PostgreSqlTaxSnapshotPersister : ITaxSnapshotPersister
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlTaxSnapshotPersister(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task PersistAsync(
        string companyId,
        string documentType,
        Guid documentId,
        IReadOnlyList<(Guid LineId, SalesTaxLineResult Result)> lineResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(lineResults);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Replace-prior-snapshots semantics: easiest correct policy is
        // delete-all-for-document then re-insert. The natural-key
        // ON CONFLICT path would also work, but a draft re-save can
        // shrink line count (operator removed a row), and orphan
        // snapshots from the removed line would persist otherwise.
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                delete from document_line_sales_tax_snapshots
                 where company_id = @company_id
                   and document_type = @document_type
                   and document_id = @document_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", companyId);
            deleteCommand.Parameters.AddWithValue("document_type", documentType);
            deleteCommand.Parameters.AddWithValue("document_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (lineId, result) in lineResults)
        {
            foreach (var snapshot in result.Snapshots)
            {
                await InsertOneAsync(
                    connection, transaction,
                    companyId, documentType, documentId, lineId,
                    snapshot,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertOneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string companyId,
        string documentType,
        Guid documentId,
        Guid lineId,
        TaxSnapshotDraft snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into document_line_sales_tax_snapshots (
                id, company_id, document_type, document_id, line_id, sequence, leg,
                tax_code_id, component_id, jurisdiction_id,
                code_snapshot, name_snapshot, regime_type_snapshot, treatment_snapshot,
                rate_percent_snapshot, is_compound_snapshot, reporting_box_codes,
                taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount,
                document_currency_code, tax_amount_base, fx_rate_snapshot, computed_at
            ) values (
                @id, @company_id, @document_type, @document_id, @line_id, @sequence, @leg,
                @tax_code_id, @component_id, @jurisdiction_id,
                @code, @name, @regime, @treatment,
                @rate, @compound, @boxes,
                @taxable, @tax, @recoverable, @non_recoverable,
                @currency, @tax_base, @fx, now()
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("line_id", lineId);
        command.Parameters.AddWithValue("sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("leg", snapshot.Leg);
        command.Parameters.AddWithValue("tax_code_id", snapshot.TaxCodeId);
        command.Parameters.AddWithValue("component_id", snapshot.ComponentId);
        command.Parameters.AddWithValue("jurisdiction_id", snapshot.JurisdictionId);
        command.Parameters.AddWithValue("code", snapshot.CodeSnapshot);
        command.Parameters.AddWithValue("name", snapshot.NameSnapshot);
        command.Parameters.AddWithValue("regime", snapshot.RegimeTypeSnapshot);
        command.Parameters.AddWithValue("treatment", snapshot.TreatmentSnapshot);
        command.Parameters.AddWithValue("rate", snapshot.RatePercentSnapshot);
        command.Parameters.AddWithValue("compound", snapshot.IsCompoundSnapshot);
        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "boxes",
            Value = snapshot.ReportingBoxCodes.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
        command.Parameters.AddWithValue("taxable", snapshot.TaxableAmount);
        command.Parameters.AddWithValue("tax", snapshot.TaxAmount);
        command.Parameters.AddWithValue("recoverable", snapshot.RecoverableAmount);
        command.Parameters.AddWithValue("non_recoverable", snapshot.NonRecoverableAmount);
        command.Parameters.AddWithValue("currency", snapshot.DocumentCurrencyCode);
        command.Parameters.AddWithValue("tax_base", snapshot.TaxAmountBase);
        command.Parameters.AddWithValue("fx", snapshot.FxRateSnapshot);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
