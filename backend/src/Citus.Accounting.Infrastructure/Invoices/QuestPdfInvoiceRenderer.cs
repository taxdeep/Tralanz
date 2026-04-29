using Citus.Accounting.Application.Invoices;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Citus.Accounting.Infrastructure.Invoices;

/// <summary>
/// QuestPDF-based invoice PDF generator. Hard-coded "default" template
/// for Batch 1 — clean, neutral, single-column header + lines table +
/// totals block. Batches 3 / 4 will turn the layout knobs into a
/// per-company InvoiceTemplate and feed values through the same
/// <see cref="InvoiceRenderModel"/> so the renderer stays the same and
/// the HTML preview can mirror the layout pixel-for-pixel.
/// </summary>
public sealed class QuestPdfInvoiceRenderer : IInvoicePdfRenderer
{
    private static readonly string PrimaryColor = Colors.Grey.Darken4;
    private static readonly string MutedColor = Colors.Grey.Darken1;
    private static readonly string SubtleColor = Colors.Grey.Medium;
    private static readonly string DividerColor = Colors.Grey.Lighten2;

    public byte[] Render(InvoiceRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Document
            .Create(container => container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(text => text.FontSize(10).FontColor(PrimaryColor));

                page.Header().Element(c => ComposeHeader(c, model));
                page.Content().PaddingVertical(16).Element(c => ComposeBody(c, model));
                page.Footer().Element(c => ComposeFooter(c, model));
            }))
            .GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, InvoiceRenderModel model)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(model.Issuer.CompanyName)
                    .FontSize(16).Bold();

                if (!string.IsNullOrWhiteSpace(model.Issuer.AddressBlock))
                {
                    col.Item().PaddingTop(4).Text(model.Issuer.AddressBlock!)
                        .FontSize(9).FontColor(MutedColor);
                }

                col.Item().PaddingTop(2).Row(meta =>
                {
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Email))
                    {
                        meta.AutoItem().Text(model.Issuer.Email!).FontSize(9).FontColor(MutedColor);
                    }
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Phone))
                    {
                        meta.AutoItem().PaddingLeft(8).Text(model.Issuer.Phone!).FontSize(9).FontColor(MutedColor);
                    }
                });
            });

            row.ConstantItem(180).AlignRight().Column(col =>
            {
                col.Item().Text("INVOICE").FontSize(20).Bold().FontColor(MutedColor);
                col.Item().PaddingTop(4).Text(model.Header.DisplayNumber)
                    .FontSize(11).Bold();
                col.Item().Text($"Internal #: {model.Header.EntityNumber}")
                    .FontSize(8).FontColor(SubtleColor);
            });
        });
    }

    private static void ComposeBody(IContainer container, InvoiceRenderModel model)
    {
        container.Column(col =>
        {
            // Bill-to + invoice meta block.
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("BILL TO").FontSize(8).Bold().FontColor(SubtleColor).LetterSpacing(0.05f);
                    c.Item().PaddingTop(4).Text(model.BillTo.DisplayName).FontSize(11).Bold();

                    if (!string.IsNullOrWhiteSpace(model.BillTo.AddressBlock))
                    {
                        c.Item().PaddingTop(2).Text(model.BillTo.AddressBlock!).FontSize(9).FontColor(MutedColor);
                    }
                    if (!string.IsNullOrWhiteSpace(model.BillTo.Email))
                    {
                        c.Item().Text(model.BillTo.Email!).FontSize(9).FontColor(MutedColor);
                    }
                    if (!string.IsNullOrWhiteSpace(model.BillTo.Phone))
                    {
                        c.Item().Text(model.BillTo.Phone!).FontSize(9).FontColor(MutedColor);
                    }
                });

                row.ConstantItem(220).Column(c =>
                {
                    AppendMetaLine(c, "Invoice date", model.Header.DocumentDate.ToString("yyyy-MM-dd"));
                    AppendMetaLine(c, "Due date",
                        model.Header.DueDate?.ToString("yyyy-MM-dd") ?? "On receipt");
                    AppendMetaLine(c, "Status", model.Header.Status);
                });
            });

            col.Item().PaddingVertical(16).LineHorizontal(0.5f).LineColor(DividerColor);

            // Lines table.
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(28);    // # column
                    cd.RelativeColumn(4);      // description
                    cd.RelativeColumn(1);      // qty
                    cd.RelativeColumn(1.4f);   // unit price
                    cd.RelativeColumn(1.5f);   // line amount
                });

                table.Header(header =>
                {
                    var headerCells = new[] { "#", "Description", "Qty", "Unit price", $"Amount ({model.Totals.CurrencyCode})" };
                    var alignRight = new[] { false, false, true, true, true };
                    for (var i = 0; i < headerCells.Length; i++)
                    {
                        var cell = header.Cell().PaddingVertical(6).PaddingHorizontal(4)
                            .BorderBottom(0.75f).BorderColor(PrimaryColor);
                        var text = cell.Text(headerCells[i]).FontSize(8).Bold().FontColor(SubtleColor).LetterSpacing(0.05f);
                        if (alignRight[i]) text.AlignRight();
                    }
                });

                foreach (var line in model.Lines)
                {
                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(DividerColor)
                        .Text(line.LineNumber.ToString());

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(DividerColor)
                        .Text(string.IsNullOrWhiteSpace(line.Description) ? "—" : line.Description);

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(DividerColor)
                        .AlignRight().Text(line.Quantity is null ? "—" : line.Quantity.Value.ToString("0.##"));

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(DividerColor)
                        .AlignRight().Text(line.UnitPrice is null ? "—" : line.UnitPrice.Value.ToString("N2"));

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(DividerColor)
                        .AlignRight().Text(line.LineAmount.ToString("N2"));
                }
            });

            // Totals block, right-aligned.
            col.Item().PaddingTop(12).AlignRight().Column(totals =>
            {
                AppendTotalRow(totals, "Subtotal", model.Totals.Subtotal, model.Totals.CurrencyCode);
                AppendTotalRow(totals, "Tax", model.Totals.Tax, model.Totals.CurrencyCode);
                totals.Item().PaddingVertical(4).Width(220).LineHorizontal(0.5f).LineColor(PrimaryColor);
                AppendTotalRow(totals, "Total", model.Totals.Total, model.Totals.CurrencyCode, bold: true);
            });

            // Notes / memo.
            if (!string.IsNullOrWhiteSpace(model.Header.Memo))
            {
                col.Item().PaddingTop(20).Column(notes =>
                {
                    notes.Item().Text("MEMO").FontSize(8).Bold().FontColor(SubtleColor).LetterSpacing(0.05f);
                    notes.Item().PaddingTop(4).Text(model.Header.Memo!).FontSize(9).FontColor(MutedColor);
                });
            }

            // Payment instructions (template-driven later; empty in v1).
            if (!string.IsNullOrWhiteSpace(model.PaymentInstructions))
            {
                col.Item().PaddingTop(16).Column(pay =>
                {
                    pay.Item().Text("PAYMENT INSTRUCTIONS").FontSize(8).Bold().FontColor(SubtleColor).LetterSpacing(0.05f);
                    pay.Item().PaddingTop(4).Text(model.PaymentInstructions).FontSize(9).FontColor(MutedColor);
                });
            }
        });
    }

    private static void ComposeFooter(IContainer container, InvoiceRenderModel model)
    {
        container.AlignCenter().Text(text =>
        {
            text.Span("Thank you for your business. ").FontSize(8).FontColor(SubtleColor);
            text.Span($"Invoice {model.Header.DisplayNumber} · ").FontSize(8).FontColor(SubtleColor);
            text.CurrentPageNumber().FontSize(8).FontColor(SubtleColor);
            text.Span(" / ").FontSize(8).FontColor(SubtleColor);
            text.TotalPages().FontSize(8).FontColor(SubtleColor);
        });
    }

    private static void AppendMetaLine(ColumnDescriptor c, string label, string value)
    {
        c.Item().PaddingBottom(2).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(8).FontColor(SubtleColor).LetterSpacing(0.05f);
            row.AutoItem().Text(value).FontSize(10);
        });
    }

    private static void AppendTotalRow(
        ColumnDescriptor c,
        string label,
        decimal amount,
        string currencyCode,
        bool bold = false)
    {
        c.Item().Width(220).Row(row =>
        {
            var labelText = row.RelativeItem().Text(label).FontSize(bold ? 11 : 9).FontColor(bold ? PrimaryColor : MutedColor);
            if (bold) labelText.Bold();

            var valueText = row.AutoItem().AlignRight().Text($"{amount:N2} {currencyCode}").FontSize(bold ? 11 : 9);
            if (bold) valueText.Bold();
        });
    }
}
