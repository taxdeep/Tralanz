using System.Text;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Infrastructure.Invoices;

namespace Citus.Accounting.Api.Tests;

public sealed class InvoicePdfRendererSmokeTests
{
    public InvoicePdfRendererSmokeTests()
    {
        // Match the runtime registration so QuestPDF doesn't throw on
        // Render(). Idempotent: setting the license repeatedly is a no-op.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    [Fact]
    public void Render_ProducesValidPdfBytes()
    {
        var model = BuildSampleModel();
        var renderer = new QuestPdfInvoiceRenderer();

        var bytes = renderer.Render(model);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1024, $"PDF was unexpectedly small: {bytes.Length} bytes");

        // Every PDF starts with the magic header "%PDF-".
        var header = Encoding.ASCII.GetString(bytes, 0, 5);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public void Render_HandlesMissingOptionalFields()
    {
        // Smoke: customer with no address / phone / email, no memo, no
        // payment instructions — renderer must not throw.
        var model = BuildSampleModel() with
        {
            BillTo = new InvoiceBillToSummary("Walk-in customer", null, null, null),
            Header = BuildSampleModel().Header with { Memo = null, DueDate = null },
            Branding = InvoiceBrandingSummary.Default with { PaymentInstructions = string.Empty },
        };
        var renderer = new QuestPdfInvoiceRenderer();

        var bytes = renderer.Render(model);

        Assert.True(bytes.Length > 1024);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Builder_ProjectsReviewIntoRenderModel()
    {
        var review = new InvoiceReviewProjection(
            DisplayNumber: "INV-2026-000001",
            EntityNumber: "EN20260000A",
            DocumentDate: new DateOnly(2026, 04, 28),
            DueDate: new DateOnly(2026, 05, 28),
            Status: "posted",
            CounterpartyDisplayName: "Acme Co.",
            TransactionCurrencyCode: "CAD",
            SubtotalAmount: 100m,
            TaxAmount: 13m,
            TotalAmount: 113m,
            Memo: "Q1 retainer",
            Lines:
            [
                new InvoiceReviewLineProjection(1, "Design work", 1m, 100m, 100m, 13m)
            ]);

        var company = new CompanyProfileSnapshot(
            Id: CompanyId.FromOrdinal(1),
            EntityNumber: "EN20260000A",
            LegalName: "Tralanz Studio Ltd.",
            Email: "ops@tralanz.com",
            Phone: "+1-604-555-0100",
            AddressLine: "1 Main St.",
            City: "Vancouver",
            ProvinceState: "BC",
            PostalCode: "V6B 1A1",
            Country: "Canada",
            BaseCurrencyCode: "CAD");

        var customer = new CustomerRecord(
            Id: Guid.NewGuid(),
            CompanyId: company.Id,
            EntityNumber: "EN20260000B",
            DisplayName: "Acme Co.",
            DefaultCurrencyCode: "CAD",
            Email: "billing@acme.example",
            Phone: null,
            AddressLine: "200 Customer Ave.",
            City: "Burnaby",
            ProvinceState: "BC",
            PostalCode: "V5A 2B2",
            Country: "Canada",
            TaxId: null,
            Notes: null,
            PaymentTermId: null,
            IsActive: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var model = InvoiceRenderModelBuilder.Build(review, company, customer);

        Assert.Equal("Tralanz Studio Ltd.", model.Issuer.CompanyName);
        Assert.Equal("Acme Co.", model.BillTo.DisplayName);
        Assert.Contains("200 Customer Ave.", model.BillTo.AddressBlock);
        Assert.Contains("V5A 2B2", model.BillTo.AddressBlock);
        Assert.Equal("INV-2026-000001", model.Header.DisplayNumber);
        Assert.Single(model.Lines);
        Assert.Equal(113m, model.Totals.Total);
        Assert.Equal("CAD", model.Totals.CurrencyCode);
    }

    private static InvoiceRenderModel BuildSampleModel() => new()
    {
        Issuer = new InvoiceIssuerSummary(
            CompanyName: "Tralanz Studio Ltd.",
            CompanyCode: "EN20260000A",
            AddressBlock: "1 Main St." + Environment.NewLine + "Vancouver, BC" + Environment.NewLine + "V6B 1A1 Canada",
            Email: "ops@tralanz.com",
            Phone: "+1-604-555-0100"),
        BillTo = new InvoiceBillToSummary(
            DisplayName: "Acme Co.",
            AddressBlock: "200 Customer Ave." + Environment.NewLine + "Burnaby, BC" + Environment.NewLine + "V5A 2B2 Canada",
            Email: "billing@acme.example",
            Phone: null),
        Header = new InvoiceHeaderSummary(
            DisplayNumber: "INV-2026-000001",
            EntityNumber: "EN20260000A",
            DocumentDate: new DateOnly(2026, 04, 28),
            DueDate: new DateOnly(2026, 05, 28),
            Status: "posted",
            Memo: "Q1 retainer fees"),
        Lines =
        [
            new InvoiceRenderLine(1, "Design work — Q1", 1m, 100m, 100m, 13m),
            new InvoiceRenderLine(2, "Hosting", 3m, 25m, 75m, 9.75m),
        ],
        Totals = new InvoiceTotalsSummary(175m, 22.75m, 197.75m, "CAD"),
        Branding = InvoiceBrandingSummary.Default with
        {
            PaymentInstructions = "Pay via Interac e-Transfer to ops@tralanz.com.",
        },
    };
}
