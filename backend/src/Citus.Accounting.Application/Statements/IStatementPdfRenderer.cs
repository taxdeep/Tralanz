namespace Citus.Accounting.Application.Statements;

/// <summary>
/// Renders an open-item statement to a PDF byte array. Mirrors
/// <c>IInvoicePdfRenderer</c>; the implementation lives in Infrastructure.
/// </summary>
public interface IStatementPdfRenderer
{
    byte[] Render(StatementRenderModel model);
}
