namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchQuery
{
    public required Guid CompanyId { get; init; }

    public Guid? UserId { get; init; }

    public string Context { get; init; } = string.Empty;

    public string SearchText { get; init; } = string.Empty;

    public int Take { get; init; } = 10;
}
