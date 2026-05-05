using System.Text.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Shell;

namespace Citus.SysAdmin.Blazor.Services;

public sealed class SysAdminControlClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<SysAdminControlClient> logger)
{
    public async Task<SysAdminControlContextSummary?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<SysAdminControlContextSummary>("control/context", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load SysAdmin control context.");
            return null;
        }
    }

    public async Task<IReadOnlyList<CompanyWorkspaceSummary>> ListCompaniesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<CompanyWorkspaceSummary>>("control/companies", cancellationToken) ??
                Array.Empty<CompanyWorkspaceSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load managed company workspaces.");
            return Array.Empty<CompanyWorkspaceSummary>();
        }
    }

    public async Task<IReadOnlyList<ManagedUserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<ManagedUserSummary>>("control/users", cancellationToken) ??
                Array.Empty<ManagedUserSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load managed users.");
            return Array.Empty<ManagedUserSummary>();
        }
    }

    public async Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListOpenMfaRecoveryRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<MfaRecoveryRequestSummary>>(
                    "control/mfa-recovery-requests",
                    cancellationToken) ??
                Array.Empty<MfaRecoveryRequestSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load MFA recovery requests.");
            return Array.Empty<MfaRecoveryRequestSummary>();
        }
    }

    public async Task<IReadOnlyList<MfaRecoveryRequestSummary>> ListAccountMfaRecoveryHistoryAsync(
        Guid accountId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<MfaRecoveryRequestSummary>>(
                    $"control/accounts/{accountId}/mfa-recovery-history?limit={Math.Clamp(limit, 1, 50)}",
                    cancellationToken) ??
                Array.Empty<MfaRecoveryRequestSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load MFA recovery history for account {AccountId}.", accountId);
            return Array.Empty<MfaRecoveryRequestSummary>();
        }
    }

    public async Task<IReadOnlyList<PlatformMfaTimelineEntrySummary>> ListAccountMfaTimelineAsync(
        Guid accountId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<PlatformMfaTimelineEntrySummary>>(
                    $"control/accounts/{accountId}/mfa-timeline?limit={Math.Clamp(limit, 1, 50)}",
                    cancellationToken) ??
                Array.Empty<PlatformMfaTimelineEntrySummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load MFA timeline for account {AccountId}.", accountId);
            return Array.Empty<PlatformMfaTimelineEntrySummary>();
        }
    }

    public async Task<IReadOnlyList<ManagedCompanyMembershipSummary>> ListCompanyMembershipsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<ManagedCompanyMembershipSummary>>(
                    $"control/companies/{companyId}/memberships",
                    cancellationToken) ??
                Array.Empty<ManagedCompanyMembershipSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load managed memberships for company {CompanyId}.", companyId);
            return Array.Empty<ManagedCompanyMembershipSummary>();
        }
    }

    public async Task<SysAdminControlContextSummary?> SetActiveCompanyAsync(CompanyId companyId, CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PutAsync($"control/active-company/{companyId}", content: null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Active company switch returned non-success status code {StatusCode} for company {CompanyId}.",
                    response.StatusCode,
                    companyId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SysAdminControlContextSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to switch active company to {CompanyId}.", companyId);
            return null;
        }
    }

    public async Task<MaintenanceStateSummary?> UpdateMaintenanceAsync(
        MaintenanceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PutAsJsonAsync("control/maintenance", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Maintenance update returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MaintenanceStateSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to update maintenance state.");
            return null;
        }
    }

    public async Task<NotificationReadinessSummary?> GetNotificationReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<NotificationReadinessSummary>(
                "control/notification-readiness",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load notification readiness state.");
            return null;
        }
    }

    public async Task<IReadOnlyList<PlatformAuditEventSummary>> ListRecentAuditEventsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            return await httpClient.GetFromJsonAsync<IReadOnlyList<PlatformAuditEventSummary>>(
                    $"control/audit-events?limit={Math.Clamp(limit, 1, 200)}",
                    cancellationToken) ??
                Array.Empty<PlatformAuditEventSummary>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load platform audit events.");
            return Array.Empty<PlatformAuditEventSummary>();
        }
    }

    public async Task<NotificationReadinessSummary?> UpdateNotificationReadinessAsync(
        NotificationReadinessUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PutAsJsonAsync("control/notification-readiness", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Notification readiness update returned non-success status code {StatusCode}.", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<NotificationReadinessSummary>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to update notification readiness state.");
            return null;
        }
    }

    public async Task<(NotificationTestSendResult? Result, string? Error)> SendNotificationTestAsync(
        NotificationTestSendRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsJsonAsync(
                "control/notification-readiness/test-send",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<NotificationTestSendResult>(cancellationToken), null);
            }

            return (null, await ReadMessageAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to send notification readiness test message.");
            return (null, "Notification test send failed.");
        }
    }

    public async Task<bool> SetCompanyStatusAsync(
        CompanyId companyId,
        string status,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PutAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/companies/{companyId}/status",
                new CompanyStatusUpdatePayload(status, reason)),
            "Company status update",
            cancellationToken);

        return outcome.Succeeded;
    }

    public async Task<bool> SetAccountStatusAsync(
        Guid accountId,
        string status,
        DateTimeOffset? lockedUntilUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PutAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/accounts/{accountId}/status",
                new AccountStatusUpdatePayload(status, lockedUntilUtc, reason)),
            "Account status update",
            cancellationToken);

        return outcome.Succeeded;
    }

    public Task<CommandOutcome> RequestPasswordResetAsync(
        Guid accountId,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PostAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/accounts/{accountId}/password-reset-requests",
                new PasswordResetRequestPayload(reason)),
            "Password reset request",
            cancellationToken);

    public Task<CommandOutcome> ResetMfaAsync(
        Guid accountId,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PostAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/accounts/{accountId}/mfa-reset",
                new AccountMfaResetPayload(reason)),
            "MFA reset",
            cancellationToken);

    public Task<CommandOutcome> ReviewMfaRecoveryRequestAsync(
        Guid requestId,
        string decision,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PutAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/mfa-recovery-requests/{requestId}/decision",
                new MfaRecoveryReviewPayload(decision, reason)),
            "MFA recovery review",
            cancellationToken);

    public Task<CommandOutcome> ExecuteMfaRecoveryRequestAsync(
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PostAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/mfa-recovery-requests/{requestId}/execute",
                new MfaRecoveryExecutePayload(reason)),
            "MFA recovery execution",
            cancellationToken);

    public async Task<bool> ChangeMembershipRoleAsync(
        CompanyId companyId,
        Guid membershipId,
        string role,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SendGovernanceCommandWithOutcomeAsync(
            static (client, request, token) => client.PutAsJsonAsync(request.Path, request.Payload, token),
            new GovernanceCommandRequest(
                $"control/companies/{companyId}/memberships/{membershipId}/role",
                new CompanyMembershipRoleUpdatePayload(role, reason)),
            "Membership role update",
            cancellationToken);

        return outcome.Succeeded;
    }

    private async Task<CommandOutcome> SendGovernanceCommandWithOutcomeAsync(
        Func<HttpClient, GovernanceCommandRequest, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        GovernanceCommandRequest request,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            ApplySessionHeader();
            using var response = await sendAsync(httpClient, request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new CommandOutcome(true, string.Empty);
            }

            var message = await ReadMessageAsync(response, cancellationToken) ??
                $"{operationName} failed with status code {(int)response.StatusCode}.";
            logger.LogWarning(
                "{OperationName} returned non-success status code {StatusCode}: {Message}",
                operationName,
                response.StatusCode,
                message);
            return new CommandOutcome(false, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{OperationName} failed.", operationName);
            return new CommandOutcome(false, $"{operationName} failed.");
        }
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }

            if (document.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.GetString();
            }

            if (document.RootElement.TryGetProperty("title", out var title))
            {
                return title.GetString();
            }
        }
        catch (JsonException)
        {
            return content;
        }

        return content;
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);

        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }

    private sealed record GovernanceCommandRequest(string Path, object Payload);

    private sealed record CompanyStatusUpdatePayload(string Status, string Reason);

    private sealed record AccountStatusUpdatePayload(string Status, DateTimeOffset? LockedUntilUtc, string Reason);

    private sealed record PasswordResetRequestPayload(string Reason);

    private sealed record AccountMfaResetPayload(string Reason);

    private sealed record MfaRecoveryReviewPayload(string Decision, string Reason);

    private sealed record MfaRecoveryExecutePayload(string Reason);

    private sealed record CompanyMembershipRoleUpdatePayload(string Role, string Reason);

    public sealed record CommandOutcome(bool Succeeded, string Message);
}
