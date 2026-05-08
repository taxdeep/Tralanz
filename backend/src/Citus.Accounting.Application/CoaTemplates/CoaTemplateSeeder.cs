using Citus.Accounting.Application.Abstractions;

namespace Citus.Accounting.Application.CoaTemplates;

/// <summary>
/// Applies a chart-of-accounts template to a company. Idempotent and
/// additive: existing rows (matched by <c>code</c>) are skipped, never
/// overwritten. Each row is committed independently so a single bad
/// account does not cancel the whole seed; failures land in the
/// <see cref="CoaSeedSummary"/> for the caller to surface.
/// </summary>
public sealed class CoaTemplateSeeder(
    ICoaTemplateRegistry registry,
    IAccountStore accountStore) : ICoaTemplateSeeder
{
    // Canonical template codes are authored at this width; FormatAccountCode
    // scales them up or down to the company's chosen account_code_length.
    // Mirrors the helper in PostgresPlatformFirstCompanyProvisioningRepository
    // so first-company provisioning and post-onboarding additive seeds use
    // identical scaling rules.
    private const int CanonicalCodeLength = 5;

    public async Task<CoaSeedSummary> SeedAsync(
        CompanyId companyId,
        string templateKey,
        CancellationToken cancellationToken,
        bool additive = false,
        int? accountCodeLength = null)
    {
        var template = registry.Get(templateKey)
            ?? throw new ArgumentException($"Unknown CoA template '{templateKey}'.", nameof(templateKey));

        // In strict mode (the default) the call is rejected once a company
        // has any accounts. This protects an operator who has curated the
        // catalog from accidentally re-introducing system rows via the
        // user-driven "apply template" affordance, and lets the UI hide
        // that affordance with a hard rule. The first-company provisioning
        // flow seeds before any accounts exist, so the guard is a no-op
        // for it.
        //
        // In additive mode the guard is bypassed: post-onboarding flows
        // (e.g. Inventory module activation) need to make sure a known set
        // of system accounts exists on a company that already has its own
        // chart. The per-row idempotency in SeedSystemAccountAsync is what
        // keeps additive mode safe — existing rows still get skipped at
        // the row level, never overwritten.
        // Both modes need the existing chart: strict mode uses Count for
        // the empty-chart guard, additive mode uses SystemRole values for
        // role-level dedup further down (different code-numbering schemes
        // won't collide on code, so role dedup is the only safe filter).
        var existing = await accountStore.ListAsync(companyId, includeInactive: true, cancellationToken)
            .ConfigureAwait(false);
        if (!additive && existing.Count > 0)
        {
            throw new InvalidOperationException(
                "Chart of accounts is not empty; templates can only be applied to a company with no accounts. Add or edit accounts individually instead.");
        }

        var takenRoles = new HashSet<string>(
            existing
                .Select(static a => a.SystemRole)
                .Where(static role => !string.IsNullOrWhiteSpace(role))!,
            StringComparer.OrdinalIgnoreCase);

        var results = new List<CoaSeedAccountResult>(template.Accounts.Count);
        var created = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var account in template.Accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Additive mode only fills in MISSING system roles. The
                // template ships with ~75 accounts; ~24 of those have a
                // system_role binding (the ones the engine actually needs
                // to find by role), the rest are generic chart structure
                // (Bank Operating, Furniture, Computer Equipment, etc.)
                // that the company will already have under whatever
                // numbering scheme the operator picked at onboarding.
                // Adding those would just clutter the chart with parallel
                // copies under the template's own numbering.
                //
                // Strict mode keeps the original behaviour (empty chart →
                // seed everything) so first-company provisioning is
                // unchanged.
                if (additive && string.IsNullOrWhiteSpace(account.SystemRole))
                {
                    skipped++;
                    results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.SkippedExisting));
                    continue;
                }

                // Role-level dedup. SeedSystemAccountAsync already does
                // ON CONFLICT (company_id, code) DO NOTHING, but two
                // different code-numbering schemes (5-digit template vs.
                // a 6-digit chart the operator may have curated) won't
                // collide on code, so we'd still end up with two accounts
                // bound to the same system_role. Skipping at the role
                // level here is the only safe fix.
                if (!string.IsNullOrWhiteSpace(account.SystemRole) &&
                    takenRoles.Contains(account.SystemRole))
                {
                    skipped++;
                    results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.SkippedExisting));
                    continue;
                }

                // Scale the canonical 5-digit code to the company's chosen
                // account_code_length when the caller provided one. Rows
                // that can't host the canonical code at a narrower width
                // (e.g. 13701 at 4 digits) come back null and are skipped
                // with a clear marker — same rule first-company
                // provisioning uses, so the two paths stay consistent.
                var insertCode = accountCodeLength is int width
                    ? FormatAccountCode(account.Code, width)
                    : account.Code;
                if (insertCode is null)
                {
                    skipped++;
                    results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.SkippedExisting));
                    continue;
                }

                var seedInput = new AccountSeedInput(
                    Code: insertCode,
                    Name: account.Name,
                    RootType: account.RootType,
                    DetailType: account.DetailType,
                    CurrencyCode: null, // template stays currency-neutral; company base applies
                    AllowManualPosting: account.AllowManualPosting,
                    IsActive: true,
                    IsSystem: !string.IsNullOrWhiteSpace(account.SystemRole),
                    IsSystemDefault: !string.IsNullOrWhiteSpace(account.SystemRole),
                    SystemKey: account.SystemKey,
                    SystemRole: account.SystemRole);

                var saved = await accountStore.SeedSystemAccountAsync(companyId, seedInput, cancellationToken)
                    .ConfigureAwait(false);

                if (saved is null)
                {
                    skipped++;
                    results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.SkippedExisting));
                }
                else
                {
                    created++;
                    results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.Created));
                    if (!string.IsNullOrWhiteSpace(saved.SystemRole))
                    {
                        takenRoles.Add(saved.SystemRole!);
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new CoaSeedAccountResult(account.Code, account.Name, CoaSeedOutcome.Failed, ex.Message));
            }
        }

        return new CoaSeedSummary(
            TemplateKey: template.Key,
            TemplateVersion: template.Version,
            CreatedCount: created,
            SkippedCount: skipped,
            FailedCount: failed,
            Results: results);
    }

    // Same scaling rules as
    // PostgresPlatformFirstCompanyProvisioningRepository.FormatAccountCode.
    // Length == 5: returned unchanged. Length > 5: pad RIGHT with zeros
    // (10000 → 100000 → 1000000…). Length < 5: drop the trailing chars; if
    // any of them is NOT '0' the truncation would lose information, so the
    // helper returns null and the seeding loop records a SkippedExisting.
    private static string? FormatAccountCode(string canonicalCode, int accountCodeLength)
    {
        var trimmed = canonicalCode.Trim();
        if (trimmed.Length != CanonicalCodeLength)
        {
            // Not authored at canonical width — leave as-is; this is most
            // likely a per-currency control account or a custom row that
            // already carries the company's preferred width.
            return trimmed;
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
        if (tail.Any(static ch => ch != '0'))
        {
            return null;
        }
        return trimmed[..accountCodeLength];
    }
}
