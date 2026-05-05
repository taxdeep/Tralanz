using SharedKernel.Identity;

namespace Tests.SharedKernel;

public sealed class EntityNumberTests
{
    [Fact]
    public void Create_Zero_ProducesAllZeroOrdinal()
    {
        Assert.Equal("EN202600000", EntityNumber.Create(2026, 0).Value);
    }

    [Fact]
    public void Create_Max_ProducesAllZ()
    {
        Assert.Equal("EN2026ZZZZZ", EntityNumber.Create(2026, EntityNumber.MaxOrdinal).Value);
    }

    [Fact]
    public void Create_RejectsYearTooLow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityNumber.Create(999, 0));
    }

    [Fact]
    public void Create_RejectsYearTooHigh()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityNumber.Create(10_000, 0));
    }

    [Fact]
    public void Create_RejectsNegativeOrdinal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityNumber.Create(2026, -1));
    }

    [Fact]
    public void Create_RejectsOverflowOrdinal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityNumber.Create(2026, EntityNumber.MaxOrdinal + 1));
    }

    [Fact]
    public void Year_RoundtripsFromCreate()
    {
        Assert.Equal(2026, EntityNumber.Create(2026, 5).Year);
        Assert.Equal(2099, EntityNumber.Create(2099, 5).Year);
    }

    [Fact]
    public void Ordinal_RoundtripsFromCreate()
    {
        var n = EntityNumber.Create(2026, 12_345);
        Assert.Equal(12_345L, n.Ordinal);
    }

    [Fact]
    public void Parse_NormalizesLowercase()
    {
        var n = EntityNumber.Parse("en2026abc12");
        Assert.Equal("EN2026ABC12", n.Value);
    }

    [Theory]
    [InlineData("ZZ202600000")]    // wrong prefix
    [InlineData("EN20260000")]     // too short
    [InlineData("EN202600000A")]   // too long
    [InlineData("EN099900000")]    // year too low (0999 < 1000)
    [InlineData("EN-02600000")]    // invalid year
    [InlineData("EN202600-00")]    // invalid base36 char
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_ReturnsFalseForInvalid(string? text)
    {
        Assert.False(EntityNumber.TryParse(text, out _));
    }

    [Fact]
    public void EqualityIsValueBased()
    {
        var a = EntityNumber.Create(2026, 100);
        var b = EntityNumber.Parse("EN20260002S"); // 100 = 2*36 + 28 → "0002S"
        Assert.Equal(a, b);
    }

    [Fact]
    public void TotalWidth_MatchesValueLength()
    {
        var n = EntityNumber.Create(2026, 0);
        Assert.Equal(EntityNumber.TotalWidth, n.Value.Length);
    }
}
