namespace Citus.Accounting.Domain.Common;

public readonly record struct CompanyId(Guid Value)
{
    public static CompanyId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public readonly record struct PostingRunId(Guid Value)
{
    public static PostingRunId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public sealed record EntityNumber
{
    public EntityNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Entity number is required.", nameof(value));
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (!normalized.StartsWith("EN", StringComparison.Ordinal) || normalized.Length < 6)
        {
            throw new ArgumentException("Entity number must use the EN-prefixed format.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DocumentNumber
{
    public DocumentNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Document number is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}
