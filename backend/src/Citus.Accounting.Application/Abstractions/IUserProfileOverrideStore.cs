namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Per-user profile overrides applied on top of whatever the auth layer
/// returns. V1 supports a display-name override so users can rename
/// themselves without round-tripping through the (not-yet-built)
/// platform user-management API; future fields (avatar, locale) plug
/// onto the same row.
/// </summary>
public sealed record UserProfileOverrideRecord(
    UserId UserId,
    string? DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IUserProfileOverrideStore
{
    Task<UserProfileOverrideRecord?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken);

    Task<UserProfileOverrideRecord> UpsertDisplayNameAsync(
        UserId userId,
        string displayName,
        CancellationToken cancellationToken);

    Task EnsureSchemaAsync(CancellationToken cancellationToken);
}
