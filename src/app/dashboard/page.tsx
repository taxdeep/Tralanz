import Link from "next/link";
import { requireAuthAndSetup } from "@/lib/guards";
import { cn } from "@/lib/cn";
import { formatCurrency, formatDate } from "@/lib/format";
import { getDashboardData } from "@/server/dashboard";
import {
  ArrowUpRightIcon,
  BillIcon,
  InvoiceIcon,
  JournalIcon,
  LedgerIcon,
  PeopleIcon,
  ReportsIcon,
  SparklesIcon
} from "@/components/icons";

type DashboardPageProps = {
  searchParams: Promise<{
    window?: string;
  }>;
};

type DashboardModel = Awaited<ReturnType<typeof getDashboardData>>;

const taskLinks = [
  {
    href: "/customers",
    label: "Add a customer",
    description: "Keep your receivables list current and ready for invoicing.",
    icon: PeopleIcon,
    statKey: "activeCustomers" as const
  },
  {
    href: "/vendors",
    label: "Add a vendor",
    description: "Set up suppliers before new bills and vendor credits arrive.",
    icon: PeopleIcon,
    statKey: "activeVendors" as const
  },
  {
    href: "/sales-invoices/new",
    label: "Create an invoice",
    description: "Issue a receivable and feed the revenue trend immediately.",
    icon: InvoiceIcon,
    statKey: "postedInvoices" as const
  },
  {
    href: "/bills/new",
    label: "Create a bill",
    description: "Capture upcoming payables and supplier costs before posting.",
    icon: BillIcon,
    statKey: "postedBills" as const
  },
  {
    href: "/reports",
    label: "Open reports",
    description: "Drill into trial balance, P&L, and balance sheet from one place.",
    icon: ReportsIcon,
    statKey: null
  },
  {
    href: "/journal-entries/new",
    label: "Record a journal entry",
    description: "Handle adjustments and edge cases without leaving the cockpit.",
    icon: JournalIcon,
    statKey: null
  }
];

