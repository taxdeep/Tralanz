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
    public async Task<CoaSeedSummary> SeedAsync(
        CompanyId companyId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var template = registry.Get(templateKey)
            ?? throw new ArgumentException($"Unknown CoA template '{templateKey}'.", nameof(templateKey));

        // Templates may only seed an empty chart of accounts. Once a company
        // has any accounts (active or inactive, system or user-created), the
        // chart is owned by the company and re-seeding is forbidden — even
        // though the underlying upsert is additive. This protects users from
        // accidentally re-introducing system rows after they have curated
        // the catalog and gives the UI a hard rule to hide the affordance
        // against. The first-company provisioning flow seeds before any
        // accounts exist, so it is unaffected.
        var existing = await accountStore.ListAsync(companyId, includeInactive: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            throw new InvalidOperationException(
                "Chart of accounts is not empty; templates can only be applied to a company with no accounts. Add or edit accounts individually instead.");
        }

        var results = new List<CoaSeedAccountResult>(template.Accounts.Count);
        var created = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var account in template.Accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seedInput = new AccountSeedInput(
                    Code: account.Code,
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
}
