import Link from "next/link";
import { requireAuthAndSetup } from "@/lib/guards";
import ReportDateFilter from "@/components/ReportDateFilter";
import { getAccountBalancesForPeriod, getReportRange, toBalanceSheet } from "@/server/reports";
import { formatCurrency } from "@/lib/format";
import { EmptyState, PageHeader, SurfaceSection } from "@/components/workbench";

type BalanceSheetPageProps = {
  searchParams: Promise<{ from?: string; to?: string }>;
};

export default async function BalanceSheetPage({ searchParams }: BalanceSheetPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const range = getReportRange(params);
  const balances = await getAccountBalancesForPeriod(user.id, range);
  const statement = toBalanceSheet(balances);

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Report"
        title="Balance sheet"
        description="Review assets, liabilities, and equity as of the selected report date."
        actions={<Link href="/reports" className="btn-secondary">Back to reports</Link>}
      />

      <SurfaceSection>
        <ReportDateFilter from={range.fromInput} to={range.toInput} />

        <div className="grid gap-4 xl:grid-cols-2">
          <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5">
            <h2 className="text-lg font-semibold text-slate-950">Assets</h2>
            {statement.assetRows.length === 0 ? (
              <EmptyState
                title="No asset balances in range"
                description="Asset accounts will appear once posted transactions affect the balance sheet."
              />
            ) : (
              <div className="mt-4 space-y-3">
                {statement.assetRows.map((row: (typeof statement.assetRows)[number]) => (
                  <div key={row.accountId} className="flex items-center justify-between text-sm">
                    <span className="text-slate-700">
                      {row.accountNumber} - {row.accountName}
                    </span>
                    <span className="font-semibold text-slate-950">{formatCurrency(row.amount)}</span>
                  </div>
                ))}
              </div>
            )}
            <div className="mt-4 border-t border-[var(--line)] pt-4 text-sm font-semibold">
              <div className="flex items-center justify-between">
                <span>Total assets</span>
                <span>{formatCurrency(statement.totalAssets)}</span>
              </div>
            </div>
          </div>

          <div className="space-y-4">
            <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5">
              <h2 className="text-lg font-semibold text-slate-950">Liabilities</h2>
              {statement.liabilityRows.length === 0 ? (
                <EmptyState
                  title="No liability balances in range"
                  description="Liability accounts will appear once posted transactions affect liabilities."
                />
              ) : (
                <div className="mt-4 space-y-3">
                  {statement.liabilityRows.map((row: (typeof statement.liabilityRows)[number]) => (
                    <div key={row.accountId} className="flex items-center justify-between text-sm">
                      <span className="text-slate-700">
                        {row.accountNumber} - {row.accountName}
                      </span>
                      <span className="font-semibold text-slate-950">{formatCurrency(row.amount)}</span>
                    </div>
                  ))}
                </div>
              )}
              <div className="mt-4 border-t border-[var(--line)] pt-4 text-sm font-semibold">
                <div className="flex items-center justify-between">
                  <span>Total liabilities</span>
                  <span>{formatCurrency(statement.totalLiabilities)}</span>
                </div>
              </div>
            </div>

            <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5">
              <h2 className="text-lg font-semibold text-slate-950">Equity</h2>
              {statement.equityRows.length === 0 ? (
                <EmptyState
                  title="No equity balances in range"
                  description="Equity accounts and current earnings will appear here as the ledger develops."
                />
              ) : (
                <div className="mt-4 space-y-3">
                  {statement.equityRows.map((row: (typeof statement.equityRows)[number]) => (
                    <div key={row.accountId} className="flex items-center justify-between text-sm">
                      <span className="text-slate-700">
                        {row.accountNumber} - {row.accountName}
                      </span>
                      <span className="font-semibold text-slate-950">{formatCurrency(row.amount)}</span>
                    </div>
                  ))}
                </div>
              )}
              <div className="mt-3 flex items-center justify-between text-sm">
                <span className="text-slate-700">Current earnings</span>
                <span className="font-semibold text-slate-950">{formatCurrency(statement.currentEarnings)}</span>
              </div>
              <div className="mt-4 border-t border-[var(--line)] pt-4 text-sm font-semibold">
                <div className="flex items-center justify-between">
                  <span>Total equity</span>
                  <span>{formatCurrency(statement.totalEquity)}</span>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div className="summary-card mt-4">
          <p className="table-head-label">Liabilities plus equity</p>
          <p className="mt-2 text-2xl font-semibold text-slate-950">
            {formatCurrency(statement.totalLiabilitiesAndEquity)}
          </p>
        </div>
      </SurfaceSection>
    </div>
  );
}