export default async function DashboardPage({ searchParams }: DashboardPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const dashboard = await getDashboardData(user.id, {
    window: params.window
  });

  const baseCurrency = dashboard.company.baseCurrency;

  return (
    <div className="space-y-6">
      <section className="flex flex-col gap-5 xl:flex-row xl:items-start xl:justify-between">
        <div className="space-y-3">
          <p className="eyebrow-label">Live bookkeeping overview</p>
          <div className="space-y-3">
            <h1 className="text-4xl font-semibold tracking-[-0.04em] text-slate-950">Dashboard</h1>
            <p className="max-w-3xl text-sm leading-6 text-slate-600">
              A rolling operating view for {dashboard.company.legalName}, built around cash flow,
              profit and loss, overdue documents, and the next bookkeeping actions that matter.
            </p>
          </div>
        </div>

        <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
          <div className="flex max-w-[480px] items-start gap-3 rounded-[24px] border border-[#dbe7ff] bg-[linear-gradient(180deg,#f5f9ff_0%,#eef4ff_100%)] px-4 py-3 shadow-[0_16px_35px_rgba(30,99,255,0.08)]">
            <span className="mt-0.5 flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl bg-white text-[var(--accent)] shadow-sm">
              <SparklesIcon className="h-5 w-5" />
            </span>
            <div className="space-y-1">
              <p className="text-sm font-semibold text-slate-950">Reimagined operating dashboard</p>
              <p className="text-sm text-slate-600">
                Rolling {dashboard.period.months} months ending {formatDate(dashboard.period.to)}.
                This view is wired to posted invoices, bills, and bank-side journal activity.
              </p>
            </div>
          </div>
          <WindowSwitcher months={dashboard.period.months} />
        </div>
      </section>

      <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="space-y-6">
          <DashboardSection
            title="Connected accounts"
            description="Primary cash account driven from posted journal activity."
          >
            <div className="space-y-4">
              {dashboard.connectedAccounts.map((account) => (
                <div
                  key={account.accountNumber}
                  className="rounded-[26px] border border-[var(--line)] bg-[linear-gradient(180deg,#ffffff_0%,#f6f9fd_100%)] p-5 shadow-[0_16px_32px_rgba(15,23,42,0.05)]"
                >
                  <div className="flex items-start gap-4">
                    <span className="flex h-12 w-12 items-center justify-center rounded-[18px] bg-[var(--accent)] text-white shadow-[0_14px_28px_rgba(30,99,255,0.22)]">
                      <LedgerIcon className="h-5 w-5" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-lg font-semibold text-slate-950">{account.name}</p>
                      <p className="mt-1 text-sm text-slate-500">
                        Operating account ••••{maskAccountNumber(account.accountNumber)}
                      </p>
                    </div>
                  </div>

                  <div className="mt-5 grid gap-3 rounded-[20px] bg-[var(--panel-soft)] p-4 sm:grid-cols-2">
                    <MetricBlock
                      label="Current balance"
                      value={formatCurrency(account.balance, baseCurrency)}
                    />
                    <MetricBlock
                      label="Last activity"
                      value={account.lastActivity ? formatDate(account.lastActivity) : "No activity"}
                    />
                  </div>

                  <p className="mt-4 text-sm text-slate-600">{account.status}</p>
                </div>
              ))}
            </div>
          </DashboardSection>

          <DashboardSection
            title="Overdue invoices & bills"
            actions={
              <Link href="/reports" className="dashboard-link">
                View report
                <ArrowUpRightIcon className="h-4 w-4" />
              </Link>
            }
          >
            <div className="space-y-5">
              <OverdueList
                title="Overdue invoices"
                href="/sales-invoices"
                total={dashboard.overdue.invoiceTotal}
                items={dashboard.overdue.invoices}
                currency={baseCurrency}
                emptyMessage="No overdue customer balances right now."
              />
              <OverdueList
                title="Overdue bills"
                href="/bills"
                total={dashboard.overdue.billTotal}
                items={dashboard.overdue.bills}
                currency={baseCurrency}
                emptyMessage="No overdue supplier balances right now."
              />
            </div>
          </DashboardSection>

          <DashboardSection title="Things you can do">
            <div className="space-y-3">
              {taskLinks.map((task) => {
                const Icon = task.icon;
                const statValue = task.statKey ? dashboard.stats[task.statKey] : null;

                return (
                  <Link
                    key={task.href}
                    href={task.href}
                    className="group flex items-start gap-3 rounded-[22px] border border-transparent px-2 py-2 transition hover:border-[var(--line)] hover:bg-[var(--panel-soft)]"
                  >
                    <span className="mt-1 flex h-10 w-10 shrink-0 items-center justify-center rounded-[16px] bg-white text-[var(--accent)] ring-1 ring-inset ring-[var(--line)] transition group-hover:bg-[var(--panel-soft)]">
                      <Icon className="h-4 w-4" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center justify-between gap-3">
                        <p className="text-sm font-semibold text-[var(--accent)]">{task.label}</p>
                        {statValue !== null ? (
                          <span className="rounded-full bg-white px-2.5 py-1 text-xs font-semibold text-slate-500 ring-1 ring-inset ring-[var(--line)]">
                            {statValue}
                          </span>
                        ) : null}
                      </div>
                      <p className="mt-1 text-sm text-slate-600">{task.description}</p>
                    </div>
                  </Link>
                );
              })}
            </div>
          </DashboardSection>
        </div>

        <div className="space-y-6">
          <DashboardSection
            title="Cash flow"
            description="Cash coming in and going out of the business through posted bank activity."
            actions={
              <Link href="/reports" className="dashboard-link">
                View report
                <ArrowUpRightIcon className="h-4 w-4" />
              </Link>
            }
          >
            <div className="space-y-6">
              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                <MetricBlock
                  label="Cash on hand"
                  value={formatCurrency(dashboard.highlights.bankBalance, baseCurrency)}
                />
                <MetricBlock
                  label="Receivables"
                  value={formatCurrency(dashboard.highlights.receivables, baseCurrency)}
                />
                <MetricBlock
                  label="Payables"
                  value={formatCurrency(dashboard.highlights.payables, baseCurrency)}
                />
                <MetricBlock
                  label={`Net change (${dashboard.period.months}m)`}
                  value={formatCurrency(dashboard.highlights.netCashChange, baseCurrency)}
                />
              </div>
              <CashFlowChart points={dashboard.trends} currency={baseCurrency} />
            </div>
          </DashboardSection>

          <DashboardSection
            title="Profit and loss"
            description="Revenue and expense pattern based on posted invoices and bills."
            actions={
              <Link href="/reports/income-statement" className="dashboard-link">
                View report
                <ArrowUpRightIcon className="h-4 w-4" />
              </Link>
            }
          >
            <ProfitLossChart points={dashboard.trends} currency={baseCurrency} />
          </DashboardSection>

          <DashboardSection title="Payable & owing">
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
              <SummaryCell
                label="Invoices coming due"
                value={formatCurrency(dashboard.payableAndOwing.invoicesComingDue, baseCurrency)}
                hint="Amounts due within the next 30 days."
                tone="positive"
              />
              <SummaryCell
                label="Invoices overdue"
                value={formatCurrency(dashboard.payableAndOwing.invoicesOverdue, baseCurrency)}
                hint="Customer balances already past due."
                tone="warning"
              />
              <SummaryCell
                label="Bills coming due"
                value={formatCurrency(dashboard.payableAndOwing.billsComingDue, baseCurrency)}
                hint="Supplier balances due within 30 days."
                tone="neutral"
              />
              <SummaryCell
                label="Bills overdue"
                value={formatCurrency(dashboard.payableAndOwing.billsOverdue, baseCurrency)}
                hint="Supplier balances that need follow-up."
                tone="warning"
              />
            </div>
          </DashboardSection>
        </div>
      </div>
    </div>
  );
}

