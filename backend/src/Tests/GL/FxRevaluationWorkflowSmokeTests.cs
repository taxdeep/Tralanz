using Citus.Accounting.Application.Commands;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Company;
using Modules.Company.MultiCurrency;
using Npgsql;

namespace Tests.GL;

public sealed class FxRevaluationWorkflowSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task PreparePostAndPrepareNextPeriodUnwindAsync_UpdatesOpenItemBaseAndBuildsDraft()
    {
        var fixture = await CreateFixtureAsync();
        var revaluationDate = BuildUniqueDate();
        var unwindDate = revaluationDate.AddDays(7);

        Guid openItemId = Guid.Empty;
        Guid snapshotId = Guid.Empty;
        Guid draftDocumentId = Guid.Empty;
        Guid unwindDraftDocumentId = Guid.Empty;

        try
        {
            snapshotId = await CreateManualFxSnapshotAsync(
                fixture.ConnectionFactory,
                fixture.BaseCurrencyCode,
                fixture.UserId,
                revaluationDate,
                1.40m,
                CancellationToken.None);
            openItemId = await CreateArOpenItemAsync(
                fixture.ConnectionFactory,
                fixture.BaseCurrencyCode,
                revaluationDate,
                100m,
                130m,
                CancellationToken.None);

            var prepared = await fixture.PrepareBatchHandler.HandleAsync(
                new PrepareFxRevaluationBatchCommand(
                    CompanyId,
                    UserId.FromOrdinal(1),
                    BookId: null,
                    revaluationDate,
                    new CurrencyCode("EUR"),
                    snapshotId,
                    IncludeAccountsReceivable: true,
                    IncludeAccountsPayable: false,
                    Memo: "FX smoke revaluation"),
                CancellationToken.None);

            draftDocumentId = prepared.DocumentId;

            Assert.Equal("draft", prepared.Status);
            Assert.Equal(1, prepared.PreparedLineCount);

            var posted = await fixture.PostBatchHandler.HandleAsync(
                new PostFxRevaluationBatchCommand(
                    CompanyId,
                    prepared.DocumentId,
                    UserId.FromOrdinal(1),
                    snapshotId,
                    IdempotencyKey: null),
                CancellationToken.None);

            Assert.Equal("posted", posted.Status);
            Assert.NotEqual(Guid.Empty, posted.JournalEntryId);

            var detail = await fixture.DocumentRepository.GetForPostingAsync(
                CompanyId,
                prepared.DocumentId,
                CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal("posted", detail!.Status);
            Assert.Equal("PRIMARY", detail.BookCode);
            Assert.Equal("ASPE", detail.AccountingStandard);
            Assert.Equal("closing", detail.FxSnapshot.RateType);
            Assert.Equal("direct", detail.FxSnapshot.QuoteBasis);
            Assert.Equal("remeasurement", detail.FxSnapshot.RateUseCase);
            Assert.Equal("revaluation", detail.FxSnapshot.PostingReason);
            Assert.Equal(revaluationDate, detail.FxSnapshot.RequestedDate);
            Assert.Equal(revaluationDate, detail.FxSnapshot.EffectiveDate);

            var recent = await fixture.DocumentRepository.ListRecentAsync(
                CompanyId,
                10,
                CancellationToken.None);
            var recentItem = Assert.Single(recent, item => item.Id == prepared.DocumentId);
            Assert.Equal("posted", recentItem.Status);
            Assert.Equal("revaluation", recentItem.BatchKind);
            Assert.Equal("PRIMARY", recentItem.BookCode);
            Assert.Equal("ASPE", recentItem.AccountingStandard);
            Assert.Equal(snapshotId, recentItem.FxSnapshotId);
            Assert.Equal(1.40m, recentItem.FxRate);
            Assert.Equal(1, recentItem.LineCount);
            Assert.Equal(10m, recentItem.UnrealizedTotalBase);
            Assert.Equal(posted.JournalEntryId, recentItem.LinkedJournalEntryId);

            var revaluedBase = await LoadArOpenItemBaseAsync(
                fixture.ConnectionFactory,
                openItemId,
                CancellationToken.None);

            Assert.Equal(140m, revaluedBase);

            var unwindPrepared = await fixture.PrepareUnwindHandler.HandleAsync(
                new PrepareFxRevaluationUnwindBatchCommand(
                    CompanyId,
                    prepared.DocumentId,
                    UserId.FromOrdinal(1),
                    unwindDate,
                    "FX smoke unwind"),
                CancellationToken.None);

            unwindDraftDocumentId = unwindPrepared.DocumentId;

            Assert.Equal("draft", unwindPrepared.Status);
            Assert.Equal(1, unwindPrepared.PreparedLineCount);

            var unwindMetadata = await LoadFxBatchMetadataAsync(
                fixture.ConnectionFactory,
                unwindPrepared.DocumentId,
                CancellationToken.None);

            Assert.NotNull(unwindMetadata);
            Assert.Equal("next_period_unwind", unwindMetadata!.BatchKind);
            Assert.Equal(prepared.DocumentId, unwindMetadata.ReversalOfDocumentId);
        }
        finally
        {
            await CleanupAsync(
                fixture,
                [draftDocumentId, unwindDraftDocumentId],
                [openItemId],
                [snapshotId],
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostCascadeUnwindAsync_RewindsDescendantsBackToOriginalCarryingBase()
    {
        var fixture = await CreateFixtureAsync();
        var firstRevaluationDate = BuildUniqueDate();
        var secondRevaluationDate = firstRevaluationDate.AddDays(3);
        var unwindDate = secondRevaluationDate.AddDays(9);

        Guid openItemId = Guid.Empty;
        Guid firstSnapshotId = Guid.Empty;
        Guid secondSnapshotId = Guid.Empty;
        Guid firstDocumentId = Guid.Empty;
        Guid secondDocumentId = Guid.Empty;

        try
        {
            openItemId = await CreateArOpenItemAsync(
                fixture.ConnectionFactory,
                fixture.BaseCurrencyCode,
                firstRevaluationDate,
                100m,
                130m,
                CancellationToken.None);
            firstSnapshotId = await CreateManualFxSnapshotAsync(
                fixture.ConnectionFactory,
                fixture.BaseCurrencyCode,
                fixture.UserId,
                firstRevaluationDate,
                1.40m,
                CancellationToken.None);
            secondSnapshotId = await CreateManualFxSnapshotAsync(
                fixture.ConnectionFactory,
                fixture.BaseCurrencyCode,
                fixture.UserId,
                secondRevaluationDate,
                1.50m,
                CancellationToken.None);

            firstDocumentId = await PrepareAndPostBatchAsync(
                fixture,
                firstRevaluationDate,
                firstSnapshotId,
                "FX smoke batch 1",
                CancellationToken.None);
            secondDocumentId = await PrepareAndPostBatchAsync(
                fixture,
                secondRevaluationDate,
                secondSnapshotId,
                "FX smoke batch 2",
                CancellationToken.None);

            var plan = await fixture.DocumentRepository.GetCascadeUnwindPlanAsync(
                CompanyId,
                firstDocumentId,
                CancellationToken.None);

            Assert.False(plan.RequestedBatchIsTail);
            Assert.Equal(firstDocumentId, plan.RequestedDocumentId);
            Assert.Equal(secondDocumentId, plan.NextDocumentId);
            Assert.True(plan.ActiveRevaluationChain.Count >= 2);

            var cascade = await fixture.PostCascadeHandler.HandleAsync(
                new PostFxRevaluationCascadeUnwindCommand(
                    CompanyId,
                    firstDocumentId,
                    UserId.FromOrdinal(1),
                    unwindDate,
                    "FX smoke cascade unwind",
                    IdempotencyKey: null),
                CancellationToken.None);

            Assert.Equal(2, cascade.PostedStepCount);
            Assert.Equal(secondDocumentId, cascade.PostedSteps[0].SourceDocumentId);
            Assert.Equal(firstDocumentId, cascade.PostedSteps[1].SourceDocumentId);
            Assert.All(cascade.PostedSteps, step => Assert.NotEqual(Guid.Empty, step.UnwindDocumentId));
            Assert.All(cascade.PostedSteps, step => Assert.NotEqual(Guid.Empty, step.JournalEntryId));

            var recent = await fixture.DocumentRepository.ListRecentAsync(
                CompanyId,
                20,
                CancellationToken.None);
            Assert.Contains(recent, item => item.Id == firstDocumentId && item.Status == "posted");
            Assert.Contains(recent, item => item.Id == secondDocumentId && item.Status == "posted");

            foreach (var step in cascade.PostedSteps)
            {
                var unwindRecent = Assert.Single(recent, item => item.Id == step.UnwindDocumentId);
                Assert.Equal("posted", unwindRecent.Status);
                Assert.Equal("next_period_unwind", unwindRecent.BatchKind);
                Assert.Equal(step.SourceDocumentId, unwindRecent.ReversalOfDocumentId);
                Assert.Equal("PRIMARY", unwindRecent.BookCode);
                Assert.Equal("ASPE", unwindRecent.AccountingStandard);
                Assert.NotNull(unwindRecent.LinkedJournalEntryId);
                Assert.Equal(step.JournalEntryId, unwindRecent.LinkedJournalEntryId);

                var unwindDetail = await fixture.DocumentRepository.GetForPostingAsync(
                    CompanyId,
                    step.UnwindDocumentId,
                    CancellationToken.None);
                Assert.NotNull(unwindDetail);
                Assert.Equal("posted", unwindDetail!.Status);
                Assert.Equal("next_period_unwind", unwindDetail.BatchKind);
                Assert.Equal(step.SourceDocumentId, unwindDetail.ReversalOfDocumentId);
                Assert.Equal("PRIMARY", unwindDetail.BookCode);
                Assert.Equal("ASPE", unwindDetail.AccountingStandard);
                Assert.Equal("closing", unwindDetail.FxSnapshot.RateType);
                Assert.Equal("direct", unwindDetail.FxSnapshot.QuoteBasis);
                Assert.Equal("remeasurement", unwindDetail.FxSnapshot.RateUseCase);
                Assert.Equal("revaluation", unwindDetail.FxSnapshot.PostingReason);
            }

            var restoredBase = await LoadArOpenItemBaseAsync(
                fixture.ConnectionFactory,
                openItemId,
                CancellationToken.None);

            Assert.Equal(130m, restoredBase);
        }
        finally
        {
            await CleanupAsync(
                fixture,
                [firstDocumentId, secondDocumentId],
                [openItemId],
                [firstSnapshotId, secondSnapshotId],
                CancellationToken.None);
        }
    }

    private static async Task<FxFixture> CreateFixtureAsync()
    {
        var connectionString = GetConnectionString();
        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContextAccessor = new PostgresExecutionContextAccessor();
        var unitOfWork = new PostgresUnitOfWork(connectionFactory, executionContextAccessor);
        var documentRepository = new PostgresFxRevaluationDocumentRepository(connectionFactory, executionContextAccessor);
        var applyRepository = new PostgresFxRevaluationApplyRepository(connectionFactory, executionContextAccessor);
        var postingEngine = new DefaultPostingEngine(
            new DefaultPostingValidator(),
            new NullPostingPeriodPolicyValidator(),
            new NullTaxEngine(),
            new LocalFirstFxResolutionService(new PostgresFxSnapshotRepository(connectionFactory, executionContextAccessor)),
            new AccountingPostingFragmentBuilder(),
            new DefaultJournalAggregator(),
            new PostgresJournalEntryWriter(connectionFactory, executionContextAccessor));

        var prepareBatchHandler = new PrepareFxRevaluationBatchCommandHandler(documentRepository, unitOfWork);
        var postBatchHandler = new PostFxRevaluationBatchCommandHandler(documentRepository, postingEngine, applyRepository, unitOfWork);
        var prepareUnwindHandler = new PrepareFxRevaluationUnwindBatchCommandHandler(documentRepository, unitOfWork);
        var postCascadeHandler = new PostFxRevaluationCascadeUnwindCommandHandler(documentRepository, postingEngine, applyRepository, unitOfWork);

        var legacyConnectionFactory = new PostgreSqlConnectionFactory(connectionString);
        var companyCurrencyStore = new PostgreSqlCompanyCurrencyProvisioningStore(legacyConnectionFactory);
        var companyCurrencyWorkflow = new CompanyCurrencyGovernanceWorkflow(companyCurrencyStore);
        var (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
        var governance = await companyCurrencyWorkflow.EnableCurrencyAsync(CompanyId, "EUR", userId, CancellationToken.None);
        var createdUnrealizedFxAccountIds = await EnsureUnrealizedFxAccountsAsync(
            legacyConnectionFactory,
            CancellationToken.None);

        return new FxFixture(
            connectionFactory,
            documentRepository,
            prepareBatchHandler,
            postBatchHandler,
            prepareUnwindHandler,
            postCascadeHandler,
            governance.Profile.BaseCurrencyCode,
            createdUnrealizedFxAccountIds,
            userId,
            createdUser);
    }

    private static async Task<Guid> PrepareAndPostBatchAsync(
        FxFixture fixture,
        DateOnly revaluationDate,
        Guid snapshotId,
        string memo,
        CancellationToken cancellationToken)
    {
        var prepared = await fixture.PrepareBatchHandler.HandleAsync(
            new PrepareFxRevaluationBatchCommand(
                CompanyId,
                UserId.FromOrdinal(1),
                BookId: null,
                revaluationDate,
                new CurrencyCode("EUR"),
                snapshotId,
                IncludeAccountsReceivable: true,
                IncludeAccountsPayable: false,
                Memo: memo),
            cancellationToken);

        await fixture.PostBatchHandler.HandleAsync(
            new PostFxRevaluationBatchCommand(
                CompanyId,
                prepared.DocumentId,
                UserId.FromOrdinal(1),
                snapshotId,
                IdempotencyKey: null),
            cancellationToken);

        return prepared.DocumentId;
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    // Tests share the same primary book + remeasurement policy, and the
    // policy is inserted with effective_from = first asOfDate seen. If a
    // later test picks an EARLIER date than an earlier test, LoadPolicy's
    // `effective_from <= as_of_date` filter rejects the policy and the
    // workflow throws "FX revaluation requires policy". Use a monotonic
    // per-process counter so dates only ever advance forward.
    private static int _dateCounter;

    private static DateOnly BuildUniqueDate() =>
        DateOnly.FromDateTime(DateTime.UtcNow.Date)
            .AddDays(60 + Interlocked.Increment(ref _dateCounter) * 10);

    private static async Task<Guid> CreateManualFxSnapshotAsync(
        PostgresConnectionFactory connectionFactory,
        string baseCurrencyCode,
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
              'EUR',
              @requested_date,
              @effective_date,
              @rate,
              'closing',
              'direct',
              'remeasurement',
              'revaluation',
              @provider_key,
              'manual',
              'manual',
              null,
              'FX revaluation smoke test snapshot',
              @created_by_user_id,
              now()
            );
            """;
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("effective_date", requestedDate);
        command.Parameters.AddWithValue("rate", rate);
        command.Parameters.AddWithValue("provider_key", $"smoke-eur-{snapshotId:N}");
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return snapshotId;
    }

    private static async Task<Guid> CreateArOpenItemAsync(
        PostgresConnectionFactory connectionFactory,
        string baseCurrencyCode,
        DateOnly dueDate,
        decimal openAmountTx,
        decimal openAmountBase,
        CancellationToken cancellationToken)
    {
        var openItemId = Guid.NewGuid();

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
              'invoice',
              @source_id,
              @due_date,
              'EUR',
              @base_currency_code,
              @original_amount_tx,
              @original_amount_base,
              @open_amount_tx,
              @open_amount_base,
              'debit',
              'open',
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", openItemId);
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("customer_id", CustomerId);
        command.Parameters.AddWithValue("source_id", Guid.NewGuid());
        command.Parameters.AddWithValue("due_date", dueDate);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("original_amount_tx", openAmountTx);
        command.Parameters.AddWithValue("original_amount_base", openAmountBase);
        command.Parameters.AddWithValue("open_amount_tx", openAmountTx);
        command.Parameters.AddWithValue("open_amount_base", openAmountBase);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return openItemId;
    }

    private static async Task<decimal> LoadArOpenItemBaseAsync(
        PostgresConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select open_amount_base
            from ar_open_items
            where company_id = @company_id
              and id = @open_item_id;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("open_item_id", openItemId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        Assert.NotNull(scalar);
        return Convert.ToDecimal(scalar);
    }

    private static async Task<FxBatchMetadata?> LoadFxBatchMetadataAsync(
        PostgresConnectionFactory connectionFactory,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select batch_kind, reversal_of_fx_revaluation_batch_id
            from fx_revaluation_batches
            where company_id = @company_id
              and id = @document_id;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FxBatchMetadata(
            reader.GetString(reader.GetOrdinal("batch_kind")),
            reader.IsDBNull(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("reversal_of_fx_revaluation_batch_id")));
    }

    private static async Task<(UserId UserId, bool Created)> GetOrCreateUserAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText =
            """
            select id
            from users
            order by created_at
            limit 1;
            """;

        var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
        if (existing is string userIdString && UserId.TryParse(userIdString, out var userId))
        {
            return (userId, false);
        }

        var newUserId = UserId.FromOrdinal(1);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (
              id,
              email,
              username,
              password_hash,
              status
            )
            values (
              @id,
              @email,
              @username,
              @password_hash,
              'active'
            );
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"smoke-{newUserId.Value}@citus.local");
        insertCommand.Parameters.AddWithValue("username", $"smoke-{newUserId.Value}");
        insertCommand.Parameters.AddWithValue("password_hash", "smoke-hash");
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return (newUserId, true);
    }

    private static async Task<IReadOnlyList<Guid>> EnsureUnrealizedFxAccountsAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var createdAccountIds = new List<Guid>();
        createdAccountIds.AddRange(await EnsureUnrealizedFxAccountAsync(
            connectionFactory,
            systemRole: "unrealized_fx_gain",
            systemKey: "fx:unrealized_gain",
            codePrefix: "FXGAIN",
            name: "Smoke Unrealized FX Gain",
            rootType: "revenue",
            detailType: "fx_gain",
            cancellationToken));
        createdAccountIds.AddRange(await EnsureUnrealizedFxAccountAsync(
            connectionFactory,
            systemRole: "unrealized_fx_loss",
            systemKey: "fx:unrealized_loss",
            codePrefix: "FXLOSS",
            name: "Smoke Unrealized FX Loss",
            rootType: "expense",
            detailType: "fx_loss",
            cancellationToken));
        return createdAccountIds;
    }

    private static async Task<IReadOnlyList<Guid>> EnsureUnrealizedFxAccountAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string systemRole,
        string systemKey,
        string codePrefix,
        string name,
        string rootType,
        string detailType,
        CancellationToken cancellationToken)
    {
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.CommandText =
                """
                select id
                from accounts
                where company_id = @company_id
                  and (system_role = @system_role or system_key = @system_key)
                limit 1;
                """;
            existingCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            existingCommand.Parameters.AddWithValue("system_role", systemRole);
            existingCommand.Parameters.AddWithValue("system_key", systemKey);
            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid)
            {
                return [];
            }
        }

        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var insertConnection = await connectionFactory.OpenAsync(cancellationToken);
        await using var insertCommand = insertConnection.CreateCommand();
        insertCommand.CommandText =
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
              system_key,
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
              @root_type,
              @detail_type,
              true,
              true,
              true,
              @system_key,
              @system_role,
              false,
              now(),
              now()
            );
            """;
        insertCommand.Parameters.AddWithValue("id", accountId);
        insertCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
        insertCommand.Parameters.AddWithValue("entity_number", entityNumber);
        insertCommand.Parameters.AddWithValue("code", $"{codePrefix}-{entityNumber[^6..]}");
        insertCommand.Parameters.AddWithValue("name", name);
        insertCommand.Parameters.AddWithValue("root_type", rootType);
        insertCommand.Parameters.AddWithValue("detail_type", detailType);
        insertCommand.Parameters.AddWithValue("system_key", systemKey);
        insertCommand.Parameters.AddWithValue("system_role", systemRole);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

        return [accountId];
    }

    private static async Task<string> ReserveEntityNumberAsync(
        PostgreSqlConnectionFactory connectionFactory,
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

        throw new InvalidOperationException("Could not reserve a unique entity number for FX revaluation smoke test.");
    }

    private static async Task<bool> EntityNumberExistsAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string entityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
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

    private static async Task CleanupAsync(
        FxFixture fixture,
        IEnumerable<Guid> seedDocumentIds,
        IEnumerable<Guid> openItemIds,
        IEnumerable<Guid> snapshotIds,
        CancellationToken cancellationToken)
    {
        var documentIds = seedDocumentIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        await using var connection = await fixture.ConnectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (documentIds.Count > 0)
        {
            await using (var expandCommand = connection.CreateCommand())
            {
                expandCommand.Transaction = transaction;
                expandCommand.CommandText =
                    """
                    with recursive batch_chain as (
                      select id
                      from fx_revaluation_batches
                      where company_id = @company_id
                        and id = any(@document_ids)
                      union
                      select child.id
                      from fx_revaluation_batches child
                      join batch_chain parent
                        on child.reversal_of_fx_revaluation_batch_id = parent.id
                      where child.company_id = @company_id
                    )
                    select id
                    from batch_chain;
                    """;
                expandCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
                expandCommand.Parameters.AddWithValue("document_ids", documentIds);

                await using var reader = await expandCommand.ExecuteReaderAsync(cancellationToken);
                documentIds.Clear();
                while (await reader.ReadAsync(cancellationToken))
                {
                    documentIds.Add(reader.GetGuid(0));
                }
            }
        }

        if (documentIds.Count > 0)
        {
            await using (var journalQuery = connection.CreateCommand())
            {
                journalQuery.Transaction = transaction;
                journalQuery.CommandText =
                    """
                    select id
                    from journal_entries
                    where company_id = @company_id
                      and source_type = 'fx_revaluation'
                      and source_id = any(@document_ids);
                    """;
                journalQuery.Parameters.AddWithValue("company_id", CompanyId.Value);
                journalQuery.Parameters.AddWithValue("document_ids", documentIds);

                var journalEntryIds = new List<Guid>();
                await using (var reader = await journalQuery.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        journalEntryIds.Add(reader.GetGuid(0));
                    }
                }

                foreach (var journalEntryId in journalEntryIds)
                {
                    await ExecuteDeleteByIdAsync(connection, transaction, "delete from ledger_entries where journal_entry_id = @id;", journalEntryId, cancellationToken);
                    await ExecuteDeleteByIdAsync(connection, transaction, "delete from journal_entry_lines where journal_entry_id = @id;", journalEntryId, cancellationToken);
                    await ExecuteDeleteByIdAsync(connection, transaction, "delete from journal_entries where id = @id;", journalEntryId, cancellationToken);
                }

                await using var deleteLinesCommand = connection.CreateCommand();
                deleteLinesCommand.Transaction = transaction;
                deleteLinesCommand.CommandText =
                    """
                    delete from fx_revaluation_batch_lines
                    where company_id = @company_id
                      and fx_revaluation_batch_id = any(@document_ids);
                    """;
                deleteLinesCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
                deleteLinesCommand.Parameters.AddWithValue("document_ids", documentIds);
                await deleteLinesCommand.ExecuteNonQueryAsync(cancellationToken);

                await using var deleteBatchCommand = connection.CreateCommand();
                deleteBatchCommand.Transaction = transaction;
                deleteBatchCommand.CommandText =
                    """
                    delete from fx_revaluation_batches
                    where company_id = @company_id
                      and id = any(@document_ids);
                    """;
                deleteBatchCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
                deleteBatchCommand.Parameters.AddWithValue("document_ids", documentIds);
                await deleteBatchCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var openItemId in openItemIds.Where(id => id != Guid.Empty).Distinct())
        {
            await ExecuteDeleteByIdAsync(connection, transaction, "delete from ar_open_items where id = @id;", openItemId, cancellationToken);
        }

        foreach (var snapshotId in snapshotIds.Where(id => id != Guid.Empty).Distinct())
        {
            await ExecuteDeleteByIdAsync(connection, transaction, "delete from company_fx_rate_snapshots where id = @id;", snapshotId, cancellationToken);
        }

        foreach (var accountId in fixture.CreatedUnrealizedFxAccountIds.Where(id => id != Guid.Empty).Distinct())
        {
            await ExecuteDeleteByIdAsync(connection, transaction, "delete from accounts where id = @id;", accountId, cancellationToken);
        }

        await ExecuteDeleteByCompanyAsync(
            connection,
            transaction,
            """
            delete from accounts
            where company_id = @company_id
              and system_role in ('accounts_receivable:EUR', 'accounts_payable:EUR');
            """,
            cancellationToken);
        await ExecuteDeleteByCompanyAsync(
            connection,
            transaction,
            """
            delete from company_currencies
            where company_id = @company_id
              and currency_code = 'EUR';
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await CleanupUserAsync(fixture.ConnectionFactory, fixture.UserId, fixture.CreatedUser, cancellationToken);
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
        command.CommandText =
            """
            delete from users
            where id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteByCompanyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record FxFixture(
        PostgresConnectionFactory ConnectionFactory,
        PostgresFxRevaluationDocumentRepository DocumentRepository,
        PrepareFxRevaluationBatchCommandHandler PrepareBatchHandler,
        PostFxRevaluationBatchCommandHandler PostBatchHandler,
        PrepareFxRevaluationUnwindBatchCommandHandler PrepareUnwindHandler,
        PostFxRevaluationCascadeUnwindCommandHandler PostCascadeHandler,
        string BaseCurrencyCode,
        IReadOnlyList<Guid> CreatedUnrealizedFxAccountIds,
        UserId UserId,
        bool CreatedUser);

    private sealed record FxBatchMetadata(
        string BatchKind,
        Guid? ReversalOfDocumentId);
}
