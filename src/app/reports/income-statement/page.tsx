import Link from "next/link";
import { requireAuthAndSetup } from "@/lib/guards";
import ReportDateFilter from "@/components/ReportDateFilter";
import { getAccountBalancesForPeriod, getReportRange, toIncomeStatement } from "@/server/reports";
import { formatCurrency } from "@/lib/format";
import { EmptyState, PageHeader, SurfaceSection } from "@/components/workbench";

type IncomeStatementPageProps = {
  searchParams: Promise<{ from?: string; to?: string }>;
};

export default async function IncomeStatementPage({ searchParams }: IncomeStatementPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const range = getReportRange(params);
  const balances = await getAccountBalancesForPeriod(user.id, range);
  const statement = toIncomeStatement(balances);

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Report"
        title="Income statement"
        description="Review revenue and expense movement for the selected period."
        actions={<Link href="/reports" className="btn-secondary">Back to reports</Link>}
      />

      <SurfaceSection>
        <ReportDateFilter from={range.fromInput} to={range.toInput} />

        <div className="grid gap-4 xl:grid-cols-2">
          <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5">
            <h2 className="text-lg font-semibold text-slate-950">Revenue</h2>
            {statement.revenueRows.length === 0 ? (
              <EmptyState
                title="No revenue accounts in range"
                description="Posted sales revenue will appear here once invoices or journals hit revenue accounts."
              />
            ) : (
              <div className="mt-4 space-y-3">
                {statement.revenueRows.map((row: (typeof statement.revenueRows)[number]) => (
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
                <span>Total revenue</span>
                <span>{formatCurrency(statement.totalRevenue)}</span>
              </div>
            </div>
          </div>

          <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5">
            <h2 className="text-lg font-semibold text-slate-950">Expenses</h2>
            {statement.expenseRows.length === 0 ? (
              <EmptyState
                title="No expense accounts in range"
                description="Posted expenses will appear here once bills or journals hit expense accounts."
              />
            ) : (
              <div className="mt-4 space-y-3">
                {statement.expenseRows.map((row: (typeof statement.expenseRows)[number]) => (
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
                <span>Total expenses</span>
                <span>{formatCurrency(statement.totalExpenses)}</span>
              </div>
            </div>
          </div>
        </div>

        <div className="summary-card mt-4">
          <p className="table-head-label">Net income</p>
          <p className="mt-2 text-2xl font-semibold text-slate-950">{formatCurrency(statement.netIncome)}</p>
        </div>
      </SurfaceSection>
    </div>
  );
}
