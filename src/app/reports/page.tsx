import { requireAuthAndSetup } from "@/lib/guards";
import Link from "next/link";
import { PageHeader, SurfaceSection } from "@/components/workbench";
import { ReportsIcon } from "@/components/icons";

export default async function ReportsPage() {
  await requireAuthAndSetup();

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Reporting center"
        title="Reports"
        description="报表中心也统一成卡片式入口，强调‘只认已过账数据’这个会计逻辑。"
      />

      <SurfaceSection
        title="Financial statements"
        description="All reports below are generated from posted journal entries and the current chart of accounts classification."
      >
        <div className="grid gap-4 md:grid-cols-3">
          {[
            {
              href: "/reports/trial-balance",
              title: "Trial Balance",
              description: "Check whether all posted balances remain in equilibrium."
            },
            {
              href: "/reports/income-statement",
              title: "Income Statement",
              description: "Review revenue, expenses, and net income by period."
            },
            {
              href: "/reports/balance-sheet",
              title: "Balance Sheet",
              description: "Review assets, liabilities, and equity at a chosen date."
            }
          ].map((report) => (
            <Link
              key={report.href}
              href={report.href}
              className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5 transition hover:bg-white hover:shadow-sm"
            >
              <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-white text-[var(--accent)] ring-1 ring-inset ring-[var(--line)]">
                <ReportsIcon className="h-5 w-5" />
              </div>
              <h2 className="mt-4 text-lg font-semibold text-slate-950">{report.title}</h2>
              <p className="mt-2 text-sm text-slate-600">{report.description}</p>
            </Link>
          ))}
        </div>
      </SurfaceSection>
    </div>
  );
}
