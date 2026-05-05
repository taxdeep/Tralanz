using SharedKernel.Identity;

namespace Tests.SharedKernel;

public sealed class UserIdTests
{
    [Fact]
    public void FromOrdinal_Zero_ProducesAllZeros()
    {
        Assert.Equal("U000000", UserId.FromOrdinal(0).Value);
    }

    [Fact]
    public void FromOrdinal_Max_ProducesAllZ()
    {
        Assert.Equal("UZZZZZZ", UserId.FromOrdinal(UserId.MaxOrdinal).Value);
    }

    [Fact]
    public void FromOrdinal_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserId.FromOrdinal(-1));
    }

    [Fact]
    public void FromOrdinal_RejectsOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserId.FromOrdinal(UserId.MaxOrdinal + 1));
    }

    [Fact]
    public void Ordinal_RoundtripsFromOrdinal()
    {
        var id = UserId.FromOrdinal(123_456);
        Assert.Equal(123_456L, id.Ordinal);
    }

    [Fact]
    public void Parse_NormalizesLowercase()
    {
        var id = UserId.Parse("u7k3f2a");
        Assert.Equal("U7K3F2A", id.Value);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var id = UserId.Parse("  U000001  ");
        Assert.Equal("U000001", id.Value);
    }

    [Theory]
    [InlineData("X000001")]   // wrong prefix
    [InlineData("U00001")]    // too short
    [InlineData("U0000001")]  // too long
    [InlineData("U-00001")]   // invalid char
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_ReturnsFalseForInvalid(string? text)
    {
        Assert.False(UserId.TryParse(text, out _));
    }

    [Fact]
    public void Parse_ThrowsForInvalid()
    {
        Assert.Throws<FormatException>(() => UserId.Parse("not-an-id"));
    }

    [Fact]
    public void EqualityIsValueBased()
    {
        var a = UserId.FromOrdinal(46);
        var b = UserId.Parse("U00001A"); // 46 = 1*36 + 10 → "00001A"
        Assert.Equal(a, b);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var id = UserId.FromOrdinal(1);
        Assert.Equal("U000001", id.ToString());
    }
}
