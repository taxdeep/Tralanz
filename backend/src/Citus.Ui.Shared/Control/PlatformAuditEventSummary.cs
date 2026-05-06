namespace Citus.Ui.Shared.Control;

public sealed record class PlatformAuditEventSummary
{
    public Guid AuditId { get; init; }

    public CompanyId? CompanyId { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string ScopeLabel { get; init; } = "Platform";

    public string ActorType { get; init; } = string.Empty;

    public UserId? ActorId { get; init; }

    public string ActorDisplayName { get; init; } = string.Empty;

    public string ActorEmail { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public string EntityLabel { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> Highlights { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAtUtc { get; init; }
}
