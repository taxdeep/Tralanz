using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory;
using Npgsql;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostgreSqlReceiptInventoryCostLayerEmissionIntegrationTests
{
    [Fact]
    public async Task EmitReceiptCostLayersAsync_EmitsOnceAndReconciles()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h10_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var connectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var foundationStore = new PostgreSqlInventoryFoundationStore(connectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(connectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(connectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(connectionFactory, foundationStore);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);

            var first = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            var second = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            var reconciliation = await emissionStore.GetReceiptCostLayerEmissionReconciliationSummaryAsync(companyId, receiptId, CancellationToken.None);
            var costLayerCount = await CountRowsAsync(schemaConnectionString, "inventory_cost_layers");

            Assert.Equal(ReceiptInventoryCostLayerEmissionStatusPolicy.FullyEmitted, first.EmissionStatus);
            Assert.Equal(ReceiptInventoryCostLayerEmissionStatusPolicy.FullyEmitted, second.EmissionStatus);
            Assert.Equal(1, costLayerCount);
            Assert.NotNull(reconciliation);
            Assert.Equal(ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.Reconciled, reconciliation.ReconciliationStatus);
            Assert.Equal(5m, reconciliation.EmittedQuantity);
            Assert.Equal(50m, reconciliation.CostLayerOriginalCostBase);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task RefreshReceiptGrIrBridgeAsync_CreatesEligibleControlTruthFromReconciledEmission()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h12_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var connectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var foundationStore = new PostgreSqlInventoryFoundationStore(connectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(connectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(connectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(connectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(connectionFactory, foundationStore);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);

            var first = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            var second = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            var rowCount = await CountRowsAsync(schemaConnectionString, "receipt_grir_bridge_lines");

            Assert.Equal(ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted, first.BridgeStatus);
            Assert.Equal(ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted, second.BridgeStatus);
            Assert.Equal(1, rowCount);
            Assert.Equal(1, second.BridgeLineCount);
            Assert.Equal(1, second.EligibleLineCount);
            Assert.Equal(0, second.BlockedReconciliationLineCount);
            Assert.Equal(5m, second.BridgeQuantity);
            Assert.Equal(50m, second.BridgeAmountBase);
            Assert.Equal(50m, second.EligibleAmountBase);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PostReceiptGrIrAsync_PostsJournalAndLinksBridgeThroughCompanyDefaultPolicy()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h13_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var postingRepository = new PostgresReceiptGrIrPostingRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var handler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                postingRepository,
                new DefaultPostingEngine(
                    new DefaultPostingValidator(),
                    new NullPostingPeriodPolicyValidator(),
                    new NullTaxEngine(),
                    new IdentityFxResolutionService(),
                    new AccountingPostingFragmentBuilder(),
                    new DefaultJournalAggregator(),
                    new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor)),
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);

            var first = await handler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            var second = await handler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            var bridgeSummary = await grIrBridgeStore.GetReceiptGrIrBridgeSummaryAsync(
                companyId,
                receiptId,
                CancellationToken.None);
            var journalEntryCount = await CountRowsAsync(schemaConnectionString, "journal_entries");
            var journalLineTotals = await GetJournalLineTotalsAsync(schemaConnectionString, first.JournalEntryId);

            Assert.Equal(first.JournalEntryId, second.JournalEntryId);
            Assert.Equal(1, journalEntryCount);
            Assert.Equal(50m, journalLineTotals.TotalDebit);
            Assert.Equal(50m, journalLineTotals.TotalCredit);
            Assert.NotNull(bridgeSummary);
            Assert.Equal(ReceiptGrIrBridgeStatusPolicy.Posted, bridgeSummary.BridgeStatus);
            Assert.Equal(1, bridgeSummary.PostedLineCount);
            Assert.Equal(50m, bridgeSummary.PostedAmountBase);
            Assert.Equal(first.JournalEntryId, bridgeSummary.JournalEntryId);
            Assert.Equal(first.JournalEntryDisplayNumber, bridgeSummary.JournalEntryDisplayNumber);
            Assert.NotNull(bridgeSummary.LastPostedAt);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task SaveDefaultGrIrClearingAccountAsync_RejectsNonLiabilityAccount()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h13_guard_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var repository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                new PostgresConnectionFactory(schemaConnectionString),
                new PostgresExecutionContextAccessor());
            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var assetAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedAccountAsync(schemaConnectionString, companyId, assetAccountId, "1200", "Inventory Asset", "asset");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveDefaultGrIrClearingAccountAsync(
                    new(companyId),
                    new(userId),
                    assetAccountId,
                    CancellationToken.None));

            Assert.Contains("active liability account", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task RefreshReceiptGrIrApSettlementControlAsync_CreatesEligibleAndPartialSettlementTruth()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h14_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var handler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                new PostgresReceiptGrIrPostingRepository(accountingConnectionFactory, executionContextAccessor),
                new DefaultPostingEngine(
                    new DefaultPostingValidator(),
                    new NullPostingPeriodPolicyValidator(),
                    new NullTaxEngine(),
                    new IdentityFxResolutionService(),
                    new AccountingPostingFragmentBuilder(),
                    new DefaultJournalAggregator(),
                    new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor)),
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementStore = new PostgresReceiptGrIrApSettlementControlStore(
                accountingConnectionFactory,
                executionContextAccessor);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SeedApOpenItemAsync(schemaConnectionString, companyId, billId);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await handler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);

            var eligible = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            await SetSettlementProgressAsync(schemaConnectionString, companyId, receiptId, settledQuantity: 2.5m, settledAmountBase: 25m);
            var partial = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var billSummary = await settlementStore.GetBillSettlementSummaryAsync(
                new(companyId),
                billId,
                CancellationToken.None);

            Assert.Equal(ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled, eligible.SettlementStatus);
            Assert.Equal(1, eligible.EligibleLineCount);
            Assert.Equal(50m, eligible.EligibleAmountBase);
            Assert.Equal(ReceiptGrIrApSettlementStatusPolicy.PartiallySettled, partial.SettlementStatus);
            Assert.Equal(1, partial.PartiallySettledLineCount);
            Assert.Equal(25m, partial.SettledAmountBase);
            Assert.Equal(25m, partial.EligibleAmountBase);
            Assert.Equal(25m, partial.RemainingAmountBase);
            Assert.NotNull(billSummary);
            Assert.Equal(ReceiptGrIrApSettlementStatusPolicy.PartiallySettled, billSummary.SettlementStatus);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task ExecuteReceiptGrIrSettlementAsync_ConsumesPartialRemainingSlicesIdempotently()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h15_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var postingHandler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                new PostgresReceiptGrIrPostingRepository(accountingConnectionFactory, executionContextAccessor),
                new DefaultPostingEngine(
                    new DefaultPostingValidator(),
                    new NullPostingPeriodPolicyValidator(),
                    new NullTaxEngine(),
                    new IdentityFxResolutionService(),
                    new AccountingPostingFragmentBuilder(),
                    new DefaultJournalAggregator(),
                    new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor)),
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementStore = new PostgresReceiptGrIrApSettlementControlStore(
                accountingConnectionFactory,
                executionContextAccessor);
            var settlementHandler = new ExecuteReceiptGrIrSettlementCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SeedApOpenItemAsync(schemaConnectionString, companyId, billId);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await postingHandler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            _ = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);

            var firstPartial = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: 25m,
                    IdempotencyKey: "h15-partial"),
                CancellationToken.None);
            var retryPartial = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: 25m,
                    IdempotencyKey: "h15-partial"),
                CancellationToken.None);
            var overSettlementError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementHandler.HandleAsync(
                    new ExecuteReceiptGrIrSettlementCommand(
                        new(companyId),
                        new(userId),
                        receiptId,
                        SettlementAmountBase: 30m,
                        IdempotencyKey: "h15-over"),
                    CancellationToken.None));
            var finalSettlement = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: null,
                    IdempotencyKey: "h15-final"),
                CancellationToken.None);
            var batchCount = await CountRowsAsync(schemaConnectionString, "receipt_grir_ap_settlement_batches");
            var batchLineCount = await CountRowsAsync(schemaConnectionString, "receipt_grir_ap_settlement_batch_lines");

            Assert.Equal(firstPartial.SettlementBatchId, retryPartial.SettlementBatchId);
            Assert.Equal(25m, firstPartial.SettledAmountBase);
            Assert.Equal(25m, retryPartial.SettledAmountBase);
            Assert.Equal(ReceiptGrIrApSettlementStatusPolicy.PartiallySettled, firstPartial.Summary.SettlementStatus);
            Assert.Equal(25m, firstPartial.Summary.RemainingAmountBase);
            Assert.Equal(25m, firstPartial.Summary.EligibleAmountBase);
            Assert.Contains("exceeds remaining eligible amount", overSettlementError.Message, StringComparison.Ordinal);
            Assert.Equal(ReceiptGrIrApSettlementStatusPolicy.Settled, finalSettlement.Summary.SettlementStatus);
            Assert.Equal(50m, finalSettlement.Summary.SettledAmountBase);
            Assert.Equal(0m, finalSettlement.Summary.RemainingAmountBase);
            Assert.Equal(2, batchCount);
            Assert.Equal(2, batchLineCount);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PostReceiptGrIrSettlementJournalAsync_PostsExecutedBatchAndDetectsVoidedJournal()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h16_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var postingEngine = new DefaultPostingEngine(
                new DefaultPostingValidator(),
                new NullPostingPeriodPolicyValidator(),
                new NullTaxEngine(),
                new IdentityFxResolutionService(),
                new AccountingPostingFragmentBuilder(),
                new DefaultJournalAggregator(),
                new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor));
            var grIrPostingHandler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                new PostgresReceiptGrIrPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementStore = new PostgresReceiptGrIrApSettlementControlStore(
                accountingConnectionFactory,
                executionContextAccessor);
            var settlementHandler = new ExecuteReceiptGrIrSettlementCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementPostingHandler = new PostReceiptGrIrSettlementJournalCommandHandler(
                new PostgresReceiptGrIrSettlementPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementClearingHandler = new ClearReceiptGrIrSettlementOpenItemCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();
            var billOffsetAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await SeedAccountAsync(schemaConnectionString, companyId, billOffsetAccountId, "5100", "Bill Goods Offset", "expense");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SeedBillLineAsync(schemaConnectionString, companyId, billId, billOffsetAccountId);
            await SeedApOpenItemAsync(schemaConnectionString, companyId, billId);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrPostingHandler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            _ = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var settlement = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: null,
                    IdempotencyKey: "h16-settlement"),
                CancellationToken.None);

            var firstPost = await settlementPostingHandler.HandleAsync(
                new PostReceiptGrIrSettlementJournalCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId,
                    IdempotencyKey: "h16-journal"),
                CancellationToken.None);
            var retryPost = await settlementPostingHandler.HandleAsync(
                new PostReceiptGrIrSettlementJournalCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId,
                    IdempotencyKey: "h16-journal"),
                CancellationToken.None);
            var journalTotals = await GetJournalLineTotalsAsync(schemaConnectionString, firstPost.JournalEntryId);
            var settlementBatchStatus = await GetSettlementBatchJournalStatusAsync(
                schemaConnectionString,
                companyId,
                settlement.SettlementBatchId);
            var postedJournalSummary = await settlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);

            await SetJournalStatusAsync(schemaConnectionString, firstPost.JournalEntryId, "void");
            var staleJournalSummary = await settlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var staleSettlementBatchStatus = await GetSettlementBatchJournalStatusAsync(
                schemaConnectionString,
                companyId,
                settlement.SettlementBatchId);
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementPostingHandler.HandleAsync(
                    new PostReceiptGrIrSettlementJournalCommand(
                        new(companyId),
                        new(userId),
                        receiptId,
                        settlement.SettlementBatchId,
                        IdempotencyKey: "h16-journal-after-void"),
                    CancellationToken.None));
            var staleClearing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementClearingHandler.HandleAsync(
                    new ClearReceiptGrIrSettlementOpenItemCommand(
                        new(companyId),
                        new(userId),
                        receiptId,
                        settlement.SettlementBatchId),
                    CancellationToken.None));

            Assert.Equal(firstPost.JournalEntryId, retryPost.JournalEntryId);
            Assert.Equal(50m, journalTotals.TotalDebit);
            Assert.Equal(50m, journalTotals.TotalCredit);
            Assert.Equal("posted", settlementBatchStatus.JournalStatus);
            Assert.Equal(firstPost.JournalEntryId, settlementBatchStatus.JournalEntryId);
            Assert.Equal(ReceiptGrIrApSettlementJournalStatusPolicy.Posted, postedJournalSummary.JournalReconciliationStatus);
            Assert.Equal(1, postedJournalSummary.JournalPostedBatchCount);
            Assert.Equal(ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale, staleJournalSummary.JournalReconciliationStatus);
            Assert.Equal(1, staleJournalSummary.JournalStaleBatchCount);
            Assert.Equal(ReceiptGrIrApSettlementJournalStatusPolicy.JournalStale, staleSettlementBatchStatus.JournalStatus);
            Assert.Equal(firstPost.JournalEntryId, staleSettlementBatchStatus.JournalEntryId);
            Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("journal status 'journal_stale'", staleClearing.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task ClearReceiptGrIrSettlementOpenItemsAsync_ConsumesPostedReconciledJournalAndIsIdempotent()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h18_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var postingEngine = new DefaultPostingEngine(
                new DefaultPostingValidator(),
                new NullPostingPeriodPolicyValidator(),
                new NullTaxEngine(),
                new IdentityFxResolutionService(),
                new AccountingPostingFragmentBuilder(),
                new DefaultJournalAggregator(),
                new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor));
            var grIrPostingHandler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                new PostgresReceiptGrIrPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementStore = new PostgresReceiptGrIrApSettlementControlStore(
                accountingConnectionFactory,
                executionContextAccessor);
            var settlementHandler = new ExecuteReceiptGrIrSettlementCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementPostingHandler = new PostReceiptGrIrSettlementJournalCommandHandler(
                new PostgresReceiptGrIrSettlementPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementClearingHandler = new ClearReceiptGrIrSettlementOpenItemCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementClearingReversalHandler = new ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();
            var billOffsetAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await SeedAccountAsync(schemaConnectionString, companyId, billOffsetAccountId, "5100", "Bill Goods Offset", "expense");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SeedBillLineAsync(schemaConnectionString, companyId, billId, billOffsetAccountId);
            await SeedApOpenItemAsync(schemaConnectionString, companyId, billId);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrPostingHandler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            _ = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var settlement = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: null,
                    IdempotencyKey: "h18-settlement"),
                CancellationToken.None);
            var settlementPost = await settlementPostingHandler.HandleAsync(
                new PostReceiptGrIrSettlementJournalCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId,
                    IdempotencyKey: "h18-journal"),
                CancellationToken.None);

            var firstClear = await settlementClearingHandler.HandleAsync(
                new ClearReceiptGrIrSettlementOpenItemCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId),
                CancellationToken.None);
            var retryClear = await settlementClearingHandler.HandleAsync(
                new ClearReceiptGrIrSettlementOpenItemCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId),
                CancellationToken.None);
            var apOpenItem = await GetApOpenItemStateByBillAsync(schemaConnectionString, companyId, billId);
            var applicationCount = await CountRowsAsync(schemaConnectionString, "settlement_applications");

            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared, firstClear.ClearingStatus);
            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared, retryClear.ClearingStatus);
            Assert.Equal(1, firstClear.ApplicationCount);
            Assert.Equal(1, retryClear.ApplicationCount);
            Assert.Equal(50m, firstClear.ClearedAmountBase);
            Assert.Equal(50m, retryClear.ClearedAmountBase);
            Assert.Equal(1, applicationCount);
            Assert.Equal(0m, apOpenItem.OpenAmountTx);
            Assert.Equal(0m, apOpenItem.OpenAmountBase);
            Assert.Equal("closed", apOpenItem.Status);
            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Cleared, firstClear.Summary.OpenItemClearingStatus);
            Assert.Equal(1, firstClear.Summary.OpenItemClearedBatchCount);
            var noVarianceSummary = await settlementStore.RefreshReceiptSettlementVarianceControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            Assert.Equal(ReceiptGrIrApPurchaseVarianceStatusPolicy.NoVariance, noVarianceSummary.PurchaseVarianceStatus);
            Assert.Equal(1, noVarianceSummary.PurchaseVarianceLineCount);
            Assert.Equal(0m, noVarianceSummary.PurchaseVarianceAmountBase);

            await SetJournalStatusAsync(schemaConnectionString, settlementPost.JournalEntryId, "void");
            var staleSummary = await settlementStore.RefreshReceiptSettlementJournalReconciliationAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var staleClear = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlementClearingHandler.HandleAsync(
                    new ClearReceiptGrIrSettlementOpenItemCommand(
                        new(companyId),
                        new(userId),
                        receiptId,
                        settlement.SettlementBatchId),
                    CancellationToken.None));

            var firstReverse = await settlementClearingReversalHandler.HandleAsync(
                new ReverseReceiptGrIrSettlementOpenItemClearingCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId),
                CancellationToken.None);
            var retryReverse = await settlementClearingReversalHandler.HandleAsync(
                new ReverseReceiptGrIrSettlementOpenItemClearingCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId),
                CancellationToken.None);
            var restoredOpenItem = await GetApOpenItemStateByBillAsync(schemaConnectionString, companyId, billId);
            var applicationCountAfterReverse = await CountRowsAsync(schemaConnectionString, "settlement_applications");

            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.ClearingStale, staleSummary.OpenItemClearingStatus);
            Assert.Equal(1, staleSummary.OpenItemStaleBatchCount);
            Assert.Contains("not eligible for clearing", staleClear.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed, firstReverse.ClearingStatus);
            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed, retryReverse.ClearingStatus);
            Assert.Equal(1, firstReverse.ReversedApplicationCount);
            Assert.Equal(1, retryReverse.ReversedApplicationCount);
            Assert.Equal(50m, firstReverse.RestoredAmountBase);
            Assert.Equal(50m, retryReverse.RestoredAmountBase);
            Assert.Equal(0, applicationCountAfterReverse);
            Assert.Equal(50m, restoredOpenItem.OpenAmountTx);
            Assert.Equal(50m, restoredOpenItem.OpenAmountBase);
            Assert.Equal("open", restoredOpenItem.Status);
            Assert.Equal(ReceiptGrIrApOpenItemClearingStatusPolicy.Reversed, firstReverse.Summary.OpenItemClearingStatus);
            Assert.Equal(1, firstReverse.Summary.OpenItemReversedBatchCount);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task RefreshReceiptSettlementVarianceControlAsync_SurfacesCandidateAfterClearedGrIrSettlement()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_h19_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var inventoryConnectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var accountingConnectionFactory = new PostgresConnectionFactory(schemaConnectionString);
            var executionContextAccessor = new PostgresExecutionContextAccessor();
            var foundationStore = new PostgreSqlInventoryFoundationStore(inventoryConnectionFactory);
            var activationStore = new PostgreSqlReceiptInventoryActivationStore(inventoryConnectionFactory, foundationStore);
            var valuationStore = new PostgreSqlReceiptInventoryValuationStore(inventoryConnectionFactory, foundationStore);
            var emissionStore = new PostgreSqlReceiptInventoryCostLayerEmissionStore(inventoryConnectionFactory, foundationStore);
            var grIrBridgeStore = new PostgreSqlReceiptGrIrBridgeStore(inventoryConnectionFactory, foundationStore);
            var clearingPolicyRepository = new PostgresReceiptGrIrClearingAccountPolicyRepository(
                accountingConnectionFactory,
                executionContextAccessor);
            var postingEngine = new DefaultPostingEngine(
                new DefaultPostingValidator(),
                new NullPostingPeriodPolicyValidator(),
                new NullTaxEngine(),
                new IdentityFxResolutionService(),
                new AccountingPostingFragmentBuilder(),
                new DefaultJournalAggregator(),
                new PostgresJournalEntryWriter(accountingConnectionFactory, executionContextAccessor));
            var grIrPostingHandler = new PostReceiptGrIrCommandHandler(
                grIrBridgeStore,
                clearingPolicyRepository,
                new PostgresReceiptGrIrPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementStore = new PostgresReceiptGrIrApSettlementControlStore(
                accountingConnectionFactory,
                executionContextAccessor);
            var settlementHandler = new ExecuteReceiptGrIrSettlementCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementPostingHandler = new PostReceiptGrIrSettlementJournalCommandHandler(
                new PostgresReceiptGrIrSettlementPostingRepository(accountingConnectionFactory, executionContextAccessor),
                postingEngine,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));
            var settlementClearingHandler = new ClearReceiptGrIrSettlementOpenItemCommandHandler(
                settlementStore,
                new PostgresUnitOfWork(accountingConnectionFactory, executionContextAccessor));

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var receiptId = Guid.NewGuid();
            var billId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var inventoryDocumentId = Guid.NewGuid();
            var inventoryDocumentLineId = Guid.NewGuid();
            var ledgerEntryId = Guid.NewGuid();
            var inventoryAssetAccountId = Guid.NewGuid();
            var grIrClearingAccountId = Guid.NewGuid();
            var billOffsetAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(schemaConnectionString, companyId, inventoryAssetAccountId, "1200", "Inventory Asset", "asset");
            await SeedAccountAsync(schemaConnectionString, companyId, grIrClearingAccountId, "2105", "GR/IR Clearing", "liability");
            await SeedAccountAsync(schemaConnectionString, companyId, billOffsetAccountId, "5100", "Bill Goods Offset", "expense");
            await clearingPolicyRepository.SaveDefaultGrIrClearingAccountAsync(
                new(companyId),
                new(userId),
                grIrClearingAccountId,
                CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);
            await SeedReceiptValuationFixtureAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId,
                ledgerEntryId);
            await SeedBillLineAsync(
                schemaConnectionString,
                companyId,
                billId,
                billOffsetAccountId,
                quantity: 5m,
                unitCost: 10m,
                lineAmount: 55m);
            await SeedApOpenItemAsync(schemaConnectionString, companyId, billId, amount: 55m);
            await SetDefaultInventoryAssetAccountAsync(
                schemaConnectionString,
                companyId,
                itemId,
                inventoryAssetAccountId);

            _ = await activationStore.GetReceiptActivationSummaryAsync(companyId, receiptId, CancellationToken.None);
            _ = await valuationStore.GetReceiptValuationSummaryAsync(companyId, receiptId, CancellationToken.None);
            await SeedActivationAndValuationRowsAsync(
                schemaConnectionString,
                companyId,
                userId,
                receiptId,
                billId,
                itemId,
                warehouseId,
                inventoryDocumentId,
                inventoryDocumentLineId);
            _ = await emissionStore.EmitReceiptCostLayersAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrBridgeStore.RefreshReceiptGrIrBridgeAsync(companyId, userId, receiptId, CancellationToken.None);
            _ = await grIrPostingHandler.HandleAsync(
                new PostReceiptGrIrCommand(new(companyId), new(userId), receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
                CancellationToken.None);
            _ = await settlementStore.RefreshReceiptSettlementControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var settlement = await settlementHandler.HandleAsync(
                new ExecuteReceiptGrIrSettlementCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    SettlementAmountBase: null,
                    IdempotencyKey: "h19-settlement"),
                CancellationToken.None);
            _ = await settlementPostingHandler.HandleAsync(
                new PostReceiptGrIrSettlementJournalCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId,
                    IdempotencyKey: "h19-journal"),
                CancellationToken.None);
            _ = await settlementClearingHandler.HandleAsync(
                new ClearReceiptGrIrSettlementOpenItemCommand(
                    new(companyId),
                    new(userId),
                    receiptId,
                    settlement.SettlementBatchId),
                CancellationToken.None);

            var varianceSummary = await settlementStore.RefreshReceiptSettlementVarianceControlAsync(
                new(companyId),
                new(userId),
                receiptId,
                CancellationToken.None);
            var billSummary = await settlementStore.GetBillSettlementSummaryAsync(
                new(companyId),
                billId,
                CancellationToken.None);
            var apOpenItem = await GetApOpenItemStateByBillAsync(schemaConnectionString, companyId, billId);

            Assert.Equal(ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement, varianceSummary.PurchaseVarianceStatus);
            Assert.Equal(1, varianceSummary.PurchaseVarianceLineCount);
            Assert.Equal(1, varianceSummary.PurchaseVarianceCandidateLineCount);
            Assert.Equal(5m, varianceSummary.PurchaseVarianceAmountBase);
            Assert.NotNull(billSummary);
            Assert.Equal(ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement, billSummary!.PurchaseVarianceStatus);
            Assert.Equal(5m, billSummary.PurchaseVarianceAmountBase);
            // Post-M4: clearing applies the bill-side proportional amount
            // (= grir + variance), so the AP open item fully closes — the
            // variance portion has already been booked to PPV by the
            // settlement journal, leaving no AP balance owed.
            Assert.Equal(0m, apOpenItem.OpenAmountBase);
            Assert.Equal("closed", apOpenItem.Status);
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    private static string? GetPostgreSqlConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB") ??
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB");

    private static string BuildSchemaConnectionString(string baseConnectionString, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schemaName
        };
        return builder.ConnectionString;
    }

    private static async Task CreateSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            create extension if not exists pgcrypto;
            create schema {schemaName};
            """;
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

    private static async Task SeedCompanyAsync(string connectionString, CompanyId companyId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table currency_catalog (
              code char(3) primary key,
              name text not null,
              minor_units integer not null default 2,
              is_active boolean not null default true
            );

            create table companies (
              id uuid primary key,
              entity_number text not null unique,
              legal_name text not null,
              base_currency_code char(3) not null references currency_catalog(code),
              multi_currency_enabled boolean not null default false,
              status text not null default 'active',
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table accounts (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null,
              code text not null,
              name text not null,
              root_type text not null,
              detail_type text null,
              is_active boolean not null default true,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            insert into currency_catalog (code, name)
            values ('USD', 'US Dollar');

            insert into companies (id, entity_number, legal_name, base_currency_code)
            values (@company_id, 'EN-H10-001', 'H10 Integration Company', 'USD');
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedPostingFoundationAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table company_numbering_sequences (
              company_id uuid not null references companies(id) on delete cascade,
              scope_key text not null,
              prefix text not null,
              next_number bigint not null,
              padding smallint not null,
              suggestion_enabled boolean not null default true,
              updated_at timestamptz not null default now(),
              primary key (company_id, scope_key)
            );

            create table journal_entries (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null,
              display_number text not null,
              status text not null,
              source_type text not null,
              source_id uuid not null,
              transaction_currency_code char(3) not null,
              base_currency_code char(3) not null,
              exchange_rate numeric(20, 8) not null,
              exchange_rate_date date not null,
              exchange_rate_source text not null,
              fx_rate_snapshot_id uuid null,
              total_tx_debit numeric(20, 6) not null,
              total_tx_credit numeric(20, 6) not null,
              total_debit numeric(20, 6) not null,
              total_credit numeric(20, 6) not null,
              posting_run_id uuid not null,
              idempotency_key text not null,
              posted_at timestamptz not null,
              created_by_user_id uuid not null,
              created_at timestamptz not null default now()
            );

            create unique index ux_journal_entries_idempotency
              on journal_entries (company_id, idempotency_key);

            create table journal_entry_lines (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              journal_entry_id uuid not null references journal_entries(id) on delete cascade,
              line_number integer not null,
              account_id uuid not null references accounts(id),
              description text null,
              party_type text null,
              party_id uuid null,
              tx_debit numeric(20, 6) not null,
              tx_credit numeric(20, 6) not null,
              debit numeric(20, 6) not null,
              credit numeric(20, 6) not null,
              tax_component_type text null,
              control_role text null,
              posting_role text null,
              source_line_number integer null
            );

            create table ledger_entries (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              journal_entry_id uuid not null references journal_entries(id) on delete cascade,
              journal_entry_line_id uuid not null references journal_entry_lines(id) on delete cascade,
              posting_date date not null,
              account_id uuid not null references accounts(id),
              debit numeric(20, 6) not null,
              credit numeric(20, 6) not null,
              transaction_currency_code char(3) not null,
              tx_debit numeric(20, 6) not null,
              tx_credit numeric(20, 6) not null
            );

            create table ap_open_items (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              vendor_id uuid not null,
              source_type text not null,
              source_id uuid not null,
              balance_side text not null,
              document_currency_code char(3) not null,
              base_currency_code char(3) not null,
              original_amount_tx numeric(20, 6) not null,
              original_amount_base numeric(20, 6) not null,
              open_amount_tx numeric(20, 6) not null,
              open_amount_base numeric(20, 6) not null,
              status text not null,
              due_date date null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table settlement_applications (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              application_type text not null,
              source_type text not null,
              source_id uuid not null,
              target_open_item_type text not null,
              target_open_item_id uuid not null,
              applied_amount_tx numeric(20, 6) not null,
              applied_amount_base numeric(20, 6) not null,
              settlement_fx_rate numeric(20, 10) null,
              realized_fx_amount numeric(20, 6) null,
              created_at timestamptz not null default now(),
              created_by_user_id uuid null
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAccountAsync(
        string connectionString,
        CompanyId companyId,
        Guid accountId,
        string code,
        string name,
        string rootType)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into accounts (
              id,
              company_id,
              entity_number,
              code,
              name,
              root_type,
              detail_type,
              is_active
            )
            values (
              @account_id,
              @company_id,
              @entity_number,
              @code,
              @name,
              @root_type,
              @root_type || '_detail',
              true
            );
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("entity_number", $"EN-{code}");
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("root_type", rootType);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedApOpenItemAsync(
        string connectionString,
        CompanyId companyId,
        Guid billId,
        decimal amount = 50m)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into ap_open_items (
              id,
              company_id,
              vendor_id,
              source_type,
              source_id,
              balance_side,
              document_currency_code,
              base_currency_code,
              original_amount_tx,
              original_amount_base,
              open_amount_tx,
              open_amount_base,
              status,
              due_date
            )
            values (
              gen_random_uuid(),
              @company_id,
              gen_random_uuid(),
              'bill',
              @bill_id,
              'credit',
              'USD',
              'USD',
              @amount,
              @amount,
              @amount,
              @amount,
              'open',
              '2026-04-19'
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_id", billId);
        command.Parameters.AddWithValue("amount", amount);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedBillLineAsync(
        string connectionString,
        CompanyId companyId,
        Guid billId,
        Guid expenseAccountId,
        decimal quantity = 5m,
        decimal unitCost = 10m,
        decimal lineAmount = 50m)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table if not exists bill_lines (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              bill_id uuid not null references bills(id) on delete cascade,
              line_number integer not null,
              expense_account_id uuid not null references accounts(id),
              description text not null,
              line_amount numeric(20, 6) not null,
              quantity numeric(18, 6) null,
              unit_cost numeric(18, 6) null,
              tax_amount numeric(20, 6) not null default 0,
              is_tax_recoverable boolean not null default false,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            insert into bill_lines (
              company_id,
              bill_id,
              line_number,
              expense_account_id,
              description,
              line_amount,
              quantity,
              unit_cost,
              tax_amount,
              is_tax_recoverable
            )
            values (
              @company_id,
              @bill_id,
              1,
              @expense_account_id,
              'Receipt matched goods',
              @line_amount,
              @quantity,
              @unit_cost,
              0,
              false
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_id", billId);
        command.Parameters.AddWithValue("expense_account_id", expenseAccountId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("unit_cost", unitCost);
        command.Parameters.AddWithValue("line_amount", lineAmount);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedReceiptValuationFixtureAsync(
        string connectionString,
        CompanyId companyId,
        UserId userId,
        Guid receiptId,
        Guid billId,
        Guid itemId,
        Guid warehouseId,
        Guid inventoryDocumentId,
        Guid inventoryDocumentLineId,
        Guid ledgerEntryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into inventory_items (
              id,
              company_id,
              item_code,
              name,
              item_kind,
              stock_uom_code,
              manage_inventory_method,
              default_costing_method,
              backorder_mode,
              low_stock_activity
            )
            values (
              @item_id,
              @company_id,
              'H10-ITEM',
              'H10 Item',
              'stock',
              'EA',
              'manage_stock',
              'fifo',
              'disallow',
              'nothing'
            );

            insert into inventory_warehouses (id, company_id, warehouse_code, name)
            values (@warehouse_id, @company_id, 'MAIN', 'Main Warehouse');

            create table receipts (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              receipt_number text not null,
              status text not null,
              vendor_id uuid not null,
              warehouse_id uuid not null references inventory_warehouses(id),
              receipt_date date not null,
              memo text null,
              posted_at timestamptz null
            );

            create table receipt_lines (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              receipt_id uuid not null references receipts(id) on delete cascade,
              line_number integer not null,
              item_id uuid not null references inventory_items(id),
              quantity numeric(20, 6) not null,
              uom_code text not null
            );

            create table bills (
              id uuid primary key,
              company_id uuid not null references companies(id) on delete cascade,
              status text not null,
              document_currency_code text not null,
              base_currency_code text not null,
              fx_rate numeric(20, 8) not null
            );

            insert into receipts (
              id,
              company_id,
              receipt_number,
              status,
              vendor_id,
              warehouse_id,
              receipt_date,
              posted_at
            )
            values (
              @receipt_id,
              @company_id,
              'RCPT-H10-001',
              'posted',
              gen_random_uuid(),
              @warehouse_id,
              '2026-04-19',
              now()
            );

            insert into receipt_lines (
              company_id,
              receipt_id,
              line_number,
              item_id,
              quantity,
              uom_code
            )
            values (
              @company_id,
              @receipt_id,
              1,
              @item_id,
              5,
              'EA'
            );

            insert into bills (
              id,
              company_id,
              status,
              document_currency_code,
              base_currency_code,
              fx_rate
            )
            values (
              @bill_id,
              @company_id,
              'posted',
              'USD',
              'USD',
              1
            );

            alter table inventory_documents
              add column if not exists document_number text null;

            insert into inventory_documents (
              id,
              company_id,
              document_number,
              document_type,
              status,
              movement_direction,
              posting_date,
              source_module,
              source_document_id,
              source_document_number,
              created_by_user_id,
              posted_at
            )
            values (
              @inventory_document_id,
              @company_id,
              'PRA-H10-001',
              'purchase_receipt',
              'posted',
              'inbound',
              '2026-04-19',
              'receipt_document',
              @receipt_id,
              'RCPT-H10-001',
              @user_id,
              now()
            );

            insert into inventory_document_lines (
              id,
              company_id,
              document_id,
              line_no,
              item_id,
              warehouse_id,
              uom_code,
              quantity,
              base_quantity
            )
            values (
              @inventory_document_line_id,
              @company_id,
              @inventory_document_id,
              1,
              @item_id,
              @warehouse_id,
              'EA',
              5,
              5
            );

            insert into inventory_ledger_entries (
              id,
              company_id,
              item_id,
              warehouse_id,
              document_id,
              document_line_id,
              movement_direction,
              movement_type,
              posting_date,
              quantity_delta,
              quantity_after,
              cost_amount_delta_base,
              cost_amount_after_base
            )
            values (
              @ledger_entry_id,
              @company_id,
              @item_id,
              @warehouse_id,
              @inventory_document_id,
              @inventory_document_line_id,
              'inbound',
              'purchase_receipt',
              '2026-04-19',
              5,
              5,
              0,
              0
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("receipt_id", receiptId);
        command.Parameters.AddWithValue("bill_id", billId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("inventory_document_id", inventoryDocumentId);
        command.Parameters.AddWithValue("inventory_document_line_id", inventoryDocumentLineId);
        command.Parameters.AddWithValue("ledger_entry_id", ledgerEntryId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedActivationAndValuationRowsAsync(
        string connectionString,
        CompanyId companyId,
        UserId userId,
        Guid receiptId,
        Guid billId,
        Guid itemId,
        Guid warehouseId,
        Guid inventoryDocumentId,
        Guid inventoryDocumentLineId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into receipt_inventory_activation_lines (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              inventory_document_id,
              inventory_document_line_id,
              item_id,
              warehouse_id,
              uom_code,
              activated_quantity,
              activated_by_user_id
            )
            values (
              gen_random_uuid(),
              @company_id,
              @receipt_id,
              1,
              @inventory_document_id,
              @inventory_document_line_id,
              @item_id,
              @warehouse_id,
              'EA',
              5,
              @user_id
            );

            insert into receipt_inventory_valuation_lines (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              bill_id,
              bill_line_number,
              item_id,
              warehouse_id,
              uom_code,
              valued_quantity,
              document_currency_code,
              base_currency_code,
              fx_rate_to_base,
              unit_cost_tx,
              unit_cost_base,
              extended_cost_base,
              valuation_source,
              valued_by_user_id
            )
            values (
              gen_random_uuid(),
              @company_id,
              @receipt_id,
              1,
              @bill_id,
              1,
              @item_id,
              @warehouse_id,
              'EA',
              5,
              'USD',
              'USD',
              1,
              10,
              10,
              50,
              'bill_receipt_matching',
              @user_id
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("receipt_id", receiptId);
        command.Parameters.AddWithValue("bill_id", billId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("inventory_document_id", inventoryDocumentId);
        command.Parameters.AddWithValue("inventory_document_line_id", inventoryDocumentLineId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetDefaultInventoryAssetAccountAsync(
        string connectionString,
        CompanyId companyId,
        Guid itemId,
        Guid inventoryAssetAccountId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update inventory_items
            set default_inventory_asset_account_id = @inventory_asset_account_id
            where company_id = @company_id
              and id = @item_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("inventory_asset_account_id", inventoryAssetAccountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetSettlementProgressAsync(
        string connectionString,
        CompanyId companyId,
        Guid receiptId,
        decimal settledQuantity,
        decimal settledAmountBase)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update receipt_grir_ap_settlement_lines
            set settled_quantity = @settled_quantity,
                settled_amount_base = @settled_amount_base,
                last_settled_at = now()
            where company_id = @company_id
              and receipt_id = @receipt_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("receipt_id", receiptId);
        command.Parameters.AddWithValue("settled_quantity", settledQuantity);
        command.Parameters.AddWithValue("settled_amount_base", settledAmountBase);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(decimal TotalDebit, decimal TotalCredit)> GetJournalLineTotalsAsync(
        string connectionString,
        Guid journalEntryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              coalesce(sum(debit), 0)::numeric(20,6) as total_debit,
              coalesce(sum(credit), 0)::numeric(20,6) as total_credit
            from journal_entry_lines
            where journal_entry_id = @journal_entry_id;
            """;
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (0m, 0m);
        }

        return (
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_debit")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_credit")));
    }

    private static async Task<(string JournalStatus, Guid? JournalEntryId)> GetSettlementBatchJournalStatusAsync(
        string connectionString,
        CompanyId companyId,
        Guid settlementBatchId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select journal_status, journal_entry_id
            from receipt_grir_ap_settlement_batches
            where company_id = @company_id
              and id = @settlement_batch_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("settlement_batch_id", settlementBatchId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Settlement batch was not found.");
        }

        return (
            reader.GetString(reader.GetOrdinal("journal_status")),
            reader.IsDBNull(reader.GetOrdinal("journal_entry_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("journal_entry_id")));
    }

    private static async Task<(decimal OpenAmountTx, decimal OpenAmountBase, string Status)> GetApOpenItemStateByBillAsync(
        string connectionString,
        CompanyId companyId,
        Guid billId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select open_amount_tx, open_amount_base, status
            from ap_open_items
            where company_id = @company_id
              and source_type = 'bill'
              and source_id = @bill_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_id", billId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("AP open item was not found.");
        }

        return (
            reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_tx")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_base")),
            reader.GetString(reader.GetOrdinal("status")));
    }

    private static async Task SetJournalStatusAsync(
        string connectionString,
        Guid journalEntryId,
        string status)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update journal_entries
            set status = @status
            where id = @journal_entry_id;
            """;
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class IdentityFxResolutionService : IFxResolutionService
    {
        public Task<FxResolutionResult> ResolveAsync(
            FxResolutionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxResolutionResult(
                new FxSnapshotRef(
                    Guid.Empty,
                    request.BaseCurrencyCode,
                    request.QuoteCurrencyCode,
                    1m,
                    request.RequestedDate,
                    request.RequestedDate,
                    "identity"),
                new[] { "Identity FX snapshot applied." }));
    }

    private static async Task<int> CountRowsAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }
}
