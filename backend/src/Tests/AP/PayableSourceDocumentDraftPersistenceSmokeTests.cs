using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;

namespace Tests.AP;

public sealed class PayableSourceDocumentDraftPersistenceSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task SaveDraftAsync_PersistsBillAndVendorCreditDrafts()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var vendorCreditRepository = new PostgresVendorCreditDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid vendorCreditId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var billResult = await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill smoke test",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Office supplies", 45m, null, 0m, false)]),
                CancellationToken.None);

            billId = billResult.DocumentId;
            Assert.StartsWith("BILL-", billResult.DisplayNumber, StringComparison.Ordinal);

            var updatedBillResult = await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    billId,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 21),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill smoke test updated",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Office supplies", 55m, null, 0m, false)]),
                CancellationToken.None);

            Assert.Equal(billId, updatedBillResult.DocumentId);

            var bill = await billRepository.GetForPostingAsync(CompanyId.FromOrdinal(1), billId, CancellationToken.None);
            Assert.NotNull(bill);
            Assert.Equal("draft", bill!.Status);
            Assert.Equal(55m, bill.TotalAmount);
            Assert.Equal(new DateOnly(2026, 5, 21), bill.DueDate);

            var vendorCreditResult = await vendorCreditRepository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 15),
                    new DateOnly(2026, 5, 15),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Vendor credit smoke test",
                    [new VendorCreditDraftLineSaveModel(1, expenseAccountId, "Purchase adjustment", 20m, null, 0m, false)]),
                CancellationToken.None);

            vendorCreditId = vendorCreditResult.DocumentId;
            Assert.StartsWith("VC-", vendorCreditResult.DisplayNumber, StringComparison.Ordinal);

            var vendorCredit = await vendorCreditRepository.GetForPostingAsync(CompanyId.FromOrdinal(1), vendorCreditId, CancellationToken.None);
            Assert.NotNull(vendorCredit);
            Assert.Equal("draft", vendorCredit!.Status);
            Assert.Equal(20m, vendorCredit.TotalAmount);
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_lines", "vendor_credit_id", "vendor_credits", vendorCreditId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsPostedBillAndVendorCreditUpdates()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var vendorCreditRepository = new PostgresVendorCreditDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid vendorCreditId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill lifecycle guard",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Posted bill", 35m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            var billException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                billRepository.SaveDraftAsync(
                    new BillDraftSaveModel(
                        billId,
                        CompanyId.FromOrdinal(1),
                        UserId.FromOrdinal(1),
                        VendorId,
                        new DateOnly(2026, 4, 14),
                        new DateOnly(2026, 5, 20),
                        "USD",
                        "USD",
                        null,
                        null,
                        null,
                        null,
                        "Bill lifecycle guard updated",
                        [new BillDraftLineSaveModel(1, expenseAccountId, "Posted bill", 45m, null, 0m, false)]),
                    CancellationToken.None));
            Assert.Contains("Only draft bills can be modified", billException.Message, StringComparison.Ordinal);

            vendorCreditId = (await vendorCreditRepository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 15),
                    new DateOnly(2026, 5, 15),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Vendor credit lifecycle guard",
                    [new VendorCreditDraftLineSaveModel(1, expenseAccountId, "Posted vendor credit", 15m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "vendor_credits", vendorCreditId, CancellationToken.None);

            var vendorCreditException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                vendorCreditRepository.SaveDraftAsync(
                    new VendorCreditDraftSaveModel(
                        vendorCreditId,
                        CompanyId.FromOrdinal(1),
                        UserId.FromOrdinal(1),
                        VendorId,
                        new DateOnly(2026, 4, 15),
                        new DateOnly(2026, 5, 21),
                        "USD",
                        "USD",
                        null,
                        null,
                        null,
                        null,
                        "Vendor credit lifecycle guard updated",
                        [new VendorCreditDraftLineSaveModel(1, expenseAccountId, "Posted vendor credit", 25m, null, 0m, false)]),
                    CancellationToken.None));
            Assert.Contains("Only draft vendor credits can be modified", vendorCreditException.Message, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_lines", "vendor_credit_id", "vendor_credits", vendorCreditId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsJournalEntryLinkForBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill review link",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Review link", 45m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            var review = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.NotNull(review);
            Assert.Equal(journalEntryId, review!.JournalEntryId);
            Assert.Equal("JE-SMOKE-AP-001", review.JournalEntryDisplayNumber);
            Assert.Equal("posted", review.JournalEntryStatus);
            Assert.Equal("posted_locked", review.LifecycleMode);
            Assert.False(review.CanEditDraft);
            Assert.False(review.CanPostDraft);
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "post_draft" && action.IsAvailable is false && action.AvailabilityMode == "blocked_by_status");
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "reopen_document" && action.IsAvailable is false && action.AvailabilityMode == "not_implemented");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsForeignCurrencyJournalEntryLinkForBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency bill review link",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Foreign review link", 100m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                expenseAccountId,
                payableControlAccountId,
                125m,
                "JE-SMOKE-AP-BILL-FX-001",
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
                "bill",
                billId,
                CancellationToken.None);
            var journalReview = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReview);
            Assert.Equal(journalEntryId, sourceReview!.JournalEntryId);
            Assert.Equal("JE-SMOKE-AP-BILL-FX-001", sourceReview.JournalEntryDisplayNumber);
            Assert.Equal("posted", sourceReview.JournalEntryStatus);
            Assert.Equal("EUR", sourceReview.TransactionCurrencyCode);
            Assert.Equal("USD", sourceReview.BaseCurrencyCode);
            Assert.Equal(100m, sourceReview.TotalAmount);
            Assert.Equal("posted_locked", sourceReview.LifecycleMode);

            Assert.NotNull(journalReview);
            Assert.Equal("bill", journalReview!.SourceType);
            Assert.Equal(billId, journalReview.SourceId);
            Assert.Equal("posted", journalReview.Status);
            Assert.Equal("EUR", journalReview.TransactionCurrencyCode);
            Assert.Equal("USD", journalReview.BaseCurrencyCode);
            Assert.Equal(1.25m, journalReview.ExchangeRate);
            Assert.Equal(fxDate, journalReview.ExchangeRateDate);
            Assert.Equal("manual", journalReview.ExchangeRateSource);
            Assert.True(journalReview.IsForeignCurrency);
            Assert.Equal(fxSnapshotId, journalReview.FxSnapshotId);
            Assert.Equal(100m, journalReview.TotalTransactionDebit);
            Assert.Equal(100m, journalReview.TotalTransactionCredit);
            Assert.Equal(125m, journalReview.TotalDebit);
            Assert.Equal(125m, journalReview.TotalCredit);
            Assert.Equal(2, journalReview.LineCount);
            Assert.Contains("snapshot", journalReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetSourceDocumentAsync_ReturnsVoidedJournalEntryLifecycleForBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill voided review link",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Voided review link", 45m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "voided",
                CancellationToken.None);

            var review = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.NotNull(review);
            Assert.Equal(journalEntryId, review!.JournalEntryId);
            Assert.Equal("voided", review.JournalEntryStatus);
            Assert.NotNull(review.JournalEntryVoidedAt);
            Assert.Equal("historical_linked_je_voided", review.LifecycleMode);
            Assert.False(review.CanEditDraft);
            Assert.False(review.CanPostDraft);
            Assert.Contains(review.LifecycleActions, action => action.ActionCode == "void_document" && action.AvailabilityMode == "blocked_by_linked_je_lifecycle");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetLifecyclePreviewAsync_ReturnsActionPreviewForVoidedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill lifecycle preview",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Lifecycle preview", 45m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "voided",
                CancellationToken.None);

            var preview = await reviewRepository.GetLifecyclePreviewAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.NotNull(preview);
            Assert.Equal("historical_linked_je_voided", preview!.LifecycleMode);
            Assert.Contains(preview.LifecycleActions, action => action.ActionCode == "void_document" && action.AvailabilityMode == "blocked_by_linked_je_lifecycle");
            Assert.Contains(preview.LifecycleActions, action => action.ActionCode == "edit_draft" && action.AvailabilityMode == "blocked_by_linked_je_lifecycle");
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetLifecycleActionPreviewAsync_ReturnsReopenActionPreviewForVoidedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill lifecycle action preview",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Lifecycle action preview", 55m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "voided",
                CancellationToken.None);

            var preview = await reviewRepository.GetLifecycleActionPreviewAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                "reopen_document",
                CancellationToken.None);

            Assert.NotNull(preview);
            Assert.Equal("historical_linked_je_voided", preview!.LifecycleMode);
            Assert.Equal("reopen_document", preview.ActionCode);
            Assert.Equal("blocked_by_linked_je_lifecycle", preview.AvailabilityMode);
            Assert.False(preview.IsAvailable);
            Assert.Contains("historical-only", preview.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AttemptVoidAsync_RejectsPostedBill_WithBlockedByPolicy()
    {
        // PR-7 (C-10): regression coverage for PR-6b's policy
        // enforcement extended to the AP side. Locked rule: posted
        // bills are NEVER voided — they're reversed. The matching
        // AR-side test lives in
        // ReceivableSourceDocumentDraftPersistenceSmokeTests.
        // AttemptVoidAsync_RejectsPostedInvoiceWithBlockedByPolicy.
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill void policy block",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Void policy", 60m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            // Posted bill, posted (NOT voided) journal entry. This is
            // the configuration that hits the lifecycle_mode=
            // 'posted_locked' / void_document='blocked_by_policy'
            // branch I added in PR-6b.
            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptVoidAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("void_document", attempt!.ActionCode);
            Assert.Equal("policy_block", attempt.ExecutionMode);
            Assert.False(attempt.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.Equal("blocked_by_policy", attempt.OutcomeCode);
            Assert.Equal("blocked_by_policy", attempt.AvailabilityMode);
            Assert.Contains("Reverse", attempt.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AttemptVoidAsync_ReturnsBlockedSkeletonForVoidedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill void skeleton",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Void skeleton", 60m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "voided",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptVoidAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("void_document", attempt!.ActionCode);
            // PR-6b: AttemptVoidAsync no longer returns "skeleton_only".
            // For the already-linked-JE-voided case the outcome is
            // "blocked" (not "blocked_by_policy") so the execution
            // mode is "request_recording" matching the reverse path's
            // vocabulary.
            Assert.Equal("request_recording", attempt.ExecutionMode);
            Assert.False(attempt.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.Equal("blocked", attempt.OutcomeCode);
            Assert.Equal("blocked_by_linked_je_lifecycle", attempt.AvailabilityMode);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AttemptReverseAsync_ReturnsBlockedSkeletonForVoidedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse skeleton",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse skeleton", 65m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "voided",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("reverse_document", attempt!.ActionCode);
            Assert.Equal("request_recording", attempt.ExecutionMode);
            Assert.False(attempt.CommandAccepted);
            Assert.False(attempt.Executed);
            Assert.False(attempt.Persisted);
            Assert.Null(attempt.RequestId);
            Assert.Equal("blocked", attempt.OutcomeCode);
            Assert.Equal("blocked_by_linked_je_lifecycle", attempt.AvailabilityMode);

            var request = await reviewRepository.GetLatestReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            Assert.Null(request);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancelReverseRequestAsync_CancelsRecordedRequestForPostedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse request lifecycle",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse lifecycle", 80m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);
            Assert.NotNull(attempt.RequestId);

            var cancelResult = await reviewRepository.CancelReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(cancelResult);
            Assert.Equal("cancelled", cancelResult!.OutcomeCode);
            Assert.Equal("cancelled", cancelResult.Request.RequestStatus);
            Assert.Equal("user", cancelResult.Request.CancelledByActorType);
            Assert.Equal((object?)userId, (object?)cancelResult.Request.CancelledByActorId);
            Assert.NotNull(cancelResult.Request.CancelledAt);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteReverseRequestAsync_BlocksWhenRequestIsStillDraft()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse execute skeleton",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse execute", 95m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);
            Assert.NotNull(attempt.RequestId);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId!.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.False(executeResult!.CommandAccepted);
            Assert.False(executeResult.Executed);
            Assert.False(executeResult.Persisted);
            Assert.Equal("blocked", executeResult.OutcomeCode);
            Assert.Equal("draft", executeResult.Request.RequestStatus);
            Assert.Equal("not_requested", executeResult.Request.ExecutionStatus);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteReverseRequestAsync_BlocksWhenBillStillHasApOpenItemTruth()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse open-item guard",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse open-item guard", 59m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            var payBillId = Guid.NewGuid();
            settlementApplicationId = await CreateSettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                openItemId,
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);
            Assert.NotNull(attempt.RequestId);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(executeResult);
            Assert.False(executeResult!.CommandAccepted);
            Assert.False(executeResult.Executed);
            Assert.False(executeResult.Persisted);
            Assert.Equal("blocked_by_subledger_truth", executeResult.OutcomeCode);
            Assert.Contains("AP settlement/application trail", executeResult.Message);

            var blockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);

            var blocker = Assert.Single(blockers);
            Assert.Equal(settlementApplicationId, blocker.SettlementApplicationId);
            Assert.Equal("pay_bill", blocker.SettlementSourceType);
            Assert.Equal(payBillId, blocker.SettlementSourceId);
            Assert.Equal("ap_open_item", blocker.TargetOpenItemType);
            Assert.Equal(openItemId, blocker.TargetOpenItemId);
            Assert.Equal("bill", blocker.TargetSourceType);
            Assert.Equal(billId, blocker.TargetSourceId);
            Assert.Equal(1m, blocker.AppliedAmountTx);
            Assert.Equal(1m, blocker.AppliedAmountBase);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetReverseRequestExecutionPlanAsync_ReturnsPlannedStepsForSubmittedBillWithoutOpenItemTruth()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse execution plan",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse plan", 88m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                "posted",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var plan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(plan);
            Assert.True(plan!.CanExecute);
            Assert.Equal("planned", plan.OverallStatus);
            Assert.Equal(5, plan.Steps.Count);
            Assert.Equal("request_status_gate", plan.Steps[0].StepCode);
            Assert.Equal("ready", plan.Steps[0].StepStatus);
            Assert.Equal("governance_readiness_gate", plan.Steps[1].StepCode);
            Assert.Equal("ready", plan.Steps[1].StepStatus);
            Assert.Equal("subledger_reversal_gate", plan.Steps[2].StepCode);
            Assert.Equal("ready", plan.Steps[2].StepStatus);
            Assert.Equal("reverse_linked_journal_entry", plan.Steps[3].StepCode);
            Assert.Equal("ready", plan.Steps[3].StepStatus);
            Assert.Equal("mark_source_document_reversed", plan.Steps[4].StepCode);
            Assert.Equal("ready", plan.Steps[4].StepStatus);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_RecordsJournalEntryReversalForSubmittedBill()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid expenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill reverse completion",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Reverse completion", 72m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                expenseAccountId,
                payableControlAccountId,
                72m,
                "JE-SMOKE-AP-REV-001",
                CancellationToken.None);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
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
                "bill",
                billId,
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
            Assert.Equal("bill_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "bills", billId, CancellationToken.None);
            var openItemStatus = await GetApOpenItemStatusAsync(connectionFactory, openItemId, CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("bill_reversal", compensationJournal.SourceType);
            Assert.Equal(billId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.Equal("voided", openItemStatus);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesPostedPayBillBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid billId = Guid.NewGuid();
        Guid payBillId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            payBillId = await InsertPayBillAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                expenseAccountId,
                openItemId,
                CancellationToken.None);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                openItemId,
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "pay_bill",
                payBillId,
                payableControlAccountId,
                expenseAccountId,
                1m,
                "JE-SMOKE-AP-PB-REV-001",
                CancellationToken.None);

            var sourceReviewBeforeReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                CancellationToken.None);
            var originalReviewBeforeReverse = await journalEntryReviewStore.GetAsync(
                CompanyId,
                journalEntryId,
                CancellationToken.None);

            Assert.NotNull(sourceReviewBeforeReverse);
            Assert.Equal(journalEntryId, sourceReviewBeforeReverse!.JournalEntryId);
            Assert.Equal("posted", sourceReviewBeforeReverse.JournalEntryStatus);
            Assert.NotNull(originalReviewBeforeReverse);
            Assert.Equal("pay_bill", originalReviewBeforeReverse!.SourceType);
            Assert.Equal(payBillId, originalReviewBeforeReverse.SourceId);
            Assert.Equal("posted", originalReviewBeforeReverse.Status);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
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
                "pay_bill",
                payBillId,
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
            Assert.Equal("pay_bill_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "pay_bills", payBillId, CancellationToken.None);
            var openItem = await GetApOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var sourceReviewAfterReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
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
                "pay_bill",
                payBillId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "pay_bill",
                payBillId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("pay_bill_reversal", compensationJournal.SourceType);
            Assert.Equal(payBillId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.NotNull(openItem);
            Assert.Equal("open", openItem!.Status);
            Assert.Equal(72m, openItem.OpenAmountTx);
            Assert.Equal(72m, openItem.OpenAmountBase);
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
                entry => entry.Id == compensationJournalEntryId && entry.SourceType == "pay_bill_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("posted", compensationReview!.Status);
            Assert.Equal("pay_bill_reversal", compensationReview.SourceType);
            Assert.Equal(payBillId, compensationReview.SourceId);
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
            Assert.Equal("pay_bill", reversalEvent.SourceType);
            Assert.Equal("ap_open_item", reversalEvent.TargetOpenItemType);
            Assert.Equal(openItemId, reversalEvent.TargetOpenItemId);
            Assert.Equal(1m, reversalEvent.AppliedAmountTx);
            Assert.Equal(1m, reversalEvent.AppliedAmountBase);
            settlementApplicationId = Guid.Empty;
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, payBillId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "pay_bill_lines", "pay_bill_id", "pay_bills", payBillId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesForeignCurrencyPayBillBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid billId = Guid.NewGuid();
        Guid payBillId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            payBillId = await InsertPayBillAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                expenseAccountId,
                openItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                fxRate: 1.25m,
                fxSource: "manual",
                totalAmount: 100m,
                appliedAmountTx: 100m,
                paymentDate: new DateOnly(2026, 4, 14));

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                openItemId,
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "pay_bill",
                payBillId,
                payableControlAccountId,
                expenseAccountId,
                125m,
                "JE-SMOKE-AP-PB-FX-REV-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: new DateOnly(2026, 4, 14),
                exchangeRateSource: "manual");

            var sourceReviewBeforeReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
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
            Assert.Equal("pay_bill", originalReviewBeforeReverse!.SourceType);
            Assert.Equal(payBillId, originalReviewBeforeReverse.SourceId);
            Assert.Equal("EUR", originalReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal("USD", originalReviewBeforeReverse.BaseCurrencyCode);
            Assert.Equal(1.25m, originalReviewBeforeReverse.ExchangeRate);
            Assert.Equal(new DateOnly(2026, 4, 14), originalReviewBeforeReverse.ExchangeRateDate);
            Assert.Equal("manual", originalReviewBeforeReverse.ExchangeRateSource);
            Assert.Null(originalReviewBeforeReverse.FxSnapshotId);
            Assert.Contains("header-only", originalReviewBeforeReverse.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
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
                "pay_bill",
                payBillId,
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
            Assert.Equal("pay_bill_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "pay_bills", payBillId, CancellationToken.None);
            var openItem = await GetApOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var sourceReviewAfterReverse = await reviewRepository.GetSourceDocumentAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
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
                "pay_bill",
                payBillId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "pay_bill",
                payBillId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("pay_bill_reversal", compensationJournal.SourceType);
            Assert.Equal(payBillId, compensationJournal.SourceId);
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
            Assert.Contains(
                originalReviewAfterReverse.RelatedEntries,
                entry => entry.Id == compensationJournalEntryId && entry.SourceType == "pay_bill_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("posted", compensationReview!.Status);
            Assert.Equal("pay_bill_reversal", compensationReview.SourceType);
            Assert.Equal(payBillId, compensationReview.SourceId);
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
            Assert.Equal("pay_bill", reversalEvent.SourceType);
            Assert.Equal("ap_open_item", reversalEvent.TargetOpenItemType);
            Assert.Equal(openItemId, reversalEvent.TargetOpenItemId);
            Assert.Equal(100m, reversalEvent.AppliedAmountTx);
            Assert.Equal(125m, reversalEvent.AppliedAmountBase);
            settlementApplicationId = Guid.Empty;
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, payBillId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "pay_bill_lines", "pay_bill_id", "pay_bills", payBillId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsBillReverseAfterBlockingPayBillIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid payBillId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid billJournalEntryId = Guid.Empty;
        Guid payBillJournalEntryId = Guid.Empty;
        Guid billCompensationJournalEntryId = Guid.Empty;
        Guid payBillCompensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill blocked-then-reversed smoke",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Blocked then reversed", 72m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            billJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                expenseAccountId,
                payableControlAccountId,
                72m,
                "JE-SMOKE-AP-CHAIN-BILL-001",
                CancellationToken.None);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            payBillId = await InsertPayBillAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                expenseAccountId,
                openItemId,
                CancellationToken.None);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                openItemId,
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);

            payBillJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "pay_bill",
                payBillId,
                payableControlAccountId,
                expenseAccountId,
                1m,
                "JE-SMOKE-AP-CHAIN-PB-001",
                CancellationToken.None);

            var billAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(billAttempt);
            Assert.Equal("request_recorded", billAttempt!.OutcomeCode);

            var billSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(billSubmit);
            Assert.Equal("submitted", billSubmit!.OutcomeCode);

            var blockedBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(blockedBillExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedBillExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None));
            Assert.Equal(payBillId, initialBlocker.SettlementSourceId);

            var payBillAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);
            Assert.NotNull(payBillAttempt);

            var payBillSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(payBillSubmit);
            Assert.Equal("submitted", payBillSubmit!.OutcomeCode);

            var payBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(payBillExecute);
            Assert.Equal("execution_request_recorded", payBillExecute!.OutcomeCode);

            var payBillLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                payBillJournalEntryId,
                userId,
                CancellationToken.None);

            payBillCompensationJournalEntryId = payBillLifecycle.CompensationJournalEntryId;

            var payBillCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt.RequestId.Value,
                userId,
                payBillLifecycle.CompensationJournalEntryId,
                payBillLifecycle.CompensationDisplayNumber,
                payBillLifecycle.CompensationSourceType,
                payBillLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(payBillCompletion);
            Assert.Equal("journal_entry_reversed", payBillCompletion!.OutcomeCode);
            settlementApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyBillPlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyBillPlan);
            Assert.True(readyBillPlan!.CanExecute);
            Assert.Equal("planned", readyBillPlan.OverallStatus);

            var readyBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyBillExecute);
            Assert.Equal("execution_request_recorded", readyBillExecute!.OutcomeCode);

            var billLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                billJournalEntryId,
                userId,
                CancellationToken.None);

            billCompensationJournalEntryId = billLifecycle.CompensationJournalEntryId;

            var invalidBillCompletion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reviewRepository.CompleteReverseRequestExecutionAsync(
                    CompanyId.FromOrdinal(1),
                    "bill",
                    billId,
                    billAttempt.RequestId.Value,
                    userId,
                    billLifecycle.CompensationJournalEntryId,
                    "JE-SMOKE-WRONG",
                    billLifecycle.CompensationSourceType,
                    billLifecycle.LifecycleAt,
                    CancellationToken.None));
            Assert.Contains("does not match", invalidBillCompletion.Message, StringComparison.OrdinalIgnoreCase);

            var billRequestAfterRejectedCompletion = await reviewRepository.GetReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                CancellationToken.None);
            var billStatusAfterRejectedCompletion = await GetDocumentStatusAsync(
                connectionFactory,
                "bills",
                billId,
                CancellationToken.None);

            Assert.NotNull(billRequestAfterRejectedCompletion);
            Assert.Equal("execution_requested", billRequestAfterRejectedCompletion!.ExecutionStatus);
            Assert.Equal("posted", billStatusAfterRejectedCompletion);

            var billCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                billLifecycle.CompensationJournalEntryId,
                billLifecycle.CompensationDisplayNumber,
                billLifecycle.CompensationSourceType,
                billLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(billCompletion);
            Assert.True(billCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", billCompletion.OutcomeCode);
            Assert.Equal("bill_reversal", billCompletion.Request.CompensationSourceType);

            var billStatus = await GetDocumentStatusAsync(connectionFactory, "bills", billId, CancellationToken.None);
            var payBillStatus = await GetDocumentStatusAsync(connectionFactory, "pay_bills", payBillId, CancellationToken.None);
            var billJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, billJournalEntryId, CancellationToken.None);
            var payBillJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, payBillJournalEntryId, CancellationToken.None);
            var openItemStatus = await GetApOpenItemStatusAsync(connectionFactory, openItemId, CancellationToken.None);

            Assert.Equal("reversed", billStatus);
            Assert.Equal("reversed", payBillStatus);
            Assert.Equal("reversed", billJournalStatus);
            Assert.Equal("reversed", payBillJournalStatus);
            Assert.Equal("voided", openItemStatus);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, payBillId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, billCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, payBillCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, billJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, payBillJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "pay_bill_lines", "pay_bill_id", "pay_bills", payBillId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsForeignCurrencyBillReverseAfterBlockingPayBillIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid payBillId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid billJournalEntryId = Guid.Empty;
        Guid payBillJournalEntryId = Guid.Empty;
        Guid billCompensationJournalEntryId = Guid.Empty;
        Guid payBillCompensationJournalEntryId = Guid.Empty;
        Guid settlementApplicationId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency bill blocked-then-reversed smoke",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "FX blocked then reversed", 100m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);

            billJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "bill",
                billId,
                expenseAccountId,
                payableControlAccountId,
                125m,
                "JE-SMOKE-AP-CHAIN-BILL-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            payBillId = await InsertPayBillAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                expenseAccountId,
                openItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                fxRate: 1.25m,
                fxSource: "manual",
                totalAmount: 100m,
                appliedAmountTx: 100m,
                paymentDate: fxDate);

            settlementApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                openItemId,
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            payBillJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "pay_bill",
                payBillId,
                payableControlAccountId,
                expenseAccountId,
                125m,
                "JE-SMOKE-AP-CHAIN-PB-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual");

            var billAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                userId,
                CancellationToken.None);

            Assert.NotNull(billAttempt);
            Assert.Equal("request_recorded", billAttempt!.OutcomeCode);

            var billSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(billSubmit);
            Assert.Equal("submitted", billSubmit!.OutcomeCode);

            var blockedBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);

            Assert.NotNull(blockedBillExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedBillExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None));
            Assert.Equal(payBillId, initialBlocker.SettlementSourceId);

            var payBillAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                userId,
                CancellationToken.None);
            Assert.NotNull(payBillAttempt);

            var payBillSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(payBillSubmit);
            Assert.Equal("submitted", payBillSubmit!.OutcomeCode);

            var payBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(payBillExecute);
            Assert.Equal("execution_request_recorded", payBillExecute!.OutcomeCode);

            var payBillLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                payBillJournalEntryId,
                userId,
                CancellationToken.None);

            payBillCompensationJournalEntryId = payBillLifecycle.CompensationJournalEntryId;

            var payBillCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "pay_bill",
                payBillId,
                payBillAttempt.RequestId.Value,
                userId,
                payBillLifecycle.CompensationJournalEntryId,
                payBillLifecycle.CompensationDisplayNumber,
                payBillLifecycle.CompensationSourceType,
                payBillLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(payBillCompletion);
            Assert.Equal("journal_entry_reversed", payBillCompletion!.OutcomeCode);
            settlementApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyBillPlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyBillPlan);
            Assert.True(readyBillPlan!.CanExecute);
            Assert.Equal("planned", readyBillPlan.OverallStatus);

            var readyBillExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyBillExecute);
            Assert.Equal("execution_request_recorded", readyBillExecute!.OutcomeCode);

            var billLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                billJournalEntryId,
                userId,
                CancellationToken.None);

            billCompensationJournalEntryId = billLifecycle.CompensationJournalEntryId;

            var billCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "bill",
                billId,
                billAttempt.RequestId.Value,
                userId,
                billLifecycle.CompensationJournalEntryId,
                billLifecycle.CompensationDisplayNumber,
                billLifecycle.CompensationSourceType,
                billLifecycle.LifecycleAt,
                CancellationToken.None);

            Assert.NotNull(billCompletion);
            Assert.True(billCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", billCompletion.OutcomeCode);
            Assert.Equal("bill_reversal", billCompletion.Request.CompensationSourceType);

            var billStatus = await GetDocumentStatusAsync(connectionFactory, "bills", billId, CancellationToken.None);
            var payBillStatus = await GetDocumentStatusAsync(connectionFactory, "pay_bills", payBillId, CancellationToken.None);
            var billJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, billJournalEntryId, CancellationToken.None);
            var payBillJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, payBillJournalEntryId, CancellationToken.None);
            var openItem = await GetApOpenItemSnapshotAsync(connectionFactory, openItemId, CancellationToken.None);
            var billReview = await journalEntryReviewStore.GetAsync(CompanyId, billJournalEntryId, CancellationToken.None);
            var billCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, billCompensationJournalEntryId, CancellationToken.None);
            var payBillReview = await journalEntryReviewStore.GetAsync(CompanyId, payBillJournalEntryId, CancellationToken.None);
            var payBillCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, payBillCompensationJournalEntryId, CancellationToken.None);
            var billSourceReview = await reviewRepository.GetSourceDocumentAsync(CompanyId.FromOrdinal(1), "bill", billId, CancellationToken.None);
            var payBillSourceReview = await reviewRepository.GetSourceDocumentAsync(CompanyId.FromOrdinal(1), "pay_bill", payBillId, CancellationToken.None);

            Assert.Equal("reversed", billStatus);
            Assert.Equal("reversed", payBillStatus);
            Assert.Equal("reversed", billJournalStatus);
            Assert.Equal("reversed", payBillJournalStatus);
            Assert.NotNull(openItem);
            Assert.Equal("voided", openItem!.Status);
            Assert.Equal(0m, openItem.OpenAmountTx);
            Assert.Equal(0m, openItem.OpenAmountBase);

            Assert.NotNull(billReview);
            Assert.Equal("reversed", billReview!.Status);
            Assert.Equal("EUR", billReview.TransactionCurrencyCode);
            Assert.Equal("USD", billReview.BaseCurrencyCode);
            Assert.Equal(fxSnapshotId, billReview.FxSnapshotId);

            Assert.NotNull(billCompensationReview);
            Assert.Equal("bill_reversal", billCompensationReview!.SourceType);
            Assert.Equal("EUR", billCompensationReview.TransactionCurrencyCode);
            Assert.Equal("USD", billCompensationReview.BaseCurrencyCode);
            Assert.Equal(fxSnapshotId, billCompensationReview.FxSnapshotId);
            Assert.Contains("snapshot", billCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(payBillReview);
            Assert.Equal("reversed", payBillReview!.Status);
            Assert.Equal("EUR", payBillReview.TransactionCurrencyCode);
            Assert.Equal("USD", payBillReview.BaseCurrencyCode);
            Assert.Null(payBillReview.FxSnapshotId);

            Assert.NotNull(payBillCompensationReview);
            Assert.Equal("pay_bill_reversal", payBillCompensationReview!.SourceType);
            Assert.Equal("EUR", payBillCompensationReview.TransactionCurrencyCode);
            Assert.Equal("USD", payBillCompensationReview.BaseCurrencyCode);
            Assert.Null(payBillCompensationReview.FxSnapshotId);
            Assert.Contains("header-only", payBillCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(billSourceReview);
            Assert.Equal("reversed", billSourceReview!.JournalEntryStatus);
            Assert.Equal("EUR", billSourceReview.TransactionCurrencyCode);
            Assert.Equal("USD", billSourceReview.BaseCurrencyCode);
            Assert.Equal(100m, billSourceReview.TotalAmount);

            Assert.NotNull(payBillSourceReview);
            Assert.Equal("reversed", payBillSourceReview!.JournalEntryStatus);
            Assert.Equal("EUR", payBillSourceReview.TransactionCurrencyCode);
            Assert.Equal("USD", payBillSourceReview.BaseCurrencyCode);
            Assert.Equal(100m, payBillSourceReview.TotalAmount);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, settlementApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, billId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, payBillId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, billCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, payBillCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, billJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, payBillJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "pay_bill_lines", "pay_bill_id", "pay_bills", payBillId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsVendorCreditReverseAfterBlockingVendorCreditApplicationIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var vendorCreditRepository = new PostgresVendorCreditDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid vendorCreditId = Guid.Empty;
        Guid billId = Guid.NewGuid();
        Guid vendorCreditApplicationId = Guid.Empty;
        Guid sourceVendorCreditOpenItemId = Guid.Empty;
        Guid targetBillOpenItemId = Guid.Empty;
        Guid vendorCreditJournalEntryId = Guid.Empty;
        Guid vendorCreditApplicationJournalEntryId = Guid.Empty;
        Guid vendorCreditCompensationJournalEntryId = Guid.Empty;
        Guid vendorCreditApplicationCompensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            vendorCreditId = (await vendorCreditRepository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Vendor credit blocked-then-reversed smoke",
                    [new VendorCreditDraftLineSaveModel(1, expenseAccountId, "Blocked then reversed", 72m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "vendor_credits", vendorCreditId, CancellationToken.None);

            vendorCreditJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "vendor_credit",
                vendorCreditId,
                payableControlAccountId,
                expenseAccountId,
                72m,
                "JE-SMOKE-AP-CHAIN-VC-001",
                CancellationToken.None);

            sourceVendorCreditOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None,
                balanceSide: "debit");

            targetBillOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            vendorCreditApplicationId = await InsertVendorCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                sourceVendorCreditOpenItemId,
                targetBillOpenItemId,
                CancellationToken.None);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                sourceVendorCreditOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);

            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                targetBillOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);

            vendorCreditApplicationJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                expenseAccountId,
                payableControlAccountId,
                1m,
                "JE-SMOKE-AP-CHAIN-VCA-001",
                CancellationToken.None);

            var vendorCreditAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditAttempt);
            Assert.Equal("request_recorded", vendorCreditAttempt!.OutcomeCode);

            var vendorCreditSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditSubmit);
            Assert.Equal("submitted", vendorCreditSubmit!.OutcomeCode);

            var blockedVendorCreditExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(blockedVendorCreditExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedVendorCreditExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None));
            Assert.Equal(vendorCreditApplicationId, initialBlocker.SettlementSourceId);
            Assert.Equal(sourceVendorCreditOpenItemId, initialBlocker.TargetOpenItemId);

            var vendorCreditApplicationAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationAttempt);

            var vendorCreditApplicationSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationSubmit);
            Assert.Equal("submitted", vendorCreditApplicationSubmit!.OutcomeCode);

            var vendorCreditApplicationExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationExecute);
            Assert.Equal("execution_request_recorded", vendorCreditApplicationExecute!.OutcomeCode);

            var vendorCreditApplicationLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                vendorCreditApplicationJournalEntryId,
                userId,
                CancellationToken.None);
            vendorCreditApplicationCompensationJournalEntryId = vendorCreditApplicationLifecycle.CompensationJournalEntryId;

            var vendorCreditApplicationCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt.RequestId.Value,
                userId,
                vendorCreditApplicationLifecycle.CompensationJournalEntryId,
                vendorCreditApplicationLifecycle.CompensationDisplayNumber,
                vendorCreditApplicationLifecycle.CompensationSourceType,
                vendorCreditApplicationLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationCompletion);
            Assert.Equal("journal_entry_reversed", vendorCreditApplicationCompletion!.OutcomeCode);
            sourceApplicationId = Guid.Empty;
            targetApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyVendorCreditPlan = await reviewRepository.GetReverseRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyVendorCreditPlan);
            Assert.True(readyVendorCreditPlan!.CanExecute);
            Assert.Equal("planned", readyVendorCreditPlan.OverallStatus);

            var readyVendorCreditExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyVendorCreditExecute);
            Assert.Equal("execution_request_recorded", readyVendorCreditExecute!.OutcomeCode);

            var vendorCreditLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                vendorCreditJournalEntryId,
                userId,
                CancellationToken.None);
            vendorCreditCompensationJournalEntryId = vendorCreditLifecycle.CompensationJournalEntryId;

            var vendorCreditCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                vendorCreditLifecycle.CompensationJournalEntryId,
                vendorCreditLifecycle.CompensationDisplayNumber,
                vendorCreditLifecycle.CompensationSourceType,
                vendorCreditLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(vendorCreditCompletion);
            Assert.True(vendorCreditCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", vendorCreditCompletion.OutcomeCode);
            Assert.Equal("vendor_credit_reversal", vendorCreditCompletion.Request.CompensationSourceType);

            var vendorCreditStatus = await GetDocumentStatusAsync(connectionFactory, "vendor_credits", vendorCreditId, CancellationToken.None);
            var vendorCreditApplicationStatus = await GetDocumentStatusAsync(connectionFactory, "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            var vendorCreditJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, vendorCreditJournalEntryId, CancellationToken.None);
            var vendorCreditApplicationJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, vendorCreditApplicationJournalEntryId, CancellationToken.None);
            var sourceOpenItemStatus = await GetApOpenItemStatusAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetApOpenItemSnapshotAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);

            Assert.Equal("reversed", vendorCreditStatus);
            Assert.Equal("reversed", vendorCreditApplicationStatus);
            Assert.Equal("reversed", vendorCreditJournalStatus);
            Assert.Equal("reversed", vendorCreditApplicationJournalStatus);
            Assert.Equal("voided", sourceOpenItemStatus);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", targetOpenItem!.Status);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, sourceApplicationId, CancellationToken.None);
            await CleanupSettlementApplicationAsync(connectionFactory, targetApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, vendorCreditId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, vendorCreditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditApplicationCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditApplicationJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_application_lines", "vendor_credit_application_id", "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_lines", "vendor_credit_id", "vendor_credits", vendorCreditId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_AllowsForeignCurrencyVendorCreditReverseAfterBlockingVendorCreditApplicationIsUnapplied()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var vendorCreditRepository = new PostgresVendorCreditDocumentRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var journalEntryReviewStore = new PostgreSqlJournalEntryReviewStore(infrastructureConnectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid vendorCreditId = Guid.Empty;
        Guid billId = Guid.NewGuid();
        Guid vendorCreditApplicationId = Guid.Empty;
        Guid sourceVendorCreditOpenItemId = Guid.Empty;
        Guid targetBillOpenItemId = Guid.Empty;
        Guid vendorCreditJournalEntryId = Guid.Empty;
        Guid vendorCreditApplicationJournalEntryId = Guid.Empty;
        Guid vendorCreditCompensationJournalEntryId = Guid.Empty;
        Guid vendorCreditApplicationCompensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        Guid fxSnapshotId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            var fxDate = await ReserveUniqueSnapshotDateAsync(connectionFactory, "USD", "EUR", CancellationToken.None);
            fxSnapshotId = await CreateManualFxSnapshotAsync(
                connectionFactory,
                "USD",
                "EUR",
                userId,
                fxDate,
                1.25m,
                CancellationToken.None);

            vendorCreditId = (await vendorCreditRepository.SaveDraftAsync(
                new VendorCreditDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "EUR",
                    "USD",
                    fxSnapshotId,
                    1.25m,
                    fxDate,
                    "manual",
                    "Foreign currency vendor credit blocked-then-reversed smoke",
                    [new VendorCreditDraftLineSaveModel(1, expenseAccountId, "FX blocked then reversed", 100m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;
            await MarkDocumentPostedAsync(connectionFactory, "vendor_credits", vendorCreditId, CancellationToken.None);

            vendorCreditJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "vendor_credit",
                vendorCreditId,
                payableControlAccountId,
                expenseAccountId,
                125m,
                "JE-SMOKE-AP-CHAIN-VC-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual",
                fxSnapshotId: fxSnapshotId);

            sourceVendorCreditOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None,
                balanceSide: "debit",
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            targetBillOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None,
                amountTx: 100m,
                amountBase: 125m,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD");

            vendorCreditApplicationId = await InsertVendorCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                sourceVendorCreditOpenItemId,
                targetBillOpenItemId,
                CancellationToken.None,
                documentCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                totalAmount: 100m,
                appliedAmountTx: 100m,
                applicationDate: fxDate);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                sourceVendorCreditOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                targetBillOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None,
                appliedAmountTx: 100m,
                appliedAmountBase: 125m);

            vendorCreditApplicationJournalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                expenseAccountId,
                payableControlAccountId,
                125m,
                "JE-SMOKE-AP-CHAIN-VCA-FX-001",
                CancellationToken.None,
                transactionCurrencyCode: "EUR",
                baseCurrencyCode: "USD",
                transactionAmount: 100m,
                exchangeRate: 1.25m,
                exchangeRateDate: fxDate,
                exchangeRateSource: "manual");

            var vendorCreditAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditAttempt);
            Assert.Equal("request_recorded", vendorCreditAttempt!.OutcomeCode);

            var vendorCreditSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditSubmit);
            Assert.Equal("submitted", vendorCreditSubmit!.OutcomeCode);

            var blockedVendorCreditExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(blockedVendorCreditExecute);
            Assert.Equal("blocked_by_subledger_truth", blockedVendorCreditExecute!.OutcomeCode);

            var initialBlocker = Assert.Single(await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None));
            Assert.Equal(vendorCreditApplicationId, initialBlocker.SettlementSourceId);

            var vendorCreditApplicationAttempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationAttempt);

            var vendorCreditApplicationSubmit = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt!.RequestId!.Value,
                userId,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationSubmit);
            Assert.Equal("submitted", vendorCreditApplicationSubmit!.OutcomeCode);

            var vendorCreditApplicationExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationExecute);
            Assert.Equal("execution_request_recorded", vendorCreditApplicationExecute!.OutcomeCode);

            var vendorCreditApplicationLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                vendorCreditApplicationJournalEntryId,
                userId,
                CancellationToken.None);
            vendorCreditApplicationCompensationJournalEntryId = vendorCreditApplicationLifecycle.CompensationJournalEntryId;

            var vendorCreditApplicationCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                vendorCreditApplicationAttempt.RequestId.Value,
                userId,
                vendorCreditApplicationLifecycle.CompensationJournalEntryId,
                vendorCreditApplicationLifecycle.CompensationDisplayNumber,
                vendorCreditApplicationLifecycle.CompensationSourceType,
                vendorCreditApplicationLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(vendorCreditApplicationCompletion);
            Assert.Equal("journal_entry_reversed", vendorCreditApplicationCompletion!.OutcomeCode);
            sourceApplicationId = Guid.Empty;
            targetApplicationId = Guid.Empty;

            var clearedBlockers = await reviewRepository.ListSubledgerReverseBlockersAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None);
            Assert.Empty(clearedBlockers);

            var readyVendorCreditExecute = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                new DateOnly(2026, 4, 14),
                CancellationToken.None);
            Assert.NotNull(readyVendorCreditExecute);
            Assert.Equal("execution_request_recorded", readyVendorCreditExecute!.OutcomeCode);

            var vendorCreditLifecycle = await lifecycleStore.ReverseAsync(
                CompanyId,
                vendorCreditJournalEntryId,
                userId,
                CancellationToken.None);
            vendorCreditCompensationJournalEntryId = vendorCreditLifecycle.CompensationJournalEntryId;

            var vendorCreditCompletion = await reviewRepository.CompleteReverseRequestExecutionAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit",
                vendorCreditId,
                vendorCreditAttempt.RequestId.Value,
                userId,
                vendorCreditLifecycle.CompensationJournalEntryId,
                vendorCreditLifecycle.CompensationDisplayNumber,
                vendorCreditLifecycle.CompensationSourceType,
                vendorCreditLifecycle.LifecycleAt,
                CancellationToken.None);
            Assert.NotNull(vendorCreditCompletion);
            Assert.True(vendorCreditCompletion!.Executed);
            Assert.Equal("journal_entry_reversed", vendorCreditCompletion.OutcomeCode);

            var vendorCreditStatus = await GetDocumentStatusAsync(connectionFactory, "vendor_credits", vendorCreditId, CancellationToken.None);
            var vendorCreditApplicationStatus = await GetDocumentStatusAsync(connectionFactory, "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            var vendorCreditJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, vendorCreditJournalEntryId, CancellationToken.None);
            var vendorCreditApplicationJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, vendorCreditApplicationJournalEntryId, CancellationToken.None);
            var sourceOpenItem = await GetApOpenItemSnapshotAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetApOpenItemSnapshotAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);
            var vendorCreditReview = await journalEntryReviewStore.GetAsync(CompanyId, vendorCreditJournalEntryId, CancellationToken.None);
            var vendorCreditCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, vendorCreditCompensationJournalEntryId, CancellationToken.None);
            var vendorCreditApplicationReview = await journalEntryReviewStore.GetAsync(CompanyId, vendorCreditApplicationJournalEntryId, CancellationToken.None);
            var vendorCreditApplicationCompensationReview = await journalEntryReviewStore.GetAsync(CompanyId, vendorCreditApplicationCompensationJournalEntryId, CancellationToken.None);

            Assert.Equal("reversed", vendorCreditStatus);
            Assert.Equal("reversed", vendorCreditApplicationStatus);
            Assert.Equal("reversed", vendorCreditJournalStatus);
            Assert.Equal("reversed", vendorCreditApplicationJournalStatus);
            Assert.NotNull(sourceOpenItem);
            Assert.Equal("voided", sourceOpenItem!.Status);
            Assert.Equal(0m, sourceOpenItem.OpenAmountTx);
            Assert.Equal(0m, sourceOpenItem.OpenAmountBase);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", targetOpenItem!.Status);
            Assert.Equal(100m, targetOpenItem.OpenAmountTx);
            Assert.Equal(125m, targetOpenItem.OpenAmountBase);

            Assert.NotNull(vendorCreditReview);
            Assert.Equal("EUR", vendorCreditReview!.TransactionCurrencyCode);
            Assert.Equal("USD", vendorCreditReview.BaseCurrencyCode);
            Assert.Equal(fxSnapshotId, vendorCreditReview.FxSnapshotId);
            Assert.NotNull(vendorCreditCompensationReview);
            Assert.Equal("vendor_credit_reversal", vendorCreditCompensationReview!.SourceType);
            Assert.Equal(fxSnapshotId, vendorCreditCompensationReview.FxSnapshotId);
            Assert.Contains("snapshot", vendorCreditCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(vendorCreditApplicationReview);
            Assert.Equal("EUR", vendorCreditApplicationReview!.TransactionCurrencyCode);
            Assert.Equal("USD", vendorCreditApplicationReview.BaseCurrencyCode);
            Assert.Null(vendorCreditApplicationReview.FxSnapshotId);
            Assert.NotNull(vendorCreditApplicationCompensationReview);
            Assert.Equal("vendor_credit_application_reversal", vendorCreditApplicationCompensationReview!.SourceType);
            Assert.Null(vendorCreditApplicationCompensationReview.FxSnapshotId);
            Assert.Contains("header-only", vendorCreditApplicationCompensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupSettlementApplicationAsync(connectionFactory, sourceApplicationId, CancellationToken.None);
            await CleanupSettlementApplicationAsync(connectionFactory, targetApplicationId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, vendorCreditId, CancellationToken.None);
            await CleanupAuditLogEntityAsync(connectionFactory, vendorCreditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditApplicationCompensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, vendorCreditApplicationJournalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_application_lines", "vendor_credit_application_id", "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_lines", "vendor_credit_id", "vendor_credits", vendorCreditId, CancellationToken.None);
            await CleanupFxSnapshotAsync(connectionFactory, fxSnapshotId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompleteReverseRequestExecutionAsync_UnappliesPostedVendorCreditApplicationBeforeMarkingReversed()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var reviewRepository = new PostgresAccountingDocumentReviewRepository(connectionFactory, new PostgresExecutionContextAccessor());
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(infrastructureConnectionFactory);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(infrastructureConnectionFactory, numberLookup);

        Guid payableControlAccountId = default;
        Guid expenseAccountId = default;
        UserId userId = default;
        Guid vendorCreditId = Guid.NewGuid();
        Guid billId = Guid.NewGuid();
        Guid vendorCreditApplicationId = Guid.Empty;
        Guid sourceVendorCreditOpenItemId = Guid.Empty;
        Guid targetBillOpenItemId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        Guid compensationJournalEntryId = Guid.Empty;
        Guid sourceApplicationId = Guid.Empty;
        Guid targetApplicationId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            sourceVendorCreditOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "vendor_credit",
                vendorCreditId,
                CancellationToken.None,
                balanceSide: "debit");
            targetBillOpenItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None);

            vendorCreditApplicationId = await InsertVendorCreditApplicationAsync(
                connectionFactory,
                CompanyId,
                userId,
                VendorId,
                sourceVendorCreditOpenItemId,
                targetBillOpenItemId,
                CancellationToken.None);

            sourceApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                sourceVendorCreditOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);
            targetApplicationId = await ApplySettlementApplicationForOpenItemAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                targetBillOpenItemId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);

            journalEntryId = await InsertJournalEntryWithBalancedLinesAsync(
                connectionFactory,
                CompanyId,
                userId,
                "vendor_credit_application",
                vendorCreditApplicationId,
                expenseAccountId,
                payableControlAccountId,
                1m,
                "JE-SMOKE-AP-VCA-REV-001",
                CancellationToken.None);

            var attempt = await reviewRepository.AttemptReverseAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                userId,
                CancellationToken.None);

            Assert.NotNull(attempt);
            Assert.Equal("request_recorded", attempt!.OutcomeCode);

            var submitResult = await reviewRepository.SubmitReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                attempt.RequestId!.Value,
                userId,
                CancellationToken.None);

            Assert.NotNull(submitResult);
            Assert.Equal("submitted", submitResult!.OutcomeCode);

            var executeResult = await reviewRepository.ExecuteReverseRequestAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
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
                "vendor_credit_application",
                vendorCreditApplicationId,
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
            Assert.Equal("vendor_credit_application_reversal", completionResult.Request.CompensationSourceType);

            var originalJournalStatus = await GetJournalEntryStatusAsync(connectionFactory, journalEntryId, CancellationToken.None);
            var compensationJournal = await GetJournalEntrySnapshotAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            var sourceStatus = await GetDocumentStatusAsync(connectionFactory, "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            var sourceOpenItem = await GetApOpenItemSnapshotAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            var targetOpenItem = await GetApOpenItemSnapshotAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);
            var applicationCount = await CountSettlementApplicationsForSourceAsync(
                connectionFactory,
                "vendor_credit_application",
                vendorCreditApplicationId,
                CancellationToken.None);
            var reversalAuditCount = await CountSettlementApplicationReversalAuditsForSourceAsync(
                connectionFactory,
                "vendor_credit_application",
                vendorCreditApplicationId,
                CancellationToken.None);
            var reversalEvents = await reviewRepository.ListSettlementApplicationReversalsAsync(
                CompanyId.FromOrdinal(1),
                "vendor_credit_application",
                vendorCreditApplicationId,
                CancellationToken.None);

            Assert.Equal("reversed", originalJournalStatus);
            Assert.NotNull(compensationJournal);
            Assert.Equal("posted", compensationJournal!.Status);
            Assert.Equal("vendor_credit_application_reversal", compensationJournal.SourceType);
            Assert.Equal(vendorCreditApplicationId, compensationJournal.SourceId);
            Assert.Equal("reversed", sourceStatus);
            Assert.NotNull(sourceOpenItem);
            Assert.NotNull(targetOpenItem);
            Assert.Equal("open", sourceOpenItem!.Status);
            Assert.Equal("open", targetOpenItem!.Status);
            Assert.Equal(72m, sourceOpenItem.OpenAmountTx);
            Assert.Equal(72m, targetOpenItem.OpenAmountTx);
            Assert.Equal(0, applicationCount);
            Assert.Equal(2, reversalAuditCount);
            Assert.Equal(2, reversalEvents.Count);
            Assert.Contains(reversalEvents, reversal => reversal.SettlementApplicationId == sourceApplicationId && reversal.TargetOpenItemId == sourceVendorCreditOpenItemId);
            Assert.Contains(reversalEvents, reversal => reversal.SettlementApplicationId == targetApplicationId && reversal.TargetOpenItemId == targetBillOpenItemId);
            Assert.All(reversalEvents, reversal =>
            {
                Assert.Equal(attempt.RequestId.Value, reversal.RequestId);
                Assert.Equal("vendor_credit_application", reversal.SourceType);
                Assert.Equal("ap_open_item", reversal.TargetOpenItemType);
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
            await CleanupAuditLogEntityAsync(connectionFactory, vendorCreditApplicationId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, compensationJournalEntryId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "vendor_credit_application_lines", "vendor_credit_application_id", "vendor_credit_applications", vendorCreditApplicationId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, sourceVendorCreditOpenItemId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, targetBillOpenItemId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestAdjustmentAsync_RecordsGovernedAdjustmentRequestWithoutChangingApOpenItemTruth()
    {
        var connectionFactory = new PostgresConnectionFactory(GetConnectionString());
        var executionContextAccessor = new PostgresExecutionContextAccessor();
        var billRepository = new PostgresBillDocumentRepository(connectionFactory, executionContextAccessor);
        var openItemRepository = new PostgresApOpenItemRepository(connectionFactory, executionContextAccessor);
        var postingEngine = new DefaultPostingEngine(
            new DefaultPostingValidator(),
            new NullPostingPeriodPolicyValidator(),
            new NullTaxEngine(),
            new LocalFirstFxResolutionService(new PostgresFxSnapshotRepository(connectionFactory, executionContextAccessor)),
            new AccountingPostingFragmentBuilder(),
            new DefaultJournalAggregator(),
            new PostgresJournalEntryWriter(connectionFactory, executionContextAccessor));
        var adjustmentHandler = new PostApOpenItemAdjustmentCommandHandler(
            openItemRepository,
            postingEngine,
            new PostgresUnitOfWork(connectionFactory, executionContextAccessor));

        Guid expenseAccountId = default;
        Guid unmappedExpenseAccountId = default;
        Guid payableControlAccountId = default;
        UserId userId = default;
        UserId approvalUserId = default;
        Guid billId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        Guid adjustmentAccountMappingId = Guid.Empty;
        Guid adjustmentJournalEntryId = Guid.Empty;
        var createdUser = false;
        var createdApprovalUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            (approvalUserId, createdApprovalUser) = await CreateAdditionalUserAsync(connectionFactory, CancellationToken.None);
            payableControlAccountId = await CreatePayableControlAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            expenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            unmappedExpenseAccountId = await CreateExpenseAccountAsync(connectionFactory, CompanyId, CancellationToken.None);

            billId = (await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "Bill adjustment governance smoke test",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Office supplies", 172m, null, 0m, false)]),
                CancellationToken.None)).DocumentId;

            await MarkDocumentPostedAsync(connectionFactory, "bills", billId, CancellationToken.None);
            openItemId = await CreateApOpenItemForSourceAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                billId,
                CancellationToken.None,
                amount: 172m);

            var before = await openItemRepository.GetDrillDownAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                CancellationToken.None);

            var approvalPreview = await openItemRepository.GetAdjustmentPreviewAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "small_balance_adjustment",
                new DateOnly(2026, 4, 15),
                150m,
                CancellationToken.None);

            var approvalAttempt = await openItemRepository.RequestAdjustmentAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "small_balance_adjustment",
                new DateOnly(2026, 4, 15),
                150m,
                userId,
                "Vendor balance cleanup review above temporary approval threshold",
                CancellationToken.None);

            var approvalSubmitResult = await openItemRepository.SubmitAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt!.Request!.RequestId,
                userId,
                CancellationToken.None);

            var approvalReadiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                new DateOnly(2026, 4, 15),
                CancellationToken.None);

            var selfApprovalResult = await openItemRepository.ApproveAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                userId,
                CancellationToken.None);

            var approvalApproveResult = await openItemRepository.ApproveAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                approvalUserId,
                CancellationToken.None);

            var approvedReadiness = await openItemRepository.GetAdjustmentRequestReadinessAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                new DateOnly(2026, 4, 15),
                CancellationToken.None);

            var approvedExecutionPlan = await openItemRepository.GetAdjustmentRequestExecutionPlanAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                new DateOnly(2026, 4, 15),
                CancellationToken.None);

            var approvalCancelResult = await openItemRepository.CancelAdjustmentRequestAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                approvalAttempt.Request.RequestId,
                userId,
                CancellationToken.None);

            var preview = await openItemRepository.GetAdjustmentPreviewAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "small_balance_adjustment",
                new DateOnly(2026, 4, 15),
                72m,
                CancellationToken.None);

            var attempt = await openItemRepository.RequestAdjustmentAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "small_balance_adjustment",
                new DateOnly(2026, 4, 15),
                72m,
                userId,
                "Vendor balance cleanup review",
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
                    new PostApOpenItemAdjustmentCommand(
                        CompanyId.FromOrdinal(1),
                        openItemId,
                        attempt.Request.RequestId,
                        UserId.FromOrdinal(1),
                        payableControlAccountId,
                        new DateOnly(2026, 4, 15),
                        null),
                    CancellationToken.None));

            adjustmentAccountMappingId = await CreateOpenItemAdjustmentAccountMappingAsync(
                connectionFactory,
                CompanyId,
                "ap_open_item",
                "small_balance_adjustment",
                expenseAccountId,
                CancellationToken.None);

            var unmappedAdjustmentAccount = await Assert.ThrowsAsync<InvalidOperationException>(
                () => adjustmentHandler.HandleAsync(
                    new PostApOpenItemAdjustmentCommand(
                        CompanyId.FromOrdinal(1),
                        openItemId,
                        attempt.Request.RequestId,
                        UserId.FromOrdinal(1),
                        unmappedExpenseAccountId,
                        new DateOnly(2026, 4, 15),
                        null),
                    CancellationToken.None));

            var executionResult = await adjustmentHandler.HandleAsync(
                new PostApOpenItemAdjustmentCommand(
                    CompanyId.FromOrdinal(1),
                    openItemId,
                    attempt.Request.RequestId,
                    UserId.FromOrdinal(1),
                    expenseAccountId,
                    new DateOnly(2026, 4, 15),
                    null),
                CancellationToken.None);
            adjustmentJournalEntryId = executionResult.JournalEntryId ?? Guid.Empty;

            var followUpAttempt = await openItemRepository.RequestAdjustmentAsync(
                CompanyId.FromOrdinal(1),
                openItemId,
                "small_balance_adjustment",
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
            Assert.NotNull(approvalPreview);
            Assert.True(approvalPreview!.RequiresApproval);
            Assert.Equal("pending", approvalPreview.ApprovalStatus);
            Assert.NotNull(approvalAttempt);
            Assert.True(approvalAttempt!.CommandAccepted);
            Assert.NotNull(approvalAttempt.Request);
            Assert.True(approvalAttempt.Request!.RequiresApproval);
            Assert.Equal("pending", approvalAttempt.Request.ApprovalStatus);
            Assert.NotNull(approvalSubmitResult);
            Assert.Equal("submitted", approvalSubmitResult!.OutcomeCode);
            Assert.NotNull(approvalReadiness);
            Assert.True(approvalReadiness!.GovernanceReady);
            Assert.True(approvalReadiness.OpenItemReady);
            Assert.False(approvalReadiness.PostingExecutionReady);
            Assert.False(approvalReadiness.IsAvailable);
            Assert.Equal("blocked_by_approval_required", approvalReadiness.AvailabilityMode);
            Assert.NotNull(selfApprovalResult);
            Assert.Equal("blocked_self_approval", selfApprovalResult!.OutcomeCode);
            Assert.NotNull(approvalApproveResult);
            Assert.Equal("approved", approvalApproveResult!.OutcomeCode);
            Assert.Equal("approved", approvalApproveResult.Request.ApprovalStatus);
            Assert.NotNull(approvalApproveResult.Request.ApprovedAt);
            Assert.Equal(approvalUserId, approvalApproveResult.Request.ApprovedByActorId);
            Assert.NotNull(approvedReadiness);
            Assert.True(approvedReadiness!.GovernanceReady);
            Assert.True(approvedReadiness.OpenItemReady);
            Assert.True(approvedReadiness.PostingExecutionReady);
            Assert.True(approvedReadiness.IsAvailable);
            Assert.Equal("available_for_execution", approvedReadiness.AvailabilityMode);
            Assert.NotNull(approvedExecutionPlan);
            Assert.True(approvedExecutionPlan!.CanExecute);
            Assert.Contains(approvedExecutionPlan.Steps, step => step.StepCode == "approval_gate" && step.StepStatus == "ready");
            Assert.NotNull(approvalCancelResult);
            Assert.Equal("cancelled", approvalCancelResult!.OutcomeCode);
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
            Assert.Equal("small_balance_adjustment", attempt.Request.AdjustmentType);
            Assert.Equal(72m, attempt.Request.RequestedAdjustmentAmountTx);
            Assert.Equal(72m, attempt.Request.RequestedAdjustmentAmountBase);
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
            Assert.Contains("not mapped by active company/book adjustment account policy", unmappedAdjustmentAccount.Message);
            Assert.NotEqual(Guid.Empty, adjustmentJournalEntryId);
            Assert.Equal("posted", executionResult.Status);
            Assert.Equal(72m, executionResult.AdjustmentAmountTx);
            Assert.Equal(72m, executionResult.AdjustmentAmountBase);
            Assert.NotNull(followUpAttempt);
            Assert.True(followUpAttempt!.CommandAccepted);
            Assert.Equal("request_recorded", followUpAttempt.OutcomeCode);
            Assert.NotNull(followUpAttempt.Request);
            Assert.Equal(100m, followUpAttempt.Request!.RequestedAdjustmentAmountTx);
            Assert.NotNull(after);
            Assert.Equal(100m, after!.OpenAmountTx);
            Assert.Equal(100m, after.OpenAmountBase);
            Assert.Equal("partially_applied", after.Status);
        }
        finally
        {
            await CleanupAuditLogEntityAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupJournalEntryAsync(connectionFactory, adjustmentJournalEntryId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, openItemId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await CleanupOpenItemAdjustmentAccountMappingAsync(connectionFactory, adjustmentAccountMappingId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, unmappedExpenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, payableControlAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, approvalUserId, createdApprovalUser, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    private static async Task<Guid> CreatePayableControlAccountAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var suffix = entityNumber[^6..];

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
              'liability',
              'accounts_payable',
              true,
              true,
              false,
              'accounts_payable',
              false,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"AP-{suffix}");
        command.Parameters.AddWithValue("name", $"Smoke Accounts Payable {suffix}");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

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
        command.Parameters.AddWithValue("name", "Smoke Expense");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static async Task<Guid> CreateOpenItemAdjustmentAccountMappingAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        string openItemType,
        string adjustmentType,
        Guid adjustmentAccountId,
        CancellationToken cancellationToken)
    {
        var repository = new PostgresOpenItemAdjustmentAccountMappingRepository(
            connectionFactory,
            new PostgresExecutionContextAccessor());
        var result = await repository.SaveAsync(
            new OpenItemAdjustmentAccountMappingSaveRequest(
                CompanyId.Parse(companyId.ToString()),
                null,
                openItemType,
                adjustmentType,
                adjustmentAccountId,
                null),
            cancellationToken);

        return result.Mapping.MappingId;
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

    private static async Task<(UserId UserId, bool Created)> CreateAdditionalUserAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        // Must NOT collide with the seed user that GetOrCreateUserAsync
        // returns (which is FromOrdinal(1) = U000001). Use ordinal 2 for
        // the secondary "approval" user so the two helpers can be invoked
        // back-to-back without a users_pkey duplicate.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var newUserId = UserId.FromOrdinal(2);
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select id
                from users
                where id = @id
                limit 1;
                """;
            findCommand.Parameters.AddWithValue("id", newUserId.Value);
            var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is string existingUserId && UserId.TryParse(existingUserId, out var userId))
            {
                return (userId, false);
            }
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (id, email, username, password_hash, status)
            values (@id, @email, @username, @password_hash, 'active');
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"smoke-approval-{newUserId.Value}@citus.local");
        insertCommand.Parameters.AddWithValue("username", $"smoke-approval-{newUserId.Value}");
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

        throw new InvalidOperationException("Could not reserve a unique entity number for payable draft smoke test.");
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

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await ReleaseJournalDisplayNumberAsync(connection, null, companyId, "JE-SMOKE-AP-001", cancellationToken);

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
              'JE-SMOKE-AP-001',
              @status,
              @source_type,
              @source_id,
              'USD',
              'USD',
              1,
              current_date,
              'smoke',
              45,
              45,
              45,
              45,
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
        decimal? exchangeRate = null,
        DateOnly? exchangeRateDate = null,
        string exchangeRateSource = "smoke",
        Guid? fxSnapshotId = null)
    {
        var journalEntryId = Guid.NewGuid();
        var debitLineId = Guid.NewGuid();
        var creditLineId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var totalTransactionAmount = transactionAmount ?? amount;
        var effectiveExchangeRate = exchangeRate ?? (transactionCurrencyCode == baseCurrencyCode ? 1m : amount / totalTransactionAmount);
        var effectiveExchangeRateDate = exchangeRateDate ?? new DateOnly(2026, 4, 14);

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
            headerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
            headerCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            headerCommand.Parameters.AddWithValue("exchange_rate", effectiveExchangeRate);
            headerCommand.Parameters.AddWithValue("exchange_rate_date", effectiveExchangeRateDate);
            headerCommand.Parameters.AddWithValue("exchange_rate_source", exchangeRateSource);
            headerCommand.Parameters.AddWithValue("fx_rate_snapshot_id", (object?)fxSnapshotId ?? DBNull.Value);
            headerCommand.Parameters.AddWithValue("transaction_amount", totalTransactionAmount);
            headerCommand.Parameters.AddWithValue("amount", amount);
            headerCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            headerCommand.Parameters.AddWithValue("idempotency_key", $"smoke-je-balanced:{sourceType}:{sourceId:D}");
            headerCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertJournalEntryLineAsync(connection, transaction, companyId, journalEntryId, debitLineId, 1, debitAccountId, totalTransactionAmount, 0m, "Smoke debit", cancellationToken, amount, 0m);
        await InsertJournalEntryLineAsync(connection, transaction, companyId, journalEntryId, creditLineId, 2, creditAccountId, 0m, totalTransactionAmount, "Smoke credit", cancellationToken, 0m, amount);
        await InsertLedgerEntryAsync(connection, transaction, companyId, journalEntryId, debitLineId, debitAccountId, amount, 0m, cancellationToken, transactionCurrencyCode, totalTransactionAmount, 0m);
        await InsertLedgerEntryAsync(connection, transaction, companyId, journalEntryId, creditLineId, creditAccountId, 0m, amount, cancellationToken, transactionCurrencyCode, 0m, totalTransactionAmount);

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

    private static async Task<Guid> CreateApOpenItemForSourceAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid vendorId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken,
        decimal amount = 72m,
        string balanceSide = "credit",
        decimal? amountTx = null,
        decimal? amountBase = null,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD")
    {
        var openItemId = Guid.NewGuid();
        var effectiveAmountTx = amountTx ?? amount;
        var effectiveAmountBase = amountBase ?? amount;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into ap_open_items (
              id,
              company_id,
              vendor_id,
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
              @vendor_id,
              @source_type,
              @source_id,
              @due_date,
              @document_currency_code,
              @base_currency_code,
              @amount_tx,
              @amount_base,
              @amount_tx,
              @amount_base,
              @balance_side,
              'open',
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", openItemId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("amount_tx", effectiveAmountTx);
        command.Parameters.AddWithValue("amount_base", effectiveAmountBase);
        command.Parameters.AddWithValue("balance_side", balanceSide);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return openItemId;
    }

    private static async Task CleanupApOpenItemAsync(
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
        command.CommandText = "delete from ap_open_items where id = @open_item_id;";
        command.Parameters.AddWithValue("open_item_id", openItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> InsertPayBillAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        Guid vendorId,
        Guid paymentAccountId,
        Guid targetApOpenItemId,
        CancellationToken cancellationToken,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD",
        decimal fxRate = 1m,
        string fxSource = "smoke",
        decimal totalAmount = 1m,
        decimal appliedAmountTx = 1m,
        DateOnly? paymentDate = null)
    {
        var payBillId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var effectivePaymentDate = paymentDate ?? new DateOnly(2026, 4, 14);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                insert into pay_bills (
                  id,
                  company_id,
                  entity_number,
                  payment_number,
                  vendor_id,
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
                  @vendor_id,
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
                  'Pay bill reverse smoke',
                  now(),
                  @created_by_user_id
                );
                """;
            headerCommand.Parameters.AddWithValue("id", payBillId);
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
            headerCommand.Parameters.AddWithValue("payment_number", $"PB-{entityNumber[^6..]}");
            headerCommand.Parameters.AddWithValue("vendor_id", vendorId);
            headerCommand.Parameters.AddWithValue("payment_date", effectivePaymentDate);
            headerCommand.Parameters.AddWithValue("bank_account_id", paymentAccountId);
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
                insert into pay_bill_lines (
                  company_id,
                  pay_bill_id,
                  line_number,
                  target_ap_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @pay_bill_id,
                  1,
                  @target_ap_open_item_id,
                  @applied_amount_tx
                );
                """;
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("pay_bill_id", payBillId);
            lineCommand.Parameters.AddWithValue("target_ap_open_item_id", targetApOpenItemId);
            lineCommand.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return payBillId;
    }

    private static async Task<Guid> InsertVendorCreditApplicationAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId userId,
        Guid vendorId,
        Guid sourceVendorCreditApOpenItemId,
        Guid targetBillApOpenItemId,
        CancellationToken cancellationToken,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD",
        decimal totalAmount = 1m,
        decimal appliedAmountTx = 1m,
        DateOnly? applicationDate = null)
    {
        var vendorCreditApplicationId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var effectiveApplicationDate = applicationDate ?? new DateOnly(2026, 4, 14);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                insert into vendor_credit_applications (
                  id,
                  company_id,
                  entity_number,
                  application_number,
                  vendor_id,
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
                  @vendor_id,
                  'posted',
                  @application_date,
                  @document_currency_code,
                  @base_currency_code,
                  @total_amount,
                  'Vendor credit application reverse smoke',
                  now(),
                  @created_by_user_id
                );
                """;
            headerCommand.Parameters.AddWithValue("id", vendorCreditApplicationId);
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("entity_number", entityNumber);
            headerCommand.Parameters.AddWithValue("application_number", $"VCA-{entityNumber[^6..]}");
            headerCommand.Parameters.AddWithValue("vendor_id", vendorId);
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
                insert into vendor_credit_application_lines (
                  company_id,
                  vendor_credit_application_id,
                  line_number,
                  source_vendor_credit_ap_open_item_id,
                  target_bill_ap_open_item_id,
                  applied_amount_tx
                )
                values (
                  @company_id,
                  @vendor_credit_application_id,
                  1,
                  @source_vendor_credit_ap_open_item_id,
                  @target_bill_ap_open_item_id,
                  @applied_amount_tx
                );
                """;
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("vendor_credit_application_id", vendorCreditApplicationId);
            lineCommand.Parameters.AddWithValue("source_vendor_credit_ap_open_item_id", sourceVendorCreditApOpenItemId);
            lineCommand.Parameters.AddWithValue("target_bill_ap_open_item_id", targetBillApOpenItemId);
            lineCommand.Parameters.AddWithValue("applied_amount_tx", appliedAmountTx);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return vendorCreditApplicationId;
    }

    private static async Task<string?> GetApOpenItemStatusAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from ap_open_items where id = @open_item_id;";
        command.Parameters.AddWithValue("open_item_id", openItemId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
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

    private static async Task<OpenItemSnapshot?> GetApOpenItemSnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select status, open_amount_tx, open_amount_base
            from ap_open_items
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
              created_by_user_id
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
              'Payable FX bill smoke snapshot',
              @created_by_user_id
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

        throw new InvalidOperationException("Could not reserve a unique payable FX snapshot date.");
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

    private static async Task CleanupOpenItemAdjustmentAccountMappingAsync(
        PostgresConnectionFactory connectionFactory,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        if (mappingId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from open_item_adjustment_account_mappings
            where id = @mapping_id;
            """;
        command.Parameters.AddWithValue("mapping_id", mappingId);
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
