namespace Modules.CompanyAccess.Memberships;

/// <summary>
/// Maps each legacy coarse permission token to the fine-grained
/// tokens that represent the same authorization grant. Used by the
/// one-time membership migration so a user who held <c>"ar"</c> ends
/// up holding <c>"ar"</c> <i>plus</i> every <c>ar.*</c> fine-grained
/// token — backward-compat for legacy callers, forward-compat for the
/// new <c>[HasPermission]</c> decorator path.
///
/// Granted with intent to surface in the new model exactly the same
/// abilities the legacy token implied, no more. Where a legacy token
/// already covered an action without a clear fine-grained equivalent
/// (e.g. <c>approve</c> covered AR-invoice posting, AP-bill posting,
/// and JE posting), the expansion enumerates each of those concrete
/// targets.
/// </summary>
public static class CompanyMembershipPermissionLegacyExpansion
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Mappings =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [CompanyMembershipPermissionCatalog.Ar] = new[]
            {
                CompanyMembershipPermissionCatalog.ArInvoiceView,
                CompanyMembershipPermissionCatalog.ArInvoiceCreate,
                CompanyMembershipPermissionCatalog.ArInvoiceEdit,
                CompanyMembershipPermissionCatalog.ArReceiptView,
                CompanyMembershipPermissionCatalog.ArReceiptCreate,
                CompanyMembershipPermissionCatalog.ArCustomerView,
                CompanyMembershipPermissionCatalog.ArCustomerCreate,
                CompanyMembershipPermissionCatalog.ArCustomerEdit,
                CompanyMembershipPermissionCatalog.ArCreditNoteView,
                CompanyMembershipPermissionCatalog.ArCreditNoteCreate,
                CompanyMembershipPermissionCatalog.ArAgingView,
            },

            [CompanyMembershipPermissionCatalog.Ap] = new[]
            {
                CompanyMembershipPermissionCatalog.ApBillView,
                CompanyMembershipPermissionCatalog.ApBillCreate,
                CompanyMembershipPermissionCatalog.ApBillEdit,
                CompanyMembershipPermissionCatalog.ApPaymentView,
                CompanyMembershipPermissionCatalog.ApPaymentCreate,
                CompanyMembershipPermissionCatalog.ApVendorView,
                CompanyMembershipPermissionCatalog.ApVendorCreate,
                CompanyMembershipPermissionCatalog.ApVendorEdit,
                CompanyMembershipPermissionCatalog.ApVendorCreditView,
                CompanyMembershipPermissionCatalog.ApVendorCreditCreate,
                CompanyMembershipPermissionCatalog.ApAgingView,
            },

            [CompanyMembershipPermissionCatalog.Approve] = new[]
            {
                CompanyMembershipPermissionCatalog.ArInvoicePost,
                CompanyMembershipPermissionCatalog.ApBillPost,
                CompanyMembershipPermissionCatalog.GlJournalPost,
            },

            [CompanyMembershipPermissionCatalog.Reports] = new[]
            {
                CompanyMembershipPermissionCatalog.ReportsView,
                CompanyMembershipPermissionCatalog.ReportsExport,
            },

            [CompanyMembershipPermissionCatalog.Reconciliation] = new[]
            {
                CompanyMembershipPermissionCatalog.GlJournalView,
                CompanyMembershipPermissionCatalog.GlJournalCreate,
                CompanyMembershipPermissionCatalog.ArReceiptApply,
                CompanyMembershipPermissionCatalog.ApPaymentApply,
            },

            [CompanyMembershipPermissionCatalog.SettingsAccess] = new[]
            {
                CompanyMembershipPermissionCatalog.SettingsCompanyView,
            },

            [CompanyMembershipPermissionCatalog.CompanyAccountingSettings] = new[]
            {
                CompanyMembershipPermissionCatalog.SettingsCompanyEdit,
                CompanyMembershipPermissionCatalog.SettingsNumberingEdit,
                CompanyMembershipPermissionCatalog.SettingsFxEdit,
                CompanyMembershipPermissionCatalog.SettingsTaxEdit,
            },

            [CompanyMembershipPermissionCatalog.CompanyBookGovernance] = new[]
            {
                CompanyMembershipPermissionCatalog.SettingsPermissionsView,
                CompanyMembershipPermissionCatalog.SettingsPermissionsAssign,
                CompanyMembershipPermissionCatalog.SettingsModulesToggle,
                CompanyMembershipPermissionCatalog.GlPeriodClose,
            },
        };

    /// <summary>
    /// Returns the input tokens plus every fine-grained equivalent of
    /// every legacy token in the input. Output is deduplicated, sorted
    /// (ordinal), and never includes unknown tokens — pass output
    /// through <see cref="CompanyMembershipPermissionCatalog.NormalizeTokens"/>
    /// at the boundary if validation matters.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> tokens)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tokens)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var token = raw.Trim().ToLowerInvariant();
            result.Add(token);

            if (Mappings.TryGetValue(token, out var expansion))
            {
                foreach (var fineGrained in expansion)
                {
                    result.Add(fineGrained);
                }
            }
        }

        return result.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// True when expanding the given set would add anything. Used by
    /// the migration to skip rows that are already fully expanded so
    /// re-running the bootstrap is a cheap no-op write-wise.
    /// </summary>
    public static bool NeedsExpansion(IReadOnlyList<string> tokens)
    {
        foreach (var raw in tokens)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var token = raw.Trim().ToLowerInvariant();
            if (!Mappings.TryGetValue(token, out var expansion))
            {
                continue;
            }

            foreach (var fineGrained in expansion)
            {
                if (!tokens.Contains(fineGrained, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
