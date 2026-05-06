namespace Citus.Accounting.Domain.Common;

public readonly record struct PostingRunId(Guid Value)
{
    public static PostingRunId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
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
