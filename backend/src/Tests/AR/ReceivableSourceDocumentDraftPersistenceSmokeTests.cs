using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Modules.SalesTax.Application;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.SalesTax;
using Microsoft.Extensions.Options;

namespace Tests.AR;

public sealed class ReceivableSourceDocumentDraftPersistenceSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task SaveDraftAsync_PersistsInvoiceAndCreditNoteDrafts()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var creditNoteRepository = new PostgresCreditNoteDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid creditNoteId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var invoiceResult = await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice smoke test",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Consulting services", 2m, 25m, null, 0m)]),
                CancellationToken.None);

            invoiceId = invoiceResult.DocumentId;
            Assert.StartsWith("INV-", invoiceResult.DisplayNumber, StringComparison.Ordinal);

            var updatedInvoiceResult = await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    invoiceId,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 20),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice smoke test updated",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Consulting services", 3m, 25m, null, 0m)]),
                CancellationToken.None);

            Assert.Equal(invoiceId, updatedInvoiceResult.DocumentId);

            var invoice = await invoiceRepository.GetForPostingAsync(CompanyId.FromOrdinal(1), invoiceId, CancellationToken.None);
            Assert.NotNull(invoice);
            Assert.Equal("draft", invoice!.Status);
            Assert.Equal(75m, invoice.TotalAmount);
            Assert.Equal(new DateOnly(2026, 5, 20), invoice.DueDate);

            var creditNoteResult = await creditNoteRepository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 15),
                    new DateOnly(2026, 5, 15),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Credit note smoke test",
                    [new CreditNoteDraftLineSaveModel(1, revenueAccountId, "Service adjustment", 1m, 30m, null, 0m)]),
                CancellationToken.None);

            creditNoteId = creditNoteResult.DocumentId;
            Assert.StartsWith("CN-", creditNoteResult.DisplayNumber, StringComparison.Ordinal);

            var creditNote = await creditNoteRepository.GetForPostingAsync(CompanyId.FromOrdinal(1), creditNoteId, CancellationToken.None);
            Assert.NotNull(creditNote);
            Assert.Equal("draft", creditNote!.Status);
            Assert.Equal(30m, creditNote.TotalAmount);
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_note_lines", "credit_note_id", "credit_notes", creditNoteId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SaveDraftAsync_WithSalesTaxV2Enabled_ComputesEngineTaxAndWritesSnapshot()
    {
        // S2.1 proof point: with the SalesTaxV2 flag on, the engine — not
        // the client — is the authority for tax. The line is sent with
        // TaxAmount 0; the engine computes GST 5% on $100 = 5.00, the
        // invoice header + line persist 5.00, and one snapshot row lands
        // in document_line_sales_tax_snapshots.
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var engine = new SalesTaxEngine(new PostgreSqlSalesTaxCatalogReader(infrastructureConnectionFactory));
        var persister = new PostgreSqlTaxSnapshotPersister(infrastructureConnectionFactory);
        var invoiceRepository = new PostgresInvoiceDocumentRepository(
            connectionFactory,
            new PostgresExecutionContextAccessor(),
            engine,
            persister,
            Options.Create(new SalesTaxV2Options { Enabled = true }));

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid legacyTaxCodeId = Guid.Empty;
        Guid salesTaxCodeId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            (legacyTaxCodeId, salesTaxCodeId) = await CreateGstFivePercentTaxCodeAsync(connectionFactory, CompanyId, CancellationToken.None);

            var saveResult = await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Sales Tax v2 engine smoke test",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Taxable consulting", 1m, 100m, legacyTaxCodeId, 0m)]),
                CancellationToken.None);

            invoiceId = saveResult.DocumentId;

            var invoice = await invoiceRepository.GetForPostingAsync(CompanyId.FromOrdinal(1), invoiceId, CancellationToken.None);
            Assert.NotNull(invoice);
            Assert.Equal("draft", invoice!.Status);
            Assert.Equal(105m, invoice.TotalAmount);

            var lineTaxTotal = await GetInvoiceLineTaxTotalAsync(connectionFactory, invoiceId, CancellationToken.None);
            Assert.Equal(5.00m, lineTaxTotal);

            var (snapshotCount, snapshotTaxTotal) = await GetSnapshotSummaryAsync(connectionFactory, "invoice", invoiceId, CancellationToken.None);
            Assert.Equal(1, snapshotCount);
            Assert.Equal(5.00m, snapshotTaxTotal);
        }
        finally
        {
            // Snapshots first: they RESTRICT-reference the v2 code/component.
            await DeleteSnapshotsAsync(connectionFactory, "invoice", invoiceId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await DeleteSalesTaxCodeAsync(connectionFactory, salesTaxCodeId, CancellationToken.None);
            await DeleteLegacyTaxCodeAsync(connectionFactory, legacyTaxCodeId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsPostedInvoiceAndCreditNoteUpdates()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var creditNoteRepository = new PostgresCreditNoteDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid creditNoteId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice lifecycle guard",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Posted invoice", 1m, 20m, null, 0m)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            var invoiceException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                invoiceRepository.SaveDraftAsync(
                    new InvoiceDraftSaveModel(
                        invoiceId,
                        CompanyId.FromOrdinal(1),
                        UserId.FromOrdinal(1),
                        CustomerId,
                        new DateOnly(2026, 4, 14),
                        new DateOnly(2026, 5, 20),
                        "USD",
                        "USD",
                        null,
                        null,
                        null,
                        null,
                        "Invoice lifecycle guard updated",
                        [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Posted invoice", 2m, 20m, null, 0m)]),
                    CancellationToken.None));
            Assert.Contains("Only draft invoices can be modified", invoiceException.Message, StringComparison.Ordinal);

            creditNoteId = (await creditNoteRepository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 15),
                    new DateOnly(2026, 5, 15),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Credit note lifecycle guard",
                    [new CreditNoteDraftLineSaveModel(1, revenueAccountId, "Posted credit note", 1m, 10m, null, 0m)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "credit_notes", creditNoteId, CancellationToken.None);

            var creditNoteException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                creditNoteRepository.SaveDraftAsync(
                    new CreditNoteDraftSaveModel(
                        creditNoteId,
                        CompanyId.FromOrdinal(1),
                        UserId.FromOrdinal(1),
                        CustomerId,
                        new DateOnly(2026, 4, 15),
                        new DateOnly(2026, 5, 21),
                        "USD",
                        "USD",
                        null,
                        null,
                        null,
                        null,
                        "Credit note lifecycle guard updated",
                        [new CreditNoteDraftLineSaveModel(1, revenueAccountId, "Posted credit note", 2m, 10m, null, 0m)]),
                    CancellationToken.None));
            Assert.Contains("Only draft credit notes can be modified", creditNoteException.Message, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_note_lines", "credit_note_id", "credit_notes", creditNoteId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsJournalEntryLinkForInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice review link",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Review link", 1m, 25m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            var review = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            Assert.NotNull(review);
            Assert.Equal(journalEntryId, review!.JournalEntryId);
            Assert.Equal("JE-SMOKE-001", review.JournalEntryDisplayNumber);
            Assert.Equal("posted", review.JournalEntryStatus);
            Assert.Equal("posted_locked", review.LifecycleMode);
            Assert.False(review.CanEditDraft);
            Assert.False(review.CanPostDraft);
            Assert.Contains("governed void or reverse flow", review.LifecycleReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "edit_draft" && action.IsAvailable is false && action.AvailabilityMode == "blocked_by_status");
            // PR-6b: posted documents now hard-block Void with a
            // policy reason redirecting to Reverse.
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "void_document" && action.IsAvailable is false && action.AvailabilityMode == "blocked_by_policy");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsForeignCurrencyJournalEntryLinkForInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency invoice review link",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "EUR review link", 2m, 50m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                receivableControlAccountId,
                revenueAccountId,
                125m,
                "JE-SMOKE-AR-INV-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            var sourceReview = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);
            var journalReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReview);
            Assert.Equal(journalEntryId, sourceReview!.JournalEntryId);
            Assert.Equal("JE-SMOKE-AR-INV-FX-001", sourceReview.JournalEntryDisplayNumber);
            Assert.Equal("posted", sourceReview.JournalEntryStatus);
            Assert.Equal("EUR", sourceReview.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReview.BaseCurrencyCode);
            Assert.Equal(100m, sourceReview.TotalAmount);
            Assert.Equal("posted_locked", sourceReview.LifecycleMode);

            Assert.NotNull(journalReview);
            Assert.Equal("invoice", journalReview!.SourceType);
            Assert.Equal(invoiceId, journalReview.SourceId);
            Assert.Equal("posted", journalReview.Status);
            Assert.Equal("EUR", journalReview.TransactionCurrencyCode);
            Assert.Equal("USD", journalReview.BaseCurrencyCode);
            Assert.Equal(1.25m, journalReview.ExchangeRate);
            Assert.Equal(fxDate, journalReview.ExchangeRateDate);
            Assert.Equal("manual", journalReview.ExchangeRateSource);
            Assert.Equal(fxSnapshotId, journalReview.FxSnapshotId);
            Assert.True(journalReview.IsForeignCurrency);
            Assert.Equal(100m, journalReview.TotalTransactionDebit);
            Assert.Equal(100m, journalReview.TotalTransactionCredit);
            Assert.Equal(125m, journalReview.TotalDebit);
            Assert.Equal(125m, journalReview.TotalCredit);
            Assert.Equal(2, journalReview.LineCount);
            Assert.Equal("spot", journalReview.FxRateType);
            Assert.Equal("direct", journalReview.FxQuoteBasis);
            Assert.Equal("general", journalReview.FxRateUseCase);
            Assert.Equal("normal", journalReview.FxPostingReason);
            Assert.Contains("snapshot", journalReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsForeignCurrencyJournalEntryLinkForReceivePayment()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.NewGuid();
        Guid openItemId = Guid.Empty;
        Guid receivePaymentId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                revenueAccountId,
                openItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                fxRate: 1.25m,
                fxSource: "manual",
                totalAmount: 100m,
                appliedAmountTx: 100m);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "receive_payment",
                receivePaymentId,
                revenueAccountId,
                receivableControlAccountId,
                125m,
                "JE-SMOKE-AR-RP-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateSource: "manual");

            var sourceReview = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var journalReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReview);
            Assert.Equal(journalEntryId, sourceReview!.JournalEntryId);
            Assert.Equal("JE-SMOKE-AR-RP-FX-001", sourceReview.JournalEntryDisplayNumber);
            Assert.Equal("posted", sourceReview.JournalEntryStatus);
            Assert.Equal("EUR", sourceReview.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReview.BaseCurrencyCode);
            Assert.Equal(100m, sourceReview.TotalAmount);
            Assert.Equal("posted_locked", sourceReview.LifecycleMode);

            Assert.NotNull(journalReview);
            Assert.Equal("receive_payment", journalReview!.SourceType);
            Assert.Equal(receivePaymentId, journalReview.SourceId);
            Assert.Equal("posted", journalReview.Status);
            Assert.Equal("EUR", journalReview.TransactionCurrencyCode);
            Assert.Equal("USD", journalReview.BaseCurrencyCode);
            Assert.Equal(1.25m, journalReview.ExchangeRate);
            Assert.Equal("manual", journalReview.ExchangeRateSource);
            Assert.True(journalReview.IsForeignCurrency);
            Assert.Equal(100m, journalReview.TotalTransactionDebit);
            Assert.Equal(100m, journalReview.TotalTransactionCredit);
            Assert.Equal(125m, journalReview.TotalDebit);
            Assert.Equal(125m, journalReview.TotalCredit);
            Assert.Equal(2, journalReview.LineCount);
            Assert.Contains("header-only", journalReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesForeignCurrencyReceivePaymentBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.NewGuid();
        Guid receivePaymentId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                revenueAccountId,
                openItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                fxRate: 1.25m,
                fxSource: "manual",
                totalAmount: 100m,
                appliedAmountTx: 100m);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                openItemId,
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "receive_payment",
                receivePaymentId,
                revenueAccountId,
                receivableControlAccountId,
                125m,
                "JE-SMOKE-AR-RP-FX-REV-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateSource: "manual");

            var sourceReviewBeforeReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var originalReviewBeforeReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReviewBeforeReverse);
            Assert.Equal(journalEntryId, sourceReviewBeforeReverse!.JournalEntryId);
            Assert.Equal("EUR", sourceReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReviewBeforeReverse.BaseCurrencyCode);
            Assert.Equal(100m, sourceReviewBeforeReverse.TotalAmount);
            Assert.NotNull(originalReviewBeforeReverse);
            Assert.Equal("receive_payment", originalReviewBeforeReverse!.SourceType);
            Assert.Equal(receivePaymentId, originalReviewBeforeReverse.SourceId);
            Assert.Equal("EUR", originalReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal("USD", originalReviewBeforeReverse.BaseCurrencyCode);
            Assert.Equal(1.25m, originalReviewBeforeReverse.ExchangeRate);
            Assert.Equal("manual", originalReviewBeforeReverse.ExchangeRateSource);
            Assert.Null(originalReviewBeforeReverse.FxSnapshotId);
            Assert.Contains("header-only", originalReviewBeforeReverse.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.Equal("execution_request_recorded", executeResult!.OutcomeCode);

            var lifecycleResult = await lifecycleStore.ReverseAsync(
                CompanyId,
                journalEntryId,
                userId,
                CancellationToken.None);

            compensationJournalEntryId = lifecycleResult.CompensationJournalEntryId;

            var completionResult = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId.Value,
                userId,
                lifecycleResult.CompensationJournalEntryId,
                lifecycleResult.CompensationDisplayNumber,
                lifecycleResult.CompensationSourceType,
                lifecycleResult.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(completionResult);
            Assert.True(completionResult!.Executed);
            Assert.Equal("journal_entry_reversed", completionResult.OutcomeCode);
            Assert.Equal("receive_payment_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "receive_payments", receivePaymentId, CancellationToken.None);
            var openItem = await GetArOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var sourceReviewAfterReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var originalReviewAfterReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);
            var compensationReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                compensationJournalEntryId,
                CancellationToken.None);
            var applicationCount = await CountSettlementApplicationsForSourceAsync(
                connectionFactory,
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("receive_payment_reversal", compensationJournal.SourceType);
            Assert.Equal(receivePaymentId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.NotNull(openItem);
            Assert.Equal("open", openItem!.Status);
            Assert.Equal(100m, openItem.OpenAmountTx);
            Assert.Equal(125m, openItem.OpenAmountBase);
            Assert.NotNull(sourceReviewAfterReverse);
            Assert.Equal(journalEntryId, sourceReviewAfterReverse!.JournalEntryId);
            Assert.Equal("reversed", sourceReviewAfterReverse.JournalEntryStatus);
            Assert.Equal("EUR", sourceReviewAfterReverse.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReviewAfterReverse.BaseCurrencyCode);
            Assert.Equal(100m, sourceReviewAfterReverse.TotalAmount);
            Assert.NotNull(originalReviewAfterReverse);
            Assert.Equal("reversed", originalReviewAfterReverse!.Status);
            Assert.Equal("EUR", originalReviewAfterReverse.TransactionCurrencyCode);
            Assert.Equal("USD", originalReviewAfterReverse.BaseCurrencyCode);
            Assert.Equal(1.25m, originalReviewAfterReverse.ExchangeRate);
            Assert.Equal("manual", originalReviewAfterReverse.ExchangeRateSource);
            Assert.Contains(
                originalReviewAfterReverse.RelatedEntries,
                entry => entry.Id == compensationJournalEntryId && entry.SourceType == "receive_payment_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("posted", compensationReview!.Status);
            Assert.Equal("receive_payment_reversal", compensationReview.SourceType);
            Assert.Equal(receivePaymentId, compensationReview.SourceId);
            Assert.Equal(originalReviewBeforeReverse.TransactionCurrencyCode, compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRate, compensationReview.ExchangeRate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateDate, compensationReview.ExchangeRateDate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Null(compensationReview.FxSnapshotId);
            Assert.Contains("header-only", compensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, applicationCount);
            Assert.Equal(1, reversalAuditCount);
            var reversalEvent = Assert.Single(reversalEvents);
            Assert.Equal(attempt.RequestId.Value, reversalEvent.RequestId);
            Assert.Equal(settlementApplicationId, reversalEvent.SettlementApplicationId);
            Assert.Equal("receive_payment", reversalEvent.SourceType);
            Assert.Equal("ar_open_item", reversalEvent.TargetOpenItemType);
            Assert.Equal(openItemId, reversalEvent.TargetOpenItemId);
            Assert.Equal(100m, reversalEvent.AppliedAmountTx);
            Assert.Equal(125m, reversalEvent.AppliedAmountBase);
            settlementApplicationId = Guid.Empty;
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, receivePaymentId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsReversedJournalEntryLifecycleForInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice reversed review link",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Reversed review link", 1m, 25m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "reversed",
                CancellationToken.None);

            var review = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            Assert.NotNull(review);
            Assert.Equal(journalEntryId, review!.JournalEntryId);
            Assert.Equal("reversed", review.JournalEntryStatus);
            Assert.NotNull(review.JournalEntryReversedAt);
            Assert.Equal("historical_linked_je_reversed", review.LifecycleMode);
            Assert.False(review.CanEditDraft);
            Assert.False(review.CanPostDraft);
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "reverse_document" && action.AvailabilityMode == "blocked_by_linked_je_lifecycle");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetLifecyclePreviewAsync_ReturnsActionPreviewForPostedInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice lifecycle preview",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Lifecycle preview", 1m, 25m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            var preview = await reviewRepository.GetLifecyclePreviewAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            Assert.NotNull(preview);
            Assert.Equal("posted_locked", preview!.LifecycleMode);
            // PR-6b: posted-locked now reports Void as blocked-by-policy.
            Assert.Contains(preview.LifecycleActions, action => action.ActionCode == "void_document" && action.AvailabilityMode == "blocked_by_policy");
            Assert.Contains(preview.LifecycleActions, action => action.ActionCode == "post_draft" && action.AvailabilityMode == "blocked_by_status");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetLifecycleActionPreviewAsync_ReturnsVoidActionPreviewForPostedInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice lifecycle action preview",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Lifecycle action preview", 1m, 35m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            var preview = await reviewRepository.GetLifecycleActionPreviewAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                "void_document",
                CancellationToken.None);

            Assert.NotNull(preview);
            Assert.Equal("posted_locked", preview!.LifecycleMode);
            Assert.Equal("void_document", preview.ActionCode);
            // PR-6b: per the locked Tralanz business rule, posted
            // documents are NEVER voided — they're reversed. The
            // preview surfaces this as a policy block redirecting
            // to Reverse.
            Assert.Equal("blocked_by_policy", preview.AvailabilityMode);
            Assert.False(preview.IsAvailable);
            Assert.Contains("Reverse", preview.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AttemptVoidAsync_RejectsPostedInvoiceWithBlockedByPolicy()
    {
        // PR-6b (C-2): per the locked Tralanz business rules, posted
        // documents are NEVER voided — they're reversed. This test
        // codifies that "Void on posted invoice" returns a hard
        // policy block (outcome="blocked_by_policy", execution_mode=
        // "policy_block") so the UI can render "Use Reverse instead"
        // rather than a misleading "still being built" message.
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice void skeleton",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Void skeleton", 1m, 40m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptVoidAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("void_document", attempt!.ActionCode);
            Assert.Equal("policy_block", attempt.ExecutionMode);
            Assert.False(attempt.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.Equal("blocked_by_policy", attempt.OutcomeCode);
            Assert.Contains("Reverse", attempt.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AttemptReverseAsync_SubmitsGovernedRequestForPostedInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice reverse skeleton",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Reverse skeleton", 1m, 45m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("reverse_document", attempt!.ActionCode);
            Assert.Equal("request_recording", attempt.ExecutionMode);
            Assert.True(attempt.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.True(attempt.Persisted);
            Assert.NotNull(attempt.RequestId);
            Assert.Equal("request_recorded", attempt.OutcomeCode);

            var request = await reviewRepository.GetLatestReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            Assert.NotNull(request);
            Assert.Equal(attempt.RequestId, request!.RequestId);
            Assert.Equal("invoice", request.SourceType);
            Assert.Equal(invoiceId, request.DocumentId);
            Assert.Equal("reverse_document", request.ActionCode);
            Assert.Equal("draft", request.RequestStatus);
            Assert.Equal("user", request.RequestedByActorType);
            Assert.Equal((object?)userId, (object?)request.RequestedByActorId);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);
            Assert.Equal("submitted", submitResult.Request.RequestStatus);
            Assert.Equal("user", submitResult.Request.SubmittedByActorType);
            Assert.Equal((object?)userId, (object?)submitResult.Request.SubmittedByActorId);
            Assert.NotNull(submitResult.Request.SubmittedAt);

            var readiness = await reviewRepository.GetReverseRequestApplyReadinessAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(readiness);
            Assert.True(readiness!.GovernanceReady);
            Assert.False(readiness.ApplyReady);
            Assert.Equal("request_recording_only", readiness.ExecutionMode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.True(executeResult!.CommandAccepted);
            Assert.False(executeResult.Executed);
            Assert.True(executeResult.Persisted);
            Assert.Equal("execution_request_recorded", executeResult.OutcomeCode);
            Assert.Equal("execution_requested", executeResult.Request.ExecutionStatus);
            Assert.Equal("user", executeResult.Request.ExecutionRequestedByActorType);
            Assert.Equal((object?)userId, (object?)executeResult.Request.ExecutionRequestedByActorId);
            Assert.NotNull(executeResult.Request.ExecutionRequestedAt);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteReverseRequestAsync_BlocksWhenInvoiceStillHasArOpenItemTruth()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid receivePaymentId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice reverse open-item guard",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Reverse open-item guard", 1m, 55m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                "posted",
                CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                receivableControlAccountId,
                openItemId,
                CancellationToken.None);

            settlementApplicationId = await CreateSettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                openItemId,
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            var blockerReverseAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            Assert.NotNull(blockerReverseAttempt);
            Assert.Equal("request_recorded", blockerReverseAttempt!.OutcomeCode);
            Assert.NotNull(blockerReverseAttempt.RequestId);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);
            Assert.NotNull(attempt.RequestId);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.False(executeResult!.CommandAccepted);
            Assert.False(executeResult.Executed);
            Assert.False(executeResult.Persisted);
            Assert.Equal("blocked_by_subledger_truth", executeResult.OutcomeCode);
            Assert.Contains("AR settlement/application trail", executeResult.Message);

            var plan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(plan);
            Assert.False(plan!.CanExecute);
            Assert.Equal("blocked", plan.OverallStatus);
            Assert.Equal("subledger_reversal_gate", plan.Steps[2].StepCode);
            Assert.Equal("blocked", plan.Steps[2].StepStatus);

            var blockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);

            var blocker = Assert.Single(blockers);
            Assert.Equal(settlementApplicationId, blocker.SettlementApplicationId);
            Assert.Equal("receive_payment", blocker.SettlementSourceType);
            Assert.Equal(receivePaymentId, blocker.SettlementSourceId);
            Assert.Equal("ar_open_item", blocker.TargetOpenItemType);
            Assert.Equal(openItemId, blocker.TargetOpenItemId);
            Assert.Equal("invoice", blocker.TargetSourceType);
            Assert.Equal(invoiceId, blocker.TargetSourceId);
            Assert.Equal(1m, blocker.AppliedAmountTx);
            Assert.Equal(1m, blocker.AppliedAmountBase);
            Assert.Equal(blockerReverseAttempt.RequestId, blocker.ReverseRequestId);
            Assert.Equal("draft", blocker.ReverseRequestStatus);
            Assert.Equal("not_requested", blocker.ReverseExecutionStatus);
            Assert.NotNull(blocker.ReverseRequestedAt);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, receivePaymentId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_RecordsJournalEntryReversalForSubmittedInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice reverse completion",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Reverse completion", 1m, 65m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                receivableControlAccountId,
                revenueAccountId,
                65m,
                "JE-SMOKE-AR-REV-001",
                CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.Equal("execution_request_recorded", executeResult!.OutcomeCode);
            Assert.Equal("execution_requested", executeResult.Request.ExecutionStatus);

            var lifecycleResult = await lifecycleStore.ReverseAsync(
                CompanyId,
                journalEntryId,
                userId,
                CancellationToken.None);

            compensationJournalEntryId = lifecycleResult.CompensationJournalEntryId;

            var completionResult = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                lifecycleResult.CompensationJournalEntryId,
                lifecycleResult.CompensationDisplayNumber,
                lifecycleResult.CompensationSourceType,
                lifecycleResult.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(completionResult);
            Assert.True(completionResult!.CommandAccepted);
            Assert.True(completionResult.Executed);
            Assert.True(completionResult.Persisted);
            Assert.Equal("journal_entry_reversed", completionResult.OutcomeCode);
            Assert.Equal("journal_entry_reversed", completionResult.Request.ExecutionStatus);
            Assert.Equal(lifecycleResult.CompensationJournalEntryId, completionResult.Request.CompensationJournalEntryId);
            Assert.Equal(lifecycleResult.CompensationDisplayNumber, completionResult.Request.CompensationJournalEntryDisplayNumber);
            Assert.Equal("invoice_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);
            var openItemStatus = await GetArOpenItemStatusAsync(connectionFactory, openItemId, CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("invoice_reversal", compensationJournal.SourceType);
            Assert.Equal(invoiceId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.Equal("voided", openItemStatus);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_RecordsForeignCurrencyJournalEntryReversalForSubmittedInvoice()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid revenueAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency invoice reverse completion",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Foreign reverse completion", 2m, 50m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                receivableControlAccountId,
                revenueAccountId,
                125m,
                "JE-SMOKE-AR-INV-FX-REV-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            var sourceReviewBeforeReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);
            var originalReviewBeforeReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReviewBeforeReverse);
            Assert.Equal(journalEntryId, sourceReviewBeforeReverse!.JournalEntryId);
            Assert.Equal("EUR", sourceReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReviewBeforeReverse.BaseCurrencyCode);
            Assert.Equal(100m, sourceReviewBeforeReverse.TotalAmount);
            Assert.NotNull(originalReviewBeforeReverse);
            Assert.Equal("invoice", originalReviewBeforeReverse!.SourceType);
            Assert.Equal(invoiceId, originalReviewBeforeReverse.SourceId);
            Assert.Equal("EUR", originalReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal("USD", originalReviewBeforeReverse.BaseCurrencyCode);
            Assert.Equal(1.25m, originalReviewBeforeReverse.ExchangeRate);
            Assert.Equal(fxDate, originalReviewBeforeReverse.ExchangeRateDate);
            Assert.Equal("manual", originalReviewBeforeReverse.ExchangeRateSource);
            Assert.Equal(fxSnapshotId, originalReviewBeforeReverse.FxSnapshotId);
            Assert.Contains("snapshot", originalReviewBeforeReverse.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.Equal("execution_request_recorded", executeResult!.OutcomeCode);
            Assert.Equal("execution_requested", executeResult.Request.ExecutionStatus);

            var lifecycleResult = await lifecycleStore.ReverseAsync(
                CompanyId,
                journalEntryId,
                userId,
                CancellationToken.None);

            compensationJournalEntryId = lifecycleResult.CompensationJournalEntryId;

            var completionResult = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                attempt.RequestId.Value,
                userId,
                lifecycleResult.CompensationJournalEntryId,
                lifecycleResult.CompensationDisplayNumber,
                lifecycleResult.CompensationSourceType,
                lifecycleResult.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(completionResult);
            Assert.True(completionResult!.CommandAccepted);
            Assert.True(completionResult.Executed);
            Assert.True(completionResult.Persisted);
            Assert.Equal("journal_entry_reversed", completionResult.OutcomeCode);
            Assert.Equal("journal_entry_reversed", completionResult.Request.ExecutionStatus);
            Assert.Equal(lifecycleResult.CompensationJournalEntryId, completionResult.Request.CompensationJournalEntryId);
            Assert.Equal("invoice_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);
            var openItemStatus = await GetArOpenItemStatusAsync(connectionFactory, openItemId, CancellationToken.None);
            var sourceReviewAfterReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);
            var originalReviewAfterReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);
            var compensationReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                compensationJournalEntryId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("invoice_reversal", compensationJournal.SourceType);
            Assert.Equal(invoiceId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.Equal("voided", openItemStatus);
            Assert.NotNull(sourceReviewAfterReverse);
            Assert.Equal(journalEntryId, sourceReviewAfterReverse!.JournalEntryId);
            Assert.Equal("reversed", sourceReviewAfterReverse.JournalEntryStatus);
            Assert.NotNull(sourceReviewAfterReverse.JournalEntryReversedAt);
            Assert.Equal("EUR", sourceReviewAfterReverse.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReviewAfterReverse.BaseCurrencyCode);
            Assert.Equal(100m, sourceReviewAfterReverse.TotalAmount);
            Assert.NotNull(originalReviewAfterReverse);
            Assert.Equal("reversed", originalReviewAfterReverse!.Status);
            Assert.Equal(fxSnapshotId, originalReviewAfterReverse.FxSnapshotId);
            Assert.Contains(
                originalReviewAfterReverse.RelatedEntries,
                entry => entry.Id == compensationJournalEntryId && entry.SourceType == "invoice_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("posted", compensationReview!.Status);
            Assert.Equal("invoice_reversal", compensationReview.SourceType);
            Assert.Equal(invoiceId, compensationReview.SourceId);
            Assert.Equal(originalReviewBeforeReverse.TransactionCurrencyCode, compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRate, compensationReview.ExchangeRate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateDate, compensationReview.ExchangeRateDate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(originalReviewBeforeReverse.FxSnapshotId, compensationReview.FxSnapshotId);
            Assert.Equal(originalReviewBeforeReverse.FxRateType, compensationReview.FxRateType);
            Assert.Equal(originalReviewBeforeReverse.FxQuoteBasis, compensationReview.FxQuoteBasis);
            Assert.Equal(originalReviewBeforeReverse.FxRateUseCase, compensationReview.FxRateUseCase);
            Assert.Equal(originalReviewBeforeReverse.FxPostingReason, compensationReview.FxPostingReason);
            Assert.Contains("snapshot", compensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesPostedReceivePaymentBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.NewGuid();
        Guid receivePaymentId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                revenueAccountId,
                openItemId,
                CancellationToken.None);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                openItemId,
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "receive_payment",
                receivePaymentId,
                revenueAccountId,
                receivableControlAccountId,
                1m,
                "JE-SMOKE-AR-RP-REV-001",
                CancellationToken.None);

            var sourceReviewBeforeReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var originalReviewBeforeReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReviewBeforeReverse);
            Assert.Equal(journalEntryId, sourceReviewBeforeReverse!.JournalEntryId);
            Assert.Equal("posted", sourceReviewBeforeReverse.JournalEntryStatus);
            Assert.NotNull(originalReviewBeforeReverse);
            Assert.Equal("receive_payment", originalReviewBeforeReverse!.SourceType);
            Assert.Equal(receivePaymentId, originalReviewBeforeReverse.SourceId);
            Assert.Equal("posted", originalReviewBeforeReverse.Status);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.Equal("execution_request_recorded", executeResult!.OutcomeCode);

            var lifecycleResult = await lifecycleStore.ReverseAsync(
                CompanyId,
                journalEntryId,
                userId,
                CancellationToken.None);

            compensationJournalEntryId = lifecycleResult.CompensationJournalEntryId;

            var completionResult = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                attempt.RequestId.Value,
                userId,
                lifecycleResult.CompensationJournalEntryId,
                lifecycleResult.CompensationDisplayNumber,
                lifecycleResult.CompensationSourceType,
                lifecycleResult.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(completionResult);
            Assert.True(completionResult!.Executed);
            Assert.Equal("journal_entry_reversed", completionResult.OutcomeCode);
            Assert.Equal("receive_payment_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "receive_payments", receivePaymentId, CancellationToken.None);
            var openItem = await GetArOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var sourceReviewAfterReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var originalReviewAfterReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);
            var compensationReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                compensationJournalEntryId,
                CancellationToken.None);
            var applicationCount = await CountSettlementApplicationsForSourceAsync(
                connectionFactory,
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("receive_payment_reversal", compensationJournal.SourceType);
            Assert.Equal(receivePaymentId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.NotNull(openItem);
            Assert.Equal("open", openItem!.Status);
            Assert.Equal(55m, openItem.OpenAmountTx);
            Assert.Equal(55m, openItem.OpenAmountBase);
            Assert.NotNull(sourceReviewAfterReverse);
            Assert.Equal(journalEntryId, sourceReviewAfterReverse!.JournalEntryId);
            Assert.Equal("reversed", sourceReviewAfterReverse.JournalEntryStatus);
            Assert.NotNull(sourceReviewAfterReverse.JournalEntryReversedAt);
            Assert.Equal(sourceReviewBeforeReverse.JournalEntryDisplayNumber, sourceReviewAfterReverse.JournalEntryDisplayNumber);
            Assert.NotNull(originalReviewAfterReverse);
            Assert.Equal("reversed", originalReviewAfterReverse!.Status);
            Assert.NotNull(originalReviewAfterReverse.ReversedAt);
            Assert.Contains(
                originalReviewAfterReverse.RelatedEntries,
                entry => entry.Id == compensationJournalEntryId && entry.SourceType == "receive_payment_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("posted", compensationReview!.Status);
            Assert.Equal("receive_payment_reversal", compensationReview.SourceType);
            Assert.Equal(receivePaymentId, compensationReview.SourceId);
            Assert.Equal(originalReviewBeforeReverse.TransactionCurrencyCode, compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRate, compensationReview.ExchangeRate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateDate, compensationReview.ExchangeRateDate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(0, applicationCount);
            Assert.Equal(1, reversalAuditCount);
            var reversalEvent = Assert.Single(reversalEvents);
            Assert.Equal(attempt.RequestId.Value, reversalEvent.RequestId);
            Assert.Equal(settlementApplicationId, reversalEvent.SettlementApplicationId);
            Assert.Equal("receive_payment", reversalEvent.SourceType);
            Assert.Equal("ar_open_item", reversalEvent.TargetOpenItemType);
            Assert.Equal(openItemId, reversalEvent.TargetOpenItemId);
            Assert.Equal(1m, reversalEvent.AppliedAmountTx);
            Assert.Equal(1m, reversalEvent.AppliedAmountBase);
            settlementApplicationId = Guid.Empty;
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, receivePaymentId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsInvoiceReverseAfterBlockingReceivePaymentIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid receivePaymentId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid invoiceJournalEntryId = Guid.Empty;
        Guid receivePaymentJournalEntryId = Guid.Empty;
        Guid invoiceCompensationJournalEntryId = Guid.Empty;
        Guid receivePaymentCompensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice blocked-then-reversed smoke",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Blocked then reversed", 1m, 55m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            invoiceJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                receivableControlAccountId,
                revenueAccountId,
                55m,
                "JE-SMOKE-AR-CHAIN-INV-001",
                CancellationToken.None);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                revenueAccountId,
                openItemId,
                CancellationToken.None);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                openItemId,
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);

            receivePaymentJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "receive_payment",
                receivePaymentId,
                revenueAccountId,
                receivableControlAccountId,
                1m,
                "JE-SMOKE-AR-CHAIN-RP-001",
                CancellationToken.None);

            var invoiceAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(invoiceAttempt);
            Assert.Equal("request_recorded", invoiceAttempt!.OutcomeCode);

            var invoiceSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(invoiceSubmit);
            Assert.Equal("submitted", invoiceSubmit!.OutcomeCode);

            var blockedInvoiceExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(blockedInvoiceExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedInvoiceExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None));
            Assert.Equal(receivePaymentId, initialBlocker.SettlementSourceId);

            var paymentAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);
            Assert.NotNull(paymentAttempt);

            var paymentSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(paymentSubmit);
            Assert.Equal("submitted", paymentSubmit!.OutcomeCode);

            var paymentExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(paymentExecute);
            Assert.Equal("execution_request_recorded", paymentExecute!.OutcomeCode);

            var paymentLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                receivePaymentJournalEntryId,
                userId,
                CancellationToken.None);

            receivePaymentCompensationJournalEntryId = paymentLifecycle.CompensationJournalEntryId;

            var paymentCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt.RequestId.Value,
                userId,
                paymentLifecycle.CompensationJournalEntryId,
                paymentLifecycle.CompensationDisplayNumber,
                paymentLifecycle.CompensationSourceType,
                paymentLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(paymentCompletion);
            Assert.Equal("journal_entry_reversed", paymentCompletion!.OutcomeCode);
            settlementApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyInvoicePlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyInvoicePlan);
            Assert.True(readyInvoicePlan!.CanExecute);
            Assert.Equal("planned", readyInvoicePlan.OverallStatus);

            var readyInvoiceExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyInvoiceExecute);
            Assert.Equal("execution_request_recorded", readyInvoiceExecute!.OutcomeCode);

            var invoiceLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                invoiceJournalEntryId,
                userId,
                CancellationToken.None);

            invoiceCompensationJournalEntryId = invoiceLifecycle.CompensationJournalEntryId;

            var invoiceCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                invoiceLifecycle.CompensationJournalEntryId,
                invoiceLifecycle.CompensationDisplayNumber,
                invoiceLifecycle.CompensationSourceType,
                invoiceLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(invoiceCompletion);
            Assert.True(invoiceCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", invoiceCompletion.OutcomeCode);
            Assert.Equal("invoice_reversal", invoiceCompletion.Request.CompensationSourceType);

            var invoiceStatus = await GetDocumentStatusAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);
            var receivePaymentStatus = await GetDocumentStatusAsync(connectionFactory, "receive_payments", receivePaymentId, CancellationToken.None);
            var invoiceJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, invoiceJournalEntryId, CancellationToken.None);
            var receivePaymentJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, receivePaymentJournalEntryId, CancellationToken.None);
            var openItemStatus = await GetArOpenItemStatusAsync(connectionFactory, openItemId, CancellationToken.None);

            Assert.Equal("reversed", invoiceStatus);
            Assert.Equal("reversed", receivePaymentStatus);
            Assert.Equal("reversed", invoiceJournalStatus);
            Assert.Equal("reversed", receivePaymentJournalStatus);
            Assert.Equal("voided", openItemStatus);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, receivePaymentId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, invoiceCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, receivePaymentCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, invoiceJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, receivePaymentJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsForeignCurrencyInvoiceReverseAfterBlockingReceivePaymentIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid receivePaymentId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid invoiceJournalEntryId = Guid.Empty;
        Guid receivePaymentJournalEntryId = Guid.Empty;
        Guid invoiceCompensationJournalEntryId = Guid.Empty;
        Guid receivePaymentCompensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency blocked-then-reversed smoke",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "FX blocked then reversed", 2m, 50m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);

            invoiceJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "invoice",
                invoiceId,
                receivableControlAccountId,
                revenueAccountId,
                125m,
                "JE-SMOKE-AR-CHAIN-INV-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            receivePaymentId = await InsertReceivePaymentAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                revenueAccountId,
                openItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                fxRate: 1.25m,
                fxSource: "manual",
                totalAmount: 100m,
                appliedAmountTx: 100m);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                openItemId,
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            receivePaymentJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "receive_payment",
                receivePaymentId,
                revenueAccountId,
                receivableControlAccountId,
                125m,
                "JE-SMOKE-AR-CHAIN-RP-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual");

            var invoiceAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                userId,
                CancellationToken.None);

            Assert.NotNull(invoiceAttempt);
            Assert.Equal("request_recorded", invoiceAttempt!.OutcomeCode);

            var invoiceSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(invoiceSubmit);
            Assert.Equal("submitted", invoiceSubmit!.OutcomeCode);

            var blockedInvoiceExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(blockedInvoiceExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedInvoiceExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None));
            Assert.Equal(receivePaymentId, initialBlocker.SettlementSourceId);

            var paymentAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                userId,
                CancellationToken.None);
            Assert.NotNull(paymentAttempt);

            var paymentSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(paymentSubmit);
            Assert.Equal("submitted", paymentSubmit!.OutcomeCode);

            var paymentExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(paymentExecute);
            Assert.Equal("execution_request_recorded", paymentExecute!.OutcomeCode);

            var paymentLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                receivePaymentJournalEntryId,
                userId,
                CancellationToken.None);

            receivePaymentCompensationJournalEntryId = paymentLifecycle.CompensationJournalEntryId;

            var paymentCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "receive_payment",
                receivePaymentId,
                paymentAttempt.RequestId.Value,
                userId,
                paymentLifecycle.CompensationJournalEntryId,
                paymentLifecycle.CompensationDisplayNumber,
                paymentLifecycle.CompensationSourceType,
                paymentLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(paymentCompletion);
            Assert.Equal("journal_entry_reversed", paymentCompletion!.OutcomeCode);
            settlementApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyInvoicePlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyInvoicePlan);
            Assert.True(readyInvoicePlan!.CanExecute);
            Assert.Equal("planned", readyInvoicePlan.OverallStatus);

            var readyInvoiceExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyInvoiceExecute);
            Assert.Equal("execution_request_recorded", readyInvoiceExecute!.OutcomeCode);

            var invoiceLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                invoiceJournalEntryId,
                userId,
                CancellationToken.None);

            invoiceCompensationJournalEntryId = invoiceLifecycle.CompensationJournalEntryId;

            var invoiceCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "invoice",
                invoiceId,
                invoiceAttempt.RequestId.Value,
                userId,
                invoiceLifecycle.CompensationJournalEntryId,
                invoiceLifecycle.CompensationDisplayNumber,
                invoiceLifecycle.CompensationSourceType,
                invoiceLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(invoiceCompletion);
            Assert.True(invoiceCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", invoiceCompletion.OutcomeCode);
            Assert.Equal("invoice_reversal", invoiceCompletion.Request.CompensationSourceType);

            var invoiceStatus = await GetDocumentStatusAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);
            var receivePaymentStatus = await GetDocumentStatusAsync(connectionFactory, "receive_payments", receivePaymentId, CancellationToken.None);
            var invoiceJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, invoiceJournalEntryId, CancellationToken.None);
            var receivePaymentJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, receivePaymentJournalEntryId, CancellationToken.None);
            var openItem = await GetArOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var invoiceReview = await journalEntryReviewStore.GetAsync(CompanyId, invoiceJournalEntryId, CancellationToken.None);
            var invoiceCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, invoiceCompensationJournalEntryId, CancellationToken.None);
            var paymentReview = await journalEntryReviewStore.GetAsync(CompanyId, receivePaymentJournalEntryId, CancellationToken.None);
            var paymentCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, receivePaymentCompensationJournalEntryId, CancellationToken.None);
            var invoiceSourceReview = await reviewRepository.GetSourceDocumentAsync(CompanyId.FromOrdinal(1), "invoice", invoiceId, CancellationToken.None);
            var paymentSourceReview = await reviewRepository.GetSourceDocumentAsync(CompanyId.FromOrdinal(1), "receive_payment", receivePaymentId, CancellationToken.None);

            Assert.Equal("reversed", invoiceStatus);
            Assert.Equal("reversed", receivePaymentStatus);
            Assert.Equal("reversed", invoiceJournalStatus);
            Assert.Equal("reversed", receivePaymentJournalStatus);
            Assert.NotNull(openItem);
            Assert.Equal("voided", openItem!.Status);
            Assert.Equal(0m, openItem.OpenAmountTx);
            Assert.Equal(0m, openItem.OpenAmountBase);

            Assert.NotNull(invoiceReview);
            Assert.Equal("reversed", invoiceReview!.Status);
            Assert.Equal(fxSnapshotId, invoiceReview.FxSnapshotId);
            Assert.NotNull(invoiceCompensationReview);
            Assert.Equal("invoice_reversal", invoiceCompensationReview!.SourceType);
            Assert.Equal(fxSnapshotId, invoiceCompensationReview.FxSnapshotId);

            Assert.NotNull(paymentReview);
            Assert.Equal("reversed", paymentReview!.Status);
            Assert.Equal("EUR", paymentReview.TransactionCurrencyCode);
            Assert.Null(paymentReview.FxSnapshotId);
            Assert.NotNull(paymentCompensationReview);
            Assert.Equal("receive_payment_reversal", paymentCompensationReview!.SourceType);
            Assert.Equal("EUR", paymentCompensationReview.TransactionCurrencyCode);
            Assert.Null(paymentCompensationReview.FxSnapshotId);

            Assert.NotNull(invoiceSourceReview);
            Assert.Equal("reversed", invoiceSourceReview!.JournalEntryStatus);
            Assert.Equal("EUR", invoiceSourceReview.TransactionCurrencyCode);
            Assert.Equal(100m, invoiceSourceReview.TotalAmount);
            Assert.NotNull(paymentSourceReview);
            Assert.Equal("reversed", paymentSourceReview!.JournalEntryStatus);
            Assert.Equal("EUR", paymentSourceReview.TransactionCurrencyCode);
            Assert.Equal(100m, paymentSourceReview.TotalAmount);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, invoiceId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, receivePaymentId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, invoiceCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, receivePaymentCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, invoiceJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, receivePaymentJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "receive_payment_lines", "receive_payment_id", "receive_payments", receivePaymentId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsCreditNoteReverseAfterBlockingCreditApplicationIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var creditNoteRepository = new PostgresCreditNoteDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid creditNoteId = Guid.Empty;
        Guid invoiceId = Guid.NewGuid();
        Guid creditApplicationId = Guid.Empty;
        Guid sourceCreditOpenItemId = Guid.Empty;
        Guid targetInvoiceOpenItemId = Guid.Empty;
        Guid creditNoteJournalEntryId = Guid.Empty;
        Guid creditApplicationJournalEntryId = Guid.Empty;
        Guid creditNoteCompensationJournalEntryId = Guid.Empty;
        Guid creditApplicationCompensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            creditNoteId = (await creditNoteRepository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Credit note blocked-then-reversed smoke",
                    [new CreditNoteDraftLineSaveModel(1, revenueAccountId, "Blocked then reversed", 1m, 55m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "credit_notes", creditNoteId, CancellationToken.None);

            creditNoteJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "credit_note",
                creditNoteId,
                revenueAccountId,
                receivableControlAccountId,
                55m,
                "JE-SMOKE-AR-CHAIN-CN-001",
                CancellationToken.None);

            sourceCreditOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "credit_note",
                creditNoteId,
                CancellationToken.None,
                balanceSide: "credit");

            targetInvoiceOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            creditApplicationId = await InsertCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                sourceCreditOpenItemId,
                targetInvoiceOpenItemId,
                CancellationToken.None);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                sourceCreditOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);

            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                targetInvoiceOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);

            creditApplicationJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "credit_application",
                creditApplicationId,
                receivableControlAccountId,
                revenueAccountId,
                1m,
                "JE-SMOKE-AR-CHAIN-CA-001",
                CancellationToken.None);

            var creditNoteAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditNoteAttempt);
            Assert.Equal("request_recorded", creditNoteAttempt!.OutcomeCode);

            var creditNoteSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditNoteSubmit);
            Assert.Equal("submitted", creditNoteSubmit!.OutcomeCode);

            var blockedCreditNoteExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(blockedCreditNoteExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedCreditNoteExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                CancellationToken.None));
            Assert.Equal(creditApplicationId, initialBlocker.SettlementSourceId);
            Assert.Equal(sourceCreditOpenItemId, initialBlocker.TargetOpenItemId);

            var creditApplicationAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditApplicationAttempt);

            var creditApplicationSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditApplicationSubmit);
            Assert.Equal("submitted", creditApplicationSubmit!.OutcomeCode);

            var creditApplicationExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(creditApplicationExecute);
            Assert.Equal("execution_request_recorded", creditApplicationExecute!.OutcomeCode);

            var creditApplicationLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                creditApplicationJournalEntryId,
                userId,
                CancellationToken.None);
            creditApplicationCompensationJournalEntryId = creditApplicationLifecycle.CompensationJournalEntryId;

            var creditApplicationCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt.RequestId.Value,
                userId,
                creditApplicationLifecycle.CompensationJournalEntryId,
                creditApplicationLifecycle.CompensationDisplayNumber,
                creditApplicationLifecycle.CompensationSourceType,
                creditApplicationLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(creditApplicationCompletion);
            Assert.Equal("journal_entry_reversed", creditApplicationCompletion!.OutcomeCode);
            sourceApplicationId = Guid.Empty;
            targetApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyCreditNotePlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyCreditNotePlan);
            Assert.True(readyCreditNotePlan!.CanExecute);
            Assert.Equal("planned", readyCreditNotePlan.OverallStatus);

            var readyCreditNoteExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyCreditNoteExecute);
            Assert.Equal("execution_request_recorded", readyCreditNoteExecute!.OutcomeCode);

            var creditNoteLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                creditNoteJournalEntryId,
                userId,
                CancellationToken.None);
            creditNoteCompensationJournalEntryId = creditNoteLifecycle.CompensationJournalEntryId;

            var creditNoteCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                creditNoteLifecycle.CompensationJournalEntryId,
                creditNoteLifecycle.CompensationDisplayNumber,
                creditNoteLifecycle.CompensationSourceType,
                creditNoteLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(creditNoteCompletion);
            Assert.True(creditNoteCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", creditNoteCompletion.OutcomeCode);
            Assert.Equal("credit_note_reversal", creditNoteCompletion.Request.CompensationSourceType);

            var creditNoteStatus = await GetDocumentStatusAsync(connectionFactory, "credit_notes", creditNoteId, CancellationToken.None);
            var creditApplicationStatus = await GetDocumentStatusAsync(connectionFactory, "credit_applications", creditApplicationId, CancellationToken.None);
            var creditNoteJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, creditNoteJournalEntryId, CancellationToken.None);
            var creditApplicationJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, creditApplicationJournalEntryId, CancellationToken.None);
            var sourceOpenItemStatus = await GetArOpenItemStatusAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetArOpenItemSnapshotAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);

            Assert.Equal("reversed", creditNoteStatus);
            Assert.Equal("reversed", creditApplicationStatus);
            Assert.Equal("reversed", creditNoteJournalStatus);
            Assert.Equal("reversed", creditApplicationJournalStatus);
            Assert.Equal("voided", sourceOpenItemStatus);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", targetOpenItem!.Status);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, sourceApplicationId, CancellationToken.None);
            await CleanupSettlementApplicationAsync(connectionFactory, targetApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, creditNoteId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, creditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditNoteCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditApplicationCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditNoteJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditApplicationJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_application_lines", "credit_application_id", "credit_applications", creditApplicationId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_note_lines", "credit_note_id", "credit_notes", creditNoteId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsForeignCurrencyCreditNoteReverseAfterBlockingCreditApplicationIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var creditNoteRepository = new PostgresCreditNoteDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid creditNoteId = Guid.Empty;
        Guid invoiceId = Guid.NewGuid();
        Guid creditApplicationId = Guid.Empty;
        Guid sourceCreditOpenItemId = Guid.Empty;
        Guid targetInvoiceOpenItemId = Guid.Empty;
        Guid creditNoteJournalEntryId = Guid.Empty;
        Guid creditApplicationJournalEntryId = Guid.Empty;
        Guid creditNoteCompensationJournalEntryId = Guid.Empty;
        Guid creditApplicationCompensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            creditNoteId = (await creditNoteRepository.SaveDraftAsync(
                new CreditNoteDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency credit note blocked-then-reversed smoke",
                    [new CreditNoteDraftLineSaveModel(1, revenueAccountId, "FX blocked then reversed", 2m, 50m, null, 0m)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "credit_notes", creditNoteId, CancellationToken.None);

            creditNoteJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "credit_note",
                creditNoteId,
                revenueAccountId,
                receivableControlAccountId,
                125m,
                "JE-SMOKE-AR-CHAIN-CN-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            sourceCreditOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "credit_note",
                creditNoteId,
                CancellationToken.None,
                balanceSide: "credit",
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            targetInvoiceOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            creditApplicationId = await InsertCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                sourceCreditOpenItemId,
                targetInvoiceOpenItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                totalAmount: 100m,
                appliedAmountTx: 100m,
                applicationDate: fxDate);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                sourceCreditOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                targetInvoiceOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            creditApplicationJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "credit_application",
                creditApplicationId,
                receivableControlAccountId,
                revenueAccountId,
                125m,
                "JE-SMOKE-AR-CHAIN-CA-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual");

            var creditNoteAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditNoteAttempt);
            Assert.Equal("request_recorded", creditNoteAttempt!.OutcomeCode);

            var creditNoteSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditNoteSubmit);
            Assert.Equal("submitted", creditNoteSubmit!.OutcomeCode);

            var blockedCreditNoteExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(blockedCreditNoteExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedCreditNoteExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                CancellationToken.None));
            Assert.Equal(creditApplicationId, initialBlocker.SettlementSourceId);

            var creditApplicationAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditApplicationAttempt);

            var creditApplicationSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(creditApplicationSubmit);
            Assert.Equal("submitted", creditApplicationSubmit!.OutcomeCode);

            var creditApplicationExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(creditApplicationExecute);
            Assert.Equal("execution_request_recorded", creditApplicationExecute!.OutcomeCode);

            var creditApplicationLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                creditApplicationJournalEntryId,
                userId,
                CancellationToken.None);
            creditApplicationCompensationJournalEntryId = creditApplicationLifecycle.CompensationJournalEntryId;

            var creditApplicationCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                creditApplicationAttempt.RequestId.Value,
                userId,
                creditApplicationLifecycle.CompensationJournalEntryId,
                creditApplicationLifecycle.CompensationDisplayNumber,
                creditApplicationLifecycle.CompensationSourceType,
                creditApplicationLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(creditApplicationCompletion);
            Assert.Equal("journal_entry_reversed", creditApplicationCompletion!.OutcomeCode);
            sourceApplicationId = Guid.Empty;
            targetApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyCreditNoteExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyCreditNoteExecute);
            Assert.Equal("execution_request_recorded", readyCreditNoteExecute!.OutcomeCode);

            var creditNoteLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                creditNoteJournalEntryId,
                userId,
                CancellationToken.None);
            creditNoteCompensationJournalEntryId = creditNoteLifecycle.CompensationJournalEntryId;

            var creditNoteCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "credit_note",
                creditNoteId,
                creditNoteAttempt.RequestId.Value,
                userId,
                creditNoteLifecycle.CompensationJournalEntryId,
                creditNoteLifecycle.CompensationDisplayNumber,
                creditNoteLifecycle.CompensationSourceType,
                creditNoteLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(creditNoteCompletion);
            Assert.True(creditNoteCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", creditNoteCompletion.OutcomeCode);

            var creditNoteStatus = await GetDocumentStatusAsync(connectionFactory, "credit_notes", creditNoteId, CancellationToken.None);
            var creditApplicationStatus = await GetDocumentStatusAsync(connectionFactory, "credit_applications", creditApplicationId, CancellationToken.None);
            var creditNoteJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, creditNoteJournalEntryId, CancellationToken.None);
            var creditApplicationJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, creditApplicationJournalEntryId, CancellationToken.None);
            var sourceOpenItem = await GetArOpenItemSnapshotAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetArOpenItemSnapshotAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);
            var creditNoteReview = await journalEntryReviewStore.GetAsync(CompanyId, creditNoteJournalEntryId, CancellationToken.None);
            var creditNoteCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, creditNoteCompensationJournalEntryId, CancellationToken.None);
            var creditApplicationReview = await journalEntryReviewStore.GetAsync(CompanyId, creditApplicationJournalEntryId, CancellationToken.None);
            var creditApplicationCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, creditApplicationCompensationJournalEntryId, CancellationToken.None);

            Assert.Equal("reversed", creditNoteStatus);
            Assert.Equal("reversed", creditApplicationStatus);
            Assert.Equal("reversed", creditNoteJournalStatus);
            Assert.Equal("reversed", creditApplicationJournalStatus);
            Assert.NotNull(sourceOpenItem);
            Assert.Equal("voided", sourceOpenItem!.Status);
            Assert.Equal(0m, sourceOpenItem.OpenAmountTx);
            Assert.Equal(0m, sourceOpenItem.OpenAmountBase);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", targetOpenItem!.Status);
            Assert.Equal(100m, targetOpenItem.OpenAmountTx);
            Assert.Equal(125m, targetOpenItem.OpenAmountBase);

            Assert.NotNull(creditNoteReview);
            Assert.Equal("EUR", creditNoteReview!.TransactionCurrencyCode);
            Assert.Equal("USD", creditNoteReview.BaseCurrencyCode);
            Assert.Equal(fxSnapshotId, creditNoteReview.FxSnapshotId);
            Assert.NotNull(creditNoteCompensationReview);
            Assert.Equal("credit_note_reversal", creditNoteCompensationReview!.SourceType);
            Assert.Equal(fxSnapshotId, creditNoteCompensationReview.FxSnapshotId);
            Assert.Contains("snapshot", creditNoteCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(creditApplicationReview);
            Assert.Equal("EUR", creditApplicationReview!.TransactionCurrencyCode);
            Assert.Equal("USD", creditApplicationReview.BaseCurrencyCode);
            Assert.Null(creditApplicationReview.FxSnapshotId);
            Assert.NotNull(creditApplicationCompensationReview);
            Assert.Equal("credit_application_reversal", creditApplicationCompensationReview!.SourceType);
            Assert.Null(creditApplicationCompensationReview.FxSnapshotId);
            Assert.Contains("header-only", creditApplicationCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, sourceApplicationId, CancellationToken.None);
            await CleanupSettlementApplicationAsync(connectionFactory, targetApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, creditNoteId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, creditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditNoteCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditApplicationCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditNoteJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, creditApplicationJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_application_lines", "credit_application_id", "credit_applications", creditApplicationId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_note_lines", "credit_note_id", "credit_notes", creditNoteId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesPostedCreditApplicationBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid receivableControlAccountId = default;
        Guid revenueAccountId = default;
        UserId userId = default;
        Guid creditNoteId = Guid.NewGuid();
        Guid invoiceId = Guid.NewGuid();
        Guid creditApplicationId = Guid.Empty;
        Guid sourceCreditOpenItemId = Guid.Empty;
        Guid targetInvoiceOpenItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            sourceCreditOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "credit_note",
                creditNoteId,
                CancellationToken.None,
                balanceSide: "credit");
            targetInvoiceOpenItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None);

            creditApplicationId = await InsertCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                CustomerId,
                sourceCreditOpenItemId,
                targetInvoiceOpenItemId,
                CancellationToken.None);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                sourceCreditOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);
            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ar_open_item",
                targetInvoiceOpenItemId,
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "credit_application",
                creditApplicationId,
                receivableControlAccountId,
                revenueAccountId,
                1m,
                "JE-SMOKE-AR-CA-REV-001",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.Equal("execution_request_recorded", executeResult!.OutcomeCode);

            var lifecycleResult = await lifecycleStore.ReverseAsync(
                CompanyId,
                journalEntryId,
                userId,
                CancellationToken.None);

            compensationJournalEntryId = lifecycleResult.CompensationJournalEntryId;

            var completionResult = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                attempt.RequestId.Value,
                userId,
                lifecycleResult.CompensationJournalEntryId,
                lifecycleResult.CompensationDisplayNumber,
                lifecycleResult.CompensationSourceType,
                lifecycleResult.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(completionResult);
            Assert.True(completionResult!.Executed);
            Assert.Equal("journal_entry_reversed", completionResult.OutcomeCode);
            Assert.Equal("credit_application_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "credit_applications", creditApplicationId, CancellationToken.None);
            var sourceOpenItem = await GetArOpenItemSnapshotAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetArOpenItemSnapshotAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);
            var applicationCount = await CountSettlementApplicationsForSourceAsync(
                connectionFactory,
                "credit_application",
                creditApplicationId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "credit_application",
                creditApplicationId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "credit_application",
                creditApplicationId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("credit_application_reversal", compensationJournal.SourceType);
            Assert.Equal(creditApplicationId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.NotNull(sourceOpenItem);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", sourceOpenItem!.Status);
            Assert.Equal("open", targetOpenItem!.Status);
            Assert.Equal(55m, sourceOpenItem.OpenAmountTx);
            Assert.Equal(55m, targetOpenItem.OpenAmountTx);
            Assert.Equal(0, applicationCount);
            Assert.Equal(2, reversalAuditCount);
            Assert.Equal(2, reversalEvents.Count);
            Assert.Contains(reversalEvents, reversal => reversal.SettlementApplicationId == sourceApplicationId && reversal.TargetOpenItemId == sourceCreditOpenItemId);
            Assert.Contains(reversalEvents, reversal => reversal.SettlementApplicationId == targetApplicationId && reversal.TargetOpenItemId == targetInvoiceOpenItemId);
            Assert.All(reversalEvents, reversal =>
            {
                Assert.Equal(attempt.RequestId.Value, reversal.RequestId);
                Assert.Equal("credit_application", reversal.SourceType);
                Assert.Equal("ar_open_item", reversal.TargetOpenItemType);
                Assert.Equal(1m, reversal.AppliedAmountTx);
                Assert.Equal(1m, reversal.AppliedAmountBase);
            });
            sourceApplicationId = Guid.Empty;
            targetApplicationId = Guid.Empty;
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, sourceApplicationId, CancellationToken.None);
            await CleanupSettlementApplicationAsync(connectionFactory, targetApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, creditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "credit_application_lines", "credit_application_id", "credit_applications", creditApplicationId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, sourceCreditOpenItemId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, targetInvoiceOpenItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestAdjustmentAsync_RecordsGovernedWriteOffRequestWithoutChangingArOpenItemTruth()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var executionContextAccessor = new PostgresExecutionContextAccessor();
        var invoiceRepository = new PostgresInvoiceDocumentRepository(connectionFactory, executionContextAccessor);
        var openItemRepository = new PostgresArOpenItemRepository(connectionFactory, executionContextAccessor);
        var postingEngine = new DefaultPostingEngine(
            new DefaultPostingValidator(),
            new NullPostingPeriodPolicyValidator(),
            new NullTaxEngine(),
            new LocalFirstFxResolutionService(new PostgresFxSnapshotRepository(connectionFactory, executionContextAccessor)),
            new AccountingPostingFragmentBuilder(),
            new DefaultJournalAggregator(),
            new PostgresJournalEntryWriter(connectionFactory, executionContextAccessor));
        var adjustmentHandler = new PostArOpenItemAdjustmentCommandHandler(
            openItemRepository,
            postingEngine,
            new PostgresUnitOfWork(connectionFactory, executionContextAccessor));

        Guid revenueAccountId = default;
        Guid writeOffAccountId = default;
        Guid receivableControlAccountId = default;
        UserId userId = default;
        Guid invoiceId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid adjustmentJournalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            receivableControlAccountId = await CreateReceivableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            revenueAccountId = await CreateRevenueAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            writeOffAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            invoiceId = (await invoiceRepository.SaveDraftAsync(
                new InvoiceDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    CustomerId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Invoice write-off governance smoke test",
                    [new InvoiceDraftLineSaveModel(1, revenueAccountId, "Consulting services", 1m, 55m, null, 0m)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "invoices", invoiceId, CancellationToken.None);
            openItemId = await CreateArOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                CustomerId,
                "invoice",
                invoiceId,
                CancellationToken.None,
                amount: 55m);

            var before = await openItemRepository.GetDrillDownAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                CancellationToken.None);

            var preview = await openItemRepository.GetAdjustmentPreviewAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "write_off",
                new DateOnly(2026, 4, 15),
                null,
                CancellationToken.None);

            var attempt = await openItemRepository.RequestAdjustmentAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "write_off",
                new DateOnly(2026, 4, 15),
                40m,
                userId,
                "Small customer balance cleanup",
                CancellationToken.None);

            var latestDraft = await openItemRepository.GetLatestAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                CancellationToken.None);

            var submitResult = await openItemRepository.SubmitAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                attempt!.Request!.RequestId,
                userId,
                CancellationToken.None);

            var readiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                attempt.Request.RequestId,
                new DateOnly(2026, 4, 15),
                CancellationToken.None);

            var executionPlan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                attempt.Request.RequestId,
                new DateOnly(2026, 4, 15),
                CancellationToken.None);

            var invalidAdjustmentAccount = await Assert.ThrowsAsync<InvalidOperationException>(
                () => adjustmentHandler.HandleAsync(
                    new PostArOpenItemAdjustmentCommand(
                        CompanyId.FromOrdinal(1),
                        openItemId,
                        attempt.Request.RequestId,
                        UserId.FromOrdinal(1),
                        receivableControlAccountId,
                        new DateOnly(2026, 4, 15),
                        null),
                    CancellationToken.None));

            var executionResult = await adjustmentHandler.HandleAsync(
                new PostArOpenItemAdjustmentCommand(
                    CompanyId.FromOrdinal(1),
                    openItemId,
                    attempt.Request.RequestId,
                    UserId.FromOrdinal(1),
                    writeOffAccountId,
                    new DateOnly(2026, 4, 15),
                    null),
                CancellationToken.None);
            adjustmentJournalEntryId = executionResult.JournalEntryId ?? Guid.Empty;

            var followUpAttempt = await openItemRepository.RequestAdjustmentAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "write_off",
                new DateOnly(2026, 4, 15),
                null,
                userId,
                "Duplicate request should be rejected",
                CancellationToken.None);

            var after = await openItemRepository.GetDrillDownAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                CancellationToken.None);

            Assert.NotNull(before);
            Assert.NotNull(preview);
            Assert.True(preview!.IsAvailable);
            Assert.Equal("available_for_request", preview.AvailabilityMode);
            Assert.Equal("request_recording_only", preview.ExecutionMode);
            Assert.NotNull(attempt);
            Assert.True(attempt!.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.True(attempt.Persisted);
            Assert.Equal("request_recorded", attempt.OutcomeCode);
            Assert.NotNull(attempt.Request);
            Assert.Equal("draft", attempt.Request!.RequestStatus);
            Assert.Equal("not_started", attempt.Request.ExecutionStatus);
            Assert.Equal("write_off", attempt.Request.AdjustmentType);
            Assert.Equal(40m, attempt.Request.RequestedAdjustmentAmountTx);
            Assert.Equal(40m, attempt.Request.RequestedAdjustmentAmountBase);
            Assert.False(attempt.Request.RequiresApproval);
            Assert.Equal("not_required", attempt.Request.ApprovalStatus);
            Assert.NotNull(latestDraft);
            Assert.Equal(attempt.Request.RequestId, latestDraft!.RequestId);
            Assert.Equal("draft", latestDraft.RequestStatus);
            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);
            Assert.Equal("submitted", submitResult.Request.RequestStatus);
            Assert.NotNull(submitResult.Request.SubmittedAt);
            Assert.NotNull(readiness);
            Assert.True(readiness!.GovernanceReady);
            Assert.True(readiness.OpenItemReady);
            Assert.True(readiness.PostingExecutionReady);
            Assert.True(readiness.IsAvailable);
            Assert.Equal("posting_engine_adjustment", readiness.ExecutionMode);
            Assert.Equal("available_for_execution", readiness.AvailabilityMode);
            Assert.NotNull(executionPlan);
            Assert.True(executionPlan!.CanExecute);
            Assert.Equal("ready", executionPlan.OverallStatus);
            Assert.Contains(executionPlan.Steps, step => step.StepCode == "request_submitted" && step.StepStatus == "ready");
            Assert.Contains(executionPlan.Steps, step => step.StepCode == "current_open_item_truth_check" && step.StepStatus == "ready");
            Assert.Contains(executionPlan.Steps, step => step.StepCode == "post_adjustment_journal_entry" && step.StepStatus == "ready");
            Assert.Contains("not an active governed adjustment account", invalidAdjustmentAccount.Message);
            Assert.NotEqual(Guid.Empty, adjustmentJournalEntryId);
            Assert.Equal("posted", executionResult.Status);
            Assert.Equal(40m, executionResult.AdjustmentAmountTx);
            Assert.Equal(40m, executionResult.AdjustmentAmountBase);
            Assert.NotNull(followUpAttempt);
            Assert.True(followUpAttempt!.CommandAccepted);
            Assert.Equal("request_recorded", followUpAttempt.OutcomeCode);
            Assert.NotNull(followUpAttempt.Request);
            Assert.Equal(15m, followUpAttempt.Request!.RequestedAdjustmentAmountTx);
            Assert.NotNull(after);
            Assert.Equal(15m, after!.OpenAmountTx);
            Assert.Equal(15m, after.OpenAmountBase);
            Assert.Equal("partially_applied", after.Status);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, adjustmentJournalEntryId, CancellationToken.None);
            await CleanupArOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "invoice_lines", "invoice_id", "invoices", invoiceId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, writeOffAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, revenueAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, receivableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    private static async Task<Guid> CreateReceivableControlAccountAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
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
              is_active,
              is_system,
              is_system_default,
              system_role,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              'asset',
              'accounts_receivable',
              true,
              true,
              false,
              'accounts_receivable',
              false,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"AR-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", $"Smoke Accounts Receivable {entityNumber[^6..]}");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<Guid> CreateRevenueAccountAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
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
              is_active,
              is_system,
              is_system_default,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              'revenue',
              'revenue',
              true,
              false,
              false,
              true,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"REV-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", "Smoke Revenue");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static async Task<Guid> CreateExpenseAccountAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
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
              is_active,
              is_system,
              is_system_default,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              'expense',
              'expense',
              true,
              false,
              false,
              true,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"EXP-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", "Smoke Write-Off Expense");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static async Task<(UserId UserId, bool Created)> GetOrCreateUserAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText = "select id from users order by created_at limit 1;";
        var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
        if (existing is string userIdString && UserId.TryParse(userIdString, out var userId))
        {
            return (userId, false);
        }

        var newUserId = UserId.FromOrdinal(1);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (id, email, username, password_hash, status)
            values (@id, @email, @username, @password_hash, 'active');
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"smoke-{newUserId.Value}@citus.local");
        insertCommand.Parameters.AddWithValue("username", $"smoke-{newUserId.Value}");
        insertCommand.Parameters.AddWithValue("password_hash", "smoke-hash");
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return (newUserId, true);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var seed = Random.Shared.Next(0, 60_466_176);
            var candidate = EntityNumber.Create(year, seed).Value;
            if (!await EntityNumberExistsAsync(connectionFactory, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique entity number for receivable draft smoke test.");
    }

    private static async Task<bool> EntityNumberExistsAsync(
        PostgresConnectionFactory connectionFactory,
        string entityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with all_entities as (
              select entity_number from manual_journal_documents
              union all
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from credit_notes
              union all
              select entity_number from vendor_credits
              union all
              select entity_number from receive_payments
              union all
              select entity_number from pay_bills
              union all
              select entity_number from credit_applications
              union all
              select entity_number from vendor_credit_applications
              union all
              select entity_number from fx_revaluation_batches
              union all
              select entity_number from accounts
            )
            select 1
            from all_entities
            where entity_number = @entity_number
            limit 1;
            """;
        command.Parameters.AddWithValue("entity_number", entityNumber);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task CleanupDraftAsync(
        PostgresConnectionFactory connectionFactory,
        string lineTable,
        string lineForeignKey,
        string headerTable,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var lineCommand = connection.CreateCommand();
        lineCommand.CommandText = $"delete from {lineTable} where {lineForeignKey} = @document_id;";
        lineCommand.Parameters.AddWithValue("document_id", documentId);
        await lineCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var headerCommand = connection.CreateCommand();
        headerCommand.CommandText = $"delete from {headerTable} where id = @document_id;";
        headerCommand.Parameters.AddWithValue("document_id", documentId);
        await headerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    // S2.1 fixture: a single-component GST 5% tax code, both legacy and v2.
    // The line carries the LEGACY tax_codes id (that is what the schema and
    // existing UI send); the engine bridges to v2 via
    // sales_tax_codes.legacy_tax_code_id. Jurisdiction is resolved from the
    // S1 catalog seed (CA federal GST) rather than inserted.
    private static async Task<(Guid LegacyTaxCodeId, Guid SalesTaxCodeId)> CreateGstFivePercentTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var legacyTaxCodeId = Guid.NewGuid();
        var salesTaxCodeId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var rateId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var suffix = entityNumber[^5..];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        Guid jurisdictionId;
        await using (var jurisdictionCommand = connection.CreateCommand())
        {
            jurisdictionCommand.CommandText =
                """
                select id
                from sales_tax_jurisdictions
                where country_code = 'CA' and regime_type = 'gst'
                order by created_at
                limit 1;
                """;
            var resolved = await jurisdictionCommand.ExecuteScalarAsync(cancellationToken);
            jurisdictionId = resolved is Guid g
                ? g
                : throw new InvalidOperationException("S1 catalog seed missing: no CA GST jurisdiction found.");
        }

        await using (var legacyCommand = connection.CreateCommand())
        {
            legacyCommand.CommandText =
                """
                insert into tax_codes (
                  id, company_id, entity_number, code, name, rate_percent,
                  applies_to, is_active, created_at, updated_at)
                values (
                  @id, @company_id, @entity_number, @code, @name, 5,
                  'both', true, now(), now());
                """;
            legacyCommand.Parameters.AddWithValue("id", legacyTaxCodeId);
            legacyCommand.Parameters.AddWithValue("company_id", companyId.Value);
            legacyCommand.Parameters.AddWithValue("entity_number", entityNumber);
            legacyCommand.Parameters.AddWithValue("code", $"GST5-{suffix}");
            legacyCommand.Parameters.AddWithValue("name", "GST 5% (smoke)");
            await legacyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var codeCommand = connection.CreateCommand())
        {
            codeCommand.CommandText =
                """
                insert into sales_tax_codes (
                  id, company_id, code, name, treatment, applies_to,
                  is_active, legacy_tax_code_id, created_at, updated_at)
                values (
                  @id, @company_id, @code, @name, 'taxable', 'both',
                  true, @legacy_id, now(), now());
                """;
            codeCommand.Parameters.AddWithValue("id", salesTaxCodeId);
            codeCommand.Parameters.AddWithValue("company_id", companyId.Value);
            codeCommand.Parameters.AddWithValue("code", $"GST5V2-{suffix}");
            codeCommand.Parameters.AddWithValue("name", "GST 5% v2 (smoke)");
            codeCommand.Parameters.AddWithValue("legacy_id", legacyTaxCodeId);
            await codeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var componentCommand = connection.CreateCommand())
        {
            componentCommand.CommandText =
                """
                insert into sales_tax_code_components (
                  id, company_id, tax_code_id, jurisdiction_id, sequence,
                  is_compound, recoverability_mode, created_at, updated_at)
                values (
                  @id, @company_id, @tax_code_id, @jurisdiction_id, 1,
                  false, 'full', now(), now());
                """;
            componentCommand.Parameters.AddWithValue("id", componentId);
            componentCommand.Parameters.AddWithValue("company_id", companyId.Value);
            componentCommand.Parameters.AddWithValue("tax_code_id", salesTaxCodeId);
            componentCommand.Parameters.AddWithValue("jurisdiction_id", jurisdictionId);
            await componentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var rateCommand = connection.CreateCommand())
        {
            rateCommand.CommandText =
                """
                insert into sales_tax_code_component_rates (
                  id, component_id, rate_percent, effective_from, created_at)
                values (
                  @id, @component_id, 5, date '2000-01-01', now());
                """;
            rateCommand.Parameters.AddWithValue("id", rateId);
            rateCommand.Parameters.AddWithValue("component_id", componentId);
            await rateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return (legacyTaxCodeId, salesTaxCodeId);
    }

    private static async Task<decimal> GetInvoiceLineTaxTotalAsync(
        PostgresConnectionFactory connectionFactory,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select coalesce(sum(tax_amount), 0) from invoice_lines where invoice_id = @invoice_id;";
        command.Parameters.AddWithValue("invoice_id", invoiceId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<(int Count, decimal TaxTotal)> GetSnapshotSummaryAsync(
        PostgresConnectionFactory connectionFactory,
        string documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*), coalesce(sum(tax_amount), 0)
            from document_line_sales_tax_snapshots
            where document_type = @document_type and document_id = @document_id;
            """;
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ((int)reader.GetInt64(0), reader.GetDecimal(1));
    }

    private static async Task DeleteSnapshotsAsync(
        PostgresConnectionFactory connectionFactory,
        string documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from document_line_sales_tax_snapshots where document_type = @document_type and document_id = @document_id;";
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSalesTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        Guid salesTaxCodeId,
        CancellationToken cancellationToken)
    {
        if (salesTaxCodeId == Guid.Empty)
        {
            return;
        }

        // Components + rates cascade-delete from sales_tax_codes.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from sales_tax_codes where id = @id;";
        command.Parameters.AddWithValue("id", salesTaxCodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteLegacyTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        Guid legacyTaxCodeId,
        CancellationToken cancellationToken)
    {
        if (legacyTaxCodeId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from tax_codes where id = @id;";
        command.Parameters.AddWithValue("id", legacyTaxCodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkDocumentPostedAsync(
        PostgresConnectionFactory connectionFactory,
        string headerTable,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"update {headerTable} set status = 'posted', posted_at = now(), updated_at = now() where id = @document_id;";
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> InsertJournalEntryAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        string sourceType,
        Guid sourceId,
        string status,
        CancellationToken cancellationToken)
    {
        var journalEntryId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        // Seven tests in this class share the hardcoded display_number
        // 'JE-SMOKE-001' as part of their fixture seed (and assert on it
        // afterwards). The unique constraint
        // journal_entries_unique_display_number rejects the second
        // insert with PostgresException 23505 — what looked like a flake
        // was every test after the first one in the class failing
        // deterministically.
        //
        // Tests in the same xUnit assembly run sequentially
        // (DisableTestParallelization=true), so the prior test's row is
        // dead state by the time we reach this point. Drop it before
        // inserting ours.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await ReleaseJournalDisplayNumberAsync(connection, null, companyId, "JE-SMOKE-001", cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into journal_entries (
              id,
              company_id,
              entity_number,
              display_number,
              status,
              source_type,
              source_id,
              transaction_currency_code,
              base_currency_code,
              exchange_rate,
              exchange_rate_date,
              exchange_rate_source,
              total_tx_debit,
              total_tx_credit,
              total_debit,
              total_credit,
              posting_run_id,
              idempotency_key,
              posted_at,
              voided_at,
              reversed_at,
              created_by_user_id
            )
            values (
              @id,
              @company_id,
              @entity_number,
              'JE-SMOKE-001',
              @status,
              @source_type,
              @source_id,
              'USD',
              'USD',
              1,
              current_date,
              'smoke',
              25,
              25,
              25,
              25,
              @posting_run_id,
              @idempotency_key,
              case when @status in ('posted', 'voided', 'reversed') then now() else null end,
              case when @status = 'voided' then now() else null end,
              case when @status = 'reversed' then now() else null end,
              @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", journalEntryId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
        command.Parameters.AddWithValue("idempotency_key", $"smoke-je:{sourceType}:{sourceId:D}");
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return journalEntryId;
    }

    private static async Task<Guid> InsertJournalEntryWithBalancedLinesAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        string sourceType,
        Guid sourceId,
        Guid debitAccountId,
        Guid creditAccountId,
        decimal amount,
        string displayNumber,
        CancellationToken cancellationToken,
        string transactionCurrencyCode = "USD",
        string baseCurrencyCode = "USD",
        decimal? transactionAmount = null,
        decimal exchangeRate = 1m,
        DateOnly? exchangeRateDate = null,
        string exchangeRateSource = "smoke",
        Guid? fxSnapshotId = null)
    {
        var journalEntryId = Guid.NewGuid();
        var debitLineId = Guid.NewGuid();
        var creditLineId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var totalTransactionAmount = transactionAmount ?? amount;
        var effectiveExchangeRateDate = exchangeRateDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ReleaseJournalDisplayNumberAsync(connection, transaction, companyId, displayNumber, cancellationToken);

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                insert into journal_entries (
                  id,
                  company_id,
                  entity_number,
                  display_number,
                  status,
                  source_type,
                  source_id,
                  transaction_currency_code,
                  base_currency_code,
                  exchange_rate,
                  exchange_rate_date,
                  exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit,
                  total_tx_credit,
                  total_debit,
                  total_credit,
                  posting_run_id,
                  idempotency_key,
                  posted_at,
                  created_by_user_id
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @display_number,
                  'posted',
                  @source_type,
                  @source_id,
                  @transaction_currency_code,
                  @base_currency_code,
                  @exchange_rate,
                  @exchange_rate_date,
                  @exchange_rate_source,
                  @fx_rate_snapshot_id,
                  @transaction_amount,
                  @transaction_amount,
                  @amount,
                  @amount,
                  @posting_run_id,
                  @idempotency_key,
                  now(),
                  @created_by_user_id
                );
                """;
            headerCommand.Parameters.AddWithValue("id", journalEntryId);
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
            headerCommand.Parameters.AddWithValue("display_number", displayNumber);
            headerCommand.Parameters.AddWithValue("source_type", sourceType);
            headerCommand.Parameters.AddWithValue("source_id", sourceId);
            headerCommand.Parameters.AddWithValue("amount", amount);
            headerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
            headerCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            headerCommand.Parameters.AddWithValue("exchange_rate", exchangeRate);
            headerCommand.Parameters.AddWithValue("exchange_rate_date", effectiveExchangeRateDate);
            headerCommand.Parameters.AddWithValue("exchange_rate_source", exchangeRateSource);
            headerCommand.Parameters.AddWithValue("transaction_amount", totalTransactionAmount);
            headerCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            headerCommand.Parameters.AddWithValue("idempotency_key", $"smoke-je-balanced:{sourceType}:{sourceId:D}");
            headerCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            var snapshotParameter = headerCommand.Parameters.Add("fx_rate_snapshot_id", NpgsqlTypes.NpgsqlDbType.Uuid);
            snapshotParameter.Value = (object?)fxSnapshotId ?? DBNull.Value;
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertJournalEntryLineAsync(
            connection,
            transaction,
            companyId,
            journalEntryId,
            debitLineId,
            1,
            debitAccountId,
            totalTransactionAmount,
            0m,
            "Smoke debit",
            cancellationToken,
            amount,
            0m);
        await InsertJournalEntryLineAsync(
            connection,
            transaction,
            companyId,
            journalEntryId,
            creditLineId,
            2,
            creditAccountId,
            0m,
            totalTransactionAmount,
            "Smoke credit",
            cancellationToken,
            0m,
            amount);
        await InsertLedgerEntryAsync(
            connection,
            transaction,
            companyId,
            journalEntryId,
            debitLineId,
            debitAccountId,
            amount,
            0m,
            cancellationToken,
            transactionCurrencyCode,
            totalTransactionAmount,
            0m);
        await InsertLedgerEntryAsync(
            connection,
            transaction,
            companyId,
            journalEntryId,
            creditLineId,
            creditAccountId,
            0m,
            amount,
            cancellationToken,
            transactionCurrencyCode,
            0m,
            totalTransactionAmount);

        await transaction.CommitAsync(cancellationToken);
        return journalEntryId;
    }

    private static async Task ReleaseJournalDisplayNumberAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction? transaction,
        CompanyId companyId,
        string displayNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update journal_entries
            set display_number = display_number || '-STALE-' || left(replace(id::text, '-', ''), 8)
            where company_id = @company_id
              and display_number = @display_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("display_number", displayNumber);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupJournalEntryAsync(
        PostgresConnectionFactory connectionFactory,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        if (journalEntryId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.CommandText = "delete from ledger_entries where journal_entry_id = @journal_entry_id;";
        ledgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var lineCommand = connection.CreateCommand();
        lineCommand.CommandText = "delete from journal_entry_lines where journal_entry_id = @journal_entry_id;";
        lineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        await lineCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "delete from journal_entries where id = @journal_entry_id;";
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetJournalEntryStatusAsync(
        PostgresConnectionFactory connectionFactory,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from journal_entries where id = @journal_entry_id;";
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<JournalEntrySnapshot?> GetJournalEntrySnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select status, source_type, source_id
            from journal_entries
            where id = @journal_entry_id
            limit 1;
            """;
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalEntrySnapshot(
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_id")));
    }

    private static async Task<string?> GetDocumentStatusAsync(
        PostgresConnectionFactory connectionFactory,
        string tableName,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select status from {tableName} where id = @document_id;";
        command.Parameters.AddWithValue("document_id", documentId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task InsertJournalEntryLineAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        Guid lineId,
        int lineNumber,
        Guid accountId,
        decimal txDebit,
        decimal txCredit,
        string description,
        CancellationToken cancellationToken,
        decimal? baseDebit = null,
        decimal? baseCredit = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into journal_entry_lines (
              id,
              company_id,
              journal_entry_id,
              line_number,
              account_id,
              description,
              tx_debit,
              tx_credit,
              debit,
              credit,
              created_at
            )
            values (
              @id,
              @company_id,
              @journal_entry_id,
              @line_number,
              @account_id,
              @description,
              @tx_debit,
              @tx_credit,
              @debit,
              @credit,
              now()
            );
            """;
        command.Parameters.AddWithValue("id", lineId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("line_number", lineNumber);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("tx_debit", txDebit);
        command.Parameters.AddWithValue("tx_credit", txCredit);
        command.Parameters.AddWithValue("debit", baseDebit ?? txDebit);
        command.Parameters.AddWithValue("credit", baseCredit ?? txCredit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLedgerEntryAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        Guid journalEntryLineId,
        Guid accountId,
        decimal debit,
        decimal credit,
        CancellationToken cancellationToken,
        string transactionCurrencyCode = "USD",
        decimal? transactionDebit = null,
        decimal? transactionCredit = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into ledger_entries (
              id,
              company_id,
              journal_entry_id,
              journal_entry_line_id,
              posting_date,
              account_id,
              debit,
              credit,
              transaction_currency_code,
              tx_debit,
              tx_credit,
              created_at
            )
            values (
              @id,
              @company_id,
              @journal_entry_id,
              @journal_entry_line_id,
              current_date,
              @account_id,
              @debit,
              @credit,
              @transaction_currency_code,
              @tx_debit,
              @tx_credit,
              now()
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("debit", debit);
        command.Parameters.AddWithValue("credit", credit);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        command.Parameters.AddWithValue("tx_debit", transactionDebit ?? debit);
        command.Parameters.AddWithValue("tx_credit", transactionCredit ?? credit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> CreateManualFxSnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        UserId userId,
        DateOnly requestedDate,
        decimal rate,
        CancellationToken cancellationToken)
    {
        var snapshotId = Guid.NewGuid();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_fx_rate_snapshots (
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              notes,
              created_by_user_id,
              created_at
            )
            values (
              @id,
              @company_id,
              @base_currency_code,
              @quote_currency_code,
              @requested_date,
              @effective_date,
              @rate,
              'spot',
              'direct',
              'general',
              'normal',
              @provider_key,
              'manual',
              'manual',
              null,
              'Receivable FX invoice smoke snapshot',
              @created_by_user_id,
              now()
            );
            """;
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("effective_date", requestedDate);
        command.Parameters.AddWithValue("rate", rate);
        command.Parameters.AddWithValue("provider_key", $"smoke-{quoteCurrencyCode.ToLowerInvariant()}-{snapshotId:N}");
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return snapshotId;
    }

    private static async Task<DateOnly> ReserveUniqueSnapshotDateAsync(
        PostgresConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken)
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(45);
        for (var offset = 0; offset < 540; offset++)
        {
            var candidate = start.AddDays(offset);
            if (!await SnapshotIdentityExistsAsync(
                    connectionFactory,
                    baseCurrencyCode,
                    quoteCurrencyCode,
                    candidate,
                    cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique receivable FX snapshot date.");
    }

    private static async Task<bool> SnapshotIdentityExistsAsync(
        PostgresConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select 1
            from company_fx_rate_snapshots
            where company_id = @company_id
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and requested_date = @requested_date
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task CleanupFxSnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        if (snapshotId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from company_fx_rate_snapshots where id = @snapshot_id;";
        command.Parameters.AddWithValue("snapshot_id", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupAuditLogEntityAsync(
        PostgresConnectionFactory connectionFactory,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from audit_logs
            where entity_type = 'source_document_reverse_request'
              and (
                entity_id = @document_id::text
                or payload ->> 'DocumentId' = @document_id_text
                or payload ->> 'RequestId' in (
                  select payload ->> 'RequestId'
                  from audit_logs
                  where entity_type = 'source_document_reverse_request'
                    and (entity_id = @document_id::text or payload ->> 'DocumentId' = @document_id_text)
                )
              )
              or (
                entity_type = 'settlement_application_reversal'
                and payload ->> 'SourceId' = @document_id_text
              )
              or (
                entity_type = 'open_item_adjustment_request'
                and (
                  entity_id = @document_id::text
                  or payload ->> 'OpenItemId' = @document_id_text
                )
              );
            """;
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("document_id_text", documentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> CreateArOpenItemForSourceAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid customerId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken,
        decimal amount = 55m,
        string balanceSide = "debit",
        decimal? amountTx = null,
        decimal? amountBase = null,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD")
    {
        var openItemId = Guid.NewGuid();
        var originalAmountTx = amountTx ?? amount;
        var originalAmountBase = amountBase ?? amount;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into ar_open_items (
              id,
              company_id,
              customer_id,
              source_type,
              source_id,
              due_date,
              document_currency_code,
              base_currency_code,
              original_amount_tx,
              original_amount_base,
              open_amount_tx,
              open_amount_base,
              balance_side,
              status,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @customer_id,
              @source_type,
              @source_id,
              @due_date,
              @document_currency_code,
              @base_currency_code,
              @original_amount_tx,
              @original_amount_base,
              @original_amount_tx,
              @original_amount_base,
              @balance_side,
              'open',
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", openItemId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("original_amount_tx", originalAmountTx);
        command.Parameters.AddWithValue("original_amount_base", originalAmountBase);
        command.Parameters.AddWithValue("balance_side", balanceSide);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return openItemId;
    }

    private static async Task CleanupArOpenItemAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        if (openItemId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from ar_open_items where id = @open_item_id;";
        command.Parameters.AddWithValue("open_item_id", openItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> InsertReceivePaymentAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        Guid customerId,
        Guid bankAccountId,
        Guid targetArOpenItemId,
        CancellationToken cancellationToken,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD",
        decimal fxRate = 1m,
        string fxSource = "smoke",
        decimal totalAmount = 1m,
        decimal appliedAmountTx = 1m,
        DateOnly? paymentDate = null)
    {
        var receivePaymentId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var effectivePaymentDate = paymentDate ?? new DateOnly(2026, 4, 14);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                insert into receive_payments (
                  id,
                  company_id,
                  entity_number,
                  payment_number,
                  customer_id,
                  status,
                  payment_date,
                  bank_account_id,
                  document_currency_code,
                  base_currency_code,
                  fx_rate,
                  fx_requested_date,
                  fx_effective_date,
                  fx_source,
                  total_amount,
                  memo,
                  posted_at,
                  created_by_user_id
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @payment_number,
                  @customer_id,
                  'posted',
                  @payment_date,
                  @bank_account_id,
                  @document_currency_code,
                  @base_currency_code,
                  @fx_rate,
                  @payment_date,
                  @payment_date,
                  @fx_source,
                  @total_amount,
                  'Receive payment reverse smoke',
                  now(),
                  @created_by_user_id
                );
                """;
            headerCommand.Parameters.AddWithValue("id", receivePaymentId);
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
            headerCommand.Parameters.AddWithValue("payment_number", $"RP-{entityNumber[^6..]}");
            headerCommand.Parameters.AddWithValue("customer_id", customerId);
            headerCommand.Parameters.AddWithValue("payment_date", effectivePaymentDate);
            headerCommand.Parameters.AddWithValue("bank_account_id", bankAccountId);
            headerCommand.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
            headerCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            headerCommand.Parameters.AddWithValue("fx_rate", fxRate);
            headerCommand.Parameters.AddWithValue("fx_source", fxSource);
            headerCommand.Parameters.AddWithValue("total_amount", totalAmount);
            headerCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = transaction;
            lineCommand.CommandText =
                """
                insert into receive_payment_lines (
                  company_id,
                  receive_payment_id,
                  line_number,
                  target_ar_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @receive_payment_id,
                  1,
                  @target_ar_open_item_id,
                  @applied_amount_tx
                );
                """;
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("receive_payment_id", receivePaymentId);
            lineCommand.Parameters.AddWithValue("target_ar_open_item_id", targetArOpenItemId);
            lineCommand.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return receivePaymentId;
    }

    private static async Task<Guid> InsertCreditApplicationAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        Guid customerId,
        Guid sourceCreditArOpenItemId,
        Guid targetInvoiceArOpenItemId,
        CancellationToken cancellationToken,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD",
        decimal totalAmount = 1m,
        decimal appliedAmountTx = 1m,
        DateOnly? applicationDate = null)
    {
        var creditApplicationId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var effectiveApplicationDate = applicationDate ?? new DateOnly(2026, 4, 14);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                insert into credit_applications (
                  id,
                  company_id,
                  entity_number,
                  application_number,
                  customer_id,
                  status,
                  application_date,
                  document_currency_code,
                  base_currency_code,
                  total_amount,
                  memo,
                  posted_at,
                  created_by_user_id
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @application_number,
                  @customer_id,
                  'posted',
                  @application_date,
                  @document_currency_code,
                  @base_currency_code,
                  @total_amount,
                  'Credit application reverse smoke',
                  now(),
                  @created_by_user_id
                );
                """;
            headerCommand.Parameters.AddWithValue("id", creditApplicationId);
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
            headerCommand.Parameters.AddWithValue("application_number", $"CA-{entityNumber[^6..]}");
            headerCommand.Parameters.AddWithValue("customer_id", customerId);
            headerCommand.Parameters.AddWithValue("application_date", effectiveApplicationDate);
            headerCommand.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
            headerCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            headerCommand.Parameters.AddWithValue("total_amount", totalAmount);
            headerCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = transaction;
            lineCommand.CommandText =
                """
                insert into credit_application_lines (
                  company_id,
                  credit_application_id,
                  line_number,
                  source_credit_ar_open_item_id,
                  target_invoice_ar_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @credit_application_id,
                  1,
                  @source_credit_ar_open_item_id,
                  @target_invoice_ar_open_item_id,
                  @applied_amount_tx
                );
                """;
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("credit_application_id", creditApplicationId);
            lineCommand.Parameters.AddWithValue("source_credit_ar_open_item_id", sourceCreditArOpenItemId);
            lineCommand.Parameters.AddWithValue("target_invoice_ar_open_item_id", targetInvoiceArOpenItemId);
            lineCommand.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return creditApplicationId;
    }

    private static async Task<Guid> CreateSettlementApplicationForOpenItemAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        string sourceType,
        Guid sourceId,
        UserId userId,
        CancellationToken cancellationToken,
        decimal appliedAmountTx = 1m,
        decimal appliedAmountBase = 1m)
    {
        var applicationId = Guid.NewGuid();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into settlement_applications (
              id,
              company_id,
              application_type,
              source_type,
              source_id,
              target_open_item_type,
              target_open_item_id,
              applied_amount_tx,
              applied_amount_base,
              created_by_user_id
            )
            values (
              @id,
              @company_id,
              @application_type,
              @source_type,
              @source_id,
              @target_open_item_type,
              @target_open_item_id,
              @applied_amount_tx,
              @applied_amount_base,
              @created_by_user_id
            );
            """;
        command.Parameters.AddWithValue("id", applicationId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("application_type", sourceType);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("target_open_item_type", targetOpenItemType);
        command.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
        command.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
        command.Parameters.AddWithValue("applied_amount_base", appliedAmountBase);
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return applicationId;
    }

    private static async Task<Guid> ApplySettlementApplicationForOpenItemAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        string targetOpenItemType,
        Guid targetOpenItemId,
        string sourceType,
        Guid sourceId,
        UserId userId,
        CancellationToken cancellationToken,
        decimal appliedAmountTx = 1m,
        decimal appliedAmountBase = 1m)
    {
        var applicationId = await CreateSettlementApplicationForOpenItemAsync(
            connectionFactory,
            companyId,
            targetOpenItemType,
            targetOpenItemId,
            sourceType,
            sourceId,
            userId,
            cancellationToken,
            appliedAmountTx,
            appliedAmountBase);

        var targetTable = targetOpenItemType == "ar_open_item" ? "ar_open_items" : "ap_open_items";

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            update {targetTable}
            set open_amount_tx = open_amount_tx - @applied_amount_tx,
                open_amount_base = open_amount_base - @applied_amount_base,
                status = 'partially_applied',
                updated_at = now()
            where company_id = @company_id
              and id = @target_open_item_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("target_open_item_id", targetOpenItemId);
        command.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
        command.Parameters.AddWithValue("applied_amount_base", appliedAmountBase);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return applicationId;
    }

    private static async Task CleanupSettlementApplicationAsync(
        PostgresConnectionFactory connectionFactory,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        if (applicationId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from settlement_applications where id = @application_id;";
        command.Parameters.AddWithValue("application_id", applicationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetArOpenItemStatusAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from ar_open_items where id = @open_item_id;";
        command.Parameters.AddWithValue("open_item_id", openItemId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<OpenItemSnapshot?> GetArOpenItemSnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select status, open_amount_tx, open_amount_base
            from ar_open_items
            where id = @open_item_id
            limit 1;
            """;
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenItemSnapshot(
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    private static async Task<int> CountSettlementApplicationsForSourceAsync(
        PostgresConnectionFactory connectionFactory,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
            from settlement_applications
            where source_type = @source_type
              and source_id = @source_id;
            """;
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountSettlementApplicationReversalAuditsForSourceAsync(
        PostgresConnectionFactory connectionFactory,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
            from audit_logs
            where entity_type = 'settlement_application_reversal'
              and action = 'settlement_application_reversed'
              and payload ->> 'SourceType' = @source_type
              and payload ->> 'SourceId' = @source_id;
            """;
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record OpenItemSnapshot(
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase);

    private static async Task CleanupAccountAsync(
        PostgresConnectionFactory connectionFactory,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from accounts where id = @account_id;";
        command.Parameters.AddWithValue("account_id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupUserAsync(
        PostgresConnectionFactory connectionFactory,
        UserId userId,
        bool createdUser,
        CancellationToken cancellationToken)
    {
        if (!createdUser || userId.Value is null)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from users where id = @user_id;";
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record JournalEntrySnapshot(
        string Status,
        string SourceType,
        Guid SourceId);
}
