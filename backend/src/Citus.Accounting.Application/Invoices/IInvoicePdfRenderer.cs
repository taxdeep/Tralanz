namespace Citus.Accounting.Application.Invoices;

public interface IInvoicePdfRenderer
{
    /// <summary>
    /// Renders the invoice render model into a PDF byte stream.
    /// Synchronous because QuestPDF's renderer is CPU-only — there's no
    /// IO inside this call.
    /// </summary>
    byte[] Render(InvoiceRenderModel model);
}
