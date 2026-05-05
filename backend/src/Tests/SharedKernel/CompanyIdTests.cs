using SharedKernel.Identity;

namespace Tests.SharedKernel;

public sealed class CompanyIdTests
{
    [Fact]
    public void FromOrdinal_Zero_ProducesAllZeros()
    {
        Assert.Equal("C000000", CompanyId.FromOrdinal(0).Value);
    }

    [Fact]
    public void FromOrdinal_Max_ProducesAllZ()
    {
        Assert.Equal("CZZZZZZ", CompanyId.FromOrdinal(CompanyId.MaxOrdinal).Value);
    }

    [Fact]
    public void FromOrdinal_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanyId.FromOrdinal(-1));
    }

    [Fact]
    public void FromOrdinal_RejectsOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanyId.FromOrdinal(CompanyId.MaxOrdinal + 1));
    }

    [Fact]
    public void Ordinal_RoundtripsFromOrdinal()
    {
        var id = CompanyId.FromOrdinal(987_654);
        Assert.Equal(987_654L, id.Ordinal);
    }

    [Fact]
    public void Parse_NormalizesLowercase()
    {
        var id = CompanyId.Parse("cabc123");
        Assert.Equal("CABC123", id.Value);
    }

    [Theory]
    [InlineData("U000001")]   // wrong prefix
    [InlineData("C00001")]    // too short
    [InlineData("C0000001")]  // too long
    [InlineData("C-00001")]   // invalid char
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_ReturnsFalseForInvalid(string? text)
    {
        Assert.False(CompanyId.TryParse(text, out _));
    }

    [Fact]
    public void EqualityIsValueBased()
    {
        var a = CompanyId.FromOrdinal(7);
        var b = CompanyId.Parse("C000007");
        Assert.Equal(a, b);
    }

    [Fact]
    public void UserAndCompanyIdsAreNotConfusable()
    {
        var u = UserId.FromOrdinal(1);
        var c = CompanyId.FromOrdinal(1);
        Assert.NotEqual(u.Value, c.Value);
        Assert.False(CompanyId.TryParse(u.Value, out _));
        Assert.False(UserId.TryParse(c.Value, out _));
    }
}
