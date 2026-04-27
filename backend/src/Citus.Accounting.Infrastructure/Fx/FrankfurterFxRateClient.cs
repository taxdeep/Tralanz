namespace Citus.Accounting.Infrastructure.Fx;

public sealed class FrankfurterRateProviderStub
{
    public const string ProviderKey = "frankfurter";
    public const string ProviderBaseUrl = "https://api.frankfurter.dev";

    public string Describe() =>
        "Lookup-only Frankfurter adapter placeholder. Formal save/post flows must use locally stored FX snapshots only.";
}
