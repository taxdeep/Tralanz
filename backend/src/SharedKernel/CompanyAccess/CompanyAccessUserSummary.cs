using SharedKernel.Identity;

namespace SharedKernel.CompanyAccess;

public sealed record class CompanyAccessUserSummary
{
    public UserId Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
