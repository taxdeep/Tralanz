namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Symmetric AES-GCM protection for secrets that operators configure
/// through the SysAdmin UI but the platform persists in plain Postgres
/// columns — SMTP passwords, AI provider API keys, eventually
/// third-party webhook signing secrets. Keep API surface small: the
/// caller hands plaintext in, gets a self-describing prefixed
/// ciphertext back, and reverses on read. <see cref="IsProtected"/>
/// lets a UI render "•••• configured" without ever materializing the
/// plaintext server-side just to detect presence.
///
/// Algorithmically equivalent to the internal PlatformTotpSecretProtector
/// (AES-GCM, 12-byte nonce, 16-byte tag, key derived from
/// <c>PlatformIdentity:TotpProtectionKey</c> via SHA-256). The wire
/// prefix is intentionally different so the two protectors cannot read
/// each other's payloads even though they share a key — TOTP secrets
/// and operator-entered API keys must stay isolated.
/// </summary>
public interface IPlatformSecretProtector
{
    /// <summary>
    /// Encrypts plaintext with AES-GCM and returns a prefixed
    /// base64 envelope ("secret-v1:..."). Empty / whitespace input
    /// throws — the caller should decide whether absent is meaningful
    /// (e.g. SMTP password not yet set) before calling.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Strings that don't carry the
    /// protector's prefix are returned unchanged so legacy plaintext
    /// rows still work during migrations. Empty / whitespace returns
    /// empty.
    /// </summary>
    string Unprotect(string protectedValue);

    /// <summary>
    /// True when <paramref name="value"/> looks like ciphertext from
    /// this protector. Used by the SysAdmin UI to render "configured /
    /// not configured" badges without round-tripping plaintext.
    /// </summary>
    bool IsProtected(string? value);
}
