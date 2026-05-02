namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Idempotent schema bootstrap for the seven V1-pending document write
/// flows that shipped without their full repositories. The canonical
/// definitions live in <c>CITUS_POSTGRESQL_MIGRATION_DRAFT.sql</c> at
/// the repo root; this class re-asserts them on app startup so dev /
/// test databases don't have to re-run the full migration script every
/// time the schema gains a new table.
///
/// Five tables ensured here (credit_notes + vendor_credits already
/// existed from earlier work and have their own bootstrap path):
///   • sales_receipts + sales_receipt_lines
///   • refund_receipts + refund_receipt_lines
///   • bank_transfers
///   • bank_deposits + bank_deposit_items
///   • tax_returns
///
/// Order matters: parent tables before children. Each <c>create table
/// if not exists</c> is wrapped in a single SQL batch so a partially-
/// applied bootstrap never leaves dangling FKs. Indexes + triggers use
/// the same idempotent <c>if not exists</c> guard.
///
/// When the matching <c>Postgres*DocumentRepository</c> ships for any
/// table, this bootstrap stays — it remains the single source of truth
/// for "is the schema present?" alongside the canonical migration SQL.
/// </summary>
public sealed class PostgresV1WriteFlowSchemaBootstrap
{
    private readonly PostgresConnectionFactory _connections;
    private int _ensured;

