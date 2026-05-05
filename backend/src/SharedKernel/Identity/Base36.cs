namespace SharedKernel.Identity;

public static class Base36
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Encode(long value, int width)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");
        }
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        Span<char> buffer = stackalloc char[width];
        var remaining = value;
        for (var i = width - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(remaining % 36)];
            remaining /= 36;
        }
        if (remaining != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Value does not fit in {width} base-36 characters.");
        }
        return new string(buffer);
    }

    public static long Decode(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }

        long result = 0;
        foreach (var ch in text)
        {
            var digit = DigitValue(ch);
            if (digit < 0)
            {
                throw new FormatException($"Invalid base-36 character: '{ch}'.");
            }
            result = checked(result * 36 + digit);
        }
        return result;
    }

    public static bool TryDecode(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        long result = 0;
        foreach (var ch in text)
        {
            var digit = DigitValue(ch);
            if (digit < 0)
            {
                return false;
            }
            try
            {
                result = checked(result * 36 + digit);
            }
            catch (OverflowException)
            {
                return false;
            }
        }
        value = result;
        return true;
    }

    public static bool IsValid(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }
        foreach (var ch in text)
        {
            if (DigitValue(ch) < 0)
            {
                return false;
            }
        }
        return true;
    }

    private static int DigitValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            return ch - '0';
        }
        if (ch >= 'A' && ch <= 'Z')
        {
            return 10 + (ch - 'A');
        }
        return -1;
    }
}
