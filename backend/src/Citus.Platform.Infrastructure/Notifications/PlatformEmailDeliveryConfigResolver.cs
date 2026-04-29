using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Citus.Platform.Infrastructure.Notifications;

/// <summary>
/// Wraps <see cref="IPlatformSmtpConfigStore"/> with a 30-second cache
/// + on-demand refresh so SMTP senders don't pay a database round-trip
/// per outbound email. Decrypts the stored password through
/// <see cref="IPlatformSecretProtector"/> so the snapshot the sender
/// gets back has plaintext ready for <see cref="System.Net.Mail.SmtpClient"/>.
///
/// Singleton lifetime — cache shared across all sender invocations.
/// SaveAsync paths in the SysAdmin SMTP endpoint call
/// <see cref="Invalidate"/> so the next send picks up the freshly-
/// saved row immediately instead of waiting for the cache to expire.
/// </summary>
public sealed class PlatformEmailDeliveryConfigResolver : IPlatformEmailDeliveryConfigResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IPlatformSmtpConfigStore _store;
    private readonly IPlatformSecretProtector _protector;
    private readonly ILogger<PlatformEmailDeliveryConfigResolver> _logger;
    private readonly SemaphoreSlim _mutex = new(initialCount: 1, maxCount: 1);

    private PlatformEmailDeliverySnapshot? _cached;
    private DateTimeOffset _cachedAt;

    public PlatformEmailDeliveryConfigResolver(
        IPlatformSmtpConfigStore store,
        IPlatformSecretProtector protector,
        ILogger<PlatformEmailDeliveryConfigResolver> logger)
    {
        _store = store;
        _protector = protector;
        _logger = logger;
    }

    public PlatformEmailDeliverySnapshot? GetCurrent()
    {
        // Sync read — returns the last successfully loaded snapshot if
        // it's still warm. The IPlatformVerificationNotificationSender
        // sync interface members rely on this being non-blocking.
        var cached = _cached;
        if (cached is null) return null;
        if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl) return cached;

        // Stale but non-null — kick off a background refresh and let the
        // caller use the slightly old value. Mail sends will call
        // RefreshAsync explicitly anyway.
        _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        return cached;
    }

    public async Task<PlatformEmailDeliverySnapshot?> RefreshAsync(CancellationToken cancellationToken)
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

            // Re-read the encrypted password column directly because
            // the snapshot doesn't carry the envelope back — only the
            // resolver decrypts.
            string password = string.Empty;
            if (row.HasPassword && _store is PostgresPlatformSmtpConfigStore concrete)
            {
                var envelope = await concrete.GetRawPasswordAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(envelope))
                {
                    try
                    {
                        password = _protector.Unprotect(envelope!);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to decrypt platform_smtp_config password — protection key mismatch?");
                        password = string.Empty;
                    }
                }
            }

            _cached = new PlatformEmailDeliverySnapshot(
                Provider: row.Provider,
                FromEmail: row.FromEmail,
                FromDisplayName: row.FromDisplayName,
                Host: row.Host,
                Port: row.Port,
                UseSsl: row.UseSsl,
                Username: row.Username,
                Password: password,
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
