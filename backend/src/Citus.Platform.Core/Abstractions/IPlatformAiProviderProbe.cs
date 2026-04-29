namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Quick "is this API key valid?" check the SysAdmin AI-config page
/// fires when the operator clicks Test connection. Implementations
/// hit the cheapest possible endpoint per provider — typically a
/// /models list — so the probe is fast and unlikely to incur token
/// charges. Returns granular status so the UI can render a useful
/// message (auth failed vs network reach vs rate limit).
/// </summary>
public interface IPlatformAiProviderProbe
{
    Task<PlatformAiProbeResult> ProbeAsync(
        string provider,
        string? baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken);
}

public sealed record PlatformAiProbeResult(
    bool Succeeded,
    /// <summary>HTTP status from the provider, or null if the call
    /// never reached the network.</summary>
    int? HttpStatus,
    /// <summary>Operator-readable message — "Authenticated", "Invalid
    /// API key (401)", "Network unreachable", etc.</summary>
    string Message,
    /// <summary>Round-trip duration when known.</summary>
    TimeSpan? Elapsed);
