namespace Citus.Accounting.Domain.Currencies;

public sealed record CurrencyCode
{
    public CurrencyCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Currency code is required.", nameof(value));
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(static c => !char.IsLetter(c)))
        {
            throw new ArgumentException("Currency code must be a 3-letter ISO code.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record Money
{
    public Money(decimal amount, CurrencyCode currencyCode)
    {
        Amount = amount;
        CurrencyCode = currencyCode ?? throw new ArgumentNullException(nameof(currencyCode));
    }

    public decimal Amount { get; }

    public CurrencyCode CurrencyCode { get; }

    public static Money Zero(CurrencyCode currencyCode) => new(0m, currencyCode);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, CurrencyCode);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (CurrencyCode != other.CurrencyCode)
        {
            throw new InvalidOperationException("Money values must share the same currency.");
        }
    }
}

public sealed record ExchangeRate
{
    public ExchangeRate(CurrencyCode baseCurrencyCode, CurrencyCode quoteCurrencyCode, decimal rate)
    {
        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "Exchange rate must be greater than zero.");
        }

        BaseCurrencyCode = baseCurrencyCode ?? throw new ArgumentNullException(nameof(baseCurrencyCode));
        QuoteCurrencyCode = quoteCurrencyCode ?? throw new ArgumentNullException(nameof(quoteCurrencyCode));
        Rate = rate;
    }

    public CurrencyCode BaseCurrencyCode { get; }

    public CurrencyCode QuoteCurrencyCode { get; }

    public decimal Rate { get; }
}

public sealed record FxSnapshotRef(
    Guid SnapshotId,
    CurrencyCode BaseCurrencyCode,
    CurrencyCode QuoteCurrencyCode,
    decimal Rate,
    DateOnly RequestedDate,
    DateOnly EffectiveDate,
    string SourceSemantics);
