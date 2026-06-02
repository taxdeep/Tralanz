using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.SalesTax;
using SharedKernel.Identity;

namespace Citus.Accounting.Application.Tests;

public sealed class SalesTaxEngineTests
{
    [Fact]
    public async Task CalculatesSalesTaxCollectedForExclusiveTaxCode()
    {
        var codeId = Guid.NewGuid();
        var engine = new ServerAuthoritativeSalesTaxEngine(new FakeSalesTaxStore(
            TaxCode(codeId, "GST", 5m, SalesTaxRecoverability.Recoverable)));

        var result = await engine.CalculateAsync(
            CompanyId.Parse("C000001"),
            new SalesTaxPreviewRequest(
                DocumentSide: SalesTaxDocumentSide.Sales,
                DocumentDate: new DateOnly(2026, 5, 29),
                TaxMode: "exclusive",
                Amount: 100m,
                TaxCodeId: codeId,
                CurrencyCode: "CAD"),
            CancellationToken.None);

        Assert.Equal(100m, result.TaxableAmount);
        Assert.Equal(5m, result.TaxAmount);
        Assert.Equal(105m, result.GrossAmount);
        Assert.Equal("tax_collected", result.Lines.Single().ReportingCategory);
    }

    [Fact]
    public async Task SplitsPurchaseTaxRecoverability()
    {
        var codeId = Guid.NewGuid();
        var gst = Component(codeId, "GST", 5m, SalesTaxRecoverability.Recoverable, 1);
        var pst = Component(codeId, "PST_BC", 7m, SalesTaxRecoverability.NonRecoverable, 2);
        var engine = new ServerAuthoritativeSalesTaxEngine(new FakeSalesTaxStore(
            TaxCode(codeId, "GST_PST_BC", gst, pst)));

        var result = await engine.CalculateAsync(
            CompanyId.Parse("C000001"),
            new SalesTaxPreviewRequest(
                DocumentSide: SalesTaxDocumentSide.Purchase,
                DocumentDate: new DateOnly(2026, 5, 29),
                TaxMode: "exclusive",
                Amount: 100m,
                TaxCodeId: codeId,
                CurrencyCode: "CAD"),
            CancellationToken.None);

        Assert.Equal(12m, result.TaxAmount);
        Assert.Equal(5m, result.RecoverableAmount);
        Assert.Equal(7m, result.NonRecoverableAmount);
    }

    [Fact]
    public async Task BacksOutTaxInclusiveBase()
    {
        var codeId = Guid.NewGuid();
        var engine = new ServerAuthoritativeSalesTaxEngine(new FakeSalesTaxStore(
            TaxCode(codeId, "GST", 5m, SalesTaxRecoverability.Recoverable)));

        var result = await engine.CalculateAsync(
            CompanyId.Parse("C000001"),
            new SalesTaxPreviewRequest(
                DocumentSide: SalesTaxDocumentSide.Sales,
                DocumentDate: new DateOnly(2026, 5, 29),
                TaxMode: "inclusive",
                Amount: 105m,
                TaxCodeId: codeId,
                CurrencyCode: "CAD"),
            CancellationToken.None);

        Assert.Equal(100m, Math.Round(result.TaxableAmount, 2));
        Assert.Equal(5m, Math.Round(result.TaxAmount, 2));
        Assert.Equal(105m, result.GrossAmount);
    }

    private static SalesTaxCodeRecord TaxCode(Guid id, string code, decimal rate, string recoverability) =>
        TaxCode(id, code, Component(id, code, rate, recoverability, 1));

    private static SalesTaxCodeRecord TaxCode(
        Guid id,
        string code,
        params SalesTaxCodeComponentRecord[] components) =>
        new(
            Id: id,
            CompanyId: CompanyId.Parse("C000001"),
            EntityNumber: "EN202600001",
            Code: code,
            Name: code,
            Treatment: SalesTaxTreatment.Taxable,
            AppliesTo: TaxCodeAppliesTo.Both,
            SalesRatePercent: components.Where(c => c.AppliesTo is TaxCodeAppliesTo.Sales or TaxCodeAppliesTo.Both).Sum(c => c.RatePercent),
            PurchaseRatePercent: components.Where(c => c.AppliesTo is TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both).Sum(c => c.RatePercent),
            RegistrationNumber: "TEST-RT0001",
            IsGroup: components.Length > 1,
            IsActive: true,
            Components: components,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static SalesTaxCodeComponentRecord Component(
        Guid taxCodeId,
        string code,
        decimal rate,
        string recoverability,
        int sequence) =>
        new(
            TaxCodeId: taxCodeId,
            TaxComponentId: Guid.NewGuid(),
            Code: code,
            Name: code,
            TaxType: "gst_hst",
            AppliesTo: TaxCodeAppliesTo.Both,
            RatePercent: rate,
            Sequence: sequence,
            CompoundMode: "none",
            Treatment: SalesTaxTreatment.Taxable,
            Recoverability: recoverability,
            RecoverablePercent: 100m,
            RegistrationNumber: "TEST-RT0001");

    private sealed class FakeSalesTaxStore(params SalesTaxCodeRecord[] taxCodes) : ISalesTaxStore
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SalesTaxCodeRecord>> ListTaxCodesAsync(
            CompanyId companyId,
            bool includeInactive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SalesTaxCodeRecord>>(taxCodes);

        public Task<SalesTaxCodeRecord> CreateTaxCodeAsync(
            CompanyId companyId,
            SalesTaxCodeUpsertInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SalesTaxCodeRecord?> UpdateTaxCodeAsync(
            CompanyId companyId,
            Guid taxCodeId,
            SalesTaxCodeUpsertInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SalesTaxReportSummaryRow>> GetSummaryReportAsync(
            CompanyId companyId,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SalesTaxReportSummaryRow>>(Array.Empty<SalesTaxReportSummaryRow>());

        public Task<IReadOnlyList<SalesTaxReportDetailRow>> GetDetailReportAsync(
            CompanyId companyId,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SalesTaxReportDetailRow>>(Array.Empty<SalesTaxReportDetailRow>());
    }
}
