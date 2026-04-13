import Link from "next/link";
import { ReactNode } from "react";
import { cn } from "@/lib/cn";
import { AlertTriangleIcon, ArrowUpRightIcon, CheckCircleIcon } from "@/components/icons";
import { titleCaseStatus } from "@/lib/format";

type PageHeaderProps = {
  eyebrow?: string;
  title: string;
  description?: string;
  actions?: ReactNode;
};

export function PageHeader({ eyebrow, title, description, actions }: PageHeaderProps) {
  return (
    <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
      <div className="space-y-3">
        {eyebrow ? <p className="eyebrow-label">{eyebrow}</p> : null}
        <div className="space-y-2">
          <h1 className="text-3xl font-semibold tracking-[-0.03em] text-slate-950">{title}</h1>
          {description ? <p className="max-w-3xl text-sm text-slate-600">{description}</p> : null}
        </div>
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-3">{actions}</div> : null}
    </div>
  );
}

export function SurfacePanel({
  className,
  children
}: {
  className?: string;
  children: ReactNode;
}) {
  return <section className={cn("surface-panel", className)}>{children}</section>;
}

export function SurfaceSection({
  className,
  title,
  description,
  actions,
  children
}: {
  className?: string;
  title?: string;
  description?: string;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <SurfacePanel className={cn("p-6 lg:p-7", className)}>
      {title || description || actions ? (
        <div className="mb-5 flex flex-col gap-4 xl:flex-row xl:items-center xl:justify-between">
          <div className="space-y-1">
            {title ? <h2 className="text-lg font-semibold text-slate-950">{title}</h2> : null}
            {description ? <p className="text-sm text-slate-600">{description}</p> : null}
          </div>
          {actions ? <div className="flex flex-wrap items-center gap-3">{actions}</div> : null}
        </div>
      ) : null}
      {children}
    </SurfacePanel>
  );
}

export function StatCard({
  label,
  value,
  hint,
  tone = "neutral"
}: {
  label: string;
  value: string;
  hint?: string;
  tone?: "neutral" | "positive" | "warning";
}) {
  const toneClasses =
    tone === "positive"
      ? "from-emerald-50 to-white border-emerald-200"
      : tone === "warning"
        ? "from-amber-50 to-white border-amber-200"
        : "from-slate-50 to-white border-[var(--line)]";

  return (
    <div
      className={cn(
        "rounded-[24px] border bg-gradient-to-br p-5 shadow-[0_12px_35px_rgba(15,23,42,0.05)]",
        toneClasses
      )}
    >
      <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
        {label}
      </p>
      <p className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-slate-950">{value}</p>
      {hint ? <p className="mt-2 text-sm text-slate-600">{hint}</p> : null}
    </div>
  );
}

export function Notice({
  variant,
  children,
  className
}: {
  variant: "success" | "warning" | "danger" | "info";
  children: ReactNode;
  className?: string;
}) {
  const styles =
    variant === "success"
      ? "border-emerald-200 bg-emerald-50 text-emerald-900"
      : variant === "warning"
        ? "border-amber-200 bg-amber-50 text-amber-900"
        : variant === "danger"
          ? "border-rose-200 bg-rose-50 text-rose-900"
          : "border-sky-200 bg-sky-50 text-sky-900";

  const Icon = variant === "danger" || variant === "warning" ? AlertTriangleIcon : CheckCircleIcon;

  return (
    <div className={cn("flex items-start gap-3 rounded-[22px] border px-4 py-3 text-sm", styles, className)}>
      <Icon className="mt-0.5 h-4 w-4" />
      <div>{children}</div>
    </div>
  );
}

export function StatusBadge({
  status,
  className
}: {
  status: string;
  className?: string;
}) {
  const normalized = status.toLowerCase();
  const tone =
    normalized === "posted" ||
    normalized === "active" ||
    normalized === "balanced" ||
    normalized === "recoverable"
      ? "bg-emerald-50 text-emerald-800 ring-emerald-200"
      : normalized === "draft" ||
          normalized === "out of balance" ||
          normalized === "non-recoverable" ||
          normalized === "inactive"
        ? "bg-amber-50 text-amber-800 ring-amber-200"
        : "bg-slate-100 text-slate-700 ring-slate-200";

  return (
    <span
      className={cn(
        "inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ring-1 ring-inset",
        tone,
        className
      )}
    >
      {titleCaseStatus(status)}
    </span>
  );
}

export function EmptyState({
  title,
  description,
  actionHref,
  actionLabel
}: {
  title: string;
  description: string;
  actionHref?: string;
  actionLabel?: string;
}) {
  return (
    <div className="rounded-[24px] border border-dashed border-[var(--line)] bg-[var(--panel-soft)] px-6 py-10 text-center">
      <p className="text-lg font-semibold text-slate-900">{title}</p>
      <p className="mx-auto mt-2 max-w-xl text-sm text-slate-600">{description}</p>
      {actionHref && actionLabel ? (
        <Link href={actionHref} className="btn-primary mt-5 inline-flex">
          {actionLabel}
          <ArrowUpRightIcon className="h-4 w-4" />
        </Link>
      ) : null}
    </div>
  );
}

export function InfoPill({ children }: { children: ReactNode }) {
  return (
    <span className="inline-flex items-center rounded-full bg-[var(--panel-soft)] px-3 py-1.5 text-xs font-medium text-slate-600 ring-1 ring-inset ring-[var(--line)]">
      {children}
    </span>
  );
}
