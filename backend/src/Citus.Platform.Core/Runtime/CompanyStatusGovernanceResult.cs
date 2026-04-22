namespace Citus.Platform.Core.Runtime;

public sealed record class CompanyStatusGovernanceResult
{
    public Guid CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string LegalName { get; init; } = string.Empty;

    public string PreviousStatus { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
