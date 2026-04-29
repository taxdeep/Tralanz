using Citus.Platform.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Citus.SysAdmin.Api.Tests;

public sealed class PlatformSecretProtectorTests
{
    [Fact]
    public void Protect_Then_Unprotect_RoundTripsPlaintext()
    {
        var protector = BuildProtector();
        var plaintext = "sk-test-1234567890abcdef";

        var protectedValue = protector.Protect(plaintext);
        var unprotected = protector.Unprotect(protectedValue);

        Assert.Equal(plaintext, unprotected);
        Assert.NotEqual(plaintext, protectedValue);
        Assert.StartsWith(PlatformSecretProtector.EncryptedPayloadPrefix, protectedValue);
    }

    [Fact]
    public void Protect_ProducesDistinctCiphertextForSamePlaintext()
    {
        // Random nonce per call — same plaintext must encrypt differently
        // every time. If this ever returns equal payloads we've lost
        // semantic security.
        var protector = BuildProtector();
        var plaintext = "smtp-password-example";

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, protector.Unprotect(first));
        Assert.Equal(plaintext, protector.Unprotect(second));
    }

    [Fact]
    public void Unprotect_PassesThroughLegacyPlaintext()
    {
        // Migration scenario: a row stored before the protector was wired
        // still contains plaintext. Unprotect must return it unchanged so
        // the next save can rewrite it as ciphertext.
        var protector = BuildProtector();

        var roundTripped = protector.Unprotect("legacy-plain-value");

        Assert.Equal("legacy-plain-value", roundTripped);
    }

    [Fact]
    public void Unprotect_ReturnsEmptyForBlankInput()
    {
        var protector = BuildProtector();
        Assert.Equal(string.Empty, protector.Unprotect(string.Empty));
        Assert.Equal(string.Empty, protector.Unprotect("   "));
    }

    [Fact]
    public void Protect_ThrowsOnEmptyPlaintext()
    {
        var protector = BuildProtector();
        Assert.Throws<InvalidOperationException>(() => protector.Protect(string.Empty));
        Assert.Throws<InvalidOperationException>(() => protector.Protect("   "));
    }

    [Fact]
    public void IsProtected_DetectsPrefixedCiphertext()
    {
        var protector = BuildProtector();
        var plain = "openai-key";
        var sealed_ = protector.Protect(plain);

        Assert.True(protector.IsProtected(sealed_));
        Assert.False(protector.IsProtected(plain));
        Assert.False(protector.IsProtected(string.Empty));
        Assert.False(protector.IsProtected(null));
    }

    [Fact]
    public void IsolatedFromTotpEnvelopePrefix()
    {
        // TOTP protector uses "enc-v1:" — verify our prefix is genuinely
        // different so a TOTP secret accidentally piped here returns
        // unchanged (legacy-plain-value path) instead of being silently
        // decrypted with the wrong context.
        var protector = BuildProtector();
        Assert.False(protector.IsProtected("enc-v1:NotOurs=="));
    }

    private static PlatformSecretProtector BuildProtector()
    {
        // Inline protection key avoids needing the Development env var
        // fallback for tests running under whatever environment the CI
        // box sets.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlatformIdentity:TotpProtectionKey"] = "test-only-protection-key-must-be-non-empty"
            })
            .Build();
        return new PlatformSecretProtector(configuration);
    }
}
