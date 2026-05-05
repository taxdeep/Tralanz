using SharedKernel.Identity;

namespace Tests.SharedKernel;

public sealed class Base36Tests
{
    [Theory]
    [InlineData(0L, 6, "000000")]
    [InlineData(1L, 6, "000001")]
    [InlineData(35L, 6, "00000Z")]
    [InlineData(36L, 6, "000010")]
    [InlineData(37L, 6, "000011")]
    [InlineData(2_176_782_335L, 6, "ZZZZZZ")] // 36^6 - 1
    [InlineData(60_466_175L, 5, "ZZZZZ")]      // 36^5 - 1
    public void Encode_ProducesPaddedUppercaseString(long value, int width, string expected)
    {
        Assert.Equal(expected, Base36.Encode(value, width));
    }

    [Fact]
    public void Encode_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base36.Encode(-1, 6));
    }

    [Fact]
    public void Encode_RejectsOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base36.Encode(2_176_782_336L, 6));
    }

    [Fact]
    public void Encode_RejectsZeroWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base36.Encode(0, 0));
    }

    [Theory]
    [InlineData("000000", 0L)]
    [InlineData("000001", 1L)]
    [InlineData("00000Z", 35L)]
    [InlineData("000010", 36L)]
    [InlineData("ZZZZZZ", 2_176_782_335L)]
    public void Decode_RoundtripsEncode(string text, long expected)
    {
        Assert.Equal(expected, Base36.Decode(text));
    }

    [Fact]
    public void Decode_RejectsLowercase()
    {
        Assert.Throws<FormatException>(() => Base36.Decode("abc"));
    }

    [Fact]
    public void Decode_RejectsInvalidCharacters()
    {
        Assert.Throws<FormatException>(() => Base36.Decode("00@000"));
    }

    [Fact]
    public void TryDecode_ReturnsFalseForLowercase()
    {
        Assert.False(Base36.TryDecode("abc", out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalseForEmpty()
    {
        Assert.False(Base36.TryDecode(ReadOnlySpan<char>.Empty, out _));
    }

    [Fact]
    public void IsValid_TrueForUppercaseAndDigits()
    {
        Assert.True(Base36.IsValid("ABC123"));
        Assert.True(Base36.IsValid("ZZZZZZ"));
        Assert.True(Base36.IsValid("000000"));
    }

    [Fact]
    public void IsValid_FalseForLowercase()
    {
        Assert.False(Base36.IsValid("abc123"));
    }

    [Fact]
    public void IsValid_FalseForSpecialChars()
    {
        Assert.False(Base36.IsValid("AB-123"));
    }

    [Fact]
    public void IsValid_FalseForEmpty()
    {
        Assert.False(Base36.IsValid(ReadOnlySpan<char>.Empty));
    }
}
