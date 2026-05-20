using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the PR-4E/PR-4F permission-management endpoints.
/// All five read/write surfaces consolidated here so the
/// PermissionsPage holds a single dependency.
///
/// Failure semantics:
/// <list type="bullet">
///   <item>List operations return empty arrays on transport failure —
///     keeps the UI rendering an empty state instead of crashing.</item>
///   <item>Grant / revoke return a typed
///     <see cref="PermissionMutationOutcome"/> with
///     <c>Succeeded=false</c> + a user-displayable error message;
///     the page renders it as a toast / inline error.</item>
/// </list>
/// </summary>
public sealed class PermissionManagementClient(
    HttpClient httpClient,
    ILogger<PermissionManagementClient> logger)
{
    public async Task<IReadOnlyList<CompanyMemberDto>> ListMembersAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<CompanyMemberDto[]>(
                $"accounting/memberships?companyId={Uri.EscapeDataString(companyId.Value ?? string.Empty)}",
                cancellationToken);
            return rows ?? Array.Empty<CompanyMemberDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list company members for {CompanyId}.", companyId);
            return Array.Empty<CompanyMemberDto>();
        }
    }

    public async Task<IReadOnlyList<PermissionRegistryEntryDto>> ListAssignableTokensAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await httpClient.GetFromJsonAsync<PermissionRegistryEntryDto[]>(
                "accounting/permissions/registry", cancellationToken);
            return rows ?? Array.Empty<PermissionRegistryEntryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to list permission registry tokens.");
            return Array.Empty<PermissionRegistryEntryDto>();
        }
    }

    public async Task<UserPermissionSnapshotDto?> GetUserPermissionsAsync(
        CompanyId companyId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<UserPermissionSnapshotDto>(
                $"accounting/memberships/{Uri.EscapeDataString(targetUserId)}/permissions?companyId={Uri.EscapeDataString(companyId.Value ?? string.Empty)}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load permissions for user {TargetUserId}.", targetUserId);
            return null;
        }
    }

    public async Task<PermissionMutationOutcome> GrantAsync(
        CompanyId companyId,
        string targetUserId,
        string permissionToken,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(
            $"accounting/memberships/{Uri.EscapeDataString(targetUserId)}/permissions/grant",
            companyId, permissionToken, action: "grant", cancellationToken);

    public async Task<PermissionMutationOutcome> RevokeAsync(
        CompanyId companyId,
        string targetUserId,
        string permissionToken,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(
            $"accounting/memberships/{Uri.EscapeDataString(targetUserId)}/permissions/revoke",
            companyId, permissionToken, action: "revoke", cancellationToken);

    private async Task<PermissionMutationOutcome> MutateAsync(
        string url,
        CompanyId companyId,
        string permissionToken,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                url,
                new { CompanyId = companyId, PermissionToken = permissionToken },
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PermissionMutationResultDto>(cancellationToken);
                return new PermissionMutationOutcome(
                    Succeeded: true,
                    Applied: result?.Applied ?? false,
                    ResultCode: result?.ResultCode ?? "Allowed",
                    Message: result?.ResultMessage ?? string.Empty);
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var (detail, code) = ParseProblem(raw);
            return new PermissionMutationOutcome(
                Succeeded: false,
                Applied: false,
                ResultCode: code,
                Message: detail);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to {Action} permission token {Token} for {TargetUserId}.",
                action, permissionToken, url);
            return new PermissionMutationOutcome(
                Succeeded: false,
                Applied: false,
                ResultCode: "Transport",
                Message: "Unable to reach the server. Please try again.");
        }
    }

    private static (string Detail, string Code) ParseProblem(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("Request failed.", "Unknown");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "Request failed."
                : "Request failed.";
            var code = root.TryGetProperty("resultCode", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "Unknown"
                : "Unknown";
            return (detail, code);
        }
        catch (JsonException)
        {
            return (raw, "Unknown");
        }
    }
}

public sealed record CompanyMemberDto(
    string UserId,
    bool IsOwner,
    bool IsActive,
    string Status,
    string Email,
    string DisplayName,
    string Username);

public sealed record PermissionRegistryEntryDto(
    string PermissionToken,
    string ModuleKey,
    string GroupKey,
    string ActionKey,
    string Description,
    bool IsHighRisk,
    bool IsAssignable);

public sealed record UserPermissionSnapshotDto(
    CompanyId CompanyId,
    string UserId,
    bool IsOwner,
    IReadOnlyList<UserGrantDto> ActiveGrants,
    IReadOnlyList<UserGrantAuthorityDto> ActiveGrantAuthorities);

public sealed record UserGrantDto(
    string PermissionToken,
    string GrantedByUserId,
    DateTimeOffset GrantedAtUtc,
    bool IsActive);

public sealed record UserGrantAuthorityDto(
    string GrantablePermissionToken,
    bool CanGrant,
    bool CanRevoke,
    string GrantedByOwnerUserId,
    DateTimeOffset GrantedAtUtc,
    bool IsActive);

public sealed record PermissionMutationResultDto(
    CompanyId CompanyId,
    string ActorUserId,
    string TargetUserId,
    string PermissionToken,
    string Action,
    bool Applied,
    string ResultCode,
    string ResultMessage);

public sealed record PermissionMutationOutcome(
    bool Succeeded,
    bool Applied,
    string ResultCode,
    string Message);
