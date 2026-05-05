using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using System.Text.Json;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresApOpenItemRepository : IApOpenItemRepository
{
    private const decimal TemporaryAdjustmentApprovalThresholdBase = 100m;

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresApOpenItemRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task EnsureForBillAsync(
        BillDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        await EnsureOpenItemAsync(
            companyId: document.CompanyId,
            partyId: document.PartyId,
            sourceType: "bill",
            sourceId: document.Id,
            documentCurrencyCode: document.TransactionCurrencyCode.Value,
            baseCurrencyCode: document.BaseCurrencyCode.Value,
            originalAmountTx: document.TotalAmount,
            originalAmountBase: originalAmountBase,
            dueDate: document.DueDate,
            balanceSide: "credit",
            cancellationToken: cancellationToken);
    }

    public async Task EnsureForVendorCreditAsync(
        VendorCreditDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        await EnsureOpenItemAsync(
            companyId: document.CompanyId,
            partyId: document.PartyId,
            sourceType: "vendor_credit",
            sourceId: document.Id,
            documentCurrencyCode: document.TransactionCurrencyCode.Value,
            baseCurrencyCode: document.BaseCurrencyCode.Value,
            originalAmountTx: document.TotalAmount,
            originalAmountBase: originalAmountBase,
            dueDate: document.DueDate,
            balanceSide: "debit",
            cancellationToken: cancellationToken);
    }

    public async Task<OpenItemDrillDown?> GetDrillDownAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select
              oi.id as open_item_id,
              oi.vendor_id as party_id,
              v.entity_number as party_entity_number,
              v.display_name as party_display_name,
              oi.source_type,
              oi.source_id as source_document_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as source_document_display_number,
              coalesce(b.bill_date, vc.vendor_credit_date, oi.due_date) as document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.balance_side,
              oi.status,
              oi.original_amount_tx,
              oi.original_amount_base,
              oi.open_amount_tx,
              oi.open_amount_base
            from ap_open_items oi
            inner join vendors v
              on v.company_id = oi.company_id
             and v.id = oi.vendor_id
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where oi.company_id = @company_id
              and oi.id = @open_item_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenItemDrillDown(
            reader.GetGuid(reader.GetOrdinal("open_item_id")),
            "ap",
            companyId,
            "vendor",
            reader.GetGuid(reader.GetOrdinal("party_id")),
            reader.GetString(reader.GetOrdinal("party_entity_number")),
            reader.GetString(reader.GetOrdinal("party_display_name")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_document_id")),
            reader.GetString(reader.GetOrdinal("source_document_display_number")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
            reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("original_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("original_amount_base")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")));
    }

    public async Task<OpenItemAdjustmentPreview?> GetAdjustmentPreviewAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustmentType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var snapshot = await GetAdjustmentSnapshotAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        return snapshot is null
            ? null
            : BuildAdjustmentPreview(snapshot, NormalizeAdjustmentType(adjustmentType), adjustmentDate, adjustmentAmountTx);
    }

    public async Task<OpenItemAdjustmentRequestAttempt?> RequestAdjustmentAsync(
        CompanyId companyId,
        Guid openItemId,
        string adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx,
        UserId? actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustmentType);

        var preview = await GetAdjustmentPreviewAsync(
            companyId,
            openItemId,
            adjustmentType,
            adjustmentDate,
            adjustmentAmountTx,
            cancellationToken);

        if (preview is null)
        {
            return null;
        }

        if (!preview.IsAvailable)
        {
            return new OpenItemAdjustmentRequestAttempt(
                preview,
                false,
                false,
                false,
                "blocked",
                preview.Reason,
                null);
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var latestRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (latestRequest is not null)
        {
            if (latestRequest.RequestStatus is not ("draft" or "submitted") ||
                latestRequest.ExecutionStatus == "journal_entry_posted")
            {
                latestRequest = null;
            }
        }

        if (latestRequest is not null)
        {
            return new OpenItemAdjustmentRequestAttempt(
                preview,
                false,
                false,
                false,
                "request_already_open",
                $"A governed {latestRequest.AdjustmentType} request is already {latestRequest.RequestStatus} for this AP open item.",
                latestRequest);
        }

        var requestId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var request = new OpenItemAdjustmentRequestRecord(
            requestId,
            openItemId,
            "ap_open_item",
            companyId,
            preview.AdjustmentType,
            adjustmentDate,
            preview.RequestedAdjustmentAmountTx,
            preview.RequestedAdjustmentAmountBase,
            preview.RequiresApproval,
            preview.ApprovalStatus,
            null,
            null,
            null,
            null,
            null,
            null,
            "draft",
            "not_started",
            actorId.HasValue ? "user" : "system",
            actorId,
            requestedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            trimmedReason);

        var payload = JsonSerializer.Serialize(new
        {
            request.RequestId,
            request.OpenItemId,
            request.OpenItemType,
            CompanyId = request.CompanyId,
            request.AdjustmentType,
            request.AdjustmentDate,
            request.RequestStatus,
            request.ExecutionStatus,
            request.Reason,
            preview.SourceType,
            preview.SourceDocumentId,
            preview.SourceDocumentDisplayNumber,
            preview.SourceDocumentStatus,
            preview.PartyRole,
            preview.PartyId,
            preview.DocumentCurrencyCode,
            preview.BaseCurrencyCode,
            preview.BalanceSide,
            preview.Status,
            preview.OpenAmountTx,
            preview.OpenAmountBase,
            preview.RequestedAdjustmentAmountTx,
            preview.RequestedAdjustmentAmountBase,
            preview.RemainingAmountTx,
            preview.RemainingAmountBase,
            preview.RequiresApproval,
            preview.ApprovalStatus,
            preview.ApprovalReason,
            preview.ApplicationCount,
            preview.ActionCode,
            preview.ActionLabel,
            preview.ExecutionMode
        });

        await using (var command = scope.CreateCommand(
                         """
                         insert into audit_logs (
                           id,
                           company_id,
                           actor_type,
                           actor_id,
                           entity_type,
                           entity_id,
                           action,
                           payload
                         )
                         values (
                           @id,
                           @company_id,
                           @actor_type,
                           @actor_id,
                           @entity_type,
                           @entity_id,
                           @action,
                           @payload::jsonb
                         );
                         """))
        {
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("actor_type", request.RequestedByActorType);
            command.Parameters.AddWithValue("actor_id", actorId.HasValue ? actorId.Value : DBNull.Value);
            command.Parameters.AddWithValue("entity_type", "open_item_adjustment_request");
            command.Parameters.AddWithValue("entity_id", requestId);
            command.Parameters.AddWithValue("action", "open_item_adjustment_requested");
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new OpenItemAdjustmentRequestAttempt(
            preview,
            true,
            false,
            true,
            "request_recorded",
            "Governed AP open item adjustment request was recorded. Open-item truth and accounting entries remain unchanged until a Posting Engine-backed execution flow is implemented.",
            request);
    }

    public async Task<OpenItemAdjustmentRequestRecord?> GetLatestAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        return await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);
    }

    public async Task<OpenItemAdjustmentRequestTransitionResult?> SubmitAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        if (request.RequestStatus == "submitted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "submit",
                "already_submitted",
                "The governed AP open item adjustment request is already submitted.");
        }

        if (request.RequestStatus == "cancelled")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "submit",
                "blocked_cancelled",
                "Cancelled AP open item adjustment requests cannot be submitted.");
        }

        await AppendAdjustmentRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "open_item_adjustment_request_submitted",
            new
            {
                request.RequestId,
                request.OpenItemId,
                request.OpenItemType,
                TransitionCode = "submit",
                RequestStatus = "submitted"
            },
            cancellationToken);

        var updatedRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken) ?? request;

        return new OpenItemAdjustmentRequestTransitionResult(
            updatedRequest,
            "submit",
            "submitted",
            "Governed AP open item adjustment request was submitted. Accounting execution is still blocked until a Posting Engine-backed flow exists.");
    }

    public async Task<OpenItemAdjustmentRequestTransitionResult?> CancelAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        if (request.RequestStatus == "cancelled")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "cancel",
                "already_cancelled",
                "The governed AP open item adjustment request is already cancelled.");
        }

        if (request.ExecutionStatus is "execution_requested" or "journal_entry_posted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "cancel",
                "blocked_execution_started",
                "AP open item adjustment requests cannot be cancelled after execution has started.");
        }

        await AppendAdjustmentRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "open_item_adjustment_request_cancelled",
            new
            {
                request.RequestId,
                request.OpenItemId,
                request.OpenItemType,
                TransitionCode = "cancel",
                RequestStatus = "cancelled"
            },
            cancellationToken);

        var updatedRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken) ?? request;

        return new OpenItemAdjustmentRequestTransitionResult(
            updatedRequest,
            "cancel",
            "cancelled",
            "Governed AP open item adjustment request was cancelled without changing open-item or accounting truth.");
    }

    public async Task<OpenItemAdjustmentRequestTransitionResult?> ApproveAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        if (!request.RequiresApproval)
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "approve",
                "approval_not_required",
                "This AP open item adjustment request does not require approval.");
        }

        var authorityBlock = ValidateAdjustmentApprovalAuthority(request, actorId, "approve", "AP");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        if (request.RequestStatus != "submitted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "approve",
                "blocked_by_request_status",
                "Only submitted AP open item adjustment requests can be approved.");
        }

        if (request.ExecutionStatus is "execution_requested" or "journal_entry_posted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "approve",
                "blocked_execution_started",
                "AP open item adjustment requests cannot be approved after execution has started.");
        }

        if (request.ApprovalStatus == "approved")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "approve",
                "already_approved",
                "The governed AP open item adjustment request is already approved.");
        }

        await AppendAdjustmentRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "open_item_adjustment_request_approved",
            new
            {
                request.RequestId,
                request.OpenItemId,
                request.OpenItemType,
                TransitionCode = "approve",
                ApprovalStatus = "approved"
            },
            cancellationToken);

        var updatedRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken) ?? request;

        return new OpenItemAdjustmentRequestTransitionResult(
            updatedRequest,
            "approve",
            "approved",
            "Governed AP open item adjustment request was approved. Posting execution may continue if current open-item truth still passes readiness.");
    }

    public async Task<OpenItemAdjustmentRequestTransitionResult?> RejectAdjustmentRequestAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        if (!request.RequiresApproval)
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "reject",
                "approval_not_required",
                "This AP open item adjustment request does not require approval.");
        }

        var authorityBlock = ValidateAdjustmentApprovalAuthority(request, actorId, "reject", "AP");
        if (authorityBlock is not null)
        {
            return authorityBlock;
        }

        if (request.RequestStatus != "submitted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "reject",
                "blocked_by_request_status",
                "Only submitted AP open item adjustment requests can be rejected.");
        }

        if (request.ExecutionStatus is "execution_requested" or "journal_entry_posted")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "reject",
                "blocked_execution_started",
                "AP open item adjustment requests cannot be rejected after execution has started.");
        }

        if (request.ApprovalStatus == "rejected")
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                "reject",
                "already_rejected",
                "The governed AP open item adjustment request is already rejected.");
        }

        await AppendAdjustmentRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "open_item_adjustment_request_rejected",
            new
            {
                request.RequestId,
                request.OpenItemId,
                request.OpenItemType,
                TransitionCode = "reject",
                ApprovalStatus = "rejected"
            },
            cancellationToken);

        var updatedRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken) ?? request;

        return new OpenItemAdjustmentRequestTransitionResult(
            updatedRequest,
            "reject",
            "rejected",
            "Governed AP open item adjustment request was rejected. Open-item and accounting truth remain unchanged.");
    }

    public async Task<OpenItemAdjustmentRequestReadiness?> GetAdjustmentRequestReadinessAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        var snapshot = await GetAdjustmentSnapshotAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        var preview = BuildAdjustmentPreview(
            snapshot,
            NormalizeAdjustmentType(request.AdjustmentType),
            asOfDate,
            request.RequestedAdjustmentAmountTx);
        return BuildAdjustmentRequestReadiness(request, preview, asOfDate);
    }

    public async Task<OpenItemAdjustmentExecutionPlan?> GetAdjustmentRequestExecutionPlanAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var readiness = await GetAdjustmentRequestReadinessAsync(
            companyId,
            openItemId,
            requestId,
            asOfDate,
            cancellationToken);

        return readiness is null ? null : BuildAdjustmentExecutionPlan(readiness);
    }

    public async Task<OpenItemAdjustmentExecutionPreparation?> PrepareAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        Guid adjustmentAccountId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        if (adjustmentAccountId == Guid.Empty)
        {
            throw new ArgumentException("Adjustment account id is required.", nameof(adjustmentAccountId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        var snapshot = await GetAdjustmentSnapshotAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        var accountPolicy = await EvaluateAdjustmentAccountPolicyAsync(
            scope,
            companyId,
            adjustmentAccountId,
            "ap_open_item",
            request.AdjustmentType,
            cancellationToken);
        if (!accountPolicy.Allowed)
        {
            throw new InvalidOperationException(accountPolicy.Message);
        }

        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId,
            "accounts_payable",
            snapshot.DocumentCurrencyCode,
            snapshot.BaseCurrencyCode,
            cancellationToken);

        if (!controlAccountId.HasValue)
        {
            throw new InvalidOperationException("No active accounts payable control account is configured for this AP adjustment.");
        }

        var preview = BuildAdjustmentPreview(
            snapshot,
            NormalizeAdjustmentType(request.AdjustmentType),
            asOfDate,
            request.RequestedAdjustmentAmountTx);
        var readiness = BuildAdjustmentRequestReadiness(request, preview, asOfDate);
        var document = BuildAdjustmentDocument(request, snapshot, controlAccountId.Value, adjustmentAccountId, asOfDate);

        return new OpenItemAdjustmentExecutionPreparation(
            readiness,
            document,
            adjustmentAccountId,
            preview.RequestedAdjustmentAmountTx,
            preview.RequestedAdjustmentAmountBase);
    }

    public async Task<OpenItemAdjustmentExecutionResult?> CompleteAdjustmentExecutionAsync(
        CompanyId companyId,
        Guid openItemId,
        Guid requestId,
        UserId? actorId,
        Guid journalEntryId,
        string journalEntryDisplayNumber,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (request is null || request.RequestId != requestId)
        {
            return null;
        }

        var snapshot = await GetAdjustmentSnapshotAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        var adjustmentAmountTx = request.RequestedAdjustmentAmountTx;
        var adjustmentAmountBase = request.RequestedAdjustmentAmountBase;
        if (adjustmentAmountTx <= 0m ||
            adjustmentAmountBase <= 0m ||
            adjustmentAmountTx > snapshot.OpenAmountTx ||
            adjustmentAmountBase > snapshot.OpenAmountBase)
        {
            throw new InvalidOperationException("AP open item adjustment amount no longer matches current open-item truth.");
        }

        var remainingAmountTx = snapshot.OpenAmountTx - adjustmentAmountTx;
        var remainingAmountBase = snapshot.OpenAmountBase - adjustmentAmountBase;
        var nextStatus = remainingAmountTx == 0m && remainingAmountBase == 0m
            ? "closed"
            : "partially_applied";

        await using (var updateCommand = scope.CreateCommand(
                         """
                         update ap_open_items
                         set open_amount_tx = @remaining_amount_tx,
                             open_amount_base = @remaining_amount_base,
                             status = @next_status,
                             updated_at = now()
                         where company_id = @company_id
                           and id = @open_item_id
                           and status in ('open', 'partially_applied')
                           and open_amount_tx >= @adjustment_amount_tx
                           and open_amount_base >= @adjustment_amount_base;
                         """))
        {
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("open_item_id", openItemId);
            updateCommand.Parameters.AddWithValue("adjustment_amount_tx", adjustmentAmountTx);
            updateCommand.Parameters.AddWithValue("adjustment_amount_base", adjustmentAmountBase);
            updateCommand.Parameters.AddWithValue("remaining_amount_tx", remainingAmountTx);
            updateCommand.Parameters.AddWithValue("remaining_amount_base", remainingAmountBase);
            updateCommand.Parameters.AddWithValue("next_status", nextStatus);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("AP open item adjustment could not reduce the current open-item balance.");
            }
        }

        await AppendAdjustmentRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "open_item_adjustment_execution_completed",
            new
            {
                request.RequestId,
                request.OpenItemId,
                request.OpenItemType,
                TransitionCode = "execute",
                ExecutionStatus = "journal_entry_posted",
                JournalEntryId = journalEntryId,
                JournalEntryDisplayNumber = journalEntryDisplayNumber,
                CompensationSourceType = "ap_open_item_adjustment",
                snapshot.OpenAmountTx,
                snapshot.OpenAmountBase,
                AdjustmentAmountTx = adjustmentAmountTx,
                AdjustmentAmountBase = adjustmentAmountBase,
                RemainingAmountTx = remainingAmountTx,
                RemainingAmountBase = remainingAmountBase,
                OpenItemStatus = nextStatus,
                ExecutedAt = executedAt
            },
            cancellationToken);

        var updatedRequest = await GetLatestAdjustmentRequestAsync(
            scope,
            companyId,
            openItemId,
            "ap_open_item",
            cancellationToken) ?? request;

        return new OpenItemAdjustmentExecutionResult(
            updatedRequest,
            executedAt.UtcDateTime == default ? DateOnly.FromDateTime(DateTime.UtcNow) : DateOnly.FromDateTime(executedAt.UtcDateTime),
            "posting_engine_adjustment",
            true,
            true,
            true,
            "journal_entry_posted",
            nextStatus == "closed"
                ? "Governed AP open item adjustment posted through the Posting Engine and closed the open item."
                : "Governed AP open item adjustment posted through the Posting Engine and reduced the open item.",
            journalEntryId,
            journalEntryDisplayNumber,
            "ap_open_item_adjustment",
            adjustmentAmountTx,
            adjustmentAmountBase);
    }

    private async Task EnsureOpenItemAsync(
        CompanyId companyId,
        Guid partyId,
        string sourceType,
        Guid sourceId,
        string documentCurrencyCode,
        string baseCurrencyCode,
        decimal originalAmountTx,
        decimal originalAmountBase,
        DateOnly? dueDate,
        string balanceSide,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using (var existingCommand = scope.CreateCommand(
                         """
                         select id
                         from ap_open_items
                         where company_id = @company_id
                           and source_type = @source_type
                           and source_id = @source_id
                         limit 1;
                         """))
        {
            existingCommand.Parameters.AddWithValue("company_id", companyId);
            existingCommand.Parameters.AddWithValue("source_type", sourceType);
            existingCommand.Parameters.AddWithValue("source_id", sourceId);

            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null && existing != DBNull.Value)
            {
                return;
            }
        }

        await using var command = scope.CreateCommand(
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
              due_date,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @vendor_id,
              @source_type,
              @source_id,
              @balance_side,
              @document_currency_code,
              @base_currency_code,
              @original_amount_tx,
              @original_amount_base,
              @open_amount_tx,
              @open_amount_base,
              'open',
              @due_date,
              now(),
              now()
            );
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", partyId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("balance_side", balanceSide);
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("original_amount_tx", originalAmountTx);
        command.Parameters.AddWithValue("original_amount_base", originalAmountBase);
        command.Parameters.AddWithValue("open_amount_tx", originalAmountTx);
        command.Parameters.AddWithValue("open_amount_base", originalAmountBase);
        command.Parameters.AddWithValue(
            "due_date",
            dueDate.HasValue ? (object)dueDate.Value : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<OpenItemAdjustmentSnapshot?> GetAdjustmentSnapshotAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid openItemId,
        string openItemType,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              oi.id as open_item_id,
              oi.company_id,
              oi.vendor_id as party_id,
              oi.source_type,
              oi.source_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as source_document_display_number,
              coalesce(b.status, vc.status, 'unknown') as source_document_status,
              coalesce(b.bill_date, vc.vendor_credit_date, oi.due_date, current_date) as document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.balance_side,
              oi.status,
              oi.open_amount_tx,
              oi.open_amount_base,
              (
                select count(*)::int
                from settlement_applications sa
                where sa.company_id = oi.company_id
                  and sa.target_open_item_type = @open_item_type
                  and sa.target_open_item_id = oi.id
              ) as application_count
            from ap_open_items oi
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where oi.company_id = @company_id
              and oi.id = @open_item_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_id", openItemId);
        command.Parameters.AddWithValue("open_item_type", openItemType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenItemAdjustmentSnapshot(
            reader.GetGuid(reader.GetOrdinal("open_item_id")),
            openItemType,
            companyId,
            "vendor",
            reader.GetGuid(reader.GetOrdinal("party_id")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_id")),
            reader.GetString(reader.GetOrdinal("source_document_display_number")),
            reader.GetString(reader.GetOrdinal("source_document_status")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
            reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
            reader.GetString(reader.GetOrdinal("document_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("balance_side")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("open_amount_base")),
            reader.GetInt32(reader.GetOrdinal("application_count")));
    }

    private static OpenItemAdjustmentPreview BuildAdjustmentPreview(
        OpenItemAdjustmentSnapshot snapshot,
        string? adjustmentType,
        DateOnly adjustmentDate,
        decimal? adjustmentAmountTx)
    {
        var requestedAmounts = CalculateRequestedAdjustmentAmounts(snapshot, adjustmentAmountTx);

        if (adjustmentType is null)
        {
            return snapshot.ToPreview(
                "unknown",
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_adjustment_type",
                false,
                "Only write_off and small_balance_adjustment are supported by the governed AP adjustment skeleton.");
        }

        if (snapshot.SourceDocumentStatus is "voided" or "reversed")
        {
            return snapshot.ToPreview(
                adjustmentType,
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_source_status",
                false,
                "The source document is already historical-only, so this open item cannot start a new adjustment request.");
        }

        if (snapshot.Status is not ("open" or "partially_applied"))
        {
            return snapshot.ToPreview(
                adjustmentType,
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_open_item_status",
                false,
                "Only open or partially applied AP open items can enter governed adjustment review.");
        }

        if (snapshot.OpenAmountTx <= 0m || snapshot.OpenAmountBase <= 0m)
        {
            return snapshot.ToPreview(
                adjustmentType,
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_zero_open_balance",
                false,
                "The open item has no remaining open balance to adjust.");
        }

        if (requestedAmounts.AmountTx <= 0m)
        {
            return snapshot.ToPreview(
                adjustmentType,
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_adjustment_amount",
                false,
                "Adjustment amount must be greater than zero.");
        }

        if (requestedAmounts.AmountTx > snapshot.OpenAmountTx)
        {
            return snapshot.ToPreview(
                adjustmentType,
                adjustmentDate,
                requestedAmounts.AmountTx,
                requestedAmounts.AmountBase,
                requestedAmounts.RemainingTx,
                requestedAmounts.RemainingBase,
                requestedAmounts.RequiresApproval,
                requestedAmounts.ApprovalStatus,
                requestedAmounts.ApprovalReason,
                "blocked_by_adjustment_amount",
                false,
                "Adjustment amount cannot exceed the current AP open-item balance.");
        }

        return snapshot.ToPreview(
            adjustmentType,
            adjustmentDate,
            requestedAmounts.AmountTx,
            requestedAmounts.AmountBase,
            requestedAmounts.RemainingTx,
            requestedAmounts.RemainingBase,
            requestedAmounts.RequiresApproval,
            requestedAmounts.ApprovalStatus,
            requestedAmounts.ApprovalReason,
            "available_for_request",
            true,
            snapshot.ApplicationCount > 0
                ? "The open item has settlement/application trail. The request can be recorded, and execution must preserve that trail."
                : "The open item can start governed adjustment review. No accounting truth is changed by this preview.");
    }

    private static string? NormalizeAdjustmentType(string adjustmentType) =>
        adjustmentType.Trim().ToLowerInvariant() switch
        {
            "write_off" => "write_off",
            "small_balance_adjustment" => "small_balance_adjustment",
            _ => null
        };

    private static OpenItemAdjustmentRequestedAmounts CalculateRequestedAdjustmentAmounts(
        OpenItemAdjustmentSnapshot snapshot,
        decimal? adjustmentAmountTx)
    {
        var amountTx = adjustmentAmountTx ?? snapshot.OpenAmountTx;
        var amountBase = snapshot.OpenAmountTx == 0m
            ? 0m
            : amountTx * snapshot.OpenAmountBase / snapshot.OpenAmountTx;
        var remainingTx = snapshot.OpenAmountTx - amountTx;
        var remainingBase = snapshot.OpenAmountBase - amountBase;
        var requiresApproval = amountBase > TemporaryAdjustmentApprovalThresholdBase;

        return new OpenItemAdjustmentRequestedAmounts(
            amountTx,
            amountBase,
            remainingTx,
            remainingBase,
            requiresApproval,
            requiresApproval ? "pending" : "not_required",
            requiresApproval
                ? $"Adjustment amount exceeds the temporary backend threshold of {TemporaryAdjustmentApprovalThresholdBase:N2} base currency units. Governed approval is required before execution."
                : "No approval is required by the temporary backend threshold.");
    }

    private static decimal? GetOptionalDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value)
            ? value
            : null;
    }

    private static string ResolveApprovalStatus(
        JsonElement root,
        bool requiresApproval,
        DateTimeOffset? approvedAt,
        DateTimeOffset? rejectedAt)
    {
        if (!requiresApproval)
        {
            return "not_required";
        }

        if (approvedAt.HasValue && (!rejectedAt.HasValue || approvedAt.Value >= rejectedAt.Value))
        {
            return "approved";
        }

        if (rejectedAt.HasValue)
        {
            return "rejected";
        }

        if (root.TryGetProperty("ApprovalStatus", out var approvalStatusProperty) &&
            approvalStatusProperty.ValueKind == JsonValueKind.String)
        {
            var payloadStatus = approvalStatusProperty.GetString();
            if (!string.IsNullOrWhiteSpace(payloadStatus) &&
                payloadStatus != "required_not_implemented")
            {
                return payloadStatus;
            }
        }

        return "pending";
    }

    private static async Task<OpenItemAdjustmentRequestRecord?> GetLatestAdjustmentRequestAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid openItemId,
        string openItemType,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              al.id,
              al.actor_type as requested_actor_type,
              al.actor_id as requested_actor_id,
              al.payload,
              al.created_at as requested_at,
              submitted.actor_type as submitted_actor_type,
              submitted.actor_id as submitted_actor_id,
              submitted.created_at as submitted_at,
              cancelled.actor_type as cancelled_actor_type,
              cancelled.actor_id as cancelled_actor_id,
              cancelled.created_at as cancelled_at,
              approved.actor_type as approved_actor_type,
              approved.actor_id as approved_actor_id,
              approved.created_at as approved_at,
              rejected.actor_type as rejected_actor_type,
              rejected.actor_id as rejected_actor_id,
              rejected.created_at as rejected_at,
              execution_requested.created_at as execution_requested_at,
              completed.created_at as completed_at
            from audit_logs al
            left join lateral (
              select tl.actor_type, tl.actor_id, tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_request_submitted'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) submitted on true
            left join lateral (
              select tl.actor_type, tl.actor_id, tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_request_cancelled'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) cancelled on true
            left join lateral (
              select tl.actor_type, tl.actor_id, tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_request_approved'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) approved on true
            left join lateral (
              select tl.actor_type, tl.actor_id, tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_request_rejected'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) rejected on true
            left join lateral (
              select tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_execution_requested'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) execution_requested on true
            left join lateral (
              select tl.created_at
              from audit_logs tl
              where tl.company_id = al.company_id
                and tl.entity_type = 'open_item_adjustment_request'
                and tl.entity_id = (al.payload ->> 'RequestId')::uuid
                and tl.action = 'open_item_adjustment_execution_completed'
              order by tl.created_at desc, tl.id desc
              limit 1
            ) completed on true
            where al.company_id = @company_id
              and al.entity_type = 'open_item_adjustment_request'
              and al.action = 'open_item_adjustment_requested'
              and al.payload ->> 'OpenItemType' = @open_item_type
              and al.payload ->> 'OpenItemId' = @open_item_id_text
            order by al.created_at desc, al.id desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_type", openItemType);
        command.Parameters.AddWithValue("open_item_id_text", openItemId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var document = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
        var root = document.RootElement;
        DateTimeOffset? submittedAt = reader.IsDBNull(reader.GetOrdinal("submitted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("submitted_at"));
        DateTimeOffset? cancelledAt = reader.IsDBNull(reader.GetOrdinal("cancelled_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("cancelled_at"));
        DateTimeOffset? approvedAt = reader.IsDBNull(reader.GetOrdinal("approved_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at"));
        DateTimeOffset? rejectedAt = reader.IsDBNull(reader.GetOrdinal("rejected_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("rejected_at"));
        DateTimeOffset? executionRequestedAt = reader.IsDBNull(reader.GetOrdinal("execution_requested_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("execution_requested_at"));
        DateTimeOffset? completedAt = reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at"));
        var requestStatus = cancelledAt.HasValue && (!submittedAt.HasValue || cancelledAt.Value >= submittedAt.Value)
            ? "cancelled"
            : submittedAt.HasValue
                ? "submitted"
                : "draft";
        var executionStatus = completedAt.HasValue
            ? "journal_entry_posted"
            : executionRequestedAt.HasValue
                ? "execution_requested"
                : root.TryGetProperty("ExecutionStatus", out var payloadExecutionStatus)
                    ? payloadExecutionStatus.GetString() ?? "not_started"
                    : "not_started";
        var requestedAdjustmentAmountTx = GetOptionalDecimal(root, "RequestedAdjustmentAmountTx")
            ?? GetOptionalDecimal(root, "OpenAmountTx")
            ?? 0m;
        var requestedAdjustmentAmountBase = GetOptionalDecimal(root, "RequestedAdjustmentAmountBase")
            ?? GetOptionalDecimal(root, "OpenAmountBase")
            ?? requestedAdjustmentAmountTx;
        var requiresApproval = root.TryGetProperty("RequiresApproval", out var requiresApprovalProperty) &&
            requiresApprovalProperty.ValueKind == JsonValueKind.True;
        var approvalStatus = ResolveApprovalStatus(root, requiresApproval, approvedAt, rejectedAt);

        return new OpenItemAdjustmentRequestRecord(
            root.GetProperty("RequestId").GetGuid(),
            root.GetProperty("OpenItemId").GetGuid(),
            root.GetProperty("OpenItemType").GetString() ?? openItemType,
            companyId,
            root.GetProperty("AdjustmentType").GetString() ?? "unknown",
            DateOnly.Parse(root.GetProperty("AdjustmentDate").GetString() ?? string.Empty),
            requestedAdjustmentAmountTx,
            requestedAdjustmentAmountBase,
            requiresApproval,
            approvalStatus,
            reader.IsDBNull(reader.GetOrdinal("approved_actor_type")) ? null : reader.GetString(reader.GetOrdinal("approved_actor_type")),
            reader.IsDBNull(reader.GetOrdinal("approved_actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("approved_actor_id")),
            approvedAt,
            reader.IsDBNull(reader.GetOrdinal("rejected_actor_type")) ? null : reader.GetString(reader.GetOrdinal("rejected_actor_type")),
            reader.IsDBNull(reader.GetOrdinal("rejected_actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("rejected_actor_id")),
            rejectedAt,
            requestStatus,
            executionStatus,
            reader.GetString(reader.GetOrdinal("requested_actor_type")),
            reader.IsDBNull(reader.GetOrdinal("requested_actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("requested_actor_id")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("requested_at")),
            reader.IsDBNull(reader.GetOrdinal("submitted_actor_type")) ? null : reader.GetString(reader.GetOrdinal("submitted_actor_type")),
            reader.IsDBNull(reader.GetOrdinal("submitted_actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("submitted_actor_id")),
            submittedAt,
            reader.IsDBNull(reader.GetOrdinal("cancelled_actor_type")) ? null : reader.GetString(reader.GetOrdinal("cancelled_actor_type")),
            reader.IsDBNull(reader.GetOrdinal("cancelled_actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("cancelled_actor_id")),
            cancelledAt,
            root.TryGetProperty("Reason", out var reasonProperty) && reasonProperty.ValueKind == JsonValueKind.String
                ? reasonProperty.GetString()
                : null);
    }

    private static async Task AppendAdjustmentRequestTransitionAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        UserId? actorId,
        string action,
        object payload,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              @company_id,
              @actor_type,
              @actor_id,
              @entity_type,
              @entity_id,
              @action,
              @payload::jsonb
            );
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_type", actorId.HasValue ? "user" : "system");
        command.Parameters.AddWithValue("actor_id", actorId.HasValue ? actorId.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_type", "open_item_adjustment_request");
        command.Parameters.AddWithValue("entity_id", requestId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OpenItemAdjustmentRequestTransitionResult? ValidateAdjustmentApprovalAuthority(
        OpenItemAdjustmentRequestRecord request,
        UserId? actorId,
        string transitionCode,
        string openItemLabel)
    {
        if (!actorId.HasValue)
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                transitionCode,
                "blocked_actor_required",
                $"A user actor is required to {transitionCode} a governed {openItemLabel} open item adjustment request.");
        }

        if (request.RequestedByActorId == actorId)
        {
            return new OpenItemAdjustmentRequestTransitionResult(
                request,
                transitionCode,
                "blocked_self_approval",
                $"The requester cannot {transitionCode} their own governed {openItemLabel} open item adjustment request.");
        }

        return null;
    }

    private static OpenItemAdjustmentRequestReadiness BuildAdjustmentRequestReadiness(
        OpenItemAdjustmentRequestRecord request,
        OpenItemAdjustmentPreview preview,
        DateOnly asOfDate)
    {
        if (request.RequestStatus != "submitted")
        {
            return new OpenItemAdjustmentRequestReadiness(
                request,
                asOfDate,
                false,
                false,
                false,
                "posting_engine_adjustment_required",
                "blocked_by_request_status",
                false,
                "Governed AP open item adjustment execution requires a submitted request.");
        }

        if (!preview.IsAvailable)
        {
            return new OpenItemAdjustmentRequestReadiness(
                request,
                asOfDate,
                true,
                false,
                false,
                "posting_engine_adjustment_required",
                preview.AvailabilityMode,
                false,
                preview.Reason);
        }

        if (request.RequiresApproval && request.ApprovalStatus != "approved")
        {
            var reason = request.ApprovalStatus == "rejected"
                ? "Governed AP adjustment was rejected. Cancel this request and create a new request if review should restart."
                : $"Governed AP adjustment requires approval before execution. Current approval status: {request.ApprovalStatus}.";

            return new OpenItemAdjustmentRequestReadiness(
                request,
                asOfDate,
                true,
                true,
                false,
                "posting_engine_adjustment_required",
                "blocked_by_approval_required",
                false,
                reason);
        }

        return new OpenItemAdjustmentRequestReadiness(
            request,
            asOfDate,
            true,
            true,
            true,
            "posting_engine_adjustment",
            "available_for_execution",
            true,
            "Governed AP request and open-item truth are ready for Posting Engine-backed adjustment execution.");
    }

    private static OpenItemAdjustmentExecutionPlan BuildAdjustmentExecutionPlan(
        OpenItemAdjustmentRequestReadiness readiness)
    {
        var requestSubmitted = readiness.Request.RequestStatus == "submitted";
        var openItemReady = readiness.OpenItemReady;
        var approvalReady = !readiness.Request.RequiresApproval || readiness.Request.ApprovalStatus == "approved";
        var executionBlockedReason = readiness.PostingExecutionReady
            ? "Posting Engine-backed adjustment execution is ready."
            : readiness.Reason;

        return new OpenItemAdjustmentExecutionPlan(
            readiness.Request,
            readiness.AsOfDate,
            readiness.ExecutionMode,
            readiness.IsAvailable && readiness.PostingExecutionReady,
            readiness.IsAvailable && readiness.PostingExecutionReady ? "ready" : requestSubmitted && openItemReady ? "blocked" : "waiting",
            executionBlockedReason,
            new[]
            {
                new OpenItemAdjustmentExecutionPlanStep(
                    1,
                    "request_submitted",
                    "Submit governed adjustment request",
                    requestSubmitted ? "ready" : "blocked",
                    requestSubmitted
                        ? "The governed adjustment request is submitted."
                        : "The governed adjustment request must be submitted first."),
                new OpenItemAdjustmentExecutionPlanStep(
                    2,
                    "current_open_item_truth_check",
                    "Re-check current open-item truth",
                    requestSubmitted && openItemReady ? "ready" : "blocked",
                    openItemReady
                        ? "The current AP open item is still eligible for adjustment review."
                        : readiness.Reason),
                new OpenItemAdjustmentExecutionPlanStep(
                    3,
                    "approval_gate",
                    "Approve governed adjustment request",
                    requestSubmitted && openItemReady && approvalReady ? "ready" : "blocked",
                    approvalReady
                        ? "Approval is satisfied for this AP adjustment request."
                        : readiness.Reason),
                new OpenItemAdjustmentExecutionPlanStep(
                    4,
                    "build_adjustment_source_document",
                    "Build governed adjustment source document",
                    readiness.IsAvailable ? "ready" : "blocked",
                    readiness.IsAvailable
                        ? "A governed AP open item adjustment source document can be built from the submitted request."
                        : readiness.Reason),
                new OpenItemAdjustmentExecutionPlanStep(
                    5,
                    "post_adjustment_journal_entry",
                    "Post adjustment through the Posting Engine",
                    readiness.PostingExecutionReady ? "ready" : "blocked",
                    readiness.PostingExecutionReady
                        ? "Formal accounting will be created by the Posting Engine."
                        : "Formal accounting must wait for the governed adjustment source document and Posting Engine integration."),
                new OpenItemAdjustmentExecutionPlanStep(
                    6,
                    "close_or_reduce_open_item",
                    "Close or reduce AP open-item balance",
                    readiness.PostingExecutionReady ? "ready" : "blocked",
                    "Open-item truth changes only after Posting Engine-backed accounting execution succeeds."),
                new OpenItemAdjustmentExecutionPlanStep(
                    7,
                    "append_completion_audit",
                    "Append governed completion audit",
                    readiness.PostingExecutionReady ? "ready" : "blocked",
                    "Completion audit references the final adjustment source and journal entry.")
            });
    }

    private static async Task<AdjustmentAccountPolicyResult> EvaluateAdjustmentAccountPolicyAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid accountId,
        string openItemType,
        string adjustmentType,
        CancellationToken cancellationToken)
    {
        if (!await IsStructurallyAllowedAdjustmentAccountAsync(scope, companyId, accountId, cancellationToken))
        {
            return new AdjustmentAccountPolicyResult(
                false,
                "The AP adjustment offset account is not an active governed adjustment account in the active company context.");
        }

        if (!await AdjustmentAccountMappingTableExistsAsync(scope, cancellationToken))
        {
            return new AdjustmentAccountPolicyResult(true, "No formal AP adjustment account mapping table exists yet; structural account guard was applied.");
        }

        var normalizedAdjustmentType = NormalizeAdjustmentType(adjustmentType);
        if (normalizedAdjustmentType is null)
        {
            return new AdjustmentAccountPolicyResult(false, "The AP adjustment type is not valid for account-mapping policy.");
        }

        var hasActiveMapping = await HasActiveAdjustmentAccountMappingAsync(
            scope,
            companyId,
            openItemType,
            normalizedAdjustmentType,
            cancellationToken);
        if (!hasActiveMapping)
        {
            return new AdjustmentAccountPolicyResult(true, "No active AP adjustment account mapping exists; structural account guard was applied.");
        }

        var isMappedAccount = await IsMappedAdjustmentAccountAsync(
            scope,
            companyId,
            accountId,
            openItemType,
            normalizedAdjustmentType,
            cancellationToken);

        return isMappedAccount
            ? new AdjustmentAccountPolicyResult(true, "The AP adjustment offset account matches active company/book adjustment account policy.")
            : new AdjustmentAccountPolicyResult(
                false,
                "The AP adjustment offset account is not mapped by active company/book adjustment account policy.");
    }

    private static async Task<bool> IsStructurallyAllowedAdjustmentAccountAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select 1
            from accounts
            where company_id = @company_id
              and id = @account_id
              and is_active = true
              and allow_manual_posting = true
              and root_type in ('revenue', 'cost_of_sales', 'expense')
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("account_id", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> AdjustmentAccountMappingTableExistsAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            "select to_regclass('public.open_item_adjustment_account_mappings') is not null;");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> HasActiveAdjustmentAccountMappingAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string openItemType,
        string adjustmentType,
        CancellationToken cancellationToken)
    {
        var companyBooksTableExists = await CompanyBooksTableExistsAsync(scope, cancellationToken);
        await using var command = scope.CreateCommand(
            companyBooksTableExists
                ?
            """
            with primary_book as (
              select id
              from company_books
              where company_id = @company_id
                and is_active = true
                and is_primary = true
              limit 1
            )
            select 1
            from open_item_adjustment_account_mappings m
            where m.company_id = @company_id
              and lower(m.open_item_type) = @open_item_type
              and lower(m.adjustment_type) = @adjustment_type
              and m.is_active = true
              and (m.book_id is null or m.book_id = (select id from primary_book))
            limit 1;
            """
                :
            """
            select 1
            from open_item_adjustment_account_mappings m
            where m.company_id = @company_id
              and lower(m.open_item_type) = @open_item_type
              and lower(m.adjustment_type) = @adjustment_type
              and m.is_active = true
              and m.book_id is null
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("open_item_type", openItemType);
        command.Parameters.AddWithValue("adjustment_type", adjustmentType);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsMappedAdjustmentAccountAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid accountId,
        string openItemType,
        string adjustmentType,
        CancellationToken cancellationToken)
    {
        var companyBooksTableExists = await CompanyBooksTableExistsAsync(scope, cancellationToken);
        await using var command = scope.CreateCommand(
            companyBooksTableExists
                ?
            """
            with primary_book as (
              select id
              from company_books
              where company_id = @company_id
                and is_active = true
                and is_primary = true
              limit 1
            )
            select 1
            from open_item_adjustment_account_mappings m
            where m.company_id = @company_id
              and lower(m.open_item_type) = @open_item_type
              and lower(m.adjustment_type) = @adjustment_type
              and m.adjustment_account_id = @account_id
              and m.is_active = true
              and (m.book_id is null or m.book_id = (select id from primary_book))
            limit 1;
            """
                :
            """
            select 1
            from open_item_adjustment_account_mappings m
            where m.company_id = @company_id
              and lower(m.open_item_type) = @open_item_type
              and lower(m.adjustment_type) = @adjustment_type
              and m.adjustment_account_id = @account_id
              and m.is_active = true
              and m.book_id is null
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("open_item_type", openItemType);
        command.Parameters.AddWithValue("adjustment_type", adjustmentType);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> CompanyBooksTableExistsAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            "select to_regclass('public.company_books') is not null;");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static OpenItemAdjustmentDocument BuildAdjustmentDocument(
        OpenItemAdjustmentRequestRecord request,
        OpenItemAdjustmentSnapshot snapshot,
        Guid controlAccountId,
        Guid adjustmentAccountId,
        DateOnly asOfDate)
    {
        var suffix = BitConverter.ToUInt32(request.RequestId.ToByteArray(), 0) % 100_000_000;
        var entityNumber = new EntityNumber($"EN{asOfDate.Year}{suffix:00000000}");
        var displayNumber = new DocumentNumber($"AP-ADJ-{request.RequestId:N}"[..19]);

        return new OpenItemAdjustmentDocument(
            request.RequestId,
            snapshot.CompanyId,
            entityNumber,
            displayNumber,
            "ap_open_item_adjustment",
            "draft",
            request.AdjustmentDate,
            new CurrencyCode(snapshot.DocumentCurrencyCode),
            new CurrencyCode(snapshot.BaseCurrencyCode),
            request.AdjustmentType,
            [
                new OpenItemAdjustmentDocumentLine(
                    1,
                    snapshot.OpenItemId,
                    snapshot.OpenItemType,
                    snapshot.BalanceSide,
                    controlAccountId,
                    adjustmentAccountId,
                    snapshot.PartyId,
                    $"AP {request.AdjustmentType} for {snapshot.SourceDocumentDisplayNumber}",
                    request.RequestedAdjustmentAmountTx,
                    request.RequestedAdjustmentAmountBase)
            ],
            request.Reason);
    }

    private sealed record OpenItemAdjustmentSnapshot(
        Guid OpenItemId,
        string OpenItemType,
        CompanyId CompanyId,
        string PartyRole,
        Guid PartyId,
        string SourceType,
        Guid SourceDocumentId,
        string SourceDocumentDisplayNumber,
        string SourceDocumentStatus,
        DateOnly DocumentDate,
        DateOnly? DueDate,
        string DocumentCurrencyCode,
        string BaseCurrencyCode,
        string BalanceSide,
        string Status,
        decimal OpenAmountTx,
        decimal OpenAmountBase,
        int ApplicationCount)
    {
        public OpenItemAdjustmentPreview ToPreview(
            string adjustmentType,
            DateOnly adjustmentDate,
            decimal requestedAdjustmentAmountTx,
            decimal requestedAdjustmentAmountBase,
            decimal remainingAmountTx,
            decimal remainingAmountBase,
            bool requiresApproval,
            string approvalStatus,
            string approvalReason,
            string availabilityMode,
            bool isAvailable,
            string reason) =>
            new(
                OpenItemId,
                OpenItemType,
                CompanyId,
                PartyRole,
                PartyId,
                SourceType,
                SourceDocumentId,
                SourceDocumentDisplayNumber,
                SourceDocumentStatus,
                DocumentDate,
                DueDate,
                DocumentCurrencyCode,
                BaseCurrencyCode,
                BalanceSide,
                Status,
                OpenAmountTx,
                OpenAmountBase,
                requestedAdjustmentAmountTx,
                requestedAdjustmentAmountBase,
                remainingAmountTx,
                remainingAmountBase,
                ApplicationCount,
                adjustmentType,
                adjustmentDate,
                requiresApproval,
                approvalStatus,
                approvalReason,
                "request_open_item_adjustment",
                "Request Governed Open Item Adjustment",
                availabilityMode,
                isAvailable,
                "request_recording_only",
                reason);
    }

    private sealed record OpenItemAdjustmentRequestedAmounts(
        decimal AmountTx,
        decimal AmountBase,
        decimal RemainingTx,
        decimal RemainingBase,
        bool RequiresApproval,
        string ApprovalStatus,
        string ApprovalReason);

    private sealed record AdjustmentAccountPolicyResult(
        bool Allowed,
        string Message);
}
