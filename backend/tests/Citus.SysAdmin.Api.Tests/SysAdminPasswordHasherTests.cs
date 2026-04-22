using Citus.Platform.Core.Runtime;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminPasswordHasherTests
{
    private readonly SysAdminPasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_ProducesVerifiableHash()
    {
        const string password = "ChangeMeNow123!";

        var hash = _hasher.HashPassword(password);

        Assert.NotEqual(password, hash);
        Assert.True(_hasher.VerifyPassword(password, hash));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForWrongPassword()
    {
        var hash = _hasher.HashPassword("CorrectHorseBatteryStaple");

        var matches = _hasher.VerifyPassword("wrong-password", hash);

        Assert.False(matches);
    }

    [Fact]
    public void VerifyPassword_SupportsLegacyPlaintextFallback()
    {
        const string legacyPassword = "legacy-bootstrap";

        Assert.True(_hasher.VerifyPassword(legacyPassword, legacyPassword));
        Assert.False(_hasher.VerifyPassword("other", legacyPassword));
    }
}
