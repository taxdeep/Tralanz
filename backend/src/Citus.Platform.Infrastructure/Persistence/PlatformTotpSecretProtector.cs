using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Citus.Platform.Infrastructure.Persistence;

internal sealed class PlatformTotpSecretProtector(IConfiguration configuration)
{
    private const string ProtectionKeyConfigPath = "PlatformIdentity:TotpProtectionKey";
    private const string DevelopmentFallbackKey = "citus-development-totp-protection-key";
    private const string EncryptedPayloadPrefix = "enc-v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string secretBase32)
    {
        if (string.IsNullOrWhiteSpace(secretBase32))
        {
            throw new InvalidOperationException("TOTP secret cannot be empty.");
        }

        var secretBytes = Encoding.UTF8.GetBytes(secretBase32.Trim());
        var cipherBytes = new byte[secretBytes.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var key = ResolveKey();

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, secretBytes, cipherBytes, tag);

        var payload = new byte[nonce.Length + cipherBytes.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipherBytes.Length, tag.Length);

        return $"{EncryptedPayloadPrefix}{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(string storedSecret)
    {
        if (string.IsNullOrWhiteSpace(storedSecret))
        {
            return string.Empty;
        }

        var normalized = storedSecret.Trim();
        if (!normalized.StartsWith(EncryptedPayloadPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        var payload = Convert.FromBase64String(normalized[EncryptedPayloadPrefix.Length..]);
        if (payload.Length <= NonceSize + TagSize)
        {
            throw new InvalidOperationException("Protected TOTP secret payload is invalid.");
        }

        var nonce = payload.AsSpan(0, NonceSize).ToArray();
        var cipherLength = payload.Length - NonceSize - TagSize;
        var cipherBytes = payload.AsSpan(NonceSize, cipherLength).ToArray();
        var tag = payload.AsSpan(NonceSize + cipherLength, TagSize).ToArray();
        var plainBytes = new byte[cipherBytes.Length];
        var key = ResolveKey();

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes).Trim();
    }

    private byte[] ResolveKey()
    {
        var configuredValue = configuration[ProtectionKeyConfigPath];
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(configuredValue.Trim()));
        }

        if (IsDevelopmentEnvironment())
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(DevelopmentFallbackKey));
        }

        throw new InvalidOperationException(
            $"'{ProtectionKeyConfigPath}' must be configured before protected TOTP secrets can be used.");
    }

    private bool IsDevelopmentEnvironment()
    {
        var environmentName =
            configuration["DOTNET_ENVIRONMENT"] ??
            configuration["ASPNETCORE_ENVIRONMENT"] ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
