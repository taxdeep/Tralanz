namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchQuery
{
    public required CompanyId CompanyId { get; init; }

    public UserId? UserId { get; init; }

    public string Context { get; init; } = string.Empty;

    public string SearchText { get; init; } = string.Empty;

    public int Take { get; init; } = 10;

    /// <summary>
    /// Permission tokens the calling user holds in the active company.
    /// Fed verbatim to the query SQL's <c>required_permissions[] &amp;&amp; @user_permissions</c>
    /// overlap check. Empty list = no permissions held → the query sees
    /// only rows whose <c>required_permissions</c> are also empty
    /// (static "jump to" rows). Endpoint callers should populate this
    /// from <c>BusinessSessionContext.Roles</c>.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
}
