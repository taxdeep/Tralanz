namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// SysAdmin-managed SMTP configuration. The platform stores exactly one
/// row (sentinel id) so reads are always a single GetAsync call. Writes
/// go through UpsertAsync which never lets the password leak back —
/// the password lives only in <see cref="PlatformSmtpConfigUpsertRequest.NewPassword"/>
/// for the lifetime of the call, then gets encrypted at rest.
///
/// Storage replaces the legacy <c>appsettings.PlatformNotifications</c>
/// section: the SMTP senders read DB rows via
/// <see cref="IPlatformEmailDeliveryConfigResolver"/> and never see the
/// appsettings shape.
/// </summary>
public interface IPlatformSmtpConfigStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Returns the singleton config row, or null if no row has
    /// been written yet (fresh install).</summary>
    Task<PlatformSmtpConfigSnapshot?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Insert or update the singleton row. When
    /// <see cref="PlatformSmtpConfigUpsertRequest.NewPassword"/> is null
    /// the existing encrypted password is preserved; passing a new
    /// non-empty string rewrites the protected envelope.</summary>
    Task<PlatformSmtpConfigSnapshot> UpsertAsync(
        PlatformSmtpConfigUpsertRequest request,
        Guid updatedByUserId,
        CancellationToken cancellationToken);
}

/// <summary>
/// What SysAdmin reads back. Carries every plaintext field plus a
/// <see cref="HasPassword"/> flag so the UI can render
/// "•••• configured" without ever seeing the password value. The raw
/// encrypted envelope is NOT exposed here — only the resolver decrypts.
/// </summary>
public sealed record PlatformSmtpConfigSnapshot(
    string Provider,
    string FromEmail,
    string FromDisplayName,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    bool HasPassword,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);

public sealed record PlatformSmtpConfigUpsertRequest(
    string Provider,
    string FromEmail,
    string FromDisplayName,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    /// <summary>New plaintext password to encrypt + store. Null means
    /// "leave the existing encrypted password alone". Empty string is
    /// also treated as "no change" so a blank password input doesn't
    /// accidentally clear the configured value. To deliberately remove
    /// the password use <see cref="ClearPassword"/>.</summary>
    string? NewPassword,
    /// <summary>When true, sets the encrypted password column to null.
    /// Useful when migrating a provider away from SMTP entirely.</summary>
    bool ClearPassword);