    public PostgresV1WriteFlowSchemaBootstrap(PostgresConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ensured) == 1)
        {
            return;
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            -- Sales Receipts ------------------------------------------------
            create table if not exists sales_receipts (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              receipt_number text not null,
              customer_id uuid not null references customers(id) on delete restrict,
              status text not null default 'draft',
              receipt_date date not null,
              document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              fx_rate_snapshot_id uuid references company_fx_rate_snapshots(id) on delete restrict,
              fx_rate numeric(20,10) not null default 1,
              fx_requested_date date not null,
              fx_effective_date date not null,
              fx_source text not null default 'identity',
              deposit_to_account_id uuid not null references accounts(id) on delete restrict,
              payment_method text not null default 'cash',
              reference_no text,
              subtotal_amount numeric(20,6) not null default 0,
              tax_amount numeric(20,6) not null default 0,
              total_amount numeric(20,6) not null default 0,
              memo text,
              posted_at timestamptz,
              created_by_user_id uuid not null references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint sales_receipts_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
              constraint sales_receipts_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
              constraint sales_receipts_payment_method_chk check (payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
              constraint sales_receipts_fx_rate_positive_chk check (fx_rate > 0),
              constraint sales_receipts_unique_company_receipt_number unique (company_id, receipt_number)
            );

            create table if not exists sales_receipt_lines (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              sales_receipt_id uuid not null references sales_receipts(id) on delete cascade,
              line_number integer not null,
              revenue_account_id uuid not null references accounts(id) on delete restrict,
              description text not null,
              quantity numeric(20,6) not null,
              unit_price numeric(20,6) not null,
              line_amount numeric(20,6) not null,
              tax_code_id uuid references tax_codes(id) on delete set null,
              tax_amount numeric(20,6) not null default 0,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint sales_receipt_lines_quantity_nonnegative_chk check (quantity >= 0),
              constraint sales_receipt_lines_unit_price_nonnegative_chk check (unit_price >= 0),
              constraint sales_receipt_lines_unique_line unique (sales_receipt_id, line_number)
            );

            create index if not exists ix_sales_receipts_company_status on sales_receipts (company_id, status);
            create index if not exists ix_sales_receipts_company_customer on sales_receipts (company_id, customer_id);
            create index if not exists ix_sales_receipt_lines_sales_receipt on sales_receipt_lines (sales_receipt_id, line_number);

            -- Refund Receipts -----------------------------------------------
            create table if not exists refund_receipts (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              refund_number text not null,
              customer_id uuid not null references customers(id) on delete restrict,
              status text not null default 'draft',
              refund_date date not null,
              document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              fx_rate_snapshot_id uuid references company_fx_rate_snapshots(id) on delete restrict,
              fx_rate numeric(20,10) not null default 1,
              fx_requested_date date not null,
              fx_effective_date date not null,
              fx_source text not null default 'identity',
              refund_from_account_id uuid not null references accounts(id) on delete restrict,
              payment_method text not null default 'cash',
              reference_no text,
              reason text,
              subtotal_amount numeric(20,6) not null default 0,
              tax_amount numeric(20,6) not null default 0,
              total_amount numeric(20,6) not null default 0,
              memo text,
              posted_at timestamptz,
              created_by_user_id uuid not null references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint refund_receipts_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
              constraint refund_receipts_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
              constraint refund_receipts_payment_method_chk check (payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
              constraint refund_receipts_fx_rate_positive_chk check (fx_rate > 0),
              constraint refund_receipts_unique_company_refund_number unique (company_id, refund_number)
            );

            create table if not exists refund_receipt_lines (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              refund_receipt_id uuid not null references refund_receipts(id) on delete cascade,
              line_number integer not null,
              revenue_account_id uuid not null references accounts(id) on delete restrict,
              description text not null,
              quantity numeric(20,6) not null,
              unit_price numeric(20,6) not null,
              line_amount numeric(20,6) not null,
              tax_code_id uuid references tax_codes(id) on delete set null,
              tax_amount numeric(20,6) not null default 0,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint refund_receipt_lines_quantity_nonnegative_chk check (quantity >= 0),
              constraint refund_receipt_lines_unit_price_nonnegative_chk check (unit_price >= 0),
              constraint refund_receipt_lines_unique_line unique (refund_receipt_id, line_number)
            );

            create index if not exists ix_refund_receipts_company_status on refund_receipts (company_id, status);
            create index if not exists ix_refund_receipts_company_customer on refund_receipts (company_id, customer_id);
            create index if not exists ix_refund_receipt_lines_refund_receipt on refund_receipt_lines (refund_receipt_id, line_number);

            -- Bank Transfers ------------------------------------------------
            create table if not exists bank_transfers (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              transfer_number text not null,
              status text not null default 'draft',
              transfer_date date not null,
              from_account_id uuid not null references accounts(id) on delete restrict,
              from_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              to_account_id uuid not null references accounts(id) on delete restrict,
              to_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              amount numeric(20,6) not null,
              fx_rate numeric(20,10),
              reference_no text,
              memo text,
              posted_at timestamptz,
              created_by_user_id uuid not null references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint bank_transfers_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
              constraint bank_transfers_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
              constraint bank_transfers_amount_positive_chk check (amount > 0),
              constraint bank_transfers_distinct_accounts_chk check (from_account_id <> to_account_id),
              constraint bank_transfers_fx_rate_positive_chk check (fx_rate is null or fx_rate > 0),
              constraint bank_transfers_fx_rate_polarity_chk check (
                (from_currency_code = to_currency_code and fx_rate is null) or
                (from_currency_code <> to_currency_code and fx_rate is not null)
              ),
              constraint bank_transfers_unique_company_transfer_number unique (company_id, transfer_number)
            );

            create index if not exists ix_bank_transfers_company_status on bank_transfers (company_id, status);
            create index if not exists ix_bank_transfers_company_from on bank_transfers (company_id, from_account_id);
            create index if not exists ix_bank_transfers_company_to on bank_transfers (company_id, to_account_id);

            -- Bank Deposits -------------------------------------------------
            create table if not exists bank_deposits (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              deposit_number text not null,
              status text not null default 'draft',
              deposit_date date not null,
              deposit_to_account_id uuid not null references accounts(id) on delete restrict,
              document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              total_amount numeric(20,6) not null default 0,
              reference_no text,
              memo text,
              posted_at timestamptz,
              created_by_user_id uuid not null references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint bank_deposits_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
              constraint bank_deposits_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
              constraint bank_deposits_total_nonnegative_chk check (total_amount >= 0),
              constraint bank_deposits_unique_company_deposit_number unique (company_id, deposit_number)
            );

            create table if not exists bank_deposit_items (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              bank_deposit_id uuid not null references bank_deposits(id) on delete cascade,
              line_number integer not null,
              source_item_kind text not null,
              source_item_id uuid,
              source_item_display_number text not null,
              payer_name text,
              payment_method text,
              reference_no text,
              amount numeric(20,6) not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint bank_deposit_items_amount_positive_chk check (amount > 0),
              constraint bank_deposit_items_kind_chk check (source_item_kind in ('sales_receipt', 'receive_payment', 'journal_entry', 'manual')),
              constraint bank_deposit_items_payment_method_chk check (payment_method is null or payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
              constraint bank_deposit_items_unique_line unique (bank_deposit_id, line_number)
            );

            create index if not exists ix_bank_deposits_company_status on bank_deposits (company_id, status);
            create index if not exists ix_bank_deposits_company_deposit_to on bank_deposits (company_id, deposit_to_account_id);
            create index if not exists ix_bank_deposit_items_bank_deposit on bank_deposit_items (bank_deposit_id, line_number);
            create index if not exists ix_bank_deposit_items_source on bank_deposit_items (company_id, source_item_kind, source_item_id);

            -- Tax Returns ---------------------------------------------------
            create table if not exists tax_returns (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              return_number text not null,
              status text not null default 'draft',
              tax_regime text not null,
              filing_frequency text not null,
              period_start date not null,
              period_end date not null,
              base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              collected_amount numeric(20,6) not null default 0,
              input_credits_amount numeric(20,6) not null default 0,
              adjustments_amount numeric(20,6) not null default 0,
              adjustments_note text,
              net_amount numeric(20,6) not null default 0,
              regulator_reference_no text,
              memo text,
              posted_at timestamptz,
              created_by_user_id uuid not null references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint tax_returns_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
              constraint tax_returns_status_chk check (status in ('draft', 'posted', 'voided', 'amended')),
              constraint tax_returns_filing_frequency_chk check (filing_frequency in ('monthly', 'quarterly', 'annual')),
              constraint tax_returns_period_chk check (period_end >= period_start),
              constraint tax_returns_unique_company_return_number unique (company_id, return_number),
              constraint tax_returns_unique_posted_period unique (company_id, tax_regime, period_end, status) deferrable initially deferred
            );

            create index if not exists ix_tax_returns_company_status on tax_returns (company_id, status);
            create index if not exists ix_tax_returns_company_period on tax_returns (company_id, tax_regime, period_end);

            -- updated_at triggers (idempotent via DROP+CREATE) -------------
            drop trigger if exists trg_sales_receipts_set_updated_at on sales_receipts;
            create trigger trg_sales_receipts_set_updated_at before update on sales_receipts for each row execute function citus_set_updated_at();

            drop trigger if exists trg_sales_receipt_lines_set_updated_at on sales_receipt_lines;
            create trigger trg_sales_receipt_lines_set_updated_at before update on sales_receipt_lines for each row execute function citus_set_updated_at();

            drop trigger if exists trg_refund_receipts_set_updated_at on refund_receipts;
            create trigger trg_refund_receipts_set_updated_at before update on refund_receipts for each row execute function citus_set_updated_at();

            drop trigger if exists trg_refund_receipt_lines_set_updated_at on refund_receipt_lines;
            create trigger trg_refund_receipt_lines_set_updated_at before update on refund_receipt_lines for each row execute function citus_set_updated_at();

            drop trigger if exists trg_bank_transfers_set_updated_at on bank_transfers;
            create trigger trg_bank_transfers_set_updated_at before update on bank_transfers for each row execute function citus_set_updated_at();

            drop trigger if exists trg_bank_deposits_set_updated_at on bank_deposits;
            create trigger trg_bank_deposits_set_updated_at before update on bank_deposits for each row execute function citus_set_updated_at();

            drop trigger if exists trg_bank_deposit_items_set_updated_at on bank_deposit_items;
            create trigger trg_bank_deposit_items_set_updated_at before update on bank_deposit_items for each row execute function citus_set_updated_at();

            drop trigger if exists trg_tax_returns_set_updated_at on tax_returns;
            create trigger trg_tax_returns_set_updated_at before update on tax_returns for each row execute function citus_set_updated_at();
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        Volatile.Write(ref _ensured, 1);
    }
}
