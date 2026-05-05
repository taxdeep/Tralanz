using Modules.AR.CreditApplication;
using Modules.AR.CustomerCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.Company;

namespace Tests.AR;

public sealed class CreditApplicationDraftPreparationWorkflowTests
{
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareDraftAsync_UsesCustomerCurrencyWhenNoOverrideProvided()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var store = new StubStore
        {
            Candidates =
            [
                CreateCandidate(sourceId, "credit_note", "credit", "USD"),
                CreateCandidate(targetId, "invoice", "debit", "USD")
            ]
        };

        var workflow = new CreditApplicationDraftPreparationWorkflow(
            store,
            new StubCustomerCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD"]));

        var result = await workflow.PrepareDraftAsync(
            new CreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                CustomerId,
                new DateOnly(2026, 4, 14),
                null,
                null),
            [new CreditApplicationDraftLine(sourceId, targetId, 100m)],
            CancellationToken.None);

        Assert.Equal("USD", result.DocumentCurrencyCode);
        Assert.Equal("USD", store.LastPreparation?.DocumentCurrencyCode);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCurrencyOverrideWhenLocked()
    {
        var store = new StubStore();
        var workflow = new CreditApplicationDraftPreparationWorkflow(
            store,
            new StubCustomerCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD", "EUR"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new CreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                CustomerId,
                new DateOnly(2026, 4, 14),
                "EUR",
                null),
            [new CreditApplicationDraftLine(Guid.NewGuid(), Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCrossCurrencyApplicationTargetsInPhaseOne()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var store = new StubStore
        {
            Candidates =
            [
                CreateCandidate(sourceId, "credit_note", "credit", "USD"),
                CreateCandidate(targetId, "invoice", "debit", "USD")
            ]
        };

        var workflow = new CreditApplicationDraftPreparationWorkflow(
            store,
            new StubCustomerCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("CAD", ["CAD", "USD", "EUR"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new CreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                CustomerId,
                new DateOnly(2026, 4, 14),
                null,
                null),
            [new CreditApplicationDraftLine(Guid.NewGuid(), Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("same-currency application", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CreditApplicationOpenItemCandidate CreateCandidate(
        Guid openItemId,
        string sourceType,
        string balanceSide,
        string currencyCode) =>
        new(
            openItemId,
            CustomerId,
            sourceType,
            Guid.NewGuid(),
            sourceType == "invoice" ? "INV-000001" : "CN-000001",
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 5, 1),
            currencyCode,
            "CAD",
            100m,
            100m,
            137.89m,
            balanceSide,
            "open");

    private sealed class StubStore : ICreditApplicationDraftPreparationStore
    {
        public CreditApplicationDraftPreparation? LastPreparation { get; private set; }

        public IReadOnlyList<CreditApplicationOpenItemCandidate> Candidates { get; set; } = [];

        public Task<IReadOnlyList<CreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
            CompanyId companyId,
            Guid customerId,
            string documentCurrencyCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(Candidates.Where(candidate =>
                string.Equals(candidate.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase)).ToArray()
                as IReadOnlyList<CreditApplicationOpenItemCandidate>);

        public Task<CreditApplicationDraftResult> PrepareDraftAsync(
            CreditApplicationDraftPreparation preparation,
            CancellationToken cancellationToken)
        {
            LastPreparation = preparation;
            return Task.FromResult(new CreditApplicationDraftResult(
                Guid.NewGuid(),
                "EN202600000001",
                "CA-000001",
                preparation.DocumentCurrencyCode,
                preparation.BaseCurrencyCode,
                100m,
                0m,
                preparation.Lines.Count,
                "draft"));
        }
    }

    private sealed class StubCustomerCurrencyWorkflow : ICustomerCurrencyWorkflow
    {
        private readonly string _currencyCode;

        public StubCustomerCurrencyWorkflow(string currencyCode)
        {
            _currencyCode = currencyCode;
        }

        public Task<CustomerCurrencyPreference> GetPreferenceAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CustomerCurrencyPreference(
                customerId,
                CompanyId,
                "Acme Retail",
                _currencyCode,
                CurrencyLocked: true,
                HasTransactionHistory: true));

        public Task<CustomerCurrencyChangeResult> ChangeDefaultCurrencyAsync(
            Guid customerId,
            string currencyCode,
            UserId userId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCompanyCurrencyCatalog : ICompanyCurrencyCatalog
    {
        private readonly string _baseCurrency;
        private readonly IReadOnlyList<string> _enabledCurrencies;

        public StubCompanyCurrencyCatalog(string baseCurrency, IReadOnlyList<string> enabledCurrencies)
        {
            _baseCurrency = baseCurrency;
            _enabledCurrencies = enabledCurrencies;
        }

        public Task<CompanyCurrencyProfile> GetProfileAsync(
            CompanyId companyId,
            CancellationToken cancellationToken)
        {
            var currencies = _enabledCurrencies
                .Select(code => new CompanyCurrencyOption(
                    code,
                    code,
                    string.Equals(code, _baseCurrency, StringComparison.OrdinalIgnoreCase),
                    true))
                .ToArray();

            return Task.FromResult(new CompanyCurrencyProfile(
                companyId,
                "Northwind Studio Ltd.",
                _baseCurrency,
                currencies.Any(currency => !currency.IsBaseCurrency),
                currencies));
        }
    }
}
