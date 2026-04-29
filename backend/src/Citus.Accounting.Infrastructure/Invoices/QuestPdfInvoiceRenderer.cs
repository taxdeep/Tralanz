using Citus.Accounting.Application.Invoices;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Citus.Accounting.Infrastructure.Invoices;

/// <summary>
/// QuestPDF-based invoice PDF generator. Layout is fixed (Letter,
/// header / bill-to / lines table / totals / footer); branding —
/// primary color, accent color, tagline, footer note, tax-column
/// visibility — is driven by <see cref="InvoiceBrandingSummary"/>
/// surfaced from the company's default <see cref="InvoiceTemplate"/>.
/// </summary>
public sealed class QuestPdfInvoiceRenderer : IInvoicePdfRenderer
{
    public byte[] Render(InvoiceRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Resolve a Palette once per Render so every Compose helper sees
        // the same brand colors without each having to re-read model.Branding.
        var palette = Palette.From(model.Branding);

        return Document
            .Create(container => container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(text => text.FontSize(10).FontColor(palette.Primary));

                page.Header().Element(c => ComposeHeader(c, model, palette));
                page.Content().PaddingVertical(16).Element(c => ComposeBody(c, model, palette));
                page.Footer().Element(c => ComposeFooter(c, model, palette));
            }))
            .GeneratePdf();
    }

    /// <summary>
    /// Resolved color set used across the renderer. Derived once per
    /// Render call so Compose helpers don't have to re-parse hex codes.
    /// </summary>
    private readonly record struct Palette(
        string Primary,
        string Muted,
        string Subtle,
        string Divider)
    {
        public static Palette From(InvoiceBrandingSummary branding) => new(
            Primary: SanitizeHex(branding.PrimaryColorHex, fallback: Colors.Grey.Darken4),
            Muted: SanitizeHex(branding.AccentColorHex, fallback: Colors.Grey.Darken1),
            Subtle: Colors.Grey.Medium,
            Divider: Colors.Grey.Lighten2);

        private static string SanitizeHex(string raw, string fallback)
        {
            // QuestPDF accepts "#RRGGBB" / "#RRGGBBAA" / "#RGB". Anything
            // weird (operator typo, missing #) falls back to the neutral
            // grey so we never throw inside the renderer.
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
            return trimmed.Length is 4 or 7 or 9 ? trimmed : fallback;
        }
    }

    private static void ComposeHeader(IContainer container, InvoiceRenderModel model, Palette palette)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(model.Issuer.CompanyName)
                    .FontSize(16).Bold().FontColor(palette.Primary);

                if (!string.IsNullOrWhiteSpace(model.Branding.Tagline))
                {
                    col.Item().PaddingTop(2).Text(model.Branding.Tagline!)
                        .FontSize(10).FontColor(palette.Muted);
                }

                if (!string.IsNullOrWhiteSpace(model.Issuer.AddressBlock))
                {
                    col.Item().PaddingTop(4).Text(model.Issuer.AddressBlock!)
                        .FontSize(9).FontColor(palette.Muted);
                }

                col.Item().PaddingTop(2).Row(meta =>
                {
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Email))
                    {
                        meta.AutoItem().Text(model.Issuer.Email!).FontSize(9).FontColor(palette.Muted);
                    }
                    if (!string.IsNullOrWhiteSpace(model.Issuer.Phone))
                    {
                        meta.AutoItem().PaddingLeft(8).Text(model.Issuer.Phone!).FontSize(9).FontColor(palette.Muted);
                    }
                });
            });

            row.ConstantItem(180).AlignRight().Column(col =>
            {
                col.Item().Text("INVOICE").FontSize(20).Bold().FontColor(palette.Muted);
                col.Item().PaddingTop(4).Text(model.Header.DisplayNumber)
                    .FontSize(11).Bold().FontColor(palette.Primary);
                col.Item().Text($"Internal #: {model.Header.EntityNumber}")
                    .FontSize(8).FontColor(palette.Subtle);
            });
        });
    }

    private static void ComposeBody(IContainer container, InvoiceRenderModel model, Palette palette)
    {
        container.Column(col =>
        {
            // Bill-to + invoice meta block.
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("BILL TO").FontSize(8).Bold().FontColor(palette.Subtle).LetterSpacing(0.05f);
                    c.Item().PaddingTop(4).Text(model.BillTo.DisplayName).FontSize(11).Bold().FontColor(palette.Primary);

                    if (!string.IsNullOrWhiteSpace(model.BillTo.AddressBlock))
                    {
                        c.Item().PaddingTop(2).Text(model.BillTo.AddressBlock!).FontSize(9).FontColor(palette.Muted);
                    }
                    if (!string.IsNullOrWhiteSpace(model.BillTo.Email))
                    {
                        c.Item().Text(model.BillTo.Email!).FontSize(9).FontColor(palette.Muted);
                    }
                    if (!string.IsNullOrWhiteSpace(model.BillTo.Phone))
                    {
                        c.Item().Text(model.BillTo.Phone!).FontSize(9).FontColor(palette.Muted);
                    }
                });

                row.ConstantItem(220).Column(c =>
                {
                    AppendMetaLine(c, "Invoice date", model.Header.DocumentDate.ToString("yyyy-MM-dd"), palette);
                    AppendMetaLine(c, "Due date",
                        model.Header.DueDate?.ToString("yyyy-MM-dd") ?? "On receipt", palette);
                    AppendMetaLine(c, "Status", model.Header.Status, palette);
                });
            });

            col.Item().PaddingVertical(16).LineHorizontal(0.5f).LineColor(palette.Divider);

            // Greeting (template-driven). Renders as a single neutral line
            // above the lines table.
            if (!string.IsNullOrWhiteSpace(model.Branding.Greeting))
            {
                col.Item().PaddingBottom(8).Text(model.Branding.Greeting)
                    .FontSize(10).FontColor(palette.Muted);
            }

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
                            .BorderBottom(0.75f).BorderColor(palette.Primary);
                        var text = cell.Text(headerCells[i]).FontSize(8).Bold().FontColor(palette.Subtle).LetterSpacing(0.05f);
                        if (alignRight[i]) text.AlignRight();
                    }
                });

                foreach (var line in model.Lines)
                {
                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(palette.Divider)
                        .Text(line.LineNumber.ToString());

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(palette.Divider)
                        .Text(string.IsNullOrWhiteSpace(line.Description) ? "—" : line.Description);

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(palette.Divider)
                        .AlignRight().Text(line.Quantity is null ? "—" : line.Quantity.Value.ToString("0.##"));

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(palette.Divider)
                        .AlignRight().Text(line.UnitPrice is null ? "—" : line.UnitPrice.Value.ToString("N2"));

                    table.Cell().PaddingVertical(6).PaddingHorizontal(4)
                        .BorderBottom(0.25f).BorderColor(palette.Divider)
                        .AlignRight().Text(line.LineAmount.ToString("N2"));
                }
            });

            // Totals block, right-aligned. Tax row is conditional on the
            // template's ShowTaxColumn flag — companies that don't levy
            // tax (or never break it out on invoices) hide the line.
            col.Item().PaddingTop(12).AlignRight().Column(totals =>
            {
                AppendTotalRow(totals, "Subtotal", model.Totals.Subtotal, model.Totals.CurrencyCode, palette);
                if (model.Branding.ShowTaxColumn)
                {
                    AppendTotalRow(totals, "Tax", model.Totals.Tax, model.Totals.CurrencyCode, palette);
                }
                totals.Item().PaddingVertical(4).Width(220).LineHorizontal(0.5f).LineColor(palette.Primary);
                AppendTotalRow(totals, "Total", model.Totals.Total, model.Totals.CurrencyCode, palette, bold: true);
            });

            // Notes / memo.
            if (!string.IsNullOrWhiteSpace(model.Header.Memo))
            {
                col.Item().PaddingTop(20).Column(notes =>
                {
                    notes.Item().Text("MEMO").FontSize(8).Bold().FontColor(palette.Subtle).LetterSpacing(0.05f);
                    notes.Item().PaddingTop(4).Text(model.Header.Memo!).FontSize(9).FontColor(palette.Muted);
                });
            }

            // Payment instructions (template-driven; empty unless the
            // operator filled it on the active template).
            if (!string.IsNullOrWhiteSpace(model.Branding.PaymentInstructions))
            {
                col.Item().PaddingTop(16).Column(pay =>
                {
                    pay.Item().Text("PAYMENT INSTRUCTIONS").FontSize(8).Bold().FontColor(palette.Subtle).LetterSpacing(0.05f);
                    pay.Item().PaddingTop(4).Text(model.Branding.PaymentInstructions).FontSize(9).FontColor(palette.Muted);
                });
            }
        });
    }

    private static void ComposeFooter(IContainer container, InvoiceRenderModel model, Palette palette)
    {
        var footerNote = string.IsNullOrWhiteSpace(model.Branding.FooterNote)
            ? string.Empty
            : model.Branding.FooterNote.Trim() + " ";

        container.AlignCenter().Text(text =>
        {
            if (!string.IsNullOrEmpty(footerNote))
            {
                text.Span(footerNote).FontSize(8).FontColor(palette.Subtle);
            }
            text.Span($"Invoice {model.Header.DisplayNumber} · ").FontSize(8).FontColor(palette.Subtle);
            text.CurrentPageNumber().FontSize(8).FontColor(palette.Subtle);
            text.Span(" / ").FontSize(8).FontColor(palette.Subtle);
            text.TotalPages().FontSize(8).FontColor(palette.Subtle);
        });
    }

    private static void AppendMetaLine(ColumnDescriptor c, string label, string value, Palette palette)
    {
        c.Item().PaddingBottom(2).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(8).FontColor(palette.Subtle).LetterSpacing(0.05f);
            row.AutoItem().Text(value).FontSize(10).FontColor(palette.Primary);
        });
    }

    private static void AppendTotalRow(
        ColumnDescriptor c,
        string label,
        decimal amount,
        string currencyCode,
        Palette palette,
        bool bold = false)
    {
        c.Item().Width(220).Row(row =>
        {
            var labelText = row.RelativeItem().Text(label).FontSize(bold ? 11 : 9).FontColor(bold ? palette.Primary : palette.Muted);
            if (bold) labelText.Bold();

            var valueText = row.AutoItem().AlignRight().Text($"{amount:N2} {currencyCode}").FontSize(bold ? 11 : 9).FontColor(palette.Primary);
            if (bold) valueText.Bold();
        });
    }
}
