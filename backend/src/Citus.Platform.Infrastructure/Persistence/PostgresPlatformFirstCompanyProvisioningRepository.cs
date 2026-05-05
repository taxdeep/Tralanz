using System.Text.Json;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Npgsql;

namespace Citus.Platform.Infrastructure.Persistence;

public sealed class PostgresPlatformFirstCompanyProvisioningRepository(
    PlatformPostgresConnectionFactory connectionFactory,
    SysAdminPasswordHasher passwordHasher,
    IPlatformRuntimeStateRepository runtimeStateRepository) : IPlatformFirstCompanyProvisioningRepository
{
    private const int MinimumPasswordLength = 12;
    private const string TemplateVersion = "2026.05";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create extension if not exists pgcrypto;

            create table if not exists users (
              id uuid primary key default gen_random_uuid(),
              email text not null unique,
              username text unique,
              display_name text,
              password_hash text not null,
              status text not null default 'active',
              email_verified_at timestamptz,
              locked_until timestamptz,
              mfa_mode text not null default 'none',
              security_stamp text not null default gen_random_uuid()::text,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            alter table users add column if not exists display_name text;
            alter table users add column if not exists status text not null default 'active';
            alter table users add column if not exists email_verified_at timestamptz;
            alter table users add column if not exists locked_until timestamptz;
            alter table users add column if not exists mfa_mode text not null default 'none';
            alter table users add column if not exists security_stamp text not null default gen_random_uuid()::text;

            create table if not exists currency_catalog (
              code char(3) primary key,
              name text not null,
              minor_unit smallint not null,
              is_active boolean not null default true
            );

            -- Seeded list mirrors the currencies frankfurter.dev publishes
            -- (ECB-derived). Currencies not in this list cannot get an
            -- automatic recommended FX rate, so we deliberately don't ship
            -- them in the picker. KWD lived here briefly but isn't ECB-
            -- published; the UPDATE below deactivates it on already-
            -- provisioned databases.
            insert into currency_catalog (code, name, minor_unit, is_active)
            values
              ('AUD', 'Australian Dollar', 2, true),
              ('BGN', 'Bulgarian Lev', 2, true),
              ('BRL', 'Brazilian Real', 2, true),
              ('CAD', 'Canadian Dollar', 2, true),
              ('CHF', 'Swiss Franc', 2, true),
              ('CNY', 'Chinese Yuan', 2, true),
              ('CZK', 'Czech Koruna', 2, true),
              ('DKK', 'Danish Krone', 2, true),
              ('EUR', 'Euro', 2, true),
              ('GBP', 'Pound Sterling', 2, true),
              ('HKD', 'Hong Kong Dollar', 2, true),
              ('HUF', 'Hungarian Forint', 2, true),
              ('IDR', 'Indonesian Rupiah', 2, true),
              ('ILS', 'Israeli New Shekel', 2, true),
              ('INR', 'Indian Rupee', 2, true),
              ('ISK', 'Icelandic Krona', 0, true),
              ('JPY', 'Japanese Yen', 0, true),
              ('KRW', 'South Korean Won', 0, true),
              ('MXN', 'Mexican Peso', 2, true),
              ('MYR', 'Malaysian Ringgit', 2, true),
              ('NOK', 'Norwegian Krone', 2, true),
              ('NZD', 'New Zealand Dollar', 2, true),
              ('PHP', 'Philippine Peso', 2, true),
              ('PLN', 'Polish Zloty', 2, true),
              ('RON', 'Romanian Leu', 2, true),
              ('SEK', 'Swedish Krona', 2, true),
              ('SGD', 'Singapore Dollar', 2, true),
              ('THB', 'Thai Baht', 2, true),
              ('TRY', 'Turkish Lira', 2, true),
              ('USD', 'US Dollar', 2, true),
              ('ZAR', 'South African Rand', 2, true)
            on conflict (code) do nothing;

            -- Deactivate currencies we no longer surface (frankfurter
            -- doesn't publish KWD, so we can't auto-recommend a rate for
            -- it). Existing rows aren't deleted because a base_currency_code
            -- FK may still point at them — but is_active=false hides the
            -- row from the multi-currency picker.
            update currency_catalog
            set is_active = false
            where code = 'KWD';

            create table if not exists companies (
              id uuid primary key default gen_random_uuid(),
              entity_number text not null unique,
              legal_name text not null,
              base_currency_code char(3) not null,
              multi_currency_enabled boolean not null default false,
              status text not null default 'active',
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            alter table companies add column if not exists entity_type text not null default 'corporation';
            alter table companies add column if not exists industry text not null default 'general_services';
            alter table companies add column if not exists incorporated_on date;
            alter table companies add column if not exists fiscal_year_end_month smallint not null default 12;
            alter table companies add column if not exists fiscal_year_end_day smallint not null default 31;
            alter table companies add column if not exists business_number text;
            alter table companies add column if not exists phone text;
            alter table companies add column if not exists email text;
            alter table companies add column if not exists address_line text;
            alter table companies add column if not exists city text;
            alter table companies add column if not exists province_state text;
            alter table companies add column if not exists postal_code text;
            alter table companies add column if not exists country text not null default 'Canada';
            alter table companies add column if not exists account_code_length smallint not null default 4;
            alter table companies alter column account_code_length set default 5;

            -- Inventory module (paid add-on, per Tralanz Inventory V1 plan).
            -- Enabled flag drives the Items page gate, the activation
            -- wizard's idempotency, and (later) the Receipt / Shipment /
            -- Adjustment workbenches' visibility. enabled_at marks the
            -- moment the module was turned on (analytics + license
            -- bookkeeping); locked_at is set on the first inventory
            -- transaction so the costing-method choice can no longer be
            -- changed without admin revaluation tooling.
            alter table companies add column if not exists inventory_module_enabled boolean not null default false;
            alter table companies add column if not exists inventory_module_enabled_at timestamptz null;
            alter table companies add column if not exists inventory_module_locked_at timestamptz null;
            alter table companies add column if not exists inventory_profile_tag text null;

            do $$
            begin
              if not exists (
                select 1
                from pg_constraint
                where conname = 'companies_base_currency_fk') then
                alter table companies
                  add constraint companies_base_currency_fk
                  foreign key (base_currency_code) references currency_catalog(code) on delete restrict;
              end if;
            end
            $$;

            create table if not exists company_memberships (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              user_id uuid not null references users(id) on delete cascade,
              role text not null,
              is_active boolean not null default true,
              permissions jsonb not null default '[]'::jsonb,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;

            create table if not exists company_currencies (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              currency_code char(3) not null references currency_catalog(code) on delete restrict,
              is_enabled boolean not null default true,
              created_at timestamptz not null default now(),
              constraint company_currencies_unique unique (company_id, currency_code)
            );

            create table if not exists company_settings (
              company_id uuid primary key references companies(id) on delete cascade,
              profile jsonb not null default '{}'::jsonb,
              security jsonb not null default '{}'::jsonb,
              notification jsonb not null default '{}'::jsonb,
              currency jsonb not null default '{}'::jsonb,
              updated_at timestamptz not null default now()
            );

            create table if not exists accounts (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              entity_number text not null unique,
              code text not null,
              name text not null,
              root_type text not null,
              detail_type text not null,
              is_active boolean not null default true,
              is_system boolean not null default false,
              is_system_default boolean not null default false,
              system_key text,
              system_role text,
              currency_code char(3) references currency_catalog(code) on delete restrict,
              allow_manual_posting boolean not null default true,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint accounts_unique_company_code unique (company_id, code)
            );

            create table if not exists company_books (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              book_code text not null,
              book_name text not null,
              book_role text not null,
              accounting_standard text not null,
              book_base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              functional_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              presentation_currency_code char(3) references currency_catalog(code) on delete restrict,
              is_primary boolean not null default false,
              is_adjustment_only boolean not null default false,
              effective_from date not null,
              is_active boolean not null default true,
              created_by_user_id uuid references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint company_books_unique unique (company_id, book_code)
            );

            create table if not exists company_book_remeasurement_policies (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null references companies(id) on delete cascade,
              company_book_id uuid not null references company_books(id) on delete cascade,
              rate_type text not null default 'closing',
              quote_basis text not null default 'direct',
              rate_use_case text not null default 'remeasurement',
              posting_reason text not null default 'revaluation',
              revaluation_profile text not null default 'monetary_open_item_closing',
              fx_rounding_policy text not null default 'currency_precision',
              effective_from date not null,
              is_active boolean not null default true,
              created_by_user_id uuid references users(id) on delete restrict,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table if not exists company_chart_template_bindings (
              company_id uuid primary key references companies(id) on delete cascade,
              template_key text not null,
              template_version text not null,
              account_code_length smallint not null,
              base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
              country text not null,
              entity_type text not null,
              industry text not null,
              reserved_ranges jsonb not null default '[]'::jsonb,
              mandatory_system_roles jsonb not null default '[]'::jsonb,
              applied_by_sysadmin_account_id uuid references sysadmin_accounts(id) on delete set null,
              applied_at timestamptz not null default now()
            );

            create table if not exists platform_entity_number_sequences (
              entity_year integer primary key,
              next_number bigint not null
            );
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlatformFirstCompanyProvisioningResult> ProvisionAsync(
        PlatformFirstCompanyProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        var validation = Validate(command);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var normalized = Normalize(command);
        var fiscalYearEnd = ParseFiscalYearEnd(normalized.FiscalYearEnd);
        var template = ResolveTemplateDefinition(
            normalized.TemplateKey,
            normalized.Country,
            normalized.EntityType,
            normalized.Industry);
        var provisionedAtUtc = DateTimeOffset.UtcNow;

        await EnsureSchemaAsync(cancellationToken);
        await runtimeStateRepository.EnsureSchemaAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await LockProvisioningScopeAsync(connection, transaction, cancellationToken);

            var setupStatus = await ReadSetupStatusAsync(connection, transaction, cancellationToken);
            if (setupStatus.CompanyCount > 0 || setupStatus.OwnerMembershipCount > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed("already_provisioned", "The first business company has already been provisioned.");
            }

            await EnsureCurrencyExistsAsync(connection, transaction, normalized.BaseCurrencyCode, cancellationToken);
            if (await UserEmailExistsAsync(connection, transaction, normalized.OwnerEmail, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failed("owner_email_exists", "The first business owner email is already in use.");
            }

            var ownerUserId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var membershipId = Guid.NewGuid();
            var companyBookId = Guid.NewGuid();
            var companyEntityNumber = await ReserveEntityNumberAsync(connection, transaction, provisionedAtUtc.Year, cancellationToken);
            var effectiveFrom = normalized.IncorporatedOn!.Value.Date;
            // FormatAccountCode skips canonical rows that cannot truncate
            // cleanly to the chosen account_code_length (i.e. trailing
            // non-zero digits). The current canonical chart authors all
            // rows with all-zero tails so nothing is filtered today, but
            // the helper is kept defensive for future template edits and
            // for the per-currency rows the multi-currency seeder allocates
            // at runtime.
            var starterAccountCodes = template.Accounts
                .Select(account => FormatAccountCode(account.CanonicalCode, normalized.AccountCodeLength))
                .OfType<string>()
                .ToArray();

            await InsertBusinessOwnerAsync(connection, transaction, ownerUserId, normalized, provisionedAtUtc, cancellationToken);
            await InsertCompanyAsync(connection, transaction, companyId, companyEntityNumber, normalized, fiscalYearEnd, provisionedAtUtc, cancellationToken);
            await InsertOwnerMembershipAsync(connection, transaction, membershipId, companyId, ownerUserId, provisionedAtUtc, cancellationToken);
            await EnableBaseCurrencyAsync(connection, transaction, companyId, normalized.BaseCurrencyCode, cancellationToken);
            await InsertCompanySettingsAsync(connection, transaction, companyId, ownerUserId, normalized, template, starterAccountCodes, provisionedAtUtc, cancellationToken);
            await InsertPrimaryBookAsync(connection, transaction, companyBookId, companyId, ownerUserId, normalized.BaseCurrencyCode, template.AccountingStandard, effectiveFrom, provisionedAtUtc, cancellationToken);
            await InsertDefaultRemeasurementPolicyAsync(connection, transaction, companyId, companyBookId, ownerUserId, effectiveFrom, provisionedAtUtc, cancellationToken);
            foreach (var account in template.Accounts)
            {
                var formattedCode = FormatAccountCode(account.CanonicalCode, normalized.AccountCodeLength);
                if (formattedCode is null)
                {
                    // Richer-length-only row; the chosen account_code_length
                    // can't host it without losing information. Skip cleanly
                    // — InsertCompanySettings already received the filtered
                    // code list above so the company settings stay
                    // consistent with what's seeded.
                    continue;
                }
                await InsertStarterAccountAsync(
                    connection,
                    transaction,
                    companyId,
                    provisionedAtUtc.Year,
                    normalized.BaseCurrencyCode,
                    formattedCode,
                    account,
                    provisionedAtUtc,
                    cancellationToken);
            }

            await InsertChartTemplateBindingAsync(connection, transaction, companyId, normalized, template, provisionedAtUtc, cancellationToken);
            await InsertAuditLogIfAvailableAsync(connection, transaction, normalized.SysAdminAccountId, companyId, normalized, template, starterAccountCodes, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            await runtimeStateRepository.UpsertFirstCompanySetupStateAsync(
                new PlatformFirstCompanySetupState
                {
                    DecisionStatus = PlatformFirstCompanySetupState.PendingDecisionStatus
                },
                cancellationToken);

            return new PlatformFirstCompanyProvisioningResult
            {
                Succeeded = true,
                CompanyId = companyId,
                CompanyEntityNumber = companyEntityNumber,
                CompanyName = normalized.CompanyName,
                OwnerUserId = ownerUserId,
                OwnerEmail = normalized.OwnerEmail,
                CompanyBookId = companyBookId,
                CompanyBookCode = "PRIMARY",
                TemplateKey = template.TemplateKey,
                TemplateVersion = template.TemplateVersion,
                BaseCurrencyCode = normalized.BaseCurrencyCode,
                AccountCodeLength = normalized.AccountCodeLength,
                StarterAccountCodes = starterAccountCodes,
                ReservedFamilies = template.ReservedFamilies.Select(static family => family.CodeRange).ToArray(),
                ProvisionedAtUtc = provisionedAtUtc
            };
        }
        catch (InvalidOperationException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failed("invalid_setup", ex.Message);
        }
    }

    private static PlatformFirstCompanyProvisioningResult Validate(PlatformFirstCompanyProvisioningCommand? command)
    {
        if (command is null)
        {
            return Failed("missing_command", "First-company provisioning input is required.");
        }

        if (string.IsNullOrWhiteSpace(command.OwnerDisplayName))
        {
            return Failed("missing_owner_display_name", "Business owner display name is required.");
        }

        if (string.IsNullOrWhiteSpace(command.OwnerEmail))
        {
            return Failed("missing_owner_email", "Business owner email is required.");
        }

        if (string.IsNullOrWhiteSpace(command.OwnerPassword))
        {
            return Failed("missing_owner_password", "Business owner password is required.");
        }

        if (command.OwnerPassword.Trim().Length < MinimumPasswordLength)
        {
            return Failed("invalid_owner_password", $"Business owner password must be at least {MinimumPasswordLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(command.CompanyName))
        {
            return Failed("missing_company_name", "Company name is required.");
        }

        if (string.IsNullOrWhiteSpace(command.EntityType))
        {
            return Failed("missing_entity_type", "Entity type is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Industry))
        {
            return Failed("missing_industry", "Industry is required.");
        }

        if (!command.IncorporatedOn.HasValue)
        {
            return Failed("missing_incorporated_on", "Incorporated date is required.");
        }

        if (!TryParseFiscalYearEnd(command.FiscalYearEnd, out _, out _))
        {
            return Failed("invalid_fiscal_year_end", "Fiscal year end must use MM-DD format.");
        }

        if (string.IsNullOrWhiteSpace(command.BusinessNumber))
        {
            return Failed("missing_business_number", "Business number is required.");
        }

        if (command.AccountCodeLength is < 4 or > 10)
        {
            return Failed("invalid_account_code_length", "Account code length must be between 4 and 10.");
        }

        if (string.IsNullOrWhiteSpace(command.CompanyEmail))
        {
            return Failed("missing_company_email", "Company email is required.");
        }

        if (string.IsNullOrWhiteSpace(command.AddressLine))
        {
            return Failed("missing_address_line", "Address line is required.");
        }

        if (string.IsNullOrWhiteSpace(command.City))
        {
            return Failed("missing_city", "City is required.");
        }

        if (string.IsNullOrWhiteSpace(command.ProvinceState))
        {
            return Failed("missing_province_state", "Province / State is required.");
        }

        if (string.IsNullOrWhiteSpace(command.PostalCode))
        {
            return Failed("missing_postal_code", "Postal code is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Country))
        {
            return Failed("missing_country", "Country is required.");
        }

        if (string.IsNullOrWhiteSpace(command.BaseCurrencyCode))
        {
            return Failed("missing_base_currency", "Base currency is required.");
        }

        return new PlatformFirstCompanyProvisioningResult
        {
            Succeeded = true
        };
    }

    private static PlatformFirstCompanyProvisioningCommand Normalize(PlatformFirstCompanyProvisioningCommand command) =>
        command with
        {
            OwnerDisplayName = command.OwnerDisplayName.Trim(),
            OwnerEmail = command.OwnerEmail.Trim().ToLowerInvariant(),
            OwnerPassword = command.OwnerPassword.Trim(),
            CompanyName = command.CompanyName.Trim(),
            EntityType = command.EntityType.Trim().ToLowerInvariant(),
            Industry = command.Industry.Trim().ToLowerInvariant(),
            IncorporatedOn = command.IncorporatedOn?.Date,
            FiscalYearEnd = command.FiscalYearEnd.Trim(),
            BusinessNumber = command.BusinessNumber.Trim(),
            Phone = command.Phone.Trim(),
            CompanyEmail = command.CompanyEmail.Trim().ToLowerInvariant(),
            AddressLine = command.AddressLine.Trim(),
            City = command.City.Trim(),
            ProvinceState = command.ProvinceState.Trim(),
            PostalCode = command.PostalCode.Trim(),
            Country = command.Country.Trim(),
            TemplateKey = NormalizeTemplateKey(
                command.TemplateKey,
                command.Country.Trim(),
                command.EntityType.Trim(),
                command.Industry.Trim()),
            BaseCurrencyCode = command.BaseCurrencyCode.Trim().ToUpperInvariant()
        };

    private static PlatformFirstCompanyProvisioningResult Failed(string code, string message) =>
        new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message
        };

    private static bool TryParseFiscalYearEnd(string? value, out int month, out int day)
    {
        month = 0;
        day = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out month) ||
            !int.TryParse(parts[1], out day))
        {
            return false;
        }

        try
        {
            _ = new DateTime(2000, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static (int Month, int Day) ParseFiscalYearEnd(string value)
    {
        if (!TryParseFiscalYearEnd(value, out var month, out var day))
        {
            throw new InvalidOperationException("Fiscal year end must use MM-DD format.");
        }

        return (month, day);
    }

    private static string NormalizeTemplateKey(
        string? templateKey,
        string country,
        string entityType,
        string industry)
    {
        if (!string.IsNullOrWhiteSpace(templateKey))
        {
            return templateKey.Trim().ToLowerInvariant();
        }

        if (string.Equals(country.Trim(), "Canada", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(industry.Trim(), "property_rental", StringComparison.OrdinalIgnoreCase))
        {
            return "ca_property_rental";
        }

        return string.Equals(country.Trim(), "Canada", StringComparison.OrdinalIgnoreCase)
            ? "ca_general_small_business"
            : $"generic_{entityType.Trim().ToLowerInvariant()}";
    }

    private static TemplateDefinition ResolveTemplateDefinition(
        string templateKey,
        string country,
        string entityType,
        string industry)
    {
        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, country, entityType, industry);
        var accountingStandard = string.Equals(country, "Canada", StringComparison.OrdinalIgnoreCase)
            ? "ASPE"
            : "IFRS";

        // The PDF-derived canonical chart has 64 user-visible accounts
        // covering bank / AR / OCA / FA / OA / AP / OCL / Equity / Income /
        // COGS / Expense / Other Expense plus 4 hidden FX rows the Posting
        // Engine needs (unrealized gain/loss, translation adjustment, and a
        // separate realized loss account so the system can distinguish gain
        // vs. loss postings even when the user-visible "Exchange Gain or
        // Loss" account is shared on the income-statement face). Codes are
        // authored at the canonical 5-digit length; FormatAccountCode
        // shifts them up or down for the company's chosen length.
        var canonicalChart = BuildCanonicalChart();

        return new TemplateDefinition(
            normalizedTemplateKey,
            TemplateVersion,
            accountingStandard,
            DefaultReservedFamilies,
            DefaultMandatorySystemRoles,
            canonicalChart);
    }

    // -----------------------------------------------------------------------
    // Canonical chart of accounts — generic / universal small-business edition.
    //
    // Source: cleaned 2026-04-27 from QuickBooks-derived reference + a Canadian
    // tax-mapped reference, with company / industry / region-specific rows
    // pruned. ~46 user-visible accounts plus 5 hidden FX system rows.
    //
    // 2026-04-27 follow-up: removed the 11001 / 20001 sub-currency reserve
    // rows. AR/AP foreign-currency control accounts are now created on
    // demand by IMultiCurrencyControlAccountSeeder when a company enables
    // an additional currency — those rows are allocated into the
    // 11001-11099 / 20001-20099 reserved families per the chart binding.
    // -----------------------------------------------------------------------
    private static IReadOnlyList<StarterAccountDefinition> BuildCanonicalChart() =>
    [
        // ---- Bank / Cash (10000-10999) ----
        new StarterAccountDefinition("10000", "Cash on Hand", "asset", "bank", false, true, null, null, true),
        new StarterAccountDefinition("10100", "Bank Operating Account", "asset", "bank", false, true, null, null, true),

        // ---- Accounts Receivable (11000 base; 11001-11099 reserved for per-currency rows) ----
        new StarterAccountDefinition("11000", "Accounts Receivable", "asset", "accounts_receivable", true, true, "control_account:accounts_receivable:base", "accounts_receivable", false),

        // ---- Other Current Asset (12000-13999) ----
        // 12000 carries pre-deposit cash from Sales Receipts / Receive
        // Payment until a Bank Deposit clears it; SystemRole pins the
        // canonical resolution used by those flows.
        new StarterAccountDefinition("12000", "Undeposited Funds", "asset", "undeposited_funds", false, true, "cash:undeposited_funds", "undeposited_funds", true),
        new StarterAccountDefinition("12500", "Short-Term Investments", "asset", "short_term_investments", false, false, null, null, true),
        new StarterAccountDefinition("12800", "Employee Advances", "asset", "employee_advances", false, false, null, null, true),
        new StarterAccountDefinition("13100", "Prepaid Expenses", "asset", "prepaids", false, true, null, null, true),

        // ---- Sales-tax receivable family (13700-13701) ----
        // Used by the TaxReturn posting fragment to clear ITC accruals
        // (13700) and land net refunds (13701) at period-close time.
        new StarterAccountDefinition("13700", "Sales Tax Receivable", "asset", "tax", false, true, "tax:receivable", "tax_receivable", true),
        new StarterAccountDefinition("13701", "Sales Tax Filing Receivable", "asset", "tax", false, true, "tax:filing_receivable", "tax_filing_receivable", true),

        // ---- Inventory (14000) ----
        // SystemRole pins the inventory-asset target for the Inventory
        // Module's M3 sales-issue → COGS bridge.
        new StarterAccountDefinition("14000", "Inventory", "asset", "inventory", false, true, "inventory:asset", "inventory_asset", true),

        // ---- Fixed Asset (15000-17000) ----
        new StarterAccountDefinition("15000", "Furniture and Equipment", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("15200", "Buildings and Improvements", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("15400", "Computer Equipment", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("15600", "Land", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("15900", "Leasehold Improvements", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("16400", "Vehicles", "asset", "fixed_asset", false, false, null, null, true),
        new StarterAccountDefinition("17000", "Accumulated Depreciation", "asset", "contra_asset", false, true, null, null, true),

        // ---- Other Asset (18700) ----
        new StarterAccountDefinition("18700", "Security Deposits Asset", "asset", "other_asset", false, false, null, null, true),

        // ---- Accounts Payable (20000 base; 20001-20099 reserved for per-currency rows) ----
        new StarterAccountDefinition("20000", "Accounts Payable", "liability", "accounts_payable", true, true, "control_account:accounts_payable:base", "accounts_payable", false),

        // ---- Credit Card Payable (21000) ----
        new StarterAccountDefinition("21000", "Credit Card Payable", "liability", "credit_card", false, true, null, null, true),

        // ---- Inventory clearing (21500-21600) ----
        // GR/IR: goods received before bill matched. Drop-ship: vendor
        // bill credit cleared by customer invoice's COGS leg. Both
        // hidden from manual posting; only inventory engines write.
        new StarterAccountDefinition("21500", "GR/IR Clearing", "liability", "inventory_clearing", true, true, "inventory:gr_ir_clearing", "gr_ir_clearing", false),
        new StarterAccountDefinition("21600", "Drop-ship Clearing", "liability", "inventory_clearing", true, true, "inventory:drop_ship_clearing", "drop_ship_clearing", false),

        // ---- Other Current Liability (24000-26999) ----
        new StarterAccountDefinition("24000", "Shareholder Loan", "liability", "shareholder_loan", false, false, null, null, true),
        // Customer Deposits family — pinned with SystemRole so the M5
        // Sales Order workflow can resolve them without per-company config.
        new StarterAccountDefinition("24700", "Customer Deposits", "liability", "customer_deposits", false, false, "ar:customer_deposit", "customer_deposit", true),
        new StarterAccountDefinition("24710", "Customer Advance Payment", "liability", "customer_deposits", false, false, "ar:customer_advance", "customer_advance_payment", true),
        new StarterAccountDefinition("24720", "Customer Store Credit", "liability", "customer_deposits", false, false, "ar:customer_store_credit", "customer_store_credit", true),
        // 25000 holds output-tax accruals from invoices / sales receipts;
        // TaxReturn clears it each period. 25001 carries operator-supplied
        // adjustments. 25002 absorbs the net-payable side of a filed return.
        new StarterAccountDefinition("25000", "Sales Tax Payable", "liability", "tax", false, true, "tax:payable", "tax_payable", true),
        new StarterAccountDefinition("25001", "Sales Tax Adjustments", "liability", "tax", false, true, "tax:adjustments", "tax_adjustments", true),
        new StarterAccountDefinition("25002", "Sales Tax Filing Liability", "liability", "tax", false, true, "tax:filing_liability", "tax_filing_liability", true),
        new StarterAccountDefinition("25500", "Income Tax Payable", "liability", "tax", false, false, null, null, true),
        new StarterAccountDefinition("26000", "Payroll Liabilities", "liability", "payroll_liability", false, false, null, null, true),

        // ---- Long-Term Debt (28000) ----
        new StarterAccountDefinition("28000", "Long-Term Debt", "liability", "long_term_debt", false, false, null, null, true),

        // ---- Equity (30000-32000) ----
        new StarterAccountDefinition("30000", "Opening Balance Equity", "equity", "opening_balance", true, true, "equity:opening_balance", null, true),
        new StarterAccountDefinition("30100", "Capital Stock", "equity", "capital_stock", false, false, null, null, true),
        new StarterAccountDefinition("30200", "Dividends Declared", "equity", "dividends", false, false, null, null, true),
        new StarterAccountDefinition("30800", "Owner's Draw", "equity", "owners_draw", false, false, null, null, true),
        new StarterAccountDefinition("32000", "Retained Earnings", "equity", "retained_earnings", true, true, "equity:retained_earnings", "retained_earnings", false),

        // ---- Income (47900-49900) ----
        new StarterAccountDefinition("47900", "Sales Revenue", "revenue", "sales", false, true, null, null, true),
        new StarterAccountDefinition("48000", "Service Revenue", "revenue", "service_revenue", false, true, null, null, true),
        // GR/IR write-off as gain lands here (vendor bill never arrives →
        // we keep the inventory effectively for free).
        new StarterAccountDefinition("49100", "Inventory Other Income", "revenue", "other_income", false, false, "inventory:other_income", "inventory_other_income", true),
        new StarterAccountDefinition("49900", "Uncategorized Income", "revenue", "uncategorized", false, false, null, null, true),

        // ---- Cost of Goods Sold (51000-51200) ----
        // SystemRole pins COGS for M3 sales-issue bridge.
        new StarterAccountDefinition("51000", "Cost of Goods Sold", "cost_of_sales", "cogs", false, true, "inventory:cogs", "cost_of_goods_sold", true),
        // PPV — bill price vs receipt-side cost-layer mismatch lands here.
        new StarterAccountDefinition("51100", "Purchase Price Variance", "cost_of_sales", "ppv", true, true, "inventory:purchase_price_variance", "purchase_price_variance", false),
        new StarterAccountDefinition("51200", "Freight Costs", "cost_of_sales", "freight", false, false, null, null, true),

        // ---- Operating Expense (60000-69800) ----
        new StarterAccountDefinition("60000", "Advertising and Promotion", "expense", "advertising", false, false, null, null, true),
        new StarterAccountDefinition("60100", "Auto and Truck Expenses", "expense", "auto", false, false, null, null, true),
        new StarterAccountDefinition("60400", "Bank Service Charges", "expense", "bank_fees", false, false, null, null, true),
        new StarterAccountDefinition("61700", "Computer and Internet Expenses", "expense", "technology", false, false, null, null, true),
        new StarterAccountDefinition("62400", "Depreciation Expense", "expense", "depreciation", false, false, null, null, true),
        new StarterAccountDefinition("63300", "Insurance Expense", "expense", "insurance", false, false, null, null, true),
        new StarterAccountDefinition("63400", "Interest Expense", "expense", "interest", false, false, null, null, true),
        new StarterAccountDefinition("64300", "Meals and Entertainment", "expense", "meals", false, false, null, null, true),
        new StarterAccountDefinition("64500", "Bad Debt Expense", "expense", "bad_debt", false, false, null, null, true),
        // Inventory Adjustment — debit side of cycle-count / damage / loss
        // adjustments and loss-side of GR/IR write-offs. System-managed.
        new StarterAccountDefinition("64600", "Inventory Adjustment", "expense", "inventory_adjustment", true, true, "inventory:adjustment", "inventory_adjustment", false),
        new StarterAccountDefinition("64900", "Office Supplies", "expense", "office", false, false, null, null, true),
        new StarterAccountDefinition("65000", "Wages and Salaries", "expense", "payroll", false, true, null, null, true),
        new StarterAccountDefinition("65100", "Employee Benefits", "expense", "payroll", false, false, null, null, true),
        new StarterAccountDefinition("65500", "Professional Development", "expense", "training", false, false, null, null, true),
        new StarterAccountDefinition("66600", "Printing and Reproduction", "expense", "printing", false, false, null, null, true),
        new StarterAccountDefinition("66700", "Professional Fees", "expense", "professional_fees", false, true, null, null, true),
        new StarterAccountDefinition("67100", "Rent Expense", "expense", "rent", false, false, null, null, true),
        new StarterAccountDefinition("67200", "Repairs and Maintenance", "expense", "repairs", false, false, null, null, true),
        new StarterAccountDefinition("68000", "Taxes & Licenses", "expense", "taxes_licenses", false, false, null, null, true),
        new StarterAccountDefinition("68100", "Telephone Expense", "expense", "telephone", false, false, null, null, true),
        new StarterAccountDefinition("68400", "Travel Expense", "expense", "travel", false, false, null, null, true),
        new StarterAccountDefinition("68600", "Utilities", "expense", "utilities", false, false, null, null, true),
        new StarterAccountDefinition("69800", "Uncategorized Expenses", "expense", "uncategorized", false, false, null, null, true),

        // ---- FX family (77000 visible + 77100-77400 hidden system) ----
        // 77000 is the user-visible "Exchange Gain or Loss" — flagged as the
        // realized_fx_gain destination. 77100-77400 are engine-required
        // system rows hidden behind is_system=true so the Posting Engine
        // has distinct accounts for realized_fx_loss, unrealized_fx_gain
        // / loss, and translation adjustment. All authored with all-zero
        // tails so the 4-digit truncation produces 7710 / 7720 / 7730 /
        // 7740 cleanly.
        new StarterAccountDefinition("77000", "Exchange Gain or Loss", "expense", "fx", false, true, "fx:realized_gain", "realized_fx_gain", true),
        new StarterAccountDefinition("77100", "Realized FX Loss", "expense", "fx", true, true, "fx:realized_loss", "realized_fx_loss", false),
        new StarterAccountDefinition("77200", "Unrealized FX Gain", "revenue", "fx", true, true, "fx:unrealized_gain", "unrealized_fx_gain", false),
        new StarterAccountDefinition("77300", "Unrealized FX Loss", "expense", "fx", true, true, "fx:unrealized_loss", "unrealized_fx_loss", false),
        new StarterAccountDefinition("77400", "Translation Adjustment Reserve", "equity", "fx", true, true, "fx:translation_adjustment", "translation_adjustment", false),

        // ---- Other Expense (78000-80000) ----
        new StarterAccountDefinition("78000", "Gain (Loss) on Sale of Assets", "expense", "asset_disposal", false, false, null, null, true),
        new StarterAccountDefinition("80000", "Ask My Accountant", "expense", "other_expense", false, false, null, null, true),

        // ---- Income Tax (90000) ----
        new StarterAccountDefinition("90000", "Income Tax Expense", "expense", "income_tax", false, false, null, null, true),
    ];

    private static IReadOnlyList<ReservedFamily> DefaultReservedFamilies { get; } =
    [
        new("10000-10999", "Cash and bank family"),
        new("11000", "Base accounts receivable control"),
        new("11001-11099", "Multi-currency AR reserve"),
        new("20000", "Base accounts payable control"),
        new("20001-20099", "Multi-currency AP reserve"),
        new("77000-77499", "FX gain/loss and translation reserve family")
    ];

    private static IReadOnlyList<string> DefaultMandatorySystemRoles { get; } =
    [
        "accounts_receivable",
        "accounts_payable",
        "realized_fx_gain",
        "realized_fx_loss",
        "unrealized_fx_gain",
        "unrealized_fx_loss",
        "translation_adjustment"
    ];

    /// <summary>
    /// Canonical chart-of-accounts codes are stored at 5 digits (the same
    /// length QuickBooks ships its default chart at). When a company picks
    /// a different account_code_length (4–10), <see cref="FormatAccountCode"/>
    /// shifts the canonical code up or down:
    ///   * length == 5: returned unchanged.
    ///   * length &gt; 5: pad RIGHT with zeros (10000 → 100000 → 1000000…),
    ///     keeping the leading digits' meaning.
    ///   * length &lt; 5: drop the trailing chars; if any of them is NOT '0'
    ///     the truncation would lose information, so the helper returns null
    ///     and the seeding loop skips the row.
    /// Returning null (rather than throwing) is intentional: future template
    /// rows or per-currency AR/AP rows allocated at runtime may carry
    /// non-zero trailing digits (e.g. 11001 Accounts Receivable - USD), and
    /// the seeder treats "skip" as the correct response for a 4-digit
    /// company that can't host those codes without collision.
    /// </summary>
    private const int CanonicalCodeLength = 5;

    private static string? FormatAccountCode(string canonicalCode, int accountCodeLength)
    {
        var trimmed = canonicalCode.Trim();
        if (trimmed.Length != CanonicalCodeLength)
        {
            // Defensive: every row in ResolveTemplateDefinition is authored
            // at exactly CanonicalCodeLength characters. A mismatch here is
            // a template bug, not user input — fail fast at startup-time
            // tests rather than silently mis-formatting.
            throw new InvalidOperationException(
                $"Canonical account code '{trimmed}' must be exactly {CanonicalCodeLength} characters; templates must author codes at the canonical length.");
        }

        if (accountCodeLength == CanonicalCodeLength)
        {
            return trimmed;
        }

        if (accountCodeLength > CanonicalCodeLength)
        {
            return trimmed.PadRight(accountCodeLength, '0');
        }

        var charsToDrop = CanonicalCodeLength - accountCodeLength;
        var tail = trimmed[^charsToDrop..];
        if (tail.Any(ch => ch != '0'))
        {
            return null;
        }
        return trimmed[..accountCodeLength];
    }

    private sealed record TemplateDefinition(
        string TemplateKey,
        string TemplateVersion,
        string AccountingStandard,
        IReadOnlyList<ReservedFamily> ReservedFamilies,
        IReadOnlyList<string> MandatorySystemRoles,
        IReadOnlyList<StarterAccountDefinition> Accounts);

    private sealed record ReservedFamily(string CodeRange, string Purpose);

    private sealed record StarterAccountDefinition(
        string CanonicalCode,
        string Name,
        string RootType,
        string DetailType,
        bool IsSystem,
        bool IsSystemDefault,
        string? SystemKey,
        string? SystemRole,
        bool AllowManualPosting);

    private static async Task LockProvisioningScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "lock table users, companies, company_memberships in access exclusive mode;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int CompanyCount, int OwnerMembershipCount)> ReadSetupStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              (select count(*)::int from companies) as company_count,
              (select count(*)::int from company_memberships where is_active = true and role = 'owner') as owner_membership_count;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (
            reader.GetInt32(reader.GetOrdinal("company_count")),
            reader.GetInt32(reader.GetOrdinal("owner_membership_count")));
    }

    private static async Task EnsureCurrencyExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select count(*)
            from currency_catalog
            where code = @code
              and is_active = true;
            """;
        command.Parameters.AddWithValue("code", currencyCode);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
        {
            throw new InvalidOperationException($"Currency {currencyCode} is not active in the catalog.");
        }
    }

    private static async Task<bool> UserEmailExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ownerEmail,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select exists (
              select 1
              from users
              where lower(email) = @email
            );
            """;
        command.Parameters.AddWithValue("email", ownerEmail);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task InsertBusinessOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserId ownerUserId,
        PlatformFirstCompanyProvisioningCommand normalized,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into users (
              id,
              email,
              username,
              display_name,
              password_hash,
              status,
              email_verified_at,
              mfa_mode,
              security_stamp,
              created_at,
              updated_at
            )
            values (
              @id,
              @email,
              null,
              @display_name,
              @password_hash,
              'active',
              @email_verified_at,
              'none',
              gen_random_uuid()::text,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("id", ownerUserId);
        command.Parameters.AddWithValue("email", normalized.OwnerEmail);
        command.Parameters.AddWithValue("display_name", normalized.OwnerDisplayName);
        command.Parameters.AddWithValue("password_hash", passwordHasher.HashPassword(normalized.OwnerPassword));
        command.Parameters.AddWithValue("email_verified_at", provisionedAtUtc);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCompanyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string companyEntityNumber,
        PlatformFirstCompanyProvisioningCommand normalized,
        (int Month, int Day) fiscalYearEnd,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into companies (
              id,
              entity_number,
              legal_name,
              base_currency_code,
              multi_currency_enabled,
              status,
              entity_type,
              industry,
              incorporated_on,
              fiscal_year_end_month,
              fiscal_year_end_day,
              business_number,
              phone,
              email,
              address_line,
              city,
              province_state,
              postal_code,
              country,
              account_code_length,
              created_at,
              updated_at
            )
            values (
              @id,
              @entity_number,
              @legal_name,
              @base_currency_code,
              false,
              'active',
              @entity_type,
              @industry,
              @incorporated_on,
              @fiscal_year_end_month,
              @fiscal_year_end_day,
              @business_number,
              @phone,
              @email,
              @address_line,
              @city,
              @province_state,
              @postal_code,
              @country,
              @account_code_length,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("id", companyId);
        command.Parameters.AddWithValue("entity_number", companyEntityNumber);
        command.Parameters.AddWithValue("legal_name", normalized.CompanyName);
        command.Parameters.AddWithValue("base_currency_code", normalized.BaseCurrencyCode);
        command.Parameters.AddWithValue("entity_type", normalized.EntityType);
        command.Parameters.AddWithValue("industry", normalized.Industry);
        command.Parameters.AddWithValue("incorporated_on", normalized.IncorporatedOn!.Value.Date);
        command.Parameters.AddWithValue("fiscal_year_end_month", fiscalYearEnd.Month);
        command.Parameters.AddWithValue("fiscal_year_end_day", fiscalYearEnd.Day);
        command.Parameters.AddWithValue("business_number", normalized.BusinessNumber);
        command.Parameters.AddWithValue("phone", normalized.Phone);
        command.Parameters.AddWithValue("email", normalized.CompanyEmail);
        command.Parameters.AddWithValue("address_line", normalized.AddressLine);
        command.Parameters.AddWithValue("city", normalized.City);
        command.Parameters.AddWithValue("province_state", normalized.ProvinceState);
        command.Parameters.AddWithValue("postal_code", normalized.PostalCode);
        command.Parameters.AddWithValue("country", normalized.Country);
        command.Parameters.AddWithValue("account_code_length", normalized.AccountCodeLength);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOwnerMembershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid membershipId,
        CompanyId companyId,
        UserId ownerUserId,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        var permissionsJson = JsonSerializer.Serialize(
            new[]
            {
                "ap",
                "approve",
                "ar",
                "company_accounting_settings",
                "company_book_governance",
                "reconciliation",
                "reports",
                "settings_access"
            },
            JsonOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_memberships (
              id,
              company_id,
              user_id,
              role,
              is_active,
              permissions,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @user_id,
              'owner',
              true,
              @permissions::jsonb,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("id", membershipId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", ownerUserId);
        command.Parameters.AddWithValue("permissions", permissionsJson);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnableBaseCurrencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string baseCurrencyCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_currencies (
              id,
              company_id,
              currency_code,
              is_enabled,
              created_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @currency_code,
              true,
              now()
            )
            on conflict (company_id, currency_code)
            do update
              set is_enabled = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("currency_code", baseCurrencyCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCompanySettingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId ownerUserId,
        PlatformFirstCompanyProvisioningCommand normalized,
        TemplateDefinition template,
        IReadOnlyList<string> starterAccountCodes,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        var profileJson = JsonSerializer.Serialize(new
        {
            normalized.CompanyName,
            normalized.EntityType,
            normalized.Industry,
            IncorporatedOn = normalized.IncorporatedOn!.Value.Date,
            normalized.FiscalYearEnd,
            normalized.BusinessNumber,
            normalized.AccountCodeLength,
            normalized.Phone,
            CompanyEmail = normalized.CompanyEmail,
            normalized.AddressLine,
            normalized.City,
            ProvinceState = normalized.ProvinceState,
            normalized.PostalCode,
            normalized.Country,
            ProvisionedOwnerUserId = ownerUserId,
            ProvisionedOwnerDisplayName = normalized.OwnerDisplayName,
            ProvisionedOwnerEmail = normalized.OwnerEmail,
            template.TemplateKey,
            template.TemplateVersion,
            FirstTimeSetupCompletedAtUtc = provisionedAtUtc,
            FirstBusinessLoginAcknowledgedAtUtc = (DateTimeOffset?)null,
            FirstBusinessLoginAcknowledgedByUserId = (Guid?)null,
            StarterAccountCodes = starterAccountCodes,
            ReservedFamilies = template.ReservedFamilies.Select(static family => new
            {
                family.CodeRange,
                family.Purpose
            }).ToArray()
        }, JsonOptions);

        var currencyJson = JsonSerializer.Serialize(new
        {
            normalized.BaseCurrencyCode,
            MultiCurrencyEnabled = false,
            ReservedFamilies = template.ReservedFamilies.Select(static family => new
            {
                family.CodeRange,
                family.Purpose
            }).ToArray()
        }, JsonOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_settings (
              company_id,
              profile,
              security,
              notification,
              currency,
              updated_at
            )
            values (
              @company_id,
              @profile::jsonb,
              '{}'::jsonb,
              '{}'::jsonb,
              @currency::jsonb,
              @updated_at
            )
            on conflict (company_id)
            do update
              set profile = excluded.profile,
                  currency = excluded.currency,
                  updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("profile", profileJson);
        command.Parameters.AddWithValue("currency", currencyJson);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPrimaryBookAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyBookId,
        CompanyId companyId,
        UserId ownerUserId,
        string baseCurrencyCode,
        string accountingStandard,
        DateTime effectiveFrom,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_books (
              id,
              company_id,
              book_code,
              book_name,
              book_role,
              accounting_standard,
              book_base_currency_code,
              functional_currency_code,
              presentation_currency_code,
              is_primary,
              is_adjustment_only,
              effective_from,
              is_active,
              created_by_user_id,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              'PRIMARY',
              'Primary Book',
              'primary',
              @accounting_standard,
              @base_currency_code,
              @base_currency_code,
              @base_currency_code,
              true,
              false,
              @effective_from,
              true,
              @created_by_user_id,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("id", companyBookId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("accounting_standard", accountingStandard);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("effective_from", effectiveFrom);
        command.Parameters.AddWithValue("created_by_user_id", ownerUserId);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDefaultRemeasurementPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid companyBookId,
        UserId ownerUserId,
        DateTime effectiveFrom,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_book_remeasurement_policies (
              id,
              company_id,
              company_book_id,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              revaluation_profile,
              fx_rounding_policy,
              effective_from,
              is_active,
              created_by_user_id,
              created_at,
              updated_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @company_book_id,
              'closing',
              'direct',
              'remeasurement',
              'revaluation',
              'monetary_open_item_closing',
              'currency_precision',
              @effective_from,
              true,
              @created_by_user_id,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("company_book_id", companyBookId);
        command.Parameters.AddWithValue("effective_from", effectiveFrom);
        command.Parameters.AddWithValue("created_by_user_id", ownerUserId);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertStarterAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int entityYear,
        string baseCurrencyCode,
        string formattedCode,
        StarterAccountDefinition account,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, entityYear, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
              system_key,
              system_role,
              currency_code,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @entity_number,
              @code,
              @name,
              @root_type,
              @detail_type,
              true,
              @is_system,
              @is_system_default,
              @system_key,
              @system_role,
              @currency_code,
              @allow_manual_posting,
              @created_at,
              @updated_at
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", formattedCode);
        command.Parameters.AddWithValue("name", account.Name);
        command.Parameters.AddWithValue("root_type", account.RootType);
        command.Parameters.AddWithValue("detail_type", account.DetailType);
        command.Parameters.AddWithValue("is_system", account.IsSystem);
        command.Parameters.AddWithValue("is_system_default", account.IsSystemDefault);
        command.Parameters.AddWithValue("system_key", (object?)account.SystemKey ?? DBNull.Value);
        command.Parameters.AddWithValue("system_role", (object?)account.SystemRole ?? DBNull.Value);
        command.Parameters.AddWithValue("currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("allow_manual_posting", account.AllowManualPosting);
        command.Parameters.AddWithValue("created_at", provisionedAtUtc);
        command.Parameters.AddWithValue("updated_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertChartTemplateBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        PlatformFirstCompanyProvisioningCommand normalized,
        TemplateDefinition template,
        DateTimeOffset provisionedAtUtc,
        CancellationToken cancellationToken)
    {
        var reservedRangesJson = JsonSerializer.Serialize(
            template.ReservedFamilies.Select(static family => new
            {
                family.CodeRange,
                family.Purpose
            }).ToArray(),
            JsonOptions);
        var mandatorySystemRolesJson = JsonSerializer.Serialize(template.MandatorySystemRoles, JsonOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_chart_template_bindings (
              company_id,
              template_key,
              template_version,
              account_code_length,
              base_currency_code,
              country,
              entity_type,
              industry,
              reserved_ranges,
              mandatory_system_roles,
              applied_by_sysadmin_account_id,
              applied_at
            )
            values (
              @company_id,
              @template_key,
              @template_version,
              @account_code_length,
              @base_currency_code,
              @country,
              @entity_type,
              @industry,
              @reserved_ranges::jsonb,
              @mandatory_system_roles::jsonb,
              @applied_by_sysadmin_account_id,
              @applied_at
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("template_key", template.TemplateKey);
        command.Parameters.AddWithValue("template_version", template.TemplateVersion);
        command.Parameters.AddWithValue("account_code_length", normalized.AccountCodeLength);
        command.Parameters.AddWithValue("base_currency_code", normalized.BaseCurrencyCode);
        command.Parameters.AddWithValue("country", normalized.Country);
        command.Parameters.AddWithValue("entity_type", normalized.EntityType);
        command.Parameters.AddWithValue("industry", normalized.Industry);
        command.Parameters.AddWithValue("reserved_ranges", reservedRangesJson);
        command.Parameters.AddWithValue("mandatory_system_roles", mandatorySystemRolesJson);
        command.Parameters.AddWithValue(
            "applied_by_sysadmin_account_id",
            normalized.SysAdminAccountId.HasValue ? normalized.SysAdminAccountId.Value : DBNull.Value);
        command.Parameters.AddWithValue("applied_at", provisionedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditLogIfAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? sysAdminAccountId,
        CompanyId companyId,
        PlatformFirstCompanyProvisioningCommand normalized,
        TemplateDefinition template,
        IReadOnlyList<string> starterAccountCodes,
        CancellationToken cancellationToken)
    {
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "select to_regclass('public.audit_logs') is not null;";
            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is not bool available || !available)
            {
                return;
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            normalized.OwnerEmail,
            normalized.CompanyName,
            normalized.EntityType,
            normalized.Industry,
            template.TemplateKey,
            template.TemplateVersion,
            normalized.BaseCurrencyCode,
            normalized.AccountCodeLength,
            StarterAccountCodes = starterAccountCodes,
            ReservedFamilies = template.ReservedFamilies.Select(static family => family.CodeRange).ToArray()
        }, JsonOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
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
              'sysadmin',
              @actor_id,
              'company',
              @entity_id,
              'first_company_provisioned',
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("actor_id", sysAdminAccountId.HasValue ? sysAdminAccountId.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", companyId);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into platform_entity_number_sequences (
              entity_year,
              next_number
            )
            values (
              @entity_year,
              2
            )
            on conflict (entity_year)
            do update
              set next_number = platform_entity_number_sequences.next_number + 1
            returning next_number - 1;
            """;
        command.Parameters.AddWithValue("entity_year", year);
        var sequenceNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
        return $"EN{year}{sequenceNumber.ToString().PadLeft(8, '0')}";
    }
}
