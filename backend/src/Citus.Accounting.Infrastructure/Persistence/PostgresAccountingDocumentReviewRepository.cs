using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using System.Text.Json;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresAccountingDocumentReviewRepository : IAccountingDocumentReviewRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresAccountingDocumentReviewRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<AccountingDocumentReview?> GetSourceDocumentAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var normalizedSourceType = sourceType.Trim().ToLowerInvariant();

        return normalizedSourceType switch
        {
            "manual_journal" => await GetManualJournalDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            "invoice" => await GetReceivableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: normalizedSourceType,
                headerTable: "invoices",
                lineTable: "invoice_lines",
                displayNumberColumn: "invoice_number",
                dateColumn: "invoice_date",
                lineAccountColumn: "revenue_account_id",
                taxAccountColumn: "payable_account_id",
                counterpartyIdColumn: "customer_id",
                lineForeignKeyColumn: "invoice_id",
                counterpartyRole: "customer",
                controlRole: "accounts_receivable",
                includeQuantity: true,
                cancellationToken),
            "credit_note" => await GetReceivableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: normalizedSourceType,
                headerTable: "credit_notes",
                lineTable: "credit_note_lines",
                displayNumberColumn: "credit_note_number",
                dateColumn: "credit_note_date",
                lineAccountColumn: "revenue_account_id",
                taxAccountColumn: "payable_account_id",
                counterpartyIdColumn: "customer_id",
                lineForeignKeyColumn: "credit_note_id",
                counterpartyRole: "customer",
                controlRole: "accounts_receivable",
                includeQuantity: true,
                cancellationToken),
            "bill" => await GetPayableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: normalizedSourceType,
                headerTable: "bills",
                lineTable: "bill_lines",
                displayNumberColumn: "bill_number",
                dateColumn: "bill_date",
                lineAccountColumn: "expense_account_id",
                taxAccountColumn: "recoverable_account_id",
                counterpartyIdColumn: "vendor_id",
                lineForeignKeyColumn: "bill_id",
                counterpartyRole: "vendor",
                controlRole: "accounts_payable",
                cancellationToken),
            "vendor_credit" => await GetPayableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: normalizedSourceType,
                headerTable: "vendor_credits",
                lineTable: "vendor_credit_lines",
                displayNumberColumn: "vendor_credit_number",
                dateColumn: "vendor_credit_date",
                lineAccountColumn: "expense_account_id",
                taxAccountColumn: "recoverable_account_id",
                counterpartyIdColumn: "vendor_id",
                lineForeignKeyColumn: "vendor_credit_id",
                counterpartyRole: "vendor",
                controlRole: "accounts_payable",
                cancellationToken),
            "receive_payment" => await GetReceivePaymentDocumentAsync(
                scope,
                companyId,
                documentId,
                normalizedSourceType,
                cancellationToken),
            "credit_application" => await GetCreditApplicationDocumentAsync(
                scope,
                companyId,
                documentId,
                normalizedSourceType,
                cancellationToken),
            "pay_bill" => await GetPayBillDocumentAsync(
                scope,
                companyId,
                documentId,
                normalizedSourceType,
                cancellationToken),
            "vendor_credit_application" => await GetVendorCreditApplicationDocumentAsync(
                scope,
                companyId,
                documentId,
                normalizedSourceType,
                cancellationToken),
            _ => null
        };
    }

    public async Task<IReadOnlyList<AccountingSourceDocumentListItem>> ListSourceDocumentsAsync(
        CompanyId companyId,
        string? sourceType,
        string? counterpartyRole,
        Guid? counterpartyId,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType);
        var normalizedCounterpartyRole = NormalizeCounterpartyRole(counterpartyRole);
        var effectiveLimit = limit <= 0 ? 50 : Math.Min(limit, 200);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            with source_documents as (
              select
                'invoice'::text as source_type,
                i.id,
                i.company_id,
                i.entity_number,
                i.invoice_number as display_number,
                i.status,
                i.invoice_date as document_date,
                i.due_date,
                'customer'::text as counterparty_role,
                i.customer_id as counterparty_id,
                c.display_name as counterparty_display_name,
                i.document_currency_code,
                i.base_currency_code,
                i.total_amount
              from invoices i
              inner join customers c
                on c.company_id = i.company_id
               and c.id = i.customer_id
              where i.company_id = @company_id

              union all

              select
                'credit_note'::text as source_type,
                cnote.id,
                cnote.company_id,
                cnote.entity_number,
                cnote.credit_note_number as display_number,
                cnote.status,
                cnote.credit_note_date as document_date,
                cnote.due_date,
                'customer'::text as counterparty_role,
                cnote.customer_id as counterparty_id,
                c.display_name as counterparty_display_name,
                cnote.document_currency_code,
                cnote.base_currency_code,
                cnote.total_amount
              from credit_notes cnote
              inner join customers c
                on c.company_id = cnote.company_id
               and c.id = cnote.customer_id
              where cnote.company_id = @company_id

              union all

              select
                'bill'::text as source_type,
                b.id,
                b.company_id,
                b.entity_number,
                b.bill_number as display_number,
                b.status,
                b.bill_date as document_date,
                b.due_date,
                'vendor'::text as counterparty_role,
                b.vendor_id as counterparty_id,
                v.display_name as counterparty_display_name,
                b.document_currency_code,
                b.base_currency_code,
                b.total_amount
              from bills b
              inner join vendors v
                on v.company_id = b.company_id
               and v.id = b.vendor_id
              where b.company_id = @company_id

              union all

              select
                'vendor_credit'::text as source_type,
                vc.id,
                vc.company_id,
                vc.entity_number,
                vc.vendor_credit_number as display_number,
                vc.status,
                vc.vendor_credit_date as document_date,
                vc.due_date,
                'vendor'::text as counterparty_role,
                vc.vendor_id as counterparty_id,
                v.display_name as counterparty_display_name,
                vc.document_currency_code,
                vc.base_currency_code,
                vc.total_amount
              from vendor_credits vc
              inner join vendors v
                on v.company_id = vc.company_id
               and v.id = vc.vendor_id
              where vc.company_id = @company_id
            )
            select
              sd.source_type,
              sd.id,
              sd.entity_number,
              sd.display_number,
              sd.status,
              sd.document_date,
              sd.due_date,
              sd.counterparty_role,
              sd.counterparty_id,
              sd.counterparty_display_name,
              sd.document_currency_code,
              sd.base_currency_code,
              sd.total_amount,
              je.id as journal_entry_id,
              je.display_number as journal_entry_display_number,
              je.status as journal_entry_status,
              je.posted_at as journal_entry_posted_at,
              je.voided_at as journal_entry_voided_at,
              je.reversed_at as journal_entry_reversed_at
            from source_documents sd
            left join lateral (
              select
                entry.id,
                entry.display_number,
                entry.status,
                entry.posted_at,
                entry.voided_at,
                entry.reversed_at
              from journal_entries entry
              where entry.company_id = @company_id
                and entry.source_type = sd.source_type
                and entry.source_id = sd.id
              order by entry.posted_at desc nulls last, entry.created_at desc
              limit 1
            ) je on true
            where (@source_type is null or sd.source_type = @source_type)
              and (@counterparty_role is null or sd.counterparty_role = @counterparty_role)
              and (@counterparty_id is null or sd.counterparty_id = @counterparty_id)
            order by sd.document_date desc, sd.display_number asc
            limit @limit;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", (object?)normalizedSourceType ?? DBNull.Value);
        command.Parameters.AddWithValue("counterparty_role", (object?)normalizedCounterpartyRole ?? DBNull.Value);
        command.Parameters.AddWithValue("counterparty_id", (object?)counterpartyId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", effectiveLimit);

        var items = new List<AccountingSourceDocumentListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AccountingSourceDocumentListItem(
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.GetGuid(reader.GetOrdinal("id")),
                companyId,
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                reader.GetString(reader.GetOrdinal("counterparty_role")),
                reader.IsDBNull(reader.GetOrdinal("counterparty_id")) ? null : reader.GetGuid(reader.GetOrdinal("counterparty_id")),
                reader.IsDBNull(reader.GetOrdinal("counterparty_display_name")) ? null : reader.GetString(reader.GetOrdinal("counterparty_display_name")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("total_amount")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_id")) ? null : reader.GetGuid(reader.GetOrdinal("journal_entry_id")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_display_number")) ? null : reader.GetString(reader.GetOrdinal("journal_entry_display_number")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_status")) ? null : reader.GetString(reader.GetOrdinal("journal_entry_status")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("journal_entry_posted_at")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_voided_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("journal_entry_voided_at")),
                reader.IsDBNull(reader.GetOrdinal("journal_entry_reversed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("journal_entry_reversed_at"))));
        }

        return items;
    }

    public async Task<IReadOnlyList<AccountingDocumentSubledgerReverseBlocker>> ListSubledgerReverseBlockersAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();
        var (openItemTableName, targetOpenItemType) = normalizedSourceType switch
        {
            "invoice" or "credit_note" => ("ar_open_items", "ar_open_item"),
            "bill" or "vendor_credit" => ("ap_open_items", "ap_open_item"),
            _ => (null, null)
        };

        if (openItemTableName is null || targetOpenItemType is null)
        {
            return Array.Empty<AccountingDocumentSubledgerReverseBlocker>();
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            $"""
            select
              sa.id as settlement_application_id,
              sa.application_type,
              sa.source_type as settlement_source_type,
              sa.source_id as settlement_source_id,
              coalesce(
                rp.payment_number,
                ca.application_number,
                pb.payment_number,
                vca.application_number,
                sa.source_id::text) as settlement_source_display_number,
              coalesce(
                rp.payment_date,
                ca.application_date,
                pb.payment_date,
                vca.application_date) as settlement_source_document_date,
              sa.target_open_item_type,
              sa.target_open_item_id,
              oi.source_type as target_source_type,
              oi.source_id as target_source_id,
              coalesce(
                i.invoice_number,
                cn.credit_note_number,
                b.bill_number,
                vc.vendor_credit_number,
                oi.source_id::text) as target_source_display_number,
              sa.applied_amount_tx,
              sa.applied_amount_base,
              sa.settlement_fx_rate,
              sa.realized_fx_amount,
              sa.created_at as applied_at,
              rr.request_id as reverse_request_id,
              case
                when rr.request_id is null then 'not_requested'
                when cancelled.created_at is not null
                 and (submitted.created_at is null or cancelled.created_at >= submitted.created_at)
                  then 'cancelled'
                when submitted.created_at is not null then 'submitted'
                else 'draft'
              end as reverse_request_status,
              case
                when completed.created_at is not null then 'journal_entry_reversed'
                when execution_requested.created_at is not null then 'execution_requested'
                else 'not_requested'
              end as reverse_execution_status,
              rr.requested_at as reverse_requested_at
            from {openItemTableName} oi
            inner join settlement_applications sa
              on sa.company_id = oi.company_id
             and sa.target_open_item_type = @target_open_item_type
             and sa.target_open_item_id = oi.id
            left join receive_payments rp
              on sa.source_type = 'receive_payment'
             and rp.company_id = sa.company_id
             and rp.id = sa.source_id
            left join credit_applications ca
              on sa.source_type = 'credit_application'
             and ca.company_id = sa.company_id
             and ca.id = sa.source_id
            left join pay_bills pb
              on sa.source_type = 'pay_bill'
             and pb.company_id = sa.company_id
             and pb.id = sa.source_id
            left join vendor_credit_applications vca
              on sa.source_type = 'vendor_credit_application'
             and vca.company_id = sa.company_id
             and vca.id = sa.source_id
            left join invoices i
              on oi.source_type = 'invoice'
             and i.company_id = oi.company_id
             and i.id = oi.source_id
            left join credit_notes cn
              on oi.source_type = 'credit_note'
             and cn.company_id = oi.company_id
             and cn.id = oi.source_id
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            left join lateral (
              select
                case
                  when al.payload ? 'RequestId' then (al.payload ->> 'RequestId')::uuid
                  else al.id
                end as request_id,
                al.created_at as requested_at
              from audit_logs al
              where al.company_id = sa.company_id
                and al.entity_type = 'source_document_reverse_request'
                and al.action = 'reverse_requested'
                and al.payload ->> 'SourceType' = sa.source_type
                and (
                  al.payload ->> 'DocumentId' = sa.source_id::text
                  or al.entity_id = sa.source_id::text
                )
              order by al.created_at desc, al.id desc
              limit 1
            ) rr on true
            left join lateral (
              select al.created_at
              from audit_logs al
              where al.company_id = sa.company_id
                and al.entity_type = 'source_document_reverse_request'
                and al.entity_id = rr.request_id::text
                and al.action = 'reverse_request_submitted'
              order by al.created_at desc, al.id desc
              limit 1
            ) submitted on rr.request_id is not null
            left join lateral (
              select al.created_at
              from audit_logs al
              where al.company_id = sa.company_id
                and al.entity_type = 'source_document_reverse_request'
                and al.entity_id = rr.request_id::text
                and al.action = 'reverse_request_cancelled'
              order by al.created_at desc, al.id desc
              limit 1
            ) cancelled on rr.request_id is not null
            left join lateral (
              select al.created_at
              from audit_logs al
              where al.company_id = sa.company_id
                and al.entity_type = 'source_document_reverse_request'
                and al.entity_id = rr.request_id::text
                and al.action = 'reverse_execution_requested'
              order by al.created_at desc, al.id desc
              limit 1
            ) execution_requested on rr.request_id is not null
            left join lateral (
              select al.created_at
              from audit_logs al
              where al.company_id = sa.company_id
                and al.entity_type = 'source_document_reverse_request'
                and al.entity_id = rr.request_id::text
                and al.action = 'reverse_execution_completed'
              order by al.created_at desc, al.id desc
              limit 1
            ) completed on rr.request_id is not null
            where oi.company_id = @company_id
              and oi.source_type = @source_type
              and oi.source_id = @document_id
            order by sa.created_at asc, sa.id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", normalizedSourceType);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("target_open_item_type", targetOpenItemType);

        var blockers = new List<AccountingDocumentSubledgerReverseBlocker>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            blockers.Add(new AccountingDocumentSubledgerReverseBlocker(
                reader.GetGuid(reader.GetOrdinal("settlement_application_id")),
                reader.GetString(reader.GetOrdinal("application_type")),
                reader.GetString(reader.GetOrdinal("settlement_source_type")),
                reader.GetGuid(reader.GetOrdinal("settlement_source_id")),
                reader.GetString(reader.GetOrdinal("settlement_source_display_number")),
                reader.IsDBNull(reader.GetOrdinal("settlement_source_document_date"))
                    ? null
                    : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("settlement_source_document_date")),
                reader.GetString(reader.GetOrdinal("target_open_item_type")),
                reader.GetGuid(reader.GetOrdinal("target_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_source_type")),
                reader.GetGuid(reader.GetOrdinal("target_source_id")),
                reader.GetString(reader.GetOrdinal("target_source_display_number")),
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                reader.GetDecimal(reader.GetOrdinal("applied_amount_base")),
                reader.IsDBNull(reader.GetOrdinal("settlement_fx_rate")) ? null : reader.GetDecimal(reader.GetOrdinal("settlement_fx_rate")),
                reader.IsDBNull(reader.GetOrdinal("realized_fx_amount")) ? null : reader.GetDecimal(reader.GetOrdinal("realized_fx_amount")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("applied_at")),
                reader.IsDBNull(reader.GetOrdinal("reverse_request_id")) ? null : reader.GetGuid(reader.GetOrdinal("reverse_request_id")),
                reader.GetString(reader.GetOrdinal("reverse_request_status")),
                reader.GetString(reader.GetOrdinal("reverse_execution_status")),
                reader.IsDBNull(reader.GetOrdinal("reverse_requested_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reverse_requested_at"))));
        }

        return blockers;
    }

    public async Task<IReadOnlyList<AccountingDocumentSettlementApplicationReversal>> ListSettlementApplicationReversalsAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select
              al.id,
              al.actor_type,
              al.actor_id,
              al.created_at,
              al.payload
            from audit_logs al
            where al.company_id = @company_id
              and al.entity_type = 'settlement_application_reversal'
              and al.action = 'settlement_application_reversed'
              and al.payload ->> 'SourceType' = @source_type
              and al.payload ->> 'SourceId' = @document_id
            order by al.created_at asc, al.id asc;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", normalizedSourceType);
        command.Parameters.AddWithValue("document_id", documentId.ToString("D"));

        var reversals = new List<AccountingDocumentSettlementApplicationReversal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var payloadDocument = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
            var payload = payloadDocument.RootElement;

            reversals.Add(new AccountingDocumentSettlementApplicationReversal(
                reader.GetGuid(reader.GetOrdinal("id")),
                GetRequiredGuid(payload, "RequestId"),
                GetRequiredGuid(payload, "SettlementApplicationId"),
                GetRequiredString(payload, "ApplicationType"),
                GetRequiredString(payload, "SourceType"),
                GetRequiredGuid(payload, "SourceId"),
                GetRequiredString(payload, "TargetOpenItemType"),
                GetRequiredGuid(payload, "TargetOpenItemId"),
                GetRequiredDecimal(payload, "AppliedAmountTx"),
                GetRequiredDecimal(payload, "AppliedAmountBase"),
                GetOptionalDecimal(payload, "SettlementFxRate"),
                GetOptionalDecimal(payload, "RealizedFxAmount"),
                GetRequiredDateTimeOffset(payload, "OriginalApplicationCreatedAt"),
                GetOptionalGuid(payload, "OriginalApplicationCreatedByUserId"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.GetString(reader.GetOrdinal("actor_type")),
                reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
                GetRequiredString(payload, "ReversalMode")));
        }

        return reversals;
    }

    public async Task<AccountingDocumentLifecyclePreview?> GetLifecyclePreviewAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var review = await GetSourceDocumentAsync(
            companyId,
            sourceType,
            documentId,
            cancellationToken);

        return review is null
            ? null
            : new AccountingDocumentLifecyclePreview(
                review.SourceType,
                review.Id,
                review.CompanyId,
                review.EntityNumber,
                review.DisplayNumber,
                review.Status,
                review.JournalEntryId,
                review.JournalEntryDisplayNumber,
                review.JournalEntryStatus,
                review.JournalEntryPostedAt,
                review.JournalEntryVoidedAt,
                review.JournalEntryReversedAt,
                review.LifecycleMode,
                review.CanEditDraft,
                review.CanPostDraft,
                review.LifecycleReason,
                review.LifecycleActions);
    }

    public async Task<AccountingDocumentLifecycleActionPreview?> GetLifecycleActionPreviewAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        string actionCode,
        CancellationToken cancellationToken)
    {
        var normalizedActionCode = NormalizeLifecycleActionCode(actionCode)
            ?? throw new ArgumentException(
                $"Lifecycle action '{actionCode}' is not supported.",
                nameof(actionCode));

        var preview = await GetLifecyclePreviewAsync(
            companyId,
            sourceType,
            documentId,
            cancellationToken);

        if (preview is null)
        {
            return null;
        }

        var action = preview.LifecycleActions.First(
            candidate => string.Equals(candidate.ActionCode, normalizedActionCode, StringComparison.Ordinal));

        return new AccountingDocumentLifecycleActionPreview(
            preview.SourceType,
            preview.Id,
            preview.CompanyId,
            preview.EntityNumber,
            preview.DisplayNumber,
            preview.Status,
            preview.JournalEntryId,
            preview.JournalEntryDisplayNumber,
            preview.JournalEntryStatus,
            preview.JournalEntryPostedAt,
            preview.JournalEntryVoidedAt,
            preview.JournalEntryReversedAt,
            preview.LifecycleMode,
            preview.CanEditDraft,
            preview.CanPostDraft,
            preview.LifecycleReason,
            action.ActionCode,
            action.ActionLabel,
            action.AvailabilityMode,
            action.IsAvailable,
            action.Reason);
    }

    public async Task<AccountingDocumentLifecycleCommandAttempt?> AttemptVoidAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var preview = await GetLifecycleActionPreviewAsync(
            companyId,
            sourceType,
            documentId,
            "void_document",
            cancellationToken);

        if (preview is null)
        {
            return null;
        }

        var (commandAccepted, executed, outcomeCode, message) = preview switch
        {
            { IsAvailable: true } => (
                true,
                false,
                "ready_for_implementation",
                "Source-owned void lifecycle preview passed, but the write-side execution flow is not implemented yet."),
            { AvailabilityMode: "not_implemented" } => (
                false,
                false,
                "not_implemented",
                preview.Reason),
            _ => (
                false,
                false,
                "blocked",
                preview.Reason)
        };

        return new AccountingDocumentLifecycleCommandAttempt(
            preview.SourceType,
            preview.Id,
            preview.CompanyId,
            preview.EntityNumber,
            preview.DisplayNumber,
            preview.Status,
            preview.JournalEntryId,
            preview.JournalEntryDisplayNumber,
            preview.JournalEntryStatus,
            preview.LifecycleMode,
            preview.ActionCode,
            preview.ActionLabel,
            preview.AvailabilityMode,
            "skeleton_only",
            commandAccepted,
            executed,
            null,
            false,
            outcomeCode,
            message);
    }

    public async Task<AccountingDocumentLifecycleCommandAttempt?> AttemptReverseAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        var preview = await GetLifecycleActionPreviewAsync(
            companyId,
            sourceType,
            documentId,
            "reverse_document",
            cancellationToken);

        if (preview is null)
        {
            return null;
        }

        if (!preview.IsAvailable && !string.Equals(preview.AvailabilityMode, "not_implemented", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleCommandAttempt(
                preview.SourceType,
                preview.Id,
                preview.CompanyId,
                preview.EntityNumber,
                preview.DisplayNumber,
                preview.Status,
                preview.JournalEntryId,
                preview.JournalEntryDisplayNumber,
                preview.JournalEntryStatus,
                preview.LifecycleMode,
                preview.ActionCode,
                preview.ActionLabel,
                preview.AvailabilityMode,
                "request_recording",
                false,
                false,
                null,
                false,
                "blocked",
                preview.Reason);
        }

        var latestRequest = await GetLatestReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            cancellationToken);

        if (latestRequest is not null &&
            latestRequest.RequestStatus is "draft" or "submitted")
        {
            return new AccountingDocumentLifecycleCommandAttempt(
                preview.SourceType,
                preview.Id,
                preview.CompanyId,
                preview.EntityNumber,
                preview.DisplayNumber,
                preview.Status,
                preview.JournalEntryId,
                preview.JournalEntryDisplayNumber,
                preview.JournalEntryStatus,
                preview.LifecycleMode,
                preview.ActionCode,
                preview.ActionLabel,
                preview.AvailabilityMode,
                "request_recording",
                false,
                false,
                latestRequest.RequestId,
                false,
                "request_already_open",
                $"A governed reverse request is already {latestRequest.RequestStatus} for this source document.");
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            RequestId = requestId,
            preview.SourceType,
            DocumentId = preview.Id,
            preview.EntityNumber,
            preview.DisplayNumber,
            preview.Status,
            preview.JournalEntryId,
            preview.JournalEntryDisplayNumber,
            preview.JournalEntryStatus,
            preview.LifecycleMode,
            preview.ActionCode,
            preview.ActionLabel,
            preview.AvailabilityMode,
            preview.IsAvailable,
            preview.Reason
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
            command.Parameters.AddWithValue("actor_type", actorId.HasValue ? "user" : "system");
            command.Parameters.AddWithValue("actor_id", actorId.HasValue ? (object)actorId.Value.Value : DBNull.Value);
            command.Parameters.AddWithValue("entity_type", "source_document_reverse_request");
            command.Parameters.AddWithValue("entity_id", requestId.ToString("D"));
            command.Parameters.AddWithValue("action", "reverse_requested");
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new AccountingDocumentLifecycleCommandAttempt(
            preview.SourceType,
            preview.Id,
            preview.CompanyId,
            preview.EntityNumber,
            preview.DisplayNumber,
            preview.Status,
            preview.JournalEntryId,
            preview.JournalEntryDisplayNumber,
            preview.JournalEntryStatus,
            preview.LifecycleMode,
            preview.ActionCode,
            preview.ActionLabel,
            preview.AvailabilityMode,
            "request_recording",
            true,
            false,
            requestId,
            true,
            "request_recorded",
            "Source-owned reverse request was recorded for later governed execution. Historical accounting truth remains unchanged until a real reverse flow is implemented.");
    }

    public async Task<AccountingDocumentLifecycleRequestRecord?> GetReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var requestedEvent = await GetRequestedReverseRequestEventAsync(
            scope,
            companyId,
            normalizedSourceType,
            documentId,
            requestId,
            cancellationToken);

        return requestedEvent is null
            ? null
            : await BuildReverseRequestRecordAsync(
                scope,
                requestedEvent,
                cancellationToken);
    }

    public async Task<AccountingDocumentLifecycleRequestRecord?> GetLatestReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var requestedEvent = await GetLatestRequestedReverseRequestEventAsync(
            scope,
            companyId,
            normalizedSourceType,
            documentId,
            cancellationToken);

        return requestedEvent is null
            ? null
            : await BuildReverseRequestRecordAsync(
                scope,
                requestedEvent,
                cancellationToken);
    }

    public async Task<AccountingDocumentLifecycleRequestTransitionResult?> SubmitReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetReverseRequestAsync(
            companyId,
            normalizedSourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (string.Equals(request.RequestStatus, "submitted", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestTransitionResult(
                request,
                "submit",
                "already_submitted",
                "The governed reverse request has already been submitted.");
        }

        if (string.Equals(request.RequestStatus, "cancelled", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestTransitionResult(
                request,
                "submit",
                "blocked_by_request_status",
                "Cancelled reverse requests cannot be submitted again.");
        }

        await AppendReverseRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "reverse_request_submitted",
            new
            {
                RequestId = requestId,
                request.SourceType,
                request.DocumentId,
                PreviousRequestStatus = request.RequestStatus,
                NextRequestStatus = "submitted"
            },
            cancellationToken);

        var updatedRequest = await GetReverseRequestAsync(
            companyId,
            normalizedSourceType,
            documentId,
            requestId,
            cancellationToken);

        return updatedRequest is null
            ? null
            : new AccountingDocumentLifecycleRequestTransitionResult(
                updatedRequest,
                "submit",
                "submitted",
                "The governed reverse request is now submitted for later execution review.");
    }

    public async Task<AccountingDocumentLifecycleRequestTransitionResult?> CancelReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var request = await GetReverseRequestAsync(
            companyId,
            normalizedSourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (string.Equals(request.RequestStatus, "cancelled", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestTransitionResult(
                request,
                "cancel",
                "already_cancelled",
                "The governed reverse request has already been cancelled.");
        }

        await AppendReverseRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "reverse_request_cancelled",
            new
            {
                RequestId = requestId,
                request.SourceType,
                request.DocumentId,
                PreviousRequestStatus = request.RequestStatus,
                NextRequestStatus = "cancelled"
            },
            cancellationToken);

        var updatedRequest = await GetReverseRequestAsync(
            companyId,
            normalizedSourceType,
            documentId,
            requestId,
            cancellationToken);

        return updatedRequest is null
            ? null
            : new AccountingDocumentLifecycleRequestTransitionResult(
                updatedRequest,
                "cancel",
                "cancelled",
                "The governed reverse request is now cancelled.");
    }

    public async Task<AccountingDocumentLifecycleRequestReadiness?> GetReverseRequestApplyReadinessAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var request = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (!string.Equals(request.RequestStatus, "submitted", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestReadiness(
                request,
                asOfDate,
                false,
                false,
                "request_recording_only",
                "blocked_by_request_status",
                false,
                $"Reverse request must be submitted before apply-readiness can pass. Current status is '{request.RequestStatus}'.");
        }

        var preview = await GetLifecycleActionPreviewAsync(
            companyId,
            sourceType,
            documentId,
            "reverse_document",
            cancellationToken);

        if (preview is null)
        {
            return new AccountingDocumentLifecycleRequestReadiness(
                request,
                asOfDate,
                false,
                false,
                "request_recording_only",
                "document_unavailable",
                false,
                "Source document could not be found in the active company context.");
        }

        if (preview.AvailabilityMode.StartsWith("blocked", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestReadiness(
                request,
                asOfDate,
                false,
                false,
                "request_recording_only",
                preview.AvailabilityMode,
                preview.IsAvailable,
                preview.Reason);
        }

        return new AccountingDocumentLifecycleRequestReadiness(
            request,
            asOfDate,
            true,
            false,
            "request_recording_only",
            preview.AvailabilityMode,
            preview.IsAvailable,
            "Governance checks still allow this reverse request, but true reverse execution is not implemented yet. Historical accounting truth remains unchanged.");
    }

    public async Task<AccountingDocumentLifecycleRequestExecutionResult?> ExecuteReverseRequestAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var request = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (string.Equals(request.ExecutionStatus, "journal_entry_reversed", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                request,
                asOfDate,
                "governed_execution_completed",
                false,
                true,
                true,
                "execution_already_completed",
                "This reverse request has already completed the linked journal-entry reversal.",
                request.CompensationJournalEntryId,
                request.CompensationJournalEntryDisplayNumber,
                request.CompensationSourceType);
        }

        if (string.Equals(request.ExecutionStatus, "execution_requested", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                request,
                asOfDate,
                "governed_execution_skeleton",
                false,
                false,
                false,
                "execution_already_requested",
                "A governed reverse execution request has already been recorded for this reverse request.",
                request.CompensationJournalEntryId,
                request.CompensationJournalEntryDisplayNumber,
                request.CompensationSourceType);
        }

        var readiness = await GetReverseRequestApplyReadinessAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            asOfDate,
            cancellationToken);

        if (readiness is null)
        {
            return null;
        }

        if (!readiness.GovernanceReady)
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                readiness.Request,
                asOfDate,
                "governed_execution_skeleton",
                false,
                false,
                false,
                "blocked",
                readiness.Reason,
                readiness.Request.CompensationJournalEntryId,
                readiness.Request.CompensationJournalEntryDisplayNumber,
                readiness.Request.CompensationSourceType);
        }

        if (!readiness.Request.JournalEntryId.HasValue)
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                readiness.Request,
                asOfDate,
                "governed_execution_skeleton",
                false,
                false,
                false,
                "blocked_by_missing_linked_journal_entry",
                "This source document has no linked journal entry, so governed reverse execution cannot continue.",
                readiness.Request.CompensationJournalEntryId,
                readiness.Request.CompensationJournalEntryDisplayNumber,
                readiness.Request.CompensationSourceType);
        }

        var subledgerBlockReason = await GetReverseExecutionSubledgerBlockReasonAsync(
            companyId,
            readiness.Request.SourceType,
            readiness.Request.DocumentId,
            cancellationToken);

        if (subledgerBlockReason is not null)
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                readiness.Request,
                asOfDate,
                "governed_execution_skeleton",
                false,
                false,
                false,
                "blocked_by_subledger_truth",
                subledgerBlockReason,
                readiness.Request.CompensationJournalEntryId,
                readiness.Request.CompensationJournalEntryDisplayNumber,
                readiness.Request.CompensationSourceType);
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await AppendReverseRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "reverse_execution_requested",
            new
            {
                RequestId = requestId,
                sourceType = readiness.Request.SourceType,
                DocumentId = readiness.Request.DocumentId,
                AsOfDate = asOfDate.ToString("yyyy-MM-dd"),
                GovernanceReady = readiness.GovernanceReady,
                ApplyReady = readiness.ApplyReady,
                PreviousExecutionStatus = request.ExecutionStatus,
                NextExecutionStatus = "execution_requested"
            },
            cancellationToken);

        var updatedRequest = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        return updatedRequest is null
            ? null
            : new AccountingDocumentLifecycleRequestExecutionResult(
                updatedRequest,
                asOfDate,
                "governed_execution_skeleton",
                true,
                false,
                true,
                "execution_request_recorded",
                "Governed reverse execution request was recorded. Linked journal-entry reversal may now be orchestrated as a separate step.",
                updatedRequest.CompensationJournalEntryId,
                updatedRequest.CompensationJournalEntryDisplayNumber,
                updatedRequest.CompensationSourceType);
    }

    public async Task<AccountingDocumentLifecycleRequestExecutionResult?> CompleteReverseRequestExecutionAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        Guid compensationJournalEntryId,
        string compensationJournalEntryDisplayNumber,
        string compensationSourceType,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken)
    {
        var request = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (string.Equals(request.ExecutionStatus, "journal_entry_reversed", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                request,
                DateOnly.FromDateTime(executedAt.UtcDateTime),
                "governed_execution_completed",
                false,
                true,
                true,
                "execution_already_completed",
                "This reverse request already recorded completion of the linked journal-entry reversal.",
                request.CompensationJournalEntryId,
                request.CompensationJournalEntryDisplayNumber,
                request.CompensationSourceType);
        }

        if (!string.Equals(request.ExecutionStatus, "execution_requested", StringComparison.Ordinal))
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                request,
                DateOnly.FromDateTime(executedAt.UtcDateTime),
                "governed_execution_completed",
                false,
                false,
                false,
                "blocked_by_execution_state",
                $"Reverse execution completion requires an 'execution_requested' state. Current state is '{request.ExecutionStatus}'.",
                request.CompensationJournalEntryId,
                request.CompensationJournalEntryDisplayNumber,
                request.CompensationSourceType);
        }

        var subledgerBlockReason = await GetReverseExecutionSubledgerBlockReasonAsync(
            companyId,
            request.SourceType,
            request.DocumentId,
            cancellationToken);

        if (subledgerBlockReason is not null)
        {
            return new AccountingDocumentLifecycleRequestExecutionResult(
                request,
                DateOnly.FromDateTime(executedAt.UtcDateTime),
                "governed_execution_completed",
                false,
                false,
                false,
                "blocked_by_subledger_truth",
                subledgerBlockReason,
                request.CompensationJournalEntryId,
                request.CompensationJournalEntryDisplayNumber,
                request.CompensationSourceType);
        }

        await using var scope = await PostgresCommandScope.CreateTransactionalAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var unapplyResult = await UnapplySettlementApplicationsAsync(
            scope,
            companyId,
            request.SourceType,
            request.DocumentId,
            requestId,
            actorId,
            cancellationToken);

        await VoidSourceOpenItemsAsync(
            scope,
            companyId,
            request.SourceType,
            request.DocumentId,
            cancellationToken);

        await MarkSourceDocumentReversedAsync(
            scope,
            companyId,
            request.SourceType,
            request.DocumentId,
            cancellationToken);

        await AppendReverseRequestTransitionAsync(
            scope,
            companyId,
            requestId,
            actorId,
            "reverse_execution_completed",
            new
            {
                RequestId = requestId,
                SourceType = request.SourceType,
                DocumentId = request.DocumentId,
                PreviousExecutionStatus = request.ExecutionStatus,
                NextExecutionStatus = "journal_entry_reversed",
                CompensationJournalEntryId = compensationJournalEntryId,
                CompensationJournalEntryDisplayNumber = compensationJournalEntryDisplayNumber,
                CompensationSourceType = compensationSourceType,
                SourceStatusAfter = "reversed",
                SettlementApplicationsUnapplied = unapplyResult.ApplicationCount,
                SettlementApplicationsUnappliedAmountTx = unapplyResult.TotalAppliedAmountTx,
                SettlementApplicationsUnappliedAmountBase = unapplyResult.TotalAppliedAmountBase,
                ExecutedAt = executedAt
            },
            cancellationToken);

        await scope.CommitAsync(cancellationToken);

        var updatedRequest = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        return updatedRequest is null
            ? null
            : new AccountingDocumentLifecycleRequestExecutionResult(
                updatedRequest,
                DateOnly.FromDateTime(executedAt.UtcDateTime),
                "governed_execution_completed",
                true,
                true,
                true,
                "journal_entry_reversed",
                "Linked journal-entry reversal completed, the source document was marked reversed, and the governed reverse request now records the compensation journal entry.",
                updatedRequest.CompensationJournalEntryId,
                updatedRequest.CompensationJournalEntryDisplayNumber,
                updatedRequest.CompensationSourceType);
    }

    public async Task<AccountingDocumentLifecycleExecutionPlan?> GetReverseRequestExecutionPlanAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var request = await GetReverseRequestAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            cancellationToken);

        if (request is null)
        {
            return null;
        }

        var steps = new List<AccountingDocumentLifecycleExecutionPlanStep>();

        var requestStatusReady = string.Equals(request.RequestStatus, "submitted", StringComparison.Ordinal);
        steps.Add(new AccountingDocumentLifecycleExecutionPlanStep(
            1,
            "request_status_gate",
            "Request must be submitted",
            requestStatusReady ? "ready" : "blocked",
            requestStatusReady
                ? "The governed reverse request has been submitted."
                : $"Reverse request must be submitted before execution. Current status is '{request.RequestStatus}'."));

        var readiness = await GetReverseRequestApplyReadinessAsync(
            companyId,
            sourceType,
            documentId,
            requestId,
            asOfDate,
            cancellationToken);

        var governanceReady = readiness is not null && readiness.GovernanceReady;
        steps.Add(new AccountingDocumentLifecycleExecutionPlanStep(
            2,
            "governance_readiness_gate",
            "Governance readiness must pass",
            governanceReady ? "ready" : "blocked",
            readiness?.Reason ?? "Reverse request could not be re-evaluated in the active company context."));

        var subledgerBlockReason = await GetReverseExecutionSubledgerBlockReasonAsync(
            companyId,
            request.SourceType,
            request.DocumentId,
            cancellationToken);
        var subledgerReady = string.IsNullOrWhiteSpace(subledgerBlockReason);
        steps.Add(new AccountingDocumentLifecycleExecutionPlanStep(
            3,
            "subledger_reversal_gate",
            "Subledger reversal orchestration must be available",
            subledgerReady ? "ready" : "blocked",
            subledgerReady
                ? "No AR/AP settlement or application trail blocks the next orchestration step."
                : subledgerBlockReason!));

        var journalStepStatus = request.JournalEntryId.HasValue && subledgerReady ? "ready" : "blocked";
        steps.Add(new AccountingDocumentLifecycleExecutionPlanStep(
            4,
            "reverse_linked_journal_entry",
            "Reverse linked journal entry through GL lifecycle workflow",
            journalStepStatus,
            request.JournalEntryId is null
                ? "Source document has no linked journal entry to reverse."
                : subledgerReady
                    ? "Linked journal entry can now be reversed through the GL lifecycle workflow."
                    : "Subledger-backed documents cannot reverse the linked journal entry before subledger orchestration exists."));

        var sourceStatusStepStatus = request.JournalEntryId.HasValue && subledgerReady ? "ready" : "blocked";
        steps.Add(new AccountingDocumentLifecycleExecutionPlanStep(
            5,
            "mark_source_document_reversed",
            "Mark source document reversed after accounting and subledger orchestration completes",
            sourceStatusStepStatus,
            sourceStatusStepStatus == "ready"
                ? "Source status can be marked reversed after linked JE reversal completes and no surviving subledger truth exists."
                : "Source status must remain historical-only until earlier orchestration steps are implemented."));

        var canExecute = steps.All(step => step.StepStatus is "ready" or "planned");

        return new AccountingDocumentLifecycleExecutionPlan(
            request,
            asOfDate,
            "governed_execution_orchestration_plan",
            canExecute,
            canExecute ? "planned" : "blocked",
            canExecute
                ? "The minimal orchestration order is defined, but final execution still depends on implementing the planned steps."
                : steps.First(step => step.StepStatus == "blocked").Reason,
            steps);
    }

    private async Task<string?> GetReverseExecutionSubledgerBlockReasonAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();

        return normalizedSourceType switch
        {
            "invoice" or "credit_note" => await HasSettlementApplicationsForArSourceAsync(companyId, normalizedSourceType, documentId, cancellationToken)
                ? "This source document has AR settlement/application trail. Governed reverse execution cannot continue until settlement-application reversal orchestration exists."
                : null,
            "bill" or "vendor_credit" => await HasSettlementApplicationsForApSourceAsync(companyId, normalizedSourceType, documentId, cancellationToken)
                ? "This source document has AP settlement/application trail. Governed reverse execution cannot continue until settlement-application reversal orchestration exists."
                : null,
            "receive_payment" or "credit_application" or "pay_bill" or "vendor_credit_application" => null,
            _ => null
        };
    }

    private static async Task<SettlementUnapplyResult> UnapplySettlementApplicationsAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        Guid requestId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();
        if (normalizedSourceType is not ("receive_payment" or "credit_application" or "pay_bill" or "vendor_credit_application"))
        {
            return new SettlementUnapplyResult(0, 0m, 0m);
        }

        var applications = new List<SettlementApplicationSnapshot>();
        await using (var command = scope.CreateCommand(
                         """
                         select
                           id,
                           application_type,
                           source_type,
                           source_id,
                           target_open_item_type,
                           target_open_item_id,
                           applied_amount_tx,
                           applied_amount_base,
                           settlement_fx_rate,
                           realized_fx_amount,
                           created_at,
                           created_by_user_id
                         from settlement_applications
                         where company_id = @company_id
                           and source_type = @source_type
                           and source_id = @document_id
                         order by created_at desc, id desc;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("source_type", normalizedSourceType);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applications.Add(new SettlementApplicationSnapshot(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("application_type")),
                    reader.GetString(reader.GetOrdinal("source_type")),
                    reader.GetGuid(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("target_open_item_type")),
                    reader.GetGuid(reader.GetOrdinal("target_open_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                    reader.GetDecimal(reader.GetOrdinal("applied_amount_base")),
                    reader.IsDBNull(reader.GetOrdinal("settlement_fx_rate")) ? null : reader.GetDecimal(reader.GetOrdinal("settlement_fx_rate")),
                    reader.IsDBNull(reader.GetOrdinal("realized_fx_amount")) ? null : reader.GetDecimal(reader.GetOrdinal("realized_fx_amount")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                    reader.IsDBNull(reader.GetOrdinal("created_by_user_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id")))));
            }
        }

        if (applications.Count == 0)
        {
            return await LoadSettlementUnapplyRecoveryAsync(
                scope,
                companyId,
                normalizedSourceType,
                documentId,
                requestId,
                cancellationToken);
        }

        foreach (var application in applications)
        {
            var targetTable = application.TargetOpenItemType switch
            {
                "ar_open_item" => "ar_open_items",
                "ap_open_item" => "ap_open_items",
                _ => throw new InvalidOperationException(
                    $"Unsupported settlement application target type '{application.TargetOpenItemType}'.")
            };

            await using var updateCommand = scope.CreateCommand(
                $"""
                update {targetTable}
                set open_amount_tx = least(original_amount_tx, open_amount_tx + @applied_amount_tx),
                    open_amount_base = least(original_amount_base, open_amount_base + @applied_amount_base),
                    status = case
                        when least(original_amount_tx, open_amount_tx + @applied_amount_tx) <= 0 then 'closed'
                        when least(original_amount_tx, open_amount_tx + @applied_amount_tx) >= original_amount_tx then 'open'
                        else 'partially_applied'
                    end,
                    updated_at = now()
                where company_id = @company_id
                  and id = @target_open_item_id
                  and status <> 'voided';
                """);
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("target_open_item_id", application.TargetOpenItemId);
            updateCommand.Parameters.AddWithValue("applied_amount_tx", application.AppliedAmountTx);
            updateCommand.Parameters.AddWithValue("applied_amount_base", application.AppliedAmountBase);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            await AppendSettlementApplicationReversalAuditAsync(
                scope,
                companyId,
                requestId,
                actorId,
                application,
                cancellationToken);

            await using var deleteCommand = scope.CreateCommand(
                """
                delete from settlement_applications
                where company_id = @company_id
                  and id = @application_id;
                """);
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("application_id", application.Id);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return new SettlementUnapplyResult(
            applications.Count,
            applications.Sum(static application => application.AppliedAmountTx),
            applications.Sum(static application => application.AppliedAmountBase));
    }

    private static async Task<SettlementUnapplyResult> LoadSettlementUnapplyRecoveryAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string normalizedSourceType,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              count(*)::integer as application_count,
              coalesce(sum((payload ->> 'AppliedAmountTx')::numeric), 0) as total_applied_amount_tx,
              coalesce(sum((payload ->> 'AppliedAmountBase')::numeric), 0) as total_applied_amount_base
            from audit_logs
            where company_id = @company_id
              and entity_type = 'settlement_application_reversal'
              and action = 'settlement_application_reversed'
              and payload ->> 'RequestId' = @request_id
              and payload ->> 'SourceType' = @source_type
              and payload ->> 'SourceId' = @document_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("request_id", requestId.ToString("D"));
        command.Parameters.AddWithValue("source_type", normalizedSourceType);
        command.Parameters.AddWithValue("document_id", documentId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SettlementUnapplyResult(0, 0m, 0m);
        }

        return new SettlementUnapplyResult(
            reader.GetInt32(reader.GetOrdinal("application_count")),
            reader.GetDecimal(reader.GetOrdinal("total_applied_amount_tx")),
            reader.GetDecimal(reader.GetOrdinal("total_applied_amount_base")));
    }

    private static async Task AppendSettlementApplicationReversalAuditAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        UserId? actorId,
        SettlementApplicationSnapshot application,
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
              'settlement_application_reversal',
              @entity_id,
              'settlement_application_reversed',
              @payload::jsonb
            );
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_type", actorId.HasValue ? "user" : "system");
        command.Parameters.AddWithValue("actor_id", actorId.HasValue ? (object)actorId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", application.Id.ToString("D"));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
        {
            RequestId = requestId,
            SettlementApplicationId = application.Id,
            ApplicationType = application.ApplicationType,
            SourceType = application.SourceType,
            SourceId = application.SourceId,
            TargetOpenItemType = application.TargetOpenItemType,
            TargetOpenItemId = application.TargetOpenItemId,
            AppliedAmountTx = application.AppliedAmountTx,
            AppliedAmountBase = application.AppliedAmountBase,
            SettlementFxRate = application.SettlementFxRate,
            RealizedFxAmount = application.RealizedFxAmount,
            OriginalApplicationCreatedAt = application.CreatedAt,
            OriginalApplicationCreatedByUserId = application.CreatedByUserId,
            ReversalMode = "governed_reverse_execution_unapply"
        }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> HasSettlementApplicationsForArSourceAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select 1
            from ar_open_items oi
            inner join settlement_applications sa
              on sa.company_id = oi.company_id
             and sa.target_open_item_type = 'ar_open_item'
             and sa.target_open_item_id = oi.id
            where oi.company_id = @company_id
              and oi.source_type = @source_type
              and oi.source_id = @document_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("document_id", documentId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<bool> HasSettlementApplicationsForApSourceAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select 1
            from ap_open_items oi
            inner join settlement_applications sa
              on sa.company_id = oi.company_id
             and sa.target_open_item_type = 'ap_open_item'
             and sa.target_open_item_id = oi.id
            where oi.company_id = @company_id
              and oi.source_type = @source_type
              and oi.source_id = @document_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("document_id", documentId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task VoidSourceOpenItemsAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType) ?? sourceType.Trim().ToLowerInvariant();
        var tableName = normalizedSourceType switch
        {
            "invoice" or "credit_note" => "ar_open_items",
            "bill" or "vendor_credit" => "ap_open_items",
            _ => null
        };

        if (tableName is null)
        {
            return;
        }

        await using var command = scope.CreateCommand(
            $"""
            update {tableName} oi
            set status = 'voided',
                open_amount_tx = 0,
                open_amount_base = 0,
                updated_at = now()
            where oi.company_id = @company_id
              and oi.source_type = @source_type
              and oi.source_id = @document_id
              and oi.status <> 'voided'
              and not exists (
                select 1
                from settlement_applications sa
                where sa.company_id = oi.company_id
                  and sa.target_open_item_type = @target_open_item_type
                  and sa.target_open_item_id = oi.id
              );
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", normalizedSourceType);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("target_open_item_type", tableName == "ar_open_items" ? "ar_open_item" : "ap_open_item");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkSourceDocumentReversedAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var tableName = NormalizeSourceType(sourceType) switch
        {
            "invoice" => "invoices",
            "credit_note" => "credit_notes",
            "bill" => "bills",
            "vendor_credit" => "vendor_credits",
            "receive_payment" => "receive_payments",
            "credit_application" => "credit_applications",
            "pay_bill" => "pay_bills",
            "vendor_credit_application" => "vendor_credit_applications",
            _ => throw new InvalidOperationException(
                $"Source document status reversal is not supported for source type '{sourceType}'.")
        };

        await using var command = scope.CreateCommand(
            $"""
            update {tableName}
            set status = 'reversed',
                updated_at = case
                    when status = 'reversed' then updated_at
                    else now()
                end
            where company_id = @company_id
              and id = @document_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("The source document could not be marked reversed in the active company context.");
        }
    }

    private async Task<ReverseRequestRequestedEvent?> GetRequestedReverseRequestEventAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string normalizedSourceType,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              al.id,
              al.company_id,
              al.entity_id,
              al.actor_type,
              al.actor_id,
              al.created_at,
              al.payload
            from audit_logs al
            where al.company_id = @company_id
              and al.entity_type = 'source_document_reverse_request'
              and al.action = 'reverse_requested'
              and al.id = @request_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("request_id", requestId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var requestedEvent = ReadReverseRequestRequestedEvent(reader);
        return string.Equals(requestedEvent.SourceType, normalizedSourceType, StringComparison.Ordinal) &&
               requestedEvent.DocumentId == documentId
            ? requestedEvent
            : null;
    }

    private async Task<ReverseRequestRequestedEvent?> GetLatestRequestedReverseRequestEventAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string normalizedSourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              al.id,
              al.company_id,
              al.entity_id,
              al.actor_type,
              al.actor_id,
              al.created_at,
              al.payload
            from audit_logs al
            where al.company_id = @company_id
              and al.entity_type = 'source_document_reverse_request'
              and al.action = 'reverse_requested'
              and al.payload ->> 'SourceType' = @source_type
              and (
                al.payload ->> 'DocumentId' = @document_id_text
                or al.entity_id = @document_id::text
              )
            order by al.created_at desc, al.id desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", normalizedSourceType);
        command.Parameters.AddWithValue("document_id_text", documentId.ToString("D"));
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadReverseRequestRequestedEvent(reader);
    }

    private async Task<AccountingDocumentLifecycleRequestRecord> BuildReverseRequestRecordAsync(
        PostgresCommandScope scope,
        ReverseRequestRequestedEvent requestedEvent,
        CancellationToken cancellationToken)
    {
        var submitted = await GetLatestReverseRequestTransitionAsync(
            scope,
            requestedEvent.CompanyId,
            requestedEvent.RequestId,
            "reverse_request_submitted",
            cancellationToken);

        var cancelled = await GetLatestReverseRequestTransitionAsync(
            scope,
            requestedEvent.CompanyId,
            requestedEvent.RequestId,
            "reverse_request_cancelled",
            cancellationToken);

        var requestStatus = "draft";
        if (cancelled is not null &&
            (submitted is null || cancelled.OccurredAt >= submitted.OccurredAt))
        {
            requestStatus = "cancelled";
        }
        else if (submitted is not null)
        {
            requestStatus = "submitted";
        }

        var executionRequested = await GetLatestReverseRequestTransitionAsync(
            scope,
            requestedEvent.CompanyId,
            requestedEvent.RequestId,
            "reverse_execution_requested",
            cancellationToken);

        var executionCompleted = await GetLatestReverseRequestCompletionAsync(
            scope,
            requestedEvent.CompanyId,
            requestedEvent.RequestId,
            cancellationToken);

        var executionStatus = executionCompleted is not null
            ? "journal_entry_reversed"
            : executionRequested is null
                ? "not_requested"
                : "execution_requested";

        return new AccountingDocumentLifecycleRequestRecord(
            requestedEvent.RequestId,
            requestedEvent.CompanyId,
            requestedEvent.SourceType,
            requestedEvent.DocumentId,
            requestedEvent.EntityNumber,
            requestedEvent.DisplayNumber,
            requestedEvent.DocumentStatus,
            requestedEvent.JournalEntryId,
            requestedEvent.JournalEntryDisplayNumber,
            requestedEvent.JournalEntryStatus,
            requestedEvent.LifecycleMode,
            requestedEvent.ActionCode,
            requestedEvent.ActionLabel,
            requestedEvent.AvailabilityMode,
            requestedEvent.IsAvailable,
            requestedEvent.Reason,
            requestStatus,
            requestedEvent.ActorType,
            requestedEvent.ActorId,
            requestedEvent.RequestedAt,
            submitted?.ActorType,
            submitted?.ActorId,
            submitted?.OccurredAt,
            cancelled?.ActorType,
            cancelled?.ActorId,
            cancelled?.OccurredAt,
            executionStatus,
            executionRequested?.ActorType,
            executionRequested?.ActorId,
            executionRequested?.OccurredAt,
            executionCompleted?.ActorType,
            executionCompleted?.ActorId,
            executionCompleted?.OccurredAt,
            executionCompleted?.CompensationJournalEntryId,
            executionCompleted?.CompensationJournalEntryDisplayNumber,
            executionCompleted?.CompensationSourceType);
    }

    private async Task<ReverseRequestTransitionEvent?> GetLatestReverseRequestTransitionAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        string action,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              al.actor_type,
              al.actor_id,
              al.created_at
            from audit_logs al
            where al.company_id = @company_id
              and al.entity_type = 'source_document_reverse_request'
              and al.entity_id = @request_id::text
              and al.action = @action
            order by al.created_at desc, al.id desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("action", action);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReverseRequestTransitionEvent(
            reader.GetString(reader.GetOrdinal("actor_type")),
            reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));
    }

    private async Task<ReverseRequestCompletionEvent?> GetLatestReverseRequestCompletionAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              al.actor_type,
              al.actor_id,
              al.created_at,
              al.payload
            from audit_logs al
            where al.company_id = @company_id
              and al.entity_type = 'source_document_reverse_request'
              and al.entity_id = @request_id::text
              and al.action = 'reverse_execution_completed'
            order by al.created_at desc, al.id desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("request_id", requestId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var payloadDocument = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
        var payload = payloadDocument.RootElement;

        return new ReverseRequestCompletionEvent(
            reader.GetString(reader.GetOrdinal("actor_type")),
            reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            GetRequiredGuid(payload, "CompensationJournalEntryId"),
            GetRequiredString(payload, "CompensationJournalEntryDisplayNumber"),
            GetRequiredString(payload, "CompensationSourceType"));
    }

    private static ReverseRequestRequestedEvent ReadReverseRequestRequestedEvent(Npgsql.NpgsqlDataReader reader)
    {
        using var payloadDocument = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
        var payload = payloadDocument.RootElement;
        var requestId = TryGetOptionalGuid(payload, "RequestId") ?? reader.GetGuid(reader.GetOrdinal("id"));
        var documentId = TryGetOptionalGuid(payload, "DocumentId") ?? Guid.Parse(reader.GetString(reader.GetOrdinal("entity_id")));

        return new ReverseRequestRequestedEvent(
            requestId,
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            GetRequiredString(payload, "SourceType"),
            documentId,
            GetRequiredString(payload, "EntityNumber"),
            GetRequiredString(payload, "DisplayNumber"),
            GetRequiredString(payload, "Status"),
            GetOptionalGuid(payload, "JournalEntryId"),
            GetOptionalString(payload, "JournalEntryDisplayNumber"),
            GetOptionalString(payload, "JournalEntryStatus"),
            GetRequiredString(payload, "LifecycleMode"),
            GetRequiredString(payload, "ActionCode"),
            GetRequiredString(payload, "ActionLabel"),
            GetRequiredString(payload, "AvailabilityMode"),
            GetRequiredBoolean(payload, "IsAvailable"),
            GetRequiredString(payload, "Reason"),
            reader.GetString(reader.GetOrdinal("actor_type")),
            reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));
    }

    private static async Task AppendReverseRequestTransitionAsync(
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
        command.Parameters.AddWithValue("actor_id", actorId.HasValue ? (object)actorId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_type", "source_document_reverse_request");
        command.Parameters.AddWithValue("entity_id", requestId.ToString("D"));
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AccountingDocumentReview?> GetReceivableDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        string headerTable,
        string lineTable,
        string displayNumberColumn,
        string dateColumn,
        string lineAccountColumn,
        string taxAccountColumn,
        string counterpartyIdColumn,
        string lineForeignKeyColumn,
        string counterpartyRole,
        string controlRole,
        bool includeQuantity,
        CancellationToken cancellationToken)
    {
        var header = await GetHeaderAsync(
            scope,
            companyId,
            documentId,
            headerTable,
            displayNumberColumn,
            dateColumn,
            counterpartyIdColumn,
            controlRole,
            counterpartyRole,
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var journalEntryLink = await TryGetJournalEntryLinkAsync(
            scope,
            companyId,
            sourceType,
            documentId,
            cancellationToken);
        var lifecycle = BuildLifecycleState(header.Status, journalEntryLink?.Status);

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           l.line_number,
                           l.{lineAccountColumn} as account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           {(includeQuantity ? "l.quantity," : "null::numeric as quantity,")}
                           {(includeQuantity ? "l.unit_price," : "null::numeric as unit_price,")}
                           l.line_amount,
                           l.tax_amount,
                           null::boolean as is_tax_recoverable,
                           tc.{taxAccountColumn} as tax_account_id
                          from {lineTable} l
                          inner join accounts a
                            on a.company_id = l.company_id
                           and a.id = l.{lineAccountColumn}
                          left join tax_codes tc
                          on tc.id = l.tax_code_id
                         and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.{lineForeignKeyColumn} = @document_id
                         order by l.line_number asc;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.IsDBNull(reader.GetOrdinal("quantity")) ? null : reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.IsDBNull(reader.GetOrdinal("unit_price")) ? null : reader.GetDecimal(reader.GetOrdinal("unit_price")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    null,
                    reader.IsDBNull(reader.GetOrdinal("tax_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("tax_account_id")),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }
        }

        var lifecycleActions = BuildLifecycleActions(lifecycle.Mode);

        return new AccountingDocumentReview(
            sourceType,
            header.Id,
            companyId,
            header.EntityNumber,
            header.DisplayNumber,
            header.Status,
            header.DocumentDate,
            header.DueDate,
            counterpartyRole,
            header.CounterpartyId,
            header.ControlAccountId,
            journalEntryLink?.JournalEntryId,
            journalEntryLink?.DisplayNumber,
            journalEntryLink?.Status,
            journalEntryLink?.PostedAt,
            journalEntryLink?.VoidedAt,
            journalEntryLink?.ReversedAt,
            lifecycle.Mode,
            lifecycle.CanEditDraft,
            lifecycle.CanPostDraft,
            lifecycle.Reason,
            lifecycleActions,
            header.TransactionCurrencyCode,
            header.BaseCurrencyCode,
            header.SubtotalAmount,
            header.TaxAmount,
            header.TotalAmount,
            header.Memo,
            lines);
    }

    private static async Task<AccountingDocumentReview?> GetPayableDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        string headerTable,
        string lineTable,
        string displayNumberColumn,
        string dateColumn,
        string lineAccountColumn,
        string taxAccountColumn,
        string counterpartyIdColumn,
        string lineForeignKeyColumn,
        string counterpartyRole,
        string controlRole,
        CancellationToken cancellationToken)
    {
        var header = await GetHeaderAsync(
            scope,
            companyId,
            documentId,
            headerTable,
            displayNumberColumn,
            dateColumn,
            counterpartyIdColumn,
            controlRole,
            counterpartyRole,
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var journalEntryLink = await TryGetJournalEntryLinkAsync(
            scope,
            companyId,
            sourceType,
            documentId,
            cancellationToken);
        var lifecycle = BuildLifecycleState(header.Status, journalEntryLink?.Status);

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           l.line_number,
                           l.{lineAccountColumn} as account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           null::numeric as quantity,
                           null::numeric as unit_price,
                           l.line_amount,
                           l.tax_amount,
                           l.is_tax_recoverable,
                           tc.{taxAccountColumn} as tax_account_id
                          from {lineTable} l
                          inner join accounts a
                            on a.company_id = l.company_id
                           and a.id = l.{lineAccountColumn}
                          left join tax_codes tc
                          on tc.id = l.tax_code_id
                         and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.{lineForeignKeyColumn} = @document_id
                         order by l.line_number asc;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.GetString(reader.GetOrdinal("description")),
                    null,
                    null,
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    reader.GetBoolean(reader.GetOrdinal("is_tax_recoverable")),
                    reader.IsDBNull(reader.GetOrdinal("tax_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("tax_account_id")),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }
        }

        var lifecycleActions = BuildLifecycleActions(lifecycle.Mode);

        return new AccountingDocumentReview(
            sourceType,
            header.Id,
            companyId,
            header.EntityNumber,
            header.DisplayNumber,
            header.Status,
            header.DocumentDate,
            header.DueDate,
            counterpartyRole,
            header.CounterpartyId,
            header.ControlAccountId,
            journalEntryLink?.JournalEntryId,
            journalEntryLink?.DisplayNumber,
            journalEntryLink?.Status,
            journalEntryLink?.PostedAt,
            journalEntryLink?.VoidedAt,
            journalEntryLink?.ReversedAt,
            lifecycle.Mode,
            lifecycle.CanEditDraft,
            lifecycle.CanPostDraft,
            lifecycle.Reason,
            lifecycleActions,
            header.TransactionCurrencyCode,
            header.BaseCurrencyCode,
            header.SubtotalAmount,
            header.TaxAmount,
            header.TotalAmount,
            header.Memo,
            lines);
    }

    private static async Task<AccountingDocumentReview?> GetReceivePaymentDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "receive_payments",
            displayNumberColumn: "payment_number",
            dateColumn: "payment_date",
            counterpartyIdColumn: "customer_id",
            controlRole: "accounts_receivable",
            counterpartyRole: "customer",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadReceivePaymentLinesAsync(scope, companyId, documentId, cancellationToken);
        return await BuildSettlementReviewAsync(scope, sourceType, companyId, header, "customer", lines, cancellationToken);
    }

    private static async Task<AccountingDocumentReview?> GetCreditApplicationDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "credit_applications",
            displayNumberColumn: "application_number",
            dateColumn: "application_date",
            counterpartyIdColumn: "customer_id",
            controlRole: "accounts_receivable",
            counterpartyRole: "customer",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadCreditApplicationLinesAsync(scope, companyId, documentId, cancellationToken);
        return await BuildSettlementReviewAsync(scope, sourceType, companyId, header, "customer", lines, cancellationToken);
    }

    private static async Task<AccountingDocumentReview?> GetPayBillDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "pay_bills",
            displayNumberColumn: "payment_number",
            dateColumn: "payment_date",
            counterpartyIdColumn: "vendor_id",
            controlRole: "accounts_payable",
            counterpartyRole: "vendor",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadPayBillLinesAsync(scope, companyId, documentId, cancellationToken);
        return await BuildSettlementReviewAsync(scope, sourceType, companyId, header, "vendor", lines, cancellationToken);
    }

    private static async Task<AccountingDocumentReview?> GetVendorCreditApplicationDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "vendor_credit_applications",
            displayNumberColumn: "application_number",
            dateColumn: "application_date",
            counterpartyIdColumn: "vendor_id",
            controlRole: "accounts_payable",
            counterpartyRole: "vendor",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadVendorCreditApplicationLinesAsync(scope, companyId, documentId, cancellationToken);
        return await BuildSettlementReviewAsync(scope, sourceType, companyId, header, "vendor", lines, cancellationToken);
    }

    private static async Task<AccountingDocumentReview> BuildSettlementReviewAsync(
        PostgresCommandScope scope,
        string sourceType,
        CompanyId companyId,
        DocumentHeader header,
        string counterpartyRole,
        IReadOnlyList<AccountingDocumentReviewLine> lines,
        CancellationToken cancellationToken)
    {
        var journalEntryLink = await TryGetJournalEntryLinkAsync(
            scope,
            companyId,
            sourceType,
            header.Id,
            cancellationToken);
        var lifecycle = BuildLifecycleState(header.Status, journalEntryLink?.Status);

        var lifecycleActions = BuildLifecycleActions(lifecycle.Mode);

        return new(
            sourceType,
            header.Id,
            companyId,
            header.EntityNumber,
            header.DisplayNumber,
            header.Status,
            header.DocumentDate,
            header.DueDate,
            counterpartyRole,
            header.CounterpartyId,
            header.ControlAccountId,
            journalEntryLink?.JournalEntryId,
            journalEntryLink?.DisplayNumber,
            journalEntryLink?.Status,
            journalEntryLink?.PostedAt,
            journalEntryLink?.VoidedAt,
            journalEntryLink?.ReversedAt,
            lifecycle.Mode,
            lifecycle.CanEditDraft,
            lifecycle.CanPostDraft,
            lifecycle.Reason,
            lifecycleActions,
            header.TransactionCurrencyCode,
            header.BaseCurrencyCode,
            header.TotalAmount,
            0m,
            header.TotalAmount,
            header.Memo,
            lines);
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadReceivePaymentLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.target_ar_open_item_id,
              oi.source_type as target_source_type,
              oi.source_id as target_source_id,
              coalesce(i.invoice_number, cn.credit_note_number, oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from receive_payment_lines l
            inner join ar_open_items oi
              on oi.company_id = l.company_id
             and oi.id = l.target_ar_open_item_id
            left join invoices i
              on oi.source_type = 'invoice'
             and i.company_id = oi.company_id
             and i.id = oi.source_id
            left join credit_notes cn
              on oi.source_type = 'credit_note'
             and cn.company_id = oi.company_id
             and cn.id = oi.source_id
            where l.company_id = @company_id
              and l.receive_payment_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description = $"Applied to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("target_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_source_type")),
                reader.GetGuid(reader.GetOrdinal("target_source_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadCreditApplicationLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.source_credit_ar_open_item_id,
              source_oi.source_type as source_document_type,
              source_oi.source_id as source_document_id,
              coalesce(cn.credit_note_number, source_oi.source_id::text) as source_display_number,
              l.target_invoice_ar_open_item_id,
              target_oi.source_type as target_document_type,
              target_oi.source_id as target_document_id,
              coalesce(i.invoice_number, target_oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from credit_application_lines l
            inner join ar_open_items source_oi
              on source_oi.company_id = l.company_id
             and source_oi.id = l.source_credit_ar_open_item_id
            inner join ar_open_items target_oi
              on target_oi.company_id = l.company_id
             and target_oi.id = l.target_invoice_ar_open_item_id
            left join credit_notes cn
              on source_oi.source_type = 'credit_note'
             and cn.company_id = source_oi.company_id
             and cn.id = source_oi.source_id
            left join invoices i
              on target_oi.source_type = 'invoice'
             and i.company_id = target_oi.company_id
             and i.id = target_oi.source_id
            where l.company_id = @company_id
              and l.credit_application_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description =
                $"Apply {reader.GetString(reader.GetOrdinal("source_display_number"))} to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("source_credit_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("source_document_type")),
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetString(reader.GetOrdinal("source_display_number")),
                reader.GetGuid(reader.GetOrdinal("target_invoice_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_document_type")),
                reader.GetGuid(reader.GetOrdinal("target_document_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadPayBillLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.target_ap_open_item_id,
              oi.source_type as target_source_type,
              oi.source_id as target_source_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from pay_bill_lines l
            inner join ap_open_items oi
              on oi.company_id = l.company_id
             and oi.id = l.target_ap_open_item_id
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where l.company_id = @company_id
              and l.pay_bill_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description = $"Applied to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("target_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_source_type")),
                reader.GetGuid(reader.GetOrdinal("target_source_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadVendorCreditApplicationLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.source_vendor_credit_ap_open_item_id,
              source_oi.source_type as source_document_type,
              source_oi.source_id as source_document_id,
              coalesce(vc.vendor_credit_number, source_oi.source_id::text) as source_display_number,
              l.target_bill_ap_open_item_id,
              target_oi.source_type as target_document_type,
              target_oi.source_id as target_document_id,
              coalesce(b.bill_number, target_oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from vendor_credit_application_lines l
            inner join ap_open_items source_oi
              on source_oi.company_id = l.company_id
             and source_oi.id = l.source_vendor_credit_ap_open_item_id
            inner join ap_open_items target_oi
              on target_oi.company_id = l.company_id
             and target_oi.id = l.target_bill_ap_open_item_id
            left join vendor_credits vc
              on source_oi.source_type = 'vendor_credit'
             and vc.company_id = source_oi.company_id
             and vc.id = source_oi.source_id
            left join bills b
              on target_oi.source_type = 'bill'
             and b.company_id = target_oi.company_id
             and b.id = target_oi.source_id
            where l.company_id = @company_id
              and l.vendor_credit_application_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description =
                $"Apply {reader.GetString(reader.GetOrdinal("source_display_number"))} to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("source_vendor_credit_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("source_document_type")),
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetString(reader.GetOrdinal("source_display_number")),
                reader.GetGuid(reader.GetOrdinal("target_bill_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_document_type")),
                reader.GetGuid(reader.GetOrdinal("target_document_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<AccountingDocumentReview?> GetManualJournalDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly entryDate;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        string? memo;
        decimal totalTxDebit;
        decimal totalTxCredit;

        await using (var command = scope.CreateCommand(
                         """
                         select
                           d.id,
                           d.entity_number,
                           d.display_number,
                           d.status,
                           d.entry_date,
                           d.transaction_currency_code,
                           d.base_currency_code,
                           d.memo,
                           coalesce(sum(l.tx_debit), 0) as total_tx_debit,
                           coalesce(sum(l.tx_credit), 0) as total_tx_credit
                         from manual_journal_documents d
                         left join manual_journal_document_lines l
                           on l.company_id = d.company_id
                          and l.manual_journal_document_id = d.id
                         where d.company_id = @company_id
                           and d.id = @document_id
                         group by
                           d.id,
                           d.entity_number,
                           d.display_number,
                           d.status,
                           d.entry_date,
                           d.transaction_currency_code,
                           d.base_currency_code,
                           d.memo
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            entryDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("entry_date"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
            totalTxDebit = reader.GetDecimal(reader.GetOrdinal("total_tx_debit"));
            totalTxCredit = reader.GetDecimal(reader.GetOrdinal("total_tx_credit"));
        }

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           l.tx_debit,
                           l.tx_credit
                         from manual_journal_document_lines l
                         inner join accounts a
                           on a.company_id = l.company_id
                          and a.id = l.account_id
                         where l.company_id = @company_id
                           and l.manual_journal_document_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var txDebit = reader.GetDecimal(reader.GetOrdinal("tx_debit"));
                var txCredit = reader.GetDecimal(reader.GetOrdinal("tx_credit"));

                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.IsDBNull(reader.GetOrdinal("description")) ? string.Empty : reader.GetString(reader.GetOrdinal("description")),
                    null,
                    null,
                    txDebit != 0m ? txDebit : txCredit,
                    0m,
                    null,
                    null,
                    txDebit,
                    txCredit,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }
        }

        var lifecycleMode = string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase) ? "draft_editable" : "posted_locked";
        var lifecycleActions = BuildLifecycleActions(lifecycleMode);

        return new AccountingDocumentReview(
            "manual_journal",
            id,
            companyId,
            entityNumber,
            displayNumber,
            status,
            entryDate,
            null,
            "journal",
            null,
            null,
            id,
            displayNumber,
            status,
            null,
            null,
            null,
            lifecycleMode,
            string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase),
            string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase),
            string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase)
                ? "Draft manual journals can still be edited or posted."
                : "Posted manual journals are read-only. Future changes must go through governed void or reverse flow.",
            lifecycleActions,
            transactionCurrencyCode,
            baseCurrencyCode,
            totalTxDebit,
            0m,
            totalTxCredit,
            memo,
            lines);
    }

    private static async Task<DocumentHeader?> GetHeaderAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string tableName,
        string displayNumberColumn,
        string dateColumn,
        string counterpartyIdColumn,
        string controlRole,
        string counterpartyRole,
        CancellationToken cancellationToken)
    {
        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly documentDate;
        DateOnly? dueDate;
        Guid counterpartyId;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        decimal subtotalAmount;
        decimal taxAmount;
        decimal totalAmount;
        string? memo;

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           d.id,
                           d.entity_number,
                           d.{displayNumberColumn} as display_number,
                           d.status,
                           d.{dateColumn} as document_date,
                           d.due_date,
                           d.{counterpartyIdColumn} as counterparty_id,
                           d.document_currency_code,
                           d.base_currency_code,
                           d.subtotal_amount,
                           d.tax_amount,
                           d.total_amount,
                           d.memo
                         from {tableName} d
                         where d.company_id = @company_id
                           and d.id = @document_id
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            documentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date"));
            dueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date"));
            counterpartyId = reader.GetGuid(reader.GetOrdinal("counterparty_id"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            subtotalAmount = reader.GetDecimal(reader.GetOrdinal("subtotal_amount"));
            taxAmount = reader.GetDecimal(reader.GetOrdinal("tax_amount"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
        }

        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId,
            controlRole,
            transactionCurrencyCode,
            baseCurrencyCode,
            cancellationToken);

        return new DocumentHeader(
            id,
            entityNumber,
            displayNumber,
            status,
            documentDate,
            dueDate,
            counterpartyRole,
            counterpartyId,
            controlAccountId,
            transactionCurrencyCode,
            baseCurrencyCode,
            subtotalAmount,
            taxAmount,
            totalAmount,
            memo);
    }

    private static async Task<DocumentHeader?> GetSettlementHeaderAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string tableName,
        string displayNumberColumn,
        string dateColumn,
        string counterpartyIdColumn,
        string controlRole,
        string counterpartyRole,
        CancellationToken cancellationToken)
    {
        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly documentDate;
        Guid counterpartyId;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        decimal totalAmount;
        string? memo;

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           d.id,
                           d.entity_number,
                           d.{displayNumberColumn} as display_number,
                           d.status,
                           d.{dateColumn} as document_date,
                           d.{counterpartyIdColumn} as counterparty_id,
                           d.document_currency_code,
                           d.base_currency_code,
                           d.total_amount,
                           d.memo
                         from {tableName} d
                         where d.company_id = @company_id
                           and d.id = @document_id
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            documentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date"));
            counterpartyId = reader.GetGuid(reader.GetOrdinal("counterparty_id"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
        }

        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId,
            controlRole,
            transactionCurrencyCode,
            baseCurrencyCode,
            cancellationToken);

        return new DocumentHeader(
            id,
            entityNumber,
            displayNumber,
            status,
            documentDate,
            null,
            counterpartyRole,
            counterpartyId,
            controlAccountId,
            transactionCurrencyCode,
            baseCurrencyCode,
            totalAmount,
            0m,
            totalAmount,
            memo);
    }

    private sealed record DocumentHeader(
        Guid Id,
        string EntityNumber,
        string DisplayNumber,
        string Status,
        DateOnly DocumentDate,
        DateOnly? DueDate,
        string CounterpartyRole,
        Guid? CounterpartyId,
        Guid? ControlAccountId,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal SubtotalAmount,
        decimal TaxAmount,
        decimal TotalAmount,
        string? Memo);

    private static async Task<JournalEntryLink?> TryGetJournalEntryLinkAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              je.id,
              je.display_number,
              je.status,
              je.posted_at,
              je.voided_at,
              je.reversed_at
            from journal_entries je
            where je.company_id = @company_id
              and je.source_type = @source_type
              and je.source_id = @source_id
            order by coalesce(je.posted_at, je.created_at) desc, je.display_number desc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalEntryLink(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
            reader.IsDBNull(reader.GetOrdinal("voided_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("voided_at")),
            reader.IsDBNull(reader.GetOrdinal("reversed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reversed_at")));
    }

    private sealed record JournalEntryLink(
        Guid JournalEntryId,
        string DisplayNumber,
        string Status,
        DateTimeOffset? PostedAt,
        DateTimeOffset? VoidedAt,
        DateTimeOffset? ReversedAt);

    private static (string Mode, bool CanEditDraft, bool CanPostDraft, string Reason) BuildLifecycleState(
        string documentStatus,
        string? journalEntryStatus)
    {
        if (string.Equals(journalEntryStatus, "voided", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "historical_linked_je_voided",
                false,
                false,
                "This source document is historical-only because its linked journal entry has been voided.");
        }

        if (string.Equals(journalEntryStatus, "reversed", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "historical_linked_je_reversed",
                false,
                false,
                "This source document is historical-only because its linked journal entry has been reversed.");
        }

        if (string.Equals(journalEntryStatus, "posted", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "posted_locked",
                false,
                false,
                "This source document is linked to a posted journal entry and is read-only. Future changes must go through governed void or reverse flow.");
        }

        if (string.Equals(documentStatus, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "draft_editable",
                true,
                true,
                "Draft source documents can still be edited or posted.");
        }

        if (string.Equals(documentStatus, "posted", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "posted_locked",
                false,
                false,
                "Posted source documents are read-only. Future changes must go through governed void or reverse flow.");
        }

        return (
            "historical_locked",
            false,
            false,
            $"Source documents in status '{documentStatus}' are not editable through the draft flow.");
    }

    private static IReadOnlyList<AccountingDocumentLifecycleAction> BuildLifecycleActions(string lifecycleMode) =>
        lifecycleMode switch
        {
            "draft_editable" =>
            [
                new AccountingDocumentLifecycleAction("edit_draft", "Edit Draft", "available_now", true, "Draft source documents can be edited."),
                new AccountingDocumentLifecycleAction("post_draft", "Post Draft", "available_now", true, "Draft source documents can be posted."),
                new AccountingDocumentLifecycleAction("reopen_document", "Reopen", "blocked_by_status", false, "Only posted source documents could ever enter a future reopen flow."),
                new AccountingDocumentLifecycleAction("void_document", "Void", "blocked_by_status", false, "Draft source documents are not voided; they remain editable until posting."),
                new AccountingDocumentLifecycleAction("reverse_document", "Reverse", "blocked_by_status", false, "Draft source documents have no posted accounting effect to reverse.")
            ],
            "posted_locked" =>
            [
                new AccountingDocumentLifecycleAction("edit_draft", "Edit Draft", "blocked_by_status", false, "Posted source documents are no longer editable through the draft flow."),
                new AccountingDocumentLifecycleAction("post_draft", "Post Draft", "blocked_by_status", false, "The source document is already posted."),
                new AccountingDocumentLifecycleAction("reopen_document", "Reopen", "not_implemented", false, "Reopen-after-post requires governed lifecycle rules and is not implemented yet."),
                new AccountingDocumentLifecycleAction("void_document", "Void", "not_implemented", false, "Source-owned void flow is not implemented yet. Current review is read-only."),
                new AccountingDocumentLifecycleAction("reverse_document", "Reverse", "not_implemented", false, "Source-owned reverse flow is not implemented yet. Current review is read-only.")
            ],
            "historical_linked_je_voided" =>
            [
                new AccountingDocumentLifecycleAction("edit_draft", "Edit Draft", "blocked_by_linked_je_lifecycle", false, "The linked journal entry has already been voided."),
                new AccountingDocumentLifecycleAction("post_draft", "Post Draft", "blocked_by_linked_je_lifecycle", false, "The linked journal entry has already been voided."),
                new AccountingDocumentLifecycleAction("reopen_document", "Reopen", "blocked_by_linked_je_lifecycle", false, "The source document is historical-only because its linked journal entry has been voided."),
                new AccountingDocumentLifecycleAction("void_document", "Void", "blocked_by_linked_je_lifecycle", false, "The linked journal entry is already voided."),
                new AccountingDocumentLifecycleAction("reverse_document", "Reverse", "blocked_by_linked_je_lifecycle", false, "The linked journal entry is already voided.")
            ],
            "historical_linked_je_reversed" =>
            [
                new AccountingDocumentLifecycleAction("edit_draft", "Edit Draft", "blocked_by_linked_je_lifecycle", false, "The linked journal entry has already been reversed."),
                new AccountingDocumentLifecycleAction("post_draft", "Post Draft", "blocked_by_linked_je_lifecycle", false, "The linked journal entry has already been reversed."),
                new AccountingDocumentLifecycleAction("reopen_document", "Reopen", "blocked_by_linked_je_lifecycle", false, "The source document is historical-only because its linked journal entry has been reversed."),
                new AccountingDocumentLifecycleAction("void_document", "Void", "blocked_by_linked_je_lifecycle", false, "The linked journal entry is already reversed."),
                new AccountingDocumentLifecycleAction("reverse_document", "Reverse", "blocked_by_linked_je_lifecycle", false, "The linked journal entry is already reversed.")
            ],
            _ =>
            [
                new AccountingDocumentLifecycleAction("edit_draft", "Edit Draft", "blocked", false, "This source document is not editable through the draft flow."),
                new AccountingDocumentLifecycleAction("post_draft", "Post Draft", "blocked", false, "This source document is not postable through the draft flow."),
                new AccountingDocumentLifecycleAction("reopen_document", "Reopen", "blocked", false, "Lifecycle rules are not available for this document state."),
                new AccountingDocumentLifecycleAction("void_document", "Void", "blocked", false, "Lifecycle rules are not available for this document state."),
                new AccountingDocumentLifecycleAction("reverse_document", "Reverse", "blocked", false, "Lifecycle rules are not available for this document state.")
            ]
        };

    private static string? NormalizeLifecycleActionCode(string? actionCode)
    {
        if (string.IsNullOrWhiteSpace(actionCode))
        {
            return null;
        }

        return actionCode.Trim().ToLowerInvariant() switch
        {
            "edit_draft" => "edit_draft",
            "post_draft" => "post_draft",
            "reopen_document" => "reopen_document",
            "void_document" => "void_document",
            "reverse_document" => "reverse_document",
            _ => null
        };
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : throw new InvalidOperationException($"Lifecycle request payload is missing required string '{propertyName}'.");

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Guid GetRequiredGuid(JsonElement element, string propertyName)
    {
        var value = GetRequiredString(element, propertyName);
        return Guid.TryParse(value, out var guid)
            ? guid
            : throw new InvalidOperationException($"Lifecycle request payload contains invalid GUID '{propertyName}'.");
    }

    private static Guid? GetOptionalGuid(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static Guid? TryGetOptionalGuid(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Guid.TryParse(property.GetString(), out var guid) ? guid : null;
    }

    private static decimal GetRequiredDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"Lifecycle request payload is missing required decimal '{propertyName}'.");
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            decimal.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Lifecycle request payload contains invalid decimal '{propertyName}'.");
    }

    private static decimal? GetOptionalDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            decimal.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset GetRequiredDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetRequiredString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var dateTimeOffset)
            ? dateTimeOffset
            : throw new InvalidOperationException($"Lifecycle request payload contains invalid timestamp '{propertyName}'.");
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
        {
            throw new InvalidOperationException($"Lifecycle request payload is missing required boolean '{propertyName}'.");
        }

        return property.GetBoolean();
    }

    private static string? NormalizeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            "receive_payment" => "receive_payment",
            "credit_application" => "credit_application",
            "pay_bill" => "pay_bill",
            "vendor_credit_application" => "vendor_credit_application",
            _ => null
        };
    }

    private static string? NormalizeCounterpartyRole(string? counterpartyRole)
    {
        if (string.IsNullOrWhiteSpace(counterpartyRole))
        {
            return null;
        }

        return counterpartyRole.Trim().ToLowerInvariant() switch
        {
            "customer" => "customer",
            "vendor" => "vendor",
            _ => null
        };
    }

    private sealed record ReverseRequestRequestedEvent(
        Guid RequestId,
        CompanyId CompanyId,
        string SourceType,
        Guid DocumentId,
        string EntityNumber,
        string DisplayNumber,
        string DocumentStatus,
        Guid? JournalEntryId,
        string? JournalEntryDisplayNumber,
        string? JournalEntryStatus,
        string LifecycleMode,
        string ActionCode,
        string ActionLabel,
        string AvailabilityMode,
        bool IsAvailable,
        string Reason,
        string ActorType,
        UserId? ActorId,
        DateTimeOffset RequestedAt);

    private sealed record ReverseRequestTransitionEvent(
        string ActorType,
        UserId? ActorId,
        DateTimeOffset OccurredAt);

    private sealed record ReverseRequestCompletionEvent(
        string ActorType,
        UserId? ActorId,
        DateTimeOffset OccurredAt,
        Guid CompensationJournalEntryId,
        string CompensationJournalEntryDisplayNumber,
        string CompensationSourceType);

    private sealed record SettlementApplicationSnapshot(
        Guid Id,
        string ApplicationType,
        string SourceType,
        Guid SourceId,
        string TargetOpenItemType,
        Guid TargetOpenItemId,
        decimal AppliedAmountTx,
        decimal AppliedAmountBase,
        decimal? SettlementFxRate,
        decimal? RealizedFxAmount,
        DateTimeOffset CreatedAt,
        UserId? CreatedByUserId);

    private sealed record SettlementUnapplyResult(
        int ApplicationCount,
        decimal TotalAppliedAmountTx,
        decimal TotalAppliedAmountBase);
}
