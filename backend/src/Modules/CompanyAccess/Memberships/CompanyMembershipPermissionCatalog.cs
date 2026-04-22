namespace Modules.CompanyAccess.Memberships;

public static class CompanyMembershipPermissionCatalog
{
    public const string Ar = "ar";
    public const string Ap = "ap";
    public const string Approve = "approve";
    public const string Reports = "reports";
    public const string SettingsAccess = "settings_access";
    public const string CompanyAccountingSettings = "company_accounting_settings";
    public const string CompanyBookGovernance = "company_book_governance";
    public const string Reconciliation = "reconciliation";

    public static IReadOnlyList<CompanyMembershipPermissionOption> Options { get; } =
    [
        new(Ar, "AR", "Access customer-side receivables workflows.", IsGovernancePermission: false),
        new(Ap, "AP", "Access vendor-side payables workflows.", IsGovernancePermission: false),
        new(Approve, "Approve", "Access controlled approval-oriented business actions.", IsGovernancePermission: false),
        new(Reports, "Reports", "Access company-scoped report surfaces.", IsGovernancePermission: false),
        new(SettingsAccess, "Settings Access", "Access company settings surfaces.", IsGovernancePermission: false),
        new(CompanyAccountingSettings, "Company Accounting Settings", "Manage accounting-governing company settings.", IsGovernancePermission: true),
        new(CompanyBookGovernance, "Book Governance", "Manage books, accounting standards, and book-governing policies.", IsGovernancePermission: true),
        new(Reconciliation, "Reconciliation", "Access reconciliation control workflows.", IsGovernancePermission: false)
    ];

    public static IReadOnlyList<string> NormalizeTokens(IEnumerable<string> tokens)
    {
        var allowed = Options
            .Select(static option => option.Token)
            .ToHashSet(StringComparer.Ordinal);

        var normalized = tokens
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Select(static token => token.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        var unknown = normalized.FirstOrDefault(token => !allowed.Contains(token));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Unknown company membership permission token '{unknown}'.");
        }

        return normalized;
    }
}
