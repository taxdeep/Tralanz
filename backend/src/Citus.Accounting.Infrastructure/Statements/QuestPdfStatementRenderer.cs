using Citus.Accounting.Application.Statements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Citus.Accounting.Infrastructure.Statements;

/// <summary>
/// QuestPDF open-item statement generator. Fixed Letter layout: issuer
/// letterhead / "statement for" party block / aging summary / open-item
/// table / total. Mirrors <c>QuestPdfInvoiceRenderer</c> but uses a fixed
/// neutral palette (statements are not template-branded).
/// </summary>
public sealed class QuestPdfStatementRenderer : IStatementPdfRenderer
{
    private static readonly string Primary = Colors.Grey.Darken4;
    private static readonly string Muted = Colors.Grey.Darken1;
    private static readonly string Subtle = Colors.Grey.Medium;
    private static readonly string Divider = Colors.Grey.Lighten2;

    public byte[] Render(StatementRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Document
            .Create(container => container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(text => text.FontSize(10).FontColor(Primary));

                page.Header().Element(c => ComposeHeader(c, model));
                page.Content().PaddingVertical(16).Element(c => ComposeBody(c, model));
                page.Footer().Element(c => ComposeFooter(c, model));
            }))
            .GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, StatementRenderModel model)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(model.Issuer.CompanyName).FontSize(16).Bold().FontColor(Primary);

                if (!string.IsNullOrWhiteSpace(model.Issuer.AddressBlock))
                {
                    col.Item().PaddingTop(4).Text(model.Issuer.AddressBlock!).FontSize(9).FontColor(Muted);
                }

                col.Item().PaddingTop(2).Row(meta =>
                {
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Email))
                    {
                        meta.AutoItem().Text(model.Issuer.Email!).FontSize(9).FontColor(Muted);
                    }
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Phone))
                    {
                        meta.AutoItem().PaddingLeft(8).Text(model.Issuer.Phone!).FontSize(9).FontColor(Muted);
                    }
                });
            });

            row.ConstantItem(180).AlignRight().Column(col =>
            {
                col.Item().Text("STATEMENT").FontSize(20).Bold().FontColor(Muted);
                col.Item().PaddingTop(4).Text($"{model.PartyKind} account").FontSize(10).FontColor(Subtle);
                col.Item().Text($"As of {model.AsOfDate:yyyy-MM-dd}").FontSize(10).Bold().FontColor(Primary);
            });
        });
    }

    private static void ComposeBody(IContainer container, StatementRenderModel model)
    {
        container.Column(col =>
        {
            // "Statement for" party block + open-balance call-out.
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("STATEMENT FOR").FontSize(8).Bold().FontColor(Subtle).LetterSpacing(0.05f);
                    c.Item().PaddingTop(4).Text(model.Party.DisplayName).FontSize(11).Bold().FontColor(Primary);
                    c.Item().Text(model.Party.EntityNumber).FontSize(8).FontColor(Subtle);

                    if (!string.IsNullOrWhiteSpace(model.Party.AddressBlock))
                    {
                        c.Item().PaddingTop(2).Text(model.Party.AddressBlock!).FontSize(9).FontColor(Muted);
                    }
                    if (!string.IsNullOrWhiteSpace(model.Party.Email))
                    {
                        c.Item().Text(model.Party.Email!).FontSize(9).FontColor(Muted);
                    }
                });

                row.ConstantItem(220).AlignRight().Column(c =>
                {
                    c.Item().Text("OPEN BALANCE").FontSize(8).Bold().FontColor(Subtle).LetterSpacing(0.05f);
                    c.Item().PaddingTop(4).Text($"{model.Totals.TotalOutstanding:N2} {model.BaseCurrencyCode}")
                        .FontSize(16).Bold().FontColor(Primary);
                    c.Item().Text($"Overdue {model.Totals.TotalOverdue:N2} {model.BaseCurrencyCode}")
                        .FontSize(9).FontColor(Muted);
                });
            });

            col.Item().PaddingVertical(14).LineHorizontal(0.5f).LineColor(Divider);

            // Aging summary band.
            col.Item().Row(row =>
            {
                AppendBucket(row, "Current", model.Totals.Current, model.BaseCurrencyCode);
                AppendBucket(row, "1-30", model.Totals.Days1To30, model.BaseCurrencyCode);
                AppendBucket(row, "31-60", model.Totals.Days31To60, model.BaseCurrencyCode);
                AppendBucket(row, "61-90", model.Totals.Days61To90, model.BaseCurrencyCode);
                AppendBucket(row, "> 90", model.Totals.DaysOver90, model.BaseCurrencyCode);
            });

            col.Item().PaddingTop(14);

            if (model.Lines.Count == 0)
            {
                col.Item().PaddingTop(8).Text($"No open items as of {model.AsOfDate:yyyy-MM-dd}. The balance is zero.")
                    .FontSize(10).FontColor(Muted);
                return;
            }

            // Open-item table.
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(2);      // document
                    cd.RelativeColumn(1.4f);   // doc date
                    cd.RelativeColumn(1.4f);   // due date
                    cd.RelativeColumn(1.2f);   // days past due
                    cd.RelativeColumn(1.2f);   // bucket
                    cd.RelativeColumn(1.8f);   // open amount
                });

                table.Header(header =>
                {
                    var cells = new[] { "Document", "Doc date", "Due date", "Days past due", "Bucket", $"Open amount ({model.BaseCurrencyCode})" };
                    var alignRight = new[] { false, false, false, true, false, true };
                    for (var i = 0; i < cells.Length; i++)
                    {
                        var cell = header.Cell().PaddingVertical(6).PaddingHorizontal(4)
                            .BorderBottom(0.75f).BorderColor(Primary);
                        var text = cell.Text(cells[i]).FontSize(8).Bold().FontColor(Subtle).LetterSpacing(0.05f);
                        if (alignRight[i]) text.AlignRight();
                    }
                });

                foreach (var line in model.Lines)
                {
                    Cell(table).Text(line.DisplayNumber);
                    Cell(table).Text(line.DocumentDate.ToString("yyyy-MM-dd"));
                    Cell(table).Text(line.DueDate is null ? "—" : line.DueDate.Value.ToString("yyyy-MM-dd"));
                    Cell(table).AlignRight().Text(line.DueDate is null ? "—" : Math.Max(0, line.DaysPastDue).ToString());
                    Cell(table).Text(BucketLabel(line.AgingBucket));
                    Cell(table).AlignRight().Text($"{line.OpenAmountBase:N2}");
                }
            });

            // Total open balance.
            col.Item().PaddingTop(12).AlignRight().Column(totals =>
            {
                totals.Item().Width(240).LineHorizontal(0.5f).LineColor(Primary);
                totals.Item().PaddingTop(4).Width(240).Row(row =>
                {
                    row.RelativeItem().Text("Total open balance").FontSize(11).Bold().FontColor(Primary);
                    row.AutoItem().AlignRight().Text($"{model.Totals.TotalOutstanding:N2} {model.BaseCurrencyCode}")
                        .FontSize(11).Bold().FontColor(Primary);
                });
            });
        });
    }

    private static void ComposeFooter(IContainer container, StatementRenderModel model)
    {
        container.AlignCenter().Text(text =>
        {
            text.Span($"{model.Issuer.CompanyName} · {model.PartyKind} statement as of {model.AsOfDate:yyyy-MM-dd} · ")
                .FontSize(8).FontColor(Subtle);
            text.CurrentPageNumber().FontSize(8).FontColor(Subtle);
            text.Span(" / ").FontSize(8).FontColor(Subtle);
            text.TotalPages().FontSize(8).FontColor(Subtle);
        });
    }

    private static IContainer Cell(TableDescriptor table) =>
        table.Cell().PaddingVertical(6).PaddingHorizontal(4).BorderBottom(0.25f).BorderColor(Divider);

    private static void AppendBucket(RowDescriptor row, string label, decimal amount, string currency)
    {
        row.RelativeItem().Column(c =>
        {
            c.Item().Text(label).FontSize(8).Bold().FontColor(Subtle).LetterSpacing(0.05f);
            c.Item().PaddingTop(2).Text($"{amount:N2} {currency}").FontSize(10).FontColor(Primary);
        });
    }

    private static string BucketLabel(string bucket) => bucket switch
    {
        "current" => "Current",
        "1_30" => "1-30",
        "31_60" => "31-60",
        "61_90" => "61-90",
        "over_90" => "> 90",
        _ => bucket
    };
}
