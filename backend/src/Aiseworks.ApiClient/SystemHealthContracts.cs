namespace Aiseworks.ApiClient;

public interface IAiseworksSystemHealthClient
{
    Task<SystemHealthSnapshot> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record SystemHealthSnapshot(
    string OverallStatus,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ServiceHealthProbe> Probes);

public sealed record ServiceHealthProbe(
    string Name,
    Uri Target,
    string Status,
    bool IsHealthy);
