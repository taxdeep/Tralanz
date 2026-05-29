namespace Citus.Accounting.Infrastructure.Persistence;

// Rollout gate for the Sales Tax v2 engine (S2.1+). When disabled
// (the default), source-document SaveDraftAsync paths behave exactly as
// before: the client-sent line.TaxAmount is persisted and no snapshot
// rows are written. When enabled, the SalesTaxEngine computes each
// line's tax_amount and document_line_sales_tax_snapshots rows are
// written. Per-company toggling is a later refinement; this is a single
// global switch read once at startup.
public sealed class SalesTaxV2Options
{
    public const string SectionName = "SalesTaxV2";

    public bool Enabled { get; set; }
}
