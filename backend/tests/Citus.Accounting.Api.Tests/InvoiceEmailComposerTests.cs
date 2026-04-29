using Citus.Accounting.Application.Invoices;

namespace Citus.Accounting.Api.Tests;

public sealed class InvoiceEmailComposerTests
{
    [Fact]
    public void Compose_BuildsSubjectFromInvoiceNumberAndIssuer()
    {
        var model = BuildModel();

        var result = InvoiceEmailComposer.Compose(model, operatorMessage: null);

        Assert.Equal("Invoice INV-2026-000001 from Tralanz Studio Ltd.", result.Subject);
    }

    [Fact]
    public void Compose_PlainTextIncludesTotalAndDueDate()
    {
        var model = BuildModel();

        var result = InvoiceEmailComposer.Compose(model, operatorMessage: null);

        Assert.Contains("197.75 CAD", result.PlainTextBody);
        Assert.Contains("2026-05-28", result.PlainTextBody);
        Assert.Contains("Tralanz Studio Ltd.", result.PlainTextBody);
    }

    [Fact]
    public void Compose_DueOnReceiptWhenDueDateMissing()
    {
        var model = BuildModel() with
        {
            Header = BuildModel().Header with { DueDate = null }
        };

        var result = InvoiceEmailComposer.Compose(model, operatorMessage: null);

        Assert.Contains("On receipt", result.PlainTextBody);
        Assert.Contains("On receipt", result.HtmlBody);
    }

    [Fact]
    public void Compose_OperatorMessageAppearsInBothBodies()
    {
        var model = BuildModel();
        var note = "Hi Acme — final invoice for Q1, please remit by month end.";

        var result = InvoiceEmailComposer.Compose(model, operatorMessage: note);

        Assert.Contains(note, result.PlainTextBody);
        Assert.Contains(note, result.HtmlBody);
    }

    [Fact]
    public void Compose_EncodesHtmlSpecialCharactersInOperatorMessage()
    {
        var model = BuildModel();
        var note = "Note: <script>alert('x')</script> & friends";

        var result = InvoiceEmailComposer.Compose(model, operatorMessage: note);

        // HTML body must escape the angle brackets so <script> can't run
        // when the recipient opens the email in a webmail client. Plain
        // text passes through verbatim — that channel renders no markup.
        Assert.DoesNotContain("<script>alert", result.HtmlBody);
        Assert.Contains("&lt;script&gt;", result.HtmlBody);
        Assert.Contains("<script>alert", result.PlainTextBody);
    }

    [Fact]
    public void Compose_PaymentInstructionsBlockOnlyAppearsWhenSet()
    {
        var modelWithoutPay = BuildModel() with { PaymentInstructions = string.Empty };
        var resultEmpty = InvoiceEmailComposer.Compose(modelWithoutPay, operatorMessage: null);
        Assert.DoesNotContain("Payment instructions", resultEmpty.HtmlBody);

        var resultFilled = InvoiceEmailComposer.Compose(BuildModel(), operatorMessage: null);
        Assert.Contains("Payment instructions", resultFilled.HtmlBody);
    }

    private static InvoiceRenderModel BuildModel() => new()
    {
        Issuer = new InvoiceIssuerSummary(
            CompanyName: "Tralanz Studio Ltd.",
            CompanyCode: "EN20260000000001",
            AddressBlock: null,
            Email: "ops@tralanz.com",
            Phone: null),
        BillTo = new InvoiceBillToSummary(
            DisplayName: "Acme Co.",
            AddressBlock: null,
            Email: "billing@acme.example",
            Phone: null),
        Header = new InvoiceHeaderSummary(
            DisplayNumber: "INV-2026-000001",
            EntityNumber: "EN20260000000002",
            DocumentDate: new DateOnly(2026, 04, 28),
            DueDate: new DateOnly(2026, 05, 28),
            Status: "posted",
            Memo: null),
        Lines = Array.Empty<InvoiceRenderLine>(),
        Totals = new InvoiceTotalsSummary(175m, 22.75m, 197.75m, "CAD"),
        PaymentInstructions = "Pay via Interac e-Transfer to ops@tralanz.com.",
    };
}
