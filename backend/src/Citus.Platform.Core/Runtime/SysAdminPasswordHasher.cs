using System.Security.Cryptography;
using System.Text;

namespace Citus.Platform.Core.Runtime;

public sealed class SysAdminPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int IterationCount = 100_000;
    private const string FormatMarker = "pbkdf2-sha256";

    public string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password is required.");
        }

        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            password.Trim(),
            salt.ToArray(),
            IterationCount,
            HashAlgorithmName.SHA256,
            KeySize);

        return $"{FormatMarker}${IterationCount}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derivedKey)}";
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        var segments = passwordHash.Split('$', StringSplitOptions.TrimEntries);
        if (segments.Length == 4 &&
            string.Equals(segments[0], FormatMarker, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[1], out var iterations))
        {
            var salt = Convert.FromBase64String(segments[2]);
            var expected = Convert.FromBase64String(segments[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password.Trim(),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password.Trim());
        var hashBytes = Encoding.UTF8.GetBytes(passwordHash.Trim());
        return CryptographicOperations.FixedTimeEquals(passwordBytes, hashBytes);
    }
}
