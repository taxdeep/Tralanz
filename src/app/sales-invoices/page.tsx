import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import Link from "next/link";
import { formatCurrency, formatDate } from "@/lib/format";
import { EmptyState, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

export default async function SalesInvoicesPage() {
  const user = await requireAuthAndSetup();
  const invoices = await prisma.invoice.findMany({
    where: { userId: user.id },
    include: { customer: true },
    orderBy: [{ invoiceDate: "desc" }, { createdAt: "desc" }]
  });

  const draftCount = invoices.filter((invoice: (typeof invoices)[number]) => invoice.status === "draft").length;
  const postedCount = invoices.filter((invoice: (typeof invoices)[number]) => invoice.status === "posted").length;
  const totalValue = invoices.reduce(
    (sum: number, invoice: (typeof invoices)[number]) => sum + invoice.totalAmount,
    0
  );

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Sales"
        title="Invoices"
        description="发票列表也统一成了工作台式表格，重点突出客户、日期、状态和应收金额。"
        actions={<Link href="/sales-invoices/new" className="btn-primary">New invoice</Link>}
      />

      <div className="grid gap-4 md:grid-cols-3">
        <StatCard label="All invoices" value={String(invoices.length)} hint="Draft and posted sales documents." />
        <StatCard label="Draft invoices" value={String(draftCount)} hint="Still editable and waiting for posting." tone="warning" />
        <StatCard label="Gross value" value={formatCurrency(totalValue)} hint={`${postedCount} invoice(s) already posted.`} tone="positive" />
      </div>

      <SurfaceSection
        title="Invoice register"
        description="Open an invoice to review lines, totals, and posting status."
      >
        {invoices.length === 0 ? (
          <EmptyState
            title="No invoices yet"
            description="Create your first sales invoice to start tracking revenue and accounts receivable."
            actionHref="/sales-invoices/new"
            actionLabel="Create invoice"
          />
        ) : (
          <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
            <div className="grid grid-cols-[170px_1fr_160px_160px_140px_150px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
              <div className="table-head-label">Invoice no.</div>
              <div className="table-head-label">Customer</div>
              <div className="table-head-label">Invoice date</div>
              <div className="table-head-label">Due date</div>
              <div className="table-head-label">Status</div>
              <div className="table-head-label text-right">Total</div>
            </div>
            {invoices.map((invoice: (typeof invoices)[number]) => (
              <Link
                key={invoice.id}
                href={`/sales-invoices/${invoice.id}`}
                className="grid grid-cols-[170px_1fr_160px_160px_140px_150px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm transition hover:bg-[var(--panel-soft)]"
              >
                <div className="font-semibold text-slate-950">{invoice.invoiceNumber}</div>
                <div className="text-slate-700">{invoice.customer.displayName}</div>
                <div className="text-slate-700">{formatDate(invoice.invoiceDate)}</div>
                <div className="text-slate-700">{formatDate(invoice.dueDate)}</div>
                <div>
                  <StatusBadge status={invoice.status} />
                </div>
                <div className="text-right font-semibold text-slate-950">
                  {formatCurrency(invoice.totalAmount)}
                </div>
              </Link>
            ))}
          </div>
        )}
      </SurfaceSection>
    </div>
  );
}
