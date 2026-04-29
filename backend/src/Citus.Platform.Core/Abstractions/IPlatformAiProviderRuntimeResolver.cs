namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Runtime-side view of the SysAdmin-managed AI provider config:
/// the snapshot returned here carries the <em>decrypted</em> API key so
/// runtime IUnityAiProvider implementations don't have to know about
/// the platform secret-protection envelope.
///
/// Mirrors <see cref="IPlatformEmailDeliveryConfigResolver"/> for SMTP —
/// 30-second in-memory cache, single point of decryption, explicit
/// <see cref="Invalidate"/> hook for the SysAdmin save path so a freshly
/// rotated key takes effect on the very next gateway call.
/// </summary>
public interface IPlatformAiProviderRuntimeResolver
{
    /// <summary>
    /// Returns the cached snapshot if warm, otherwise null. Non-blocking.
    /// Stale-but-non-null reads kick a background refresh.
    /// </summary>
    PlatformAiProviderRuntimeSnapshot? GetCurrent();

    /// <summary>
    /// Forces a fresh read of <c>platform_ai_provider_config</c> +
    /// decryption. Always returns the latest committed row, even if
    /// the cache was warm.
    /// </summary>
    Task<PlatformAiProviderRuntimeSnapshot?> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates the cache. Call after any write to the AI provider
    /// config so the next runtime call re-reads.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Decrypted runtime view of <c>platform_ai_provider_config</c>. Empty
/// <see cref="ApiKey"/> means the row has no key configured (probably
/// disabled). The runtime gateway should treat empty key the same as
/// provider=<c>disabled</c>.
/// </summary>
public sealed record PlatformAiProviderRuntimeSnapshot(
    string Provider,
    string? BaseUrl,
    string Model,
    int MaxTokens,
    double Temperature,
    string ApiKey,
    DateTimeOffset LoadedAt);
