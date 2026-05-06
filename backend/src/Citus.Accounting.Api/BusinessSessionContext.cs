namespace Citus.Accounting.Api;

public sealed record class BusinessSessionContext
{
    public required UserId UserId { get; init; }

    public required CompanyId ActiveCompanyId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
