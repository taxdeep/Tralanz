using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Platform.Infrastructure.Notifications;

/// <summary>
/// 30-second cached, decrypt-once runtime view of
/// <c>platform_ai_provider_config</c>. Singleton lifetime — cache shared
/// across every IUnityAiProvider call, regardless of which task or
/// company drove the call. SysAdmin save paths must call
/// <see cref="Invalidate"/> so a freshly rotated key takes effect on
/// the very next gateway call instead of waiting for cache expiry.
/// </summary>
public sealed class PlatformAiProviderRuntimeResolver : IPlatformAiProviderRuntimeResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IPlatformAiProviderConfigStore _store;
    private readonly IPlatformSecretProtector _protector;
    private readonly ILogger<PlatformAiProviderRuntimeResolver> _logger;
    private readonly SemaphoreSlim _mutex = new(initialCount: 1, maxCount: 1);

    private PlatformAiProviderRuntimeSnapshot? _cached;
    private DateTimeOffset _cachedAt;

    public PlatformAiProviderRuntimeResolver(
        IPlatformAiProviderConfigStore store,
        IPlatformSecretProtector protector,
        ILogger<PlatformAiProviderRuntimeResolver> logger)
    {
        _store = store;
        _protector = protector;
        _logger = logger;
    }

    public PlatformAiProviderRuntimeSnapshot? GetCurrent()
    {
        var cached = _cached;
        if (cached is null) return null;
        if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl) return cached;

        // Stale but non-null — kick off background refresh, return slightly
        // stale value so the caller doesn't block. Hot-path reads (the
        // model router) call this synchronously per gateway invocation.
        _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        return cached;
    }

    public async Task<PlatformAiProviderRuntimeSnapshot?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var row = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                _cached = null;
                _cachedAt = DateTimeOffset.UtcNow;
                return null;
            }

            string apiKey = string.Empty;
            if (row.HasApiKey)
            {
                var envelope = await _store.GetRawApiKeyAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(envelope))
                {
                    try
                    {
                        apiKey = _protector.Unprotect(envelope!);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to decrypt platform_ai_provider_config api key — protection key mismatch?");
                        apiKey = string.Empty;
                    }
                }
            }

            _cached = new PlatformAiProviderRuntimeSnapshot(
                Provider: row.Provider,
                BaseUrl: row.BaseUrl,
                Model: row.Model,
                MaxTokens: row.MaxTokens,
                Temperature: row.Temperature,
                ApiKey: apiKey,
                LoadedAt: DateTimeOffset.UtcNow);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Invalidate()
    {
        _cached = null;
        _cachedAt = default;
    }
}
