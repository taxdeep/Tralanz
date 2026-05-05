namespace Citus.Modules.UnitySearch.Domain.Shared;

public sealed record class SearchDocumentRecord(
    CompanyId CompanyId,
    string EntityType,
    Guid SourceId,
    string GroupKey,
    string PrimaryText,
    string SecondaryText,
    string SearchText,
    string ExactCodeNorm,
    string NavigationHref,
    string MetadataJson,
    DateOnly? EffectiveDate,
    decimal? Amount,
    bool IsActive,
    bool IsVoided,
    decimal RankBoost,
    long Version,
    decimal ComputedScore = 0m);