function WindowSwitcher({ months }: { months: 12 | 24 }) {
  return (
    <div className="inline-flex items-center rounded-full border border-[var(--line)] bg-white p-1 shadow-sm">
      {[12, 24].map((option) => (
        <Link
          key={option}
          href={`/dashboard?window=${option}`}
          className={cn(
            "rounded-full px-4 py-2 text-sm font-semibold transition",
            months === option
              ? "bg-[var(--accent)] text-white shadow-[0_12px_26px_rgba(30,99,255,0.22)]"
              : "text-slate-500 hover:text-slate-900"
          )}
        >
          Last {option} Months
        </Link>
      ))}
    </div>
  );
}

function DashboardSection({
  title,
  description,
  actions,
  children
}: {
  title: string;
  description?: string;
  actions?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section className="surface-panel p-6 lg:p-7">
      <div className="mb-5 flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="space-y-1">
          <h2 className="text-[1.9rem] font-semibold tracking-[-0.04em] text-slate-950 xl:text-[2rem]">
            {title}
          </h2>
          {description ? <p className="max-w-3xl text-sm text-slate-600">{description}</p> : null}
        </div>
        {actions ? <div className="flex items-center gap-3">{actions}</div> : null}
      </div>
      {children}
    </section>
  );
}

function MetricBlock({
  label,
  value
}: {
  label: string;
  value: string;
}) {
  return (
    <div className="rounded-[20px] border border-[var(--line)] bg-[var(--panel-soft)] px-4 py-3">
      <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">{label}</p>
      <p className="mt-2 text-lg font-semibold tracking-[-0.02em] text-slate-950">{value}</p>
    </div>
  );
}

