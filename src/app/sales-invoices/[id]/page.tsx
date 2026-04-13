import Link from "next/link";
import { redirect } from "next/navigation";
import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import { postInvoiceAction } from "@/app/sales-invoices/actions";
import { formatCurrency, formatDate } from "@/lib/format";
import { Notice, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

type InvoiceDetailPageProps = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ error?: string; posted?: string; info?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-required-accounts":
    "Missing required active accounts (AR, Sales Revenue, or Sales Tax Payable)."
};

export default async function InvoiceDetailPage({ params, searchParams }: InvoiceDetailPageProps) {
  const user = await requireAuthAndSetup();
  const { id } = await params;
  const query = await searchParams;

  const invoice = await prisma.invoice.findFirst({
    where: { id, userId: user.id },
    include: {
      customer: true,
      lines: { include: { taxCode: true } }
    }
  });
  if (!invoice) {
    redirect("/sales-invoices");
  }

  const error = query.error ? errorMessages[query.error] : null;
  const posted = query.posted === "1";
  const alreadyPosted = query.info === "already-posted";

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Sales document"
        title={`Invoice ${invoice.invoiceNumber}`}
        description="发票详情页已经统一到新的单据详情样式，重点突出客户、账期、行项目和过账动作。"
        actions={
          <>
            <Link href="/sales-invoices" className="btn-secondary">
              Back to invoices
            </Link>
            {invoice.status === "draft" ? (
              <form action={postInvoiceAction}>
                <input type="hidden" name="invoiceId" value={invoice.id} />
                <button type="submit" className="btn-primary">
                  Post invoice
                </button>
              </form>
            ) : null}
          </>
        }
      />

      {error ? <Notice variant="danger">{error}</Notice> : null}
      {posted ? <Notice variant="success">Invoice posted successfully and locked.</Notice> : null}
      {alreadyPosted ? <Notice variant="info">Invoice is already posted.</Notice> : null}

      <div className="grid gap-4 md:grid-cols-4">
        <StatCard label="Customer" value={invoice.customer.displayName} hint="Customer linked to this receivable." />
        <StatCard label="Invoice date" value={formatDate(invoice.invoiceDate)} hint={`Due ${formatDate(invoice.dueDate)}`} />
        <StatCard label="Status" value={invoice.status.toUpperCase()} hint="Draft invoices can still be reviewed before posting." tone={invoice.status === "posted" ? "positive" : "warning"} />
        <StatCard label="Total receivable" value={formatCurrency(invoice.totalAmount)} hint={`${invoice.lines.length} line(s) captured on this document.`} />
      </div>

      <SurfaceSection title="Invoice lines" description={invoice.memo || "No memo was added to this invoice."}>
        <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
          <div className="grid grid-cols-[1fr_120px_150px_140px_160px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
            <div className="table-head-label">Description</div>
            <div className="table-head-label text-right">Qty</div>
            <div className="table-head-label text-right">Rate</div>
            <div className="table-head-label text-right">Tax</div>
            <div className="table-head-label text-right">Line total</div>
          </div>
          {invoice.lines.map((line: (typeof invoice.lines)[number]) => (
            <div
              key={line.id}
              className="grid grid-cols-[1fr_120px_150px_140px_160px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
            >
              <div className="text-slate-700">{line.description}</div>
              <div className="text-right font-medium text-slate-950">{line.quantity}</div>
              <div className="text-right font-medium text-slate-950">{formatCurrency(line.unitPrice)}</div>
              <div className="text-right font-medium text-slate-950">{formatCurrency(line.taxAmount)}</div>
              <div className="text-right font-semibold text-slate-950">
                {formatCurrency(line.lineAmount + line.taxAmount)}
              </div>
            </div>
          ))}
        </div>

        <div className="mt-5 grid gap-4 md:grid-cols-3">
          <div className="summary-card">
            <p className="table-head-label">Subtotal</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(invoice.subtotal)}</p>
          </div>
          <div className="summary-card">
            <p className="table-head-label">Sales tax</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(invoice.taxAmount)}</p>
          </div>
          <div className="summary-card">
            <p className="table-head-label">Total</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(invoice.totalAmount)}</p>
          </div>
        </div>
      </SurfaceSection>

      {invoice.status === "posted" ? (
        <Notice variant="info">Posted invoices are locked and cannot be edited.</Notice>
      ) : null}
    </div>
  );
}
