namespace Citus.Modules.UnitySearch.Domain.Shared;

public sealed record class SearchPolicyDefinition(
    string Context,
    IReadOnlyList<string> EntityTypes,
    bool EnforceActiveOnly,
    bool EnforceBusinessEligibility);
