using System.Security.Cryptography;
using System.Text;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Citus.Platform.Infrastructure.Persistence;

/// <summary>
/// Public AES-GCM secret protector used by SysAdmin-configured secrets
/// (SMTP password, AI provider API key, etc). Algorithmically identical
/// to <see cref="PlatformTotpSecretProtector"/> but uses a distinct
/// envelope prefix so the two protectors cannot accidentally read each
/// other's payloads — TOTP secrets and operator-entered API keys must
/// stay isolated even though they share a key.
/// </summary>
public sealed class PlatformSecretProtector : IPlatformSecretProtector
{
    private const string ProtectionKeyConfigPath = "PlatformIdentity:TotpProtectionKey";
    private const string DevelopmentFallbackKey = "citus-development-totp-protection-key";
    public const string EncryptedPayloadPrefix = "secret-v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IConfiguration _configuration;

    public PlatformSecretProtector(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new InvalidOperationException("Cannot protect an empty secret.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var key = ResolveKey();

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[nonce.Length + cipherBytes.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipherBytes.Length, tag.Length);

        return $"{EncryptedPayloadPrefix}{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        var normalized = protectedValue.Trim();
        if (!normalized.StartsWith(EncryptedPayloadPrefix, StringComparison.Ordinal))
        {
            // Legacy / migration path — operator-entered plaintext that
            // was persisted before encryption was applied. Returning it
            // verbatim keeps existing data working while the next save
            // will rewrite it as a protected envelope.
            return normalized;
        }

        var payload = Convert.FromBase64String(normalized[EncryptedPayloadPrefix.Length..]);
        if (payload.Length <= NonceSize + TagSize)
        {
            throw new InvalidOperationException("Protected secret payload is invalid.");
        }

        var nonce = payload.AsSpan(0, NonceSize).ToArray();
        var cipherLength = payload.Length - NonceSize - TagSize;
        var cipherBytes = payload.AsSpan(NonceSize, cipherLength).ToArray();
        var tag = payload.AsSpan(NonceSize + cipherLength, TagSize).ToArray();
        var plainBytes = new byte[cipherBytes.Length];
        var key = ResolveKey();

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public bool IsProtected(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.AsSpan().Trim().StartsWith(EncryptedPayloadPrefix.AsSpan(), StringComparison.Ordinal);

    private byte[] ResolveKey()
    {
        var configuredValue = _configuration[ProtectionKeyConfigPath];
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(configuredValue.Trim()));
        }

        if (IsDevelopmentEnvironment())
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(DevelopmentFallbackKey));
        }

        throw new InvalidOperationException(
            $"'{ProtectionKeyConfigPath}' must be configured before protected secrets can be used.");
    }

    private bool IsDevelopmentEnvironment()
    {
        var environmentName =
            _configuration["DOTNET_ENVIRONMENT"] ??
            _configuration["ASPNETCORE_ENVIRONMENT"] ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
