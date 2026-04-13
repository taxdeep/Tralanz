import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import Link from "next/link";
import { formatCurrency, formatDate } from "@/lib/format";
import { EmptyState, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

export default async function JournalEntriesPage() {
  const user = await requireAuthAndSetup();
  const entries = await prisma.journalEntry.findMany({
    where: { userId: user.id },
    orderBy: [{ entryDate: "desc" }, { createdAt: "desc" }],
    include: { lines: true }
  });

  const draftCount = entries.filter((entry: (typeof entries)[number]) => entry.status === "draft").length;
  const postedCount = entries.filter((entry: (typeof entries)[number]) => entry.status === "posted").length;
  const totalVolume = entries.reduce(
    (sum: number, entry: (typeof entries)[number]) =>
      sum +
      entry.lines.reduce(
        (lineSum: number, line: (typeof entry.lines)[number]) => lineSum + line.debitAmount,
        0
      ),
    0
  );

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="General ledger"
        title="Journal entries"
        description="手工分录列表被整理成会计人员更熟悉的台账视图，直接看到日期、编号、借贷总额和状态。"
        actions={<Link href="/journal-entries/new" className="btn-primary">New entry</Link>}
      />

      <div className="grid gap-4 md:grid-cols-3">
        <StatCard label="All journals" value={String(entries.length)} hint="Draft and posted journal documents." />
        <StatCard label="Draft journals" value={String(draftCount)} hint="Still editable and pending posting." tone="warning" />
        <StatCard label="Posted volume" value={formatCurrency(totalVolume)} hint={`${postedCount} journal(s) already posted.`} tone="positive" />
      </div>

      <SurfaceSection
        title="Journal register"
        description="Open an entry to inspect lines and complete the posting step."
      >
        {entries.length === 0 ? (
          <EmptyState
            title="No journal entries yet"
            description="Create your first manual journal to record adjustments, opening balances, or bookkeeping corrections."
            actionHref="/journal-entries/new"
            actionLabel="Create entry"
          />
        ) : (
          <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
            <div className="grid grid-cols-[170px_160px_140px_150px_150px_1fr] gap-3 bg-[var(--panel-soft)] px-5 py-3">
              <div className="table-head-label">Journal no.</div>
              <div className="table-head-label">Date</div>
              <div className="table-head-label">Status</div>
              <div className="table-head-label text-right">Debit</div>
              <div className="table-head-label text-right">Credit</div>
              <div className="table-head-label">Memo</div>
            </div>
            {entries.map((entry: (typeof entries)[number]) => {
              const totalDebit = entry.lines.reduce(
                (sum: number, line: (typeof entry.lines)[number]) => sum + line.debitAmount,
                0
              );
              const totalCredit = entry.lines.reduce(
                (sum: number, line: (typeof entry.lines)[number]) => sum + line.creditAmount,
                0
              );

              return (
                <Link
                  key={entry.id}
                  href={`/journal-entries/${entry.id}`}
                  className="grid grid-cols-[170px_160px_140px_150px_150px_1fr] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm transition hover:bg-[var(--panel-soft)]"
                >
                  <div className="font-semibold text-slate-950">{entry.entryNumber}</div>
                  <div className="text-slate-700">{formatDate(entry.entryDate)}</div>
                  <div>
                    <StatusBadge status={entry.status} />
                  </div>
                  <div className="text-right font-semibold text-slate-950">
                    {formatCurrency(totalDebit)}
                  </div>
                  <div className="text-right font-semibold text-slate-950">
                    {formatCurrency(totalCredit)}
                  </div>
                  <div className="truncate text-slate-700">{entry.memo || "No memo"}</div>
                </Link>
              );
            })}
          </div>
        )}
      </SurfaceSection>
    </div>
  );
}
