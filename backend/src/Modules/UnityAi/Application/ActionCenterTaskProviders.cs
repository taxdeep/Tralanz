using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Null provider — used while a domain (AR open invoices, AP bills, banking
/// reconciliation, sales-tax filing calendar) does not yet expose the read
/// shape the rule needs. Returns no tasks. Logs once-per-call so operators
/// can see the gap without it being silent.
///
/// We register one of these per pending provider name so the gap is visible
/// in the runtime without fabricating tasks.
/// </summary>
public sealed class NullActionCenterTaskProvider : IActionCenterTaskProvider
{
    private readonly string _name;
    private readonly string _missingDomain;
    private readonly ILogger<NullActionCenterTaskProvider> _logger;

    public NullActionCenterTaskProvider(string name, string missingDomain, ILogger<NullActionCenterTaskProvider> logger)
    {
        _name = name;
        _missingDomain = missingDomain;
        _logger = logger;
    }

    public string ProviderName => _name;

    public Task<IReadOnlyList<ActionCenterTaskDraft>> GenerateAsync(
        CompanyId companyId,
        UserId? userId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "action center provider {Provider} returning no tasks because domain '{MissingDomain}' is not yet integrated (company={CompanyId})",
            _name, _missingDomain, companyId);

        return Task.FromResult<IReadOnlyList<ActionCenterTaskDraft>>(Array.Empty<ActionCenterTaskDraft>());
    }
}

/// <summary>
/// Determines what setup tasks the company is missing based purely on the
/// platform-level configuration we can read today (SMTP toggle).
///
/// This provider is intentionally minimal — it ships a single, deterministic
/// task ("Configure SMTP") gated by an injected predicate. The host wires the
/// predicate to whichever settings source is current. If the host doesn't
/// configure it, the predicate returns "configured" and no task is emitted.
/// </summary>
public sealed class SystemSetupActionCenterTaskProvider : IActionCenterTaskProvider
{
    private readonly Func<Guid, CancellationToken, ValueTask<SystemSetupSnapshot>> _readSnapshotAsync;
    private readonly ILogger<SystemSetupActionCenterTaskProvider> _logger;

    public SystemSetupActionCenterTaskProvider(
        Func<Guid, CancellationToken, ValueTask<SystemSetupSnapshot>> readSnapshotAsync,
        ILogger<SystemSetupActionCenterTaskProvider> logger)
    {
        _readSnapshotAsync = readSnapshotAsync;
        _logger = logger;
    }

    public string ProviderName => "system_setup";

    public async Task<IReadOnlyList<ActionCenterTaskDraft>> GenerateAsync(
        CompanyId companyId,
        UserId? userId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        SystemSetupSnapshot snapshot;
        try
        {
            snapshot = await _readSnapshotAsync(companyId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "system_setup snapshot read failed for company {CompanyId}", companyId);
            return Array.Empty<ActionCenterTaskDraft>();
        }

        var drafts = new List<ActionCenterTaskDraft>();

        if (!snapshot.SmtpConfigured)
        {
            drafts.Add(new ActionCenterTaskDraft(
                CompanyId: companyId,
                AssignedUserId: null,
                TaskType: ActionCenterTaskType.SystemSetupSmtp,
                SourceEngine: "system_setup",
                SourceType: ActionCenterTaskSourceType.Rule,
                SourceObjectId: null,
                Title: "Configure SMTP",
                Description: "Email delivery requires SMTP. Configure it under Settings to enable invoice email and verification flows.",
                Reason: "SMTP is not configured for this company.",
                EvidenceJson: System.Text.Json.JsonSerializer.Serialize(new { snapshot.SmtpConfigured }),
                Priority: ActionCenterTaskPriority.Medium,
                DueDate: null,
                ActionUrl: "/settings/notifications",
                Fingerprint: $"sys-setup:smtp:{companyId:N}"));
        }

        if (!snapshot.CompanyProfileComplete)
        {
            drafts.Add(new ActionCenterTaskDraft(
                CompanyId: companyId,
                AssignedUserId: null,
                TaskType: ActionCenterTaskType.SystemSetupCompanyProfile,
                SourceEngine: "system_setup",
                SourceType: ActionCenterTaskSourceType.Rule,
                SourceObjectId: null,
                Title: "Complete company profile",
                Description: "Add the company legal name, address, and base currency to enable proper document headers.",
                Reason: "Company profile is incomplete.",
                EvidenceJson: System.Text.Json.JsonSerializer.Serialize(new { snapshot.CompanyProfileComplete }),
                Priority: ActionCenterTaskPriority.Medium,
                DueDate: null,
                ActionUrl: "/settings/profile",
                Fingerprint: $"sys-setup:profile:{companyId:N}"));
        }

        return drafts;
    }
}

public sealed record SystemSetupSnapshot(bool SmtpConfigured, bool CompanyProfileComplete);
