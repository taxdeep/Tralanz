import Link from "next/link";
import { requireAuthAndSetup } from "@/lib/guards";
import ReportDateFilter from "@/components/ReportDateFilter";
import { getAccountBalancesForPeriod, getReportRange, toTrialBalanceRows } from "@/server/reports";
import { formatCurrency } from "@/lib/format";
import { EmptyState, PageHeader, SurfaceSection } from "@/components/workbench";

type TrialBalancePageProps = {
  searchParams: Promise<{ from?: string; to?: string }>;
};

export default async function TrialBalancePage({ searchParams }: TrialBalancePageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const range = getReportRange(params);
  const balances = await getAccountBalancesForPeriod(user.id, range);
  const trialBalance = toTrialBalanceRows(balances);

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Report"
        title="Trial balance"
        description="Check that posted ledgers remain balanced across the selected period."
        actions={<Link href="/reports" className="btn-secondary">Back to reports</Link>}
      />

      <SurfaceSection>
        <ReportDateFilter from={range.fromInput} to={range.toInput} />
        {trialBalance.rows.length === 0 ? (
          <EmptyState
            title="No posted entries in this date range"
            description="Post invoices, bills, or journal entries before running this report."
          />
        ) : (
          <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
            <div className="grid grid-cols-[180px_1fr_180px_180px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
              <div className="table-head-label">Account no.</div>
              <div className="table-head-label">Account name</div>
              <div className="table-head-label text-right">Debit</div>
              <div className="table-head-label text-right">Credit</div>
            </div>
            {trialBalance.rows.map((row: (typeof trialBalance.rows)[number]) => (
              <div
                key={row.accountId}
                className="grid grid-cols-[180px_1fr_180px_180px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
              >
                <div className="font-medium text-slate-950">{row.accountNumber}</div>
                <div className="text-slate-700">{row.accountName}</div>
                <div className="text-right font-semibold text-slate-950">
                  {row.debit ? formatCurrency(row.debit) : "-"}
                </div>
                <div className="text-right font-semibold text-slate-950">
                  {row.credit ? formatCurrency(row.credit) : "-"}
                </div>
              </div>
            ))}
            <div className="grid grid-cols-[180px_1fr_180px_180px] gap-3 border-t border-[var(--line)] bg-[var(--panel-soft)] px-5 py-4 text-sm font-semibold">
              <div>Totals</div>
              <div />
              <div className="text-right">{formatCurrency(trialBalance.totalDebit)}</div>
              <div className="text-right">{formatCurrency(trialBalance.totalCredit)}</div>
            </div>
          </div>
        )}
      </SurfaceSection>
    </div>
  );
}
