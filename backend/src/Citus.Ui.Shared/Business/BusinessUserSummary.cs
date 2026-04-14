namespace Citus.Ui.Shared.Business;

public sealed record class BusinessUserSummary
{
    public Guid Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
