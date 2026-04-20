using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostgreSqlPurchaseOrderThreeQuantityTruthTests
{
    [Fact]
    public async Task PurchaseOrderThreeQuantityTruth_CountsOnlyPostedExplicitAnchors()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h200_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var repository = new PostgresPurchaseOrderDocumentRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                new PostgresExecutionContextAccessor());
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var itemAId = Guid.NewGuid();
            var itemBId = Guid.NewGuid();

            var saved = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 20),
                    null,
                    "PO-VENDOR-1",
                    "H.20.0 test PO",
                    [
                        new PurchaseOrderDraftLineSaveModel(1, itemAId, 10m, "EA", "Item A", 10m),
                        new PurchaseOrderDraftLineSaveModel(2, itemBId, 5m, "EA", "Item B", 20m)
                    ]),
                CancellationToken.None);
            _ = await repository.IssueAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);

            var orderedOnly = await repository.GetThreeQuantitySummaryAsync(new(companyId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(orderedOnly);
            Assert.Equal(PurchaseOrderThreeQuantityStatusPolicy.OrderedOnly, orderedOnly!.QuantityStatus);
            Assert.Equal(15m, orderedOnly.OrderedQuantity);
            Assert.Equal(0m, orderedOnly.ReceivedQuantity);
            Assert.Equal(0m, orderedOnly.BilledQuantity);

            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 1, itemAId, quantity: 4m, status: "draft");
            await SeedBillAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 1, itemAId, quantity: 3m, status: "draft");

            var draftAnchorsIgnored = await repository.GetThreeQuantitySummaryAsync(new(companyId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(draftAnchorsIgnored);
            Assert.Equal(0m, draftAnchorsIgnored!.ReceivedQuantity);
            Assert.Equal(0m, draftAnchorsIgnored.BilledQuantity);

            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 1, itemAId, quantity: 4m, status: "posted");
            await SeedBillAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 1, itemAId, quantity: 3m, status: "posted");

            var partial = await repository.GetThreeQuantitySummaryAsync(new(companyId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(partial);
            Assert.Equal(PurchaseOrderThreeQuantityStatusPolicy.PartiallyBilled, partial!.QuantityStatus);
            Assert.Equal(4m, partial.ReceivedQuantity);
            Assert.Equal(3m, partial.BilledQuantity);
            Assert.Equal(11m, partial.RemainingToReceiveQuantity);
            Assert.Equal(12m, partial.RemainingToBillQuantity);

            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 1, itemAId, quantity: 7m, status: "posted");
            await SeedBillAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, lineNumber: 2, itemBId, quantity: 6m, status: "posted");

            var over = await repository.GetThreeQuantitySummaryAsync(new(companyId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(over);
            Assert.Equal(PurchaseOrderThreeQuantityStatusPolicy.OverReceived, over!.QuantityStatus);
            Assert.Equal(1, over.OverReceivedLineCount);
            Assert.Equal(1, over.OverBilledLineCount);
            Assert.Contains(over.Lines, static line => line.LineNumber == 1 && line.QuantityStatus == PurchaseOrderThreeQuantityStatusPolicy.OverReceived);
            Assert.Contains(over.Lines, static line => line.LineNumber == 2 && line.QuantityStatus == PurchaseOrderThreeQuantityStatusPolicy.OverBilled);

            var refreshed = await repository.RefreshQuantityDiscrepanciesAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(refreshed);
            Assert.Equal(2, refreshed!.OpenDiscrepancyCount);
            Assert.Contains(refreshed.Discrepancies, static lane => lane.DiscrepancyType == PurchaseOrderQuantityDiscrepancyPolicy.OverReceived);
            Assert.Contains(refreshed.Discrepancies, static lane => lane.DiscrepancyType == PurchaseOrderQuantityDiscrepancyPolicy.OverBilled);

            var retried = await repository.RefreshQuantityDiscrepanciesAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(retried);
            Assert.Equal(2, retried!.OpenDiscrepancyCount);

            var resolved = await repository.ReviewQuantityDiscrepancyAsync(
                new(companyId),
                new(userId),
                saved.DocumentId,
                1,
                PurchaseOrderQuantityDiscrepancyPolicy.OverReceived,
                PurchaseOrderQuantityDiscrepancyPolicy.Resolved,
                "Warehouse corrected the receiving investigation note.",
                CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.Equal(1, resolved!.OpenDiscrepancyCount);
            Assert.DoesNotContain(resolved.Discrepancies, static lane => lane.DiscrepancyType == PurchaseOrderQuantityDiscrepancyPolicy.OverReceived);

            var reopened = await repository.RefreshQuantityDiscrepanciesAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(reopened);
            Assert.Equal(2, reopened!.OpenDiscrepancyCount);

            var overrideAuthorized = await repository.ReviewQuantityDiscrepancyAsync(
                new(companyId),
                new(userId),
                saved.DocumentId,
                1,
                PurchaseOrderQuantityDiscrepancyPolicy.OverReceived,
                PurchaseOrderQuantityDiscrepancyPolicy.OverrideAuthorized,
                "Ops lead authorized tolerance investigation; execution remains separately governed.",
                CancellationToken.None);
            Assert.NotNull(overrideAuthorized);
            Assert.Equal(1, overrideAuthorized!.OpenDiscrepancyCount);
            Assert.Contains(overrideAuthorized.Discrepancies, static lane =>
                lane.DiscrepancyType == PurchaseOrderQuantityDiscrepancyPolicy.OverReceived &&
                lane.InvestigationStatus == PurchaseOrderQuantityDiscrepancyPolicy.OverrideAuthorized &&
                lane.ReviewNote is not null &&
                lane.ReviewedAt.HasValue);

            var preserved = await repository.RefreshQuantityDiscrepanciesAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);
            Assert.NotNull(preserved);
            Assert.Equal(1, preserved!.OpenDiscrepancyCount);
            Assert.Contains(preserved.Discrepancies, static lane =>
                lane.DiscrepancyType == PurchaseOrderQuantityDiscrepancyPolicy.OverReceived &&
                lane.InvestigationStatus == PurchaseOrderQuantityDiscrepancyPolicy.OverrideAuthorized);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PurchaseOrderAnchorGovernance_BlocksNonIssuedAndOverReceipt()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h201_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var executionContext = new PostgresExecutionContextAccessor();
            var poRepository = new PostgresPurchaseOrderDocumentRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                executionContext);
            var receiptRepository = new PostgresReceiptDocumentRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                executionContext);
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var saved = await poRepository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 20),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 10m, "EA")]),
                CancellationToken.None);

            var draftAnchor = new ReceiptDraftSaveModel(
                null,
                new(companyId),
                new(userId),
                vendorId,
                warehouseId,
                new DateOnly(2026, 4, 21),
                null,
                null,
                null,
                [new ReceiptDraftLineSaveModel(1, itemId, 8m, "EA", null, saved.DocumentId, 1)]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => receiptRepository.SaveDraftAsync(draftAnchor, CancellationToken.None));

            _ = await poRepository.IssueAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);
            var receipt = await receiptRepository.SaveDraftAsync(draftAnchor, CancellationToken.None);
            _ = await receiptRepository.PostAsync(new(companyId), new(userId), receipt.DocumentId, CancellationToken.None);

            var overReceipt = draftAnchor with
            {
                Lines = [new ReceiptDraftLineSaveModel(1, itemId, 3m, "EA", null, saved.DocumentId, 1)]
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiptRepository.SaveDraftAsync(overReceipt, CancellationToken.None));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PurchaseOrderBillPostingGovernance_BlocksBillAheadOfReceiptTruth()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h201_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var poRepository = new PostgresPurchaseOrderDocumentRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                new PostgresExecutionContextAccessor());
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var saved = await poRepository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 20),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 10m, "EA")]),
                CancellationToken.None);
            _ = await poRepository.IssueAsync(new(companyId), new(userId), saved.DocumentId, CancellationToken.None);

            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, 1, itemId, 4m, "posted");
            var coveredBillId = await SeedBillAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, 1, itemId, 4m, "submitted", vendorId);
            await poRepository.ValidateBillAnchorsForPostingAsync(new(companyId), coveredBillId, CancellationToken.None);

            var overBillId = await SeedBillAnchorAsync(schemaConnectionString, companyId, saved.DocumentId, 1, itemId, 5m, "submitted", vendorId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => poRepository.ValidateBillAnchorsForPostingAsync(new(companyId), overBillId, CancellationToken.None));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PurchaseOrderLifecycleGuard_CancelsOnlyUntouchedAndClosesOnlyFullyAligned()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h203_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var repository = new PostgresPurchaseOrderDocumentRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                new PostgresExecutionContextAccessor());
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var cancellableDraft = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 20),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 5m, "EA")]),
                CancellationToken.None);
            var cancelledDraft = await repository.CancelAsync(new(companyId), new(userId), cancellableDraft.DocumentId, CancellationToken.None);
            Assert.Equal(PurchaseOrderDocumentStatuses.Cancelled, cancelledDraft.Status);

            var untouchedIssued = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 21),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 5m, "EA")]),
                CancellationToken.None);
            _ = await repository.IssueAsync(new(companyId), new(userId), untouchedIssued.DocumentId, CancellationToken.None);
            var cancelledIssued = await repository.CancelAsync(new(companyId), new(userId), untouchedIssued.DocumentId, CancellationToken.None);
            Assert.Equal(PurchaseOrderDocumentStatuses.Cancelled, cancelledIssued.Status);

            var partiallyReceived = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 22),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 10m, "EA")]),
                CancellationToken.None);
            _ = await repository.IssueAsync(new(companyId), new(userId), partiallyReceived.DocumentId, CancellationToken.None);
            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, partiallyReceived.DocumentId, 1, itemId, 4m, "posted");
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CancelAsync(new(companyId), new(userId), partiallyReceived.DocumentId, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CloseAsync(new(companyId), new(userId), partiallyReceived.DocumentId, CancellationToken.None));

            var fullyAligned = await repository.SaveDraftAsync(
                new PurchaseOrderDraftSaveModel(
                    null,
                    new(companyId),
                    new(userId),
                    vendorId,
                    new DateOnly(2026, 4, 23),
                    null,
                    null,
                    null,
                    [new PurchaseOrderDraftLineSaveModel(1, itemId, 10m, "EA")]),
                CancellationToken.None);
            _ = await repository.IssueAsync(new(companyId), new(userId), fullyAligned.DocumentId, CancellationToken.None);
            await SeedReceiptAnchorAsync(schemaConnectionString, companyId, fullyAligned.DocumentId, 1, itemId, 10m, "posted");
            _ = await SeedBillAnchorAsync(schemaConnectionString, companyId, fullyAligned.DocumentId, 1, itemId, 10m, "posted", vendorId);

            var closed = await repository.CloseAsync(new(companyId), new(userId), fullyAligned.DocumentId, CancellationToken.None);
            Assert.Equal(PurchaseOrderDocumentStatuses.Closed, closed.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CancelAsync(new(companyId), new(userId), fullyAligned.DocumentId, CancellationToken.None));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    private static async Task SeedReceiptAnchorAsync(
        string connectionString,
        Guid companyId,
        Guid purchaseOrderId,
        int lineNumber,
        Guid itemId,
        decimal quantity,
        string status)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists receipts (
              id uuid primary key,
              company_id uuid not null,
              status text not null
            );

            create table if not exists receipt_lines (
              id uuid primary key,
              company_id uuid not null,
              receipt_id uuid not null,
              line_number integer not null,
              item_id uuid not null,
              quantity numeric(18,6) not null,
              uom_code text not null,
              purchase_order_id uuid null,
              purchase_order_line_number integer null
            );

            insert into receipts (id, company_id, status)
            values (@receipt_id, @company_id, @status);

            insert into receipt_lines (
              id,
              company_id,
              receipt_id,
              line_number,
              item_id,
              quantity,
              uom_code,
              purchase_order_id,
              purchase_order_line_number
            )
            values (
              gen_random_uuid(),
              @company_id,
              @receipt_id,
              @line_number,
              @item_id,
              @quantity,
              'EA',
              @purchase_order_id,
              @purchase_order_line_number
            );
            """;
        command.Parameters.AddWithValue("receipt_id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("line_number", lineNumber);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);
        command.Parameters.AddWithValue("purchase_order_line_number", lineNumber);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> SeedBillAnchorAsync(
        string connectionString,
        Guid companyId,
        Guid purchaseOrderId,
        int lineNumber,
        Guid itemId,
        decimal quantity,
        string status,
        Guid? vendorId = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists bills (
              id uuid primary key,
              company_id uuid not null,
              vendor_id uuid null,
              status text not null
            );

            create table if not exists bill_lines (
              id uuid primary key,
              company_id uuid not null,
              bill_id uuid not null,
              line_number integer not null,
              item_id uuid null,
              uom_code text null,
              quantity numeric(18,6) null,
              purchase_order_id uuid null,
              purchase_order_line_number integer null
            );

            insert into bills (id, company_id, vendor_id, status)
            values (@bill_id, @company_id, @vendor_id, @status);

            insert into bill_lines (
              id,
              company_id,
              bill_id,
              line_number,
              item_id,
              uom_code,
              quantity,
              purchase_order_id,
              purchase_order_line_number
            )
            values (
              gen_random_uuid(),
              @company_id,
              @bill_id,
              @line_number,
              @item_id,
              'EA',
              @quantity,
              @purchase_order_id,
              @purchase_order_line_number
            );
            """;
        var billId = Guid.NewGuid();
        command.Parameters.AddWithValue("bill_id", billId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("line_number", lineNumber);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);
        command.Parameters.AddWithValue("purchase_order_line_number", lineNumber);
        await command.ExecuteNonQueryAsync();
        return billId;
    }

    private static string? GetPostgreSqlConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB") ??
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB");

    private static async Task CreateSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"create schema {schemaName};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists {schemaName} cascade;";
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildSchemaConnectionString(string connectionString, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schemaName
        };
        return builder.ConnectionString;
    }
}
