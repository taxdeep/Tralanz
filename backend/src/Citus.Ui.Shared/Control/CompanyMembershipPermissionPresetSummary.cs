namespace Citus.Ui.Shared.Control;

/// <summary>
/// Transport DTO for SysAdmin → "Apply Permission Preset" picker.
/// Mirrors <c>Modules.CompanyAccess.Memberships.CompanyMembershipPermissionPresetOption</c>
/// so the Blazor host can deserialize without dragging the business
/// module into its csproj.
/// </summary>
public sealed record class CompanyMembershipPermissionPresetSummary
{
    public string Code { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}
