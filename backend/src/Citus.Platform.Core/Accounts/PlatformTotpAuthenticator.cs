using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Citus.Platform.Core.Accounts;

public static class PlatformTotpAuthenticator
{
    private const int SecretByteLength = 20;
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    public static string GenerateSecretBase32()
    {
        Span<byte> secretBytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(secretBytes);
        return EncodeBase32(secretBytes);
    }

    public static string CreateOtpAuthUri(string issuer, string accountLabel, string secretBase32)
    {
        var normalizedIssuer = issuer.Trim();
        var normalizedAccountLabel = accountLabel.Trim();
        var label = $"{normalizedIssuer}:{normalizedAccountLabel}";

        return $"otpauth://totp/{Uri.EscapeDataString(label)}" +
               $"?secret={Uri.EscapeDataString(secretBase32.Trim())}" +
               $"&issuer={Uri.EscapeDataString(normalizedIssuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    public static bool VerifyCode(
        string secretBase32,
        string verificationCode,
        DateTimeOffset timestampUtc,
        int allowedTimeStepSkew = 1)
    {
        var normalizedCode = verificationCode.Trim();
        if (normalizedCode.Length != Digits || !normalizedCode.All(char.IsDigit))
        {
            return false;
        }

        var secret = DecodeBase32(secretBase32);
        var timeStep = timestampUtc.ToUnixTimeSeconds() / PeriodSeconds;
        for (var offset = -allowedTimeStepSkew; offset <= allowedTimeStepSkew; offset++)
        {
            if (GenerateCode(secret, timeStep + offset) == normalizedCode)
            {
                return true;
            }
        }

        return false;
    }

    private static string GenerateCode(byte[] secret, long timeStep)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        var counter = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian)
        {
            counter.AsSpan().Reverse();
        }

        counter.AsSpan().CopyTo(counterBytes);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binaryCode =
            ((hash[offset] & 0x7f) << 24) |
            (hash[offset + 1] << 16) |
            (hash[offset + 2] << 8) |
            hash[offset + 3];

        var numericCode = binaryCode % 1_000_000;
        return numericCode.ToString("D6");
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 0x1f]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1f]);
        }

        return output.ToString();
    }

    private static byte[] DecodeBase32(string base32)
    {
        var normalized = base32
            .Trim()
            .ToUpperInvariant()
            .Replace("=", string.Empty, StringComparison.Ordinal);

        var output = new List<byte>((normalized.Length * 5) / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var character in normalized)
        {
            var value = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new InvalidOperationException("TOTP secret is not a valid base32 value.")
            };

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft < 8)
            {
                continue;
            }

            output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
            bitsLeft -= 8;
        }

        return output.ToArray();
    }
}
