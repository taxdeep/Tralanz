namespace Citus.Accounting.Api;

public sealed record class BusinessSessionContext
{
    public required Guid UserId { get; init; }

    public required Guid ActiveCompanyId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
