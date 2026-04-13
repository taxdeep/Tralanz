import Link from "next/link";
import { redirect } from "next/navigation";
import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import { postBillAction } from "@/app/bills/actions";
import { formatCurrency, formatDate } from "@/lib/format";
import { Notice, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

type BillDetailPageProps = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ error?: string; posted?: string; info?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-ap-account": "Missing active AP account (2000 Accounts Payable).",
  "missing-recoverable-tax-account":
    "A recoverable tax code is missing a recoverable tax account mapping."
};

export default async function BillDetailPage({ params, searchParams }: BillDetailPageProps) {
  const user = await requireAuthAndSetup();
  const { id } = await params;
  const query = await searchParams;

  const bill = await prisma.bill.findFirst({
    where: { id, userId: user.id },
    include: {
      vendor: true,
      lines: { include: { expenseAccount: true, taxCode: true } }
    }
  });
  if (!bill) {
    redirect("/bills");
  }

  const error = query.error ? errorMessages[query.error] : null;
  const posted = query.posted === "1";
  const alreadyPosted = query.info === "already-posted";

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Purchases document"
        title={`Bill ${bill.billNumber}`}
        description="单据详情也同步到统一样式，头部展示关键信息，主体突出分摊行与过账动作。"
        actions={
          <>
            <Link href="/bills" className="btn-secondary">
              Back to bills
            </Link>
            {bill.status === "draft" ? (
              <form action={postBillAction}>
                <input type="hidden" name="billId" value={bill.id} />
                <button type="submit" className="btn-primary">
                  Post bill
                </button>
              </form>
            ) : null}
          </>
        }
      />

      {error ? <Notice variant="danger">{error}</Notice> : null}
      {posted ? <Notice variant="success">Bill posted successfully and locked.</Notice> : null}
      {alreadyPosted ? <Notice variant="info">Bill is already posted.</Notice> : null}

      <div className="grid gap-4 md:grid-cols-4">
        <StatCard label="Vendor" value={bill.vendor.displayName} hint="Supplier linked to this payable." />
        <StatCard label="Bill date" value={formatDate(bill.billDate)} hint={`Due ${formatDate(bill.dueDate)}`} />
        <StatCard label="Status" value={bill.status.toUpperCase()} hint="Draft bills can still be reviewed before posting." tone={bill.status === "posted" ? "positive" : "warning"} />
        <StatCard label="Total payable" value={formatCurrency(bill.totalAmount)} hint={`${bill.lines.length} line(s) captured on this document.`} />
      </div>

      <SurfaceSection title="Bill lines" description={bill.memo || "No memo was added to this bill."}>
        <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
          <div className="grid grid-cols-[280px_1fr_150px_140px_160px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
            <div className="table-head-label">Expense or asset account</div>
            <div className="table-head-label">Description</div>
            <div className="table-head-label text-right">Amount</div>
            <div className="table-head-label text-right">Tax</div>
            <div className="table-head-label">Tax handling</div>
          </div>
          {bill.lines.map((line: (typeof bill.lines)[number]) => (
            <div
              key={line.id}
              className="grid grid-cols-[280px_1fr_150px_140px_160px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
            >
              <div className="text-slate-700">
                {line.expenseAccount.accountNumber} - {line.expenseAccount.accountName}
              </div>
              <div className="text-slate-700">{line.description}</div>
              <div className="text-right font-semibold text-slate-950">{formatCurrency(line.amount)}</div>
              <div className="text-right font-semibold text-slate-950">{formatCurrency(line.taxAmount)}</div>
              <div>
                <StatusBadge status={line.isTaxRecoverable ? "recoverable" : "non-recoverable"} />
              </div>
            </div>
          ))}
        </div>

        <div className="mt-5 grid gap-4 md:grid-cols-3">
          <div className="summary-card">
            <p className="table-head-label">Subtotal</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(bill.subtotal)}</p>
          </div>
          <div className="summary-card">
            <p className="table-head-label">Sales tax</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(bill.taxAmount)}</p>
          </div>
          <div className="summary-card">
            <p className="table-head-label">Total</p>
            <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(bill.totalAmount)}</p>
          </div>
        </div>
      </SurfaceSection>

      {bill.status === "posted" ? (
        <Notice variant="info">Posted bills are locked and cannot be edited.</Notice>
      ) : null}
    </div>
  );
}
