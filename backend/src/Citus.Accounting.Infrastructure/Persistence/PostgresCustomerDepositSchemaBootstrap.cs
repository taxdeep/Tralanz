namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Idempotent schema bootstrap for the Customer Deposits feature. The
/// canonical definitions live in TRALANZ_POSTGRESQL_MIGRATION_DRAFT.sql at
/// the repo root; this class re-asserts them on app startup so dev / test
/// databases don't have to re-run the full migration script every time
/// the schema gains a new column or table.
///
/// Touches two surfaces:
/// 1. Adds <c>receive_payments.extra_deposit_amount</c> when missing — the
///    field tracks the cash slice that didn't apply to AR and is being
///    parked as a Customer Deposit.
/// 2. Creates <c>customer_deposits</c> when missing — one row per
///    receive-payment that produced a deposit. The matching open-item
///    lives on the existing <c>ar_open_items</c> table with
///    source_type='customer_deposit', balance_side='credit'.
/// </summary>
public sealed class PostgresCustomerDepositSchemaBootstrap
{
    private readonly PostgresConnectionFactory _connections;
    private int _ensured;

    public PostgresCustomerDepositSchemaBootstrap(PostgresConnectionFactory connections)
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
            do $$
            begin
              if not exists (
                select 1
                from information_schema.columns
                where table_name = 'receive_payments'
                  and column_name = 'extra_deposit_amount'
              ) then
                alter table receive_payments
                  add column extra_deposit_amount numeric(20,6) not null default 0;
                alter table receive_payments
                  add constraint receive_payments_extra_deposit_nonnegative_chk
                  check (extra_deposit_amount >= 0);
              end if;
            end $$;

            create table if not exists customer_deposits (
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null,
              customer_id uuid not null,
              entity_number char(11) not null unique,
              display_number text not null,
              status text not null default 'open',
              deposit_date date not null,
              transaction_currency_code char(3) not null,
              base_currency_code char(3) not null,
              fx_rate_snapshot_id uuid null,
              fx_rate numeric(20,10) not null default 1,
              fx_requested_date date not null,
              fx_effective_date date not null,
              fx_source text not null default 'identity',
              original_amount_tx numeric(20,6) not null,
              original_amount_base numeric(20,6) not null,
              source_receive_payment_id uuid null,
              memo text null,
              posted_at timestamptz null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint customer_deposits_status_chk
                check (status in ('open', 'partially_applied', 'closed', 'voided')),
              constraint customer_deposits_fx_rate_positive_chk
                check (fx_rate > 0),
              constraint customer_deposits_amount_positive_chk
                check (original_amount_tx > 0 and original_amount_base > 0),
              constraint customer_deposits_unique_company_display_number
                unique (company_id, display_number)
            );

            create index if not exists ix_customer_deposits_customer_open
              on customer_deposits (company_id, customer_id, status);

            create index if not exists ix_customer_deposits_source_receive_payment
              on customer_deposits (source_receive_payment_id)
              where source_receive_payment_id is not null;

            create unique index if not exists ux_customer_deposits_company_source_receive_payment
              on customer_deposits (company_id, source_receive_payment_id)
              where source_receive_payment_id is not null;

            -- M5 iter 3: standalone-deposit path. Customer pays directly
            -- against an open / confirmed Sales Order (not via overpaying an
            -- invoice). The deposit row carries the SO id so M5 iter 4 can
            -- look up open deposits per SO and pro-rata clear them on
            -- shipment / invoice. Existing overpay-on-invoice path leaves
            -- this column NULL.
            alter table customer_deposits
              add column if not exists source_sales_order_id uuid null;

            create index if not exists ix_customer_deposits_source_sales_order
              on customer_deposits (source_sales_order_id)
              where source_sales_order_id is not null;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        Volatile.Write(ref _ensured, 1);
    }
}