function OverdueList({
  title,
  href,
  total,
  items,
  currency,
  emptyMessage
}: {
  title: string;
  href: string;
  total: number;
  items: DashboardModel["overdue"]["invoices"];
  currency: string;
  emptyMessage: string;
}) {
  return (
    <div className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <p className="text-sm font-semibold text-slate-950">{title}</p>
          <p className="mt-1 text-sm text-slate-500">{formatCurrency(total, currency)} total</p>
        </div>
        <Link href={href} className="dashboard-link">
          View
        </Link>
      </div>

      {items.length === 0 ? (
        <p className="mt-4 text-sm text-slate-500">{emptyMessage}</p>
      ) : (
        <ul className="mt-4 space-y-3">
          {items.map((item) => (
            <li key={item.id}>
              <Link href={item.href} className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold text-[var(--accent)]">{item.name}</p>
                  <p className="mt-1 text-xs text-slate-500">
                    Due {formatDate(item.dueDate)} • {item.daysOverdue} day
                    {item.daysOverdue === 1 ? "" : "s"} overdue
                  </p>
                </div>
                <span className="shrink-0 text-sm font-medium text-slate-700">
                  {formatCurrency(item.amount, currency)}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function CashFlowChart({
  points,
  currency
}: {
  points: DashboardModel["trends"];
  currency: string;
}) {
  const maxMagnitude = Math.max(
    1,
    ...points.flatMap((point) => [point.cashIn, point.cashOut, Math.abs(point.netChange)])
  );
  const axisValues = [maxMagnitude, maxMagnitude / 2, 0, -maxMagnitude / 2, -maxMagnitude];
  const chartWidth = Math.max(720, points.length * 52);
  const linePoints = points
    .map((point, index) => {
      const x = points.length === 1 ? 50 : (index / (points.length - 1)) * 100;
      const y = 50 - (point.netChange / maxMagnitude) * 45;
      return `${x},${y}`;
    })
    .join(" ");

  return (
    <div className="space-y-4">
      <Legend
        items={[
          { label: "Inflow", className: "bg-emerald-500" },
          { label: "Outflow", className: "bg-slate-300" },
          { label: "Net change", className: "border border-slate-950 bg-white" }
        ]}
      />
      <div className="overflow-x-auto pb-2">
        <div
          className="relative rounded-[28px] border border-[var(--line)] bg-[linear-gradient(180deg,#ffffff_0%,#f7fbff_100%)] p-5"
          style={{ minWidth: chartWidth }}
        >
          <div className="relative h-[320px]">
            <div className="absolute inset-y-0 left-0 w-16">
              {axisValues.map((value) => {
                const position = 50 - (value / maxMagnitude) * 45;

                return (
                  <div
                    key={value}
                    className="absolute left-0 w-full -translate-y-1/2 pr-3 text-right text-[11px] font-medium text-slate-400"
                    style={{ top: `${position}%` }}
                  >
                    {formatAxisCurrency(value, currency)}
                  </div>
                );
              })}
            </div>

            <div className="absolute inset-y-0 left-16 right-0">
              {axisValues.map((value) => {
                const position = 50 - (value / maxMagnitude) * 45;

                return (
                  <div
                    key={value}
                    className={cn(
                      "absolute inset-x-0 border-t",
                      value === 0 ? "border-slate-300" : "border-dashed border-slate-200"
                    )}
                    style={{ top: `${position}%` }}
                  />
                );
              })}

              <svg
                aria-hidden="true"
                viewBox="0 0 100 100"
                className="pointer-events-none absolute inset-0 h-full w-full overflow-visible"
                preserveAspectRatio="none"
              >
                <polyline
                  points={linePoints}
                  fill="none"
                  stroke="#0f172a"
                  strokeWidth="1.6"
                  vectorEffect="non-scaling-stroke"
                />
              </svg>

              <div
                className="absolute inset-x-0 top-0 grid h-full gap-3"
                style={{ gridTemplateColumns: `repeat(${points.length}, minmax(0, 1fr))` }}
              >
                {points.map((point) => {
                  const netPosition = 50 - (point.netChange / maxMagnitude) * 45;
                  const inflowHeight = (point.cashIn / maxMagnitude) * 45;
                  const outflowHeight = (point.cashOut / maxMagnitude) * 45;

                  return (
                    <div key={point.key} className="relative h-full">
                      <div
                        className="absolute left-1/2 w-5 -translate-x-1/2 rounded-t-[14px] bg-emerald-500/85"
                        style={{
                          bottom: "50%",
                          height: `${inflowHeight}%`
                        }}
                      />
                      <div
                        className="absolute left-1/2 w-5 -translate-x-1/2 rounded-b-[14px] bg-slate-300"
                        style={{
                          top: "50%",
                          height: `${outflowHeight}%`
                        }}
                      />
                      <div
                        className="absolute left-1/2 h-2.5 w-2.5 -translate-x-1/2 rounded-full border border-slate-950 bg-white shadow-sm"
                        style={{ top: `calc(${netPosition}% - 5px)` }}
                      />
                    </div>
                  );
                })}
              </div>
            </div>

            <div
              className="absolute bottom-0 left-16 right-0 grid gap-3 pt-4"
              style={{ gridTemplateColumns: `repeat(${points.length}, minmax(0, 1fr))` }}
            >
              {points.map((point) => (
                <div key={`${point.key}-label`} className="text-center text-[11px] text-slate-500">
                  <div>{point.monthLabel}</div>
                  <div>{point.yearLabel}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function ProfitLossChart({
  points,
  currency
}: {
  points: DashboardModel["trends"];
  currency: string;
}) {
  const maxValue = Math.max(1, ...points.flatMap((point) => [point.income, point.expense]));
  const axisValues = [maxValue, maxValue * 0.75, maxValue * 0.5, maxValue * 0.25, 0];
  const chartWidth = Math.max(720, points.length * 52);

  return (
    <div className="space-y-4">
      <Legend
        items={[
          { label: "Income", className: "bg-emerald-500" },
          { label: "Expense", className: "bg-slate-300" }
        ]}
      />
      <div className="overflow-x-auto pb-2">
        <div
          className="relative rounded-[28px] border border-[var(--line)] bg-[linear-gradient(180deg,#ffffff_0%,#f7fbff_100%)] p-5"
          style={{ minWidth: chartWidth }}
        >
          <div className="relative h-[320px]">
            <div className="absolute inset-y-0 left-0 w-16">
              {axisValues.map((value) => {
                const position = 100 - (value / maxValue) * 90;

                return (
                  <div
                    key={value}
                    className="absolute left-0 w-full -translate-y-1/2 pr-3 text-right text-[11px] font-medium text-slate-400"
                    style={{ top: `${position}%` }}
                  >
                    {formatAxisCurrency(value, currency)}
                  </div>
                );
              })}
            </div>

            <div className="absolute inset-y-0 left-16 right-0">
              {axisValues.map((value) => {
                const position = 100 - (value / maxValue) * 90;

                return (
                  <div
                    key={value}
                    className={cn(
                      "absolute inset-x-0 border-t",
                      value === 0 ? "border-slate-300" : "border-dashed border-slate-200"
                    )}
                    style={{ top: `${position}%` }}
                  />
                );
              })}

              <div
                className="absolute inset-x-0 bottom-10 top-0 grid gap-3"
                style={{ gridTemplateColumns: `repeat(${points.length}, minmax(0, 1fr))` }}
              >
                {points.map((point) => {
                  const incomeHeight = (point.income / maxValue) * 90;
                  const expenseHeight = (point.expense / maxValue) * 90;

                  return (
                    <div key={point.key} className="flex h-full items-end justify-center gap-2">
                      <div
                        className="w-4 rounded-t-[14px] bg-emerald-500/85"
                        style={{ height: `${incomeHeight}%` }}
                      />
                      <div
                        className="w-4 rounded-t-[14px] bg-slate-300"
                        style={{ height: `${expenseHeight}%` }}
                      />
                    </div>
                  );
                })}
              </div>
            </div>

            <div
              className="absolute bottom-0 left-16 right-0 grid gap-3 pt-4"
              style={{ gridTemplateColumns: `repeat(${points.length}, minmax(0, 1fr))` }}
            >
              {points.map((point) => (
                <div key={`${point.key}-label`} className="text-center text-[11px] text-slate-500">
                  <div>{point.monthLabel}</div>
                  <div>{point.yearLabel}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function Legend({
  items
}: {
  items: Array<{ label: string; className: string }>;
}) {
  return (
    <div className="flex flex-wrap items-center gap-5 text-sm text-slate-600">
      {items.map((item) => (
        <div key={item.label} className="flex items-center gap-2">
          <span className={cn("h-3 w-3 rounded-sm", item.className)} />
          <span>{item.label}</span>
        </div>
      ))}
    </div>
  );
}

function SummaryCell({
  label,
  value,
  hint,
  tone
}: {
  label: string;
  value: string;
  hint: string;
  tone: "neutral" | "positive" | "warning";
}) {
  const toneClass =
    tone === "positive"
      ? "border-emerald-200 bg-emerald-50/70"
      : tone === "warning"
        ? "border-amber-200 bg-amber-50/80"
        : "border-[var(--line)] bg-[var(--panel-soft)]";

  return (
    <div className={cn("rounded-[24px] border p-5", toneClass)}>
      <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">{label}</p>
      <p className="mt-3 text-2xl font-semibold tracking-[-0.03em] text-slate-950">{value}</p>
      <p className="mt-2 text-sm text-slate-600">{hint}</p>
    </div>
  );
}

function formatAxisCurrency(value: number, currency: string) {
  return new Intl.NumberFormat("en-CA", {
    style: "currency",
    currency,
    notation: "compact",
    maximumFractionDigits: 0
  }).format(value);
}

function maskAccountNumber(accountNumber: string) {
  return accountNumber.slice(-3).padStart(4, "0");
}
