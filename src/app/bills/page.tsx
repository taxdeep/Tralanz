import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import Link from "next/link";
import { formatCurrency, formatDate } from "@/lib/format";
import { EmptyState, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

export default async function BillsPage() {
  const user = await requireAuthAndSetup();
  const bills = await prisma.bill.findMany({
    where: { userId: user.id },
    include: { vendor: true },
    orderBy: [{ billDate: "desc" }, { createdAt: "desc" }]
  });

  const draftCount = bills.filter((bill: (typeof bills)[number]) => bill.status === "draft").length;
  const postedCount = bills.filter((bill: (typeof bills)[number]) => bill.status === "posted").length;
  const totalValue = bills.reduce(
    (sum: number, bill: (typeof bills)[number]) => sum + bill.totalAmount,
    0
  );

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Purchases"
        title="Bills"
        description="账单列表页已经改成更像会计系统的表格总览：先看状态和总额，再进入单据。"
        actions={<Link href="/bills/new" className="btn-primary">New bill</Link>}
      />

      <div className="grid gap-4 md:grid-cols-3">
        <StatCard label="All bills" value={String(bills.length)} hint="Draft and posted purchase documents." />
        <StatCard label="Draft bills" value={String(draftCount)} hint="Still editable and not yet posted." tone="warning" />
        <StatCard label="Gross value" value={formatCurrency(totalValue)} hint={`${postedCount} bill(s) already posted.`} tone="positive" />
      </div>

      <SurfaceSection
        title="Bill register"
        description="Open a bill to review lines, totals, and posting status."
      >
        {bills.length === 0 ? (
          <EmptyState
            title="No bills yet"
            description="Create your first supplier bill to start tracking expenses and accounts payable."
            actionHref="/bills/new"
            actionLabel="Create bill"
          />
        ) : (
          <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
            <div className="grid grid-cols-[170px_1fr_160px_160px_140px_150px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
              <div className="table-head-label">Bill no.</div>
              <div className="table-head-label">Vendor</div>
              <div className="table-head-label">Bill date</div>
              <div className="table-head-label">Due date</div>
              <div className="table-head-label">Status</div>
              <div className="table-head-label text-right">Total</div>
            </div>
            {bills.map((bill: (typeof bills)[number]) => (
              <Link
                key={bill.id}
                href={`/bills/${bill.id}`}
                className="grid grid-cols-[170px_1fr_160px_160px_140px_150px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm transition hover:bg-[var(--panel-soft)]"
              >
                <div className="font-semibold text-slate-950">{bill.billNumber}</div>
                <div className="text-slate-700">{bill.vendor.displayName}</div>
                <div className="text-slate-700">{formatDate(bill.billDate)}</div>
                <div className="text-slate-700">{formatDate(bill.dueDate)}</div>
                <div>
                  <StatusBadge status={bill.status} />
                </div>
                <div className="text-right font-semibold text-slate-950">
                  {formatCurrency(bill.totalAmount)}
                </div>
              </Link>
            ))}
          </div>
        )}
      </SurfaceSection>
    </div>
  );
}
