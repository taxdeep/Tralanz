using Citus.Accounting.Application.Statements;
using Citus.Accounting.Infrastructure.Statements;
using QuestPDF.Infrastructure;
using Xunit;

namespace Citus.Accounting.Api.Tests;

/// <summary>
/// Smoke tests for the open-item statement PDF renderer. QuestPDF only
/// surfaces layout faults at GeneratePdf() time, so these render both the
/// populated and the empty (zero open items) statement and assert a valid,
/// non-trivial PDF comes back.
/// </summary>
public sealed class StatementPdfRenderSmokeTests
{
    static StatementPdfRenderSmokeTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static StatementRenderModel SampleModel(bool withLines) => new()
    {
        Issuer = new StatementIssuerSummary(
            "Acme Holdings Inc.", "C000001",
            "123 Main St\nVancouver, BC, V6B 1A1\nCanada", "ar@acme.test", "+1 555 0100"),
        Party = new StatementPartySummary(
            "Acme Corp", "EN2026FBZQK",
            "987 Market St\nSeattle, WA, 98101\nUSA", "ap@acmecorp.test", null),
        PartyKind = "Customer",
        AsOfDate = new DateOnly(2026, 6, 4),
        BaseCurrencyCode = "CAD",
        Lines = withLines
            ? new[]
            {
                new StatementRenderLine("INV-000006", "invoice", new DateOnly(2026, 5, 8), new DateOnly(2026, 6, 7), 0, "current", 715.46m, 525.00m, "USD"),
                new StatementRenderLine("INV-000007", "invoice", new DateOnly(2026, 5, 8), new DateOnly(2026, 4, 1), 64, "31_60", 763.16m, 560.00m, "USD"),
                new StatementRenderLine("CN-000001", "credit_note", new DateOnly(2026, 5, 10), null, 0, "current", -100.00m, -100.00m, "CAD"),
            }
            : Array.Empty<StatementRenderLine>(),
        Totals = new StatementTotalsSummary(
            withLines ? 2869.52m : 0m, withLines ? 763.16m : 0m, 0m, 0m, 0m,
            withLines ? 763.16m : 0m, withLines ? 2869.52m : 0m)
    };

    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 200, $"PDF was suspiciously small ({bytes.Length} bytes).");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Render_WithOpenItems_ProducesPdf() =>
        AssertIsPdf(new QuestPdfStatementRenderer().Render(SampleModel(withLines: true)));

    [Fact]
    public void Render_WithNoOpenItems_ProducesPdf() =>
        AssertIsPdf(new QuestPdfStatementRenderer().Render(SampleModel(withLines: false)));
}
