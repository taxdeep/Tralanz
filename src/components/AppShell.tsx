"use client";

import Link from "next/link";
import { ReactNode } from "react";
import { usePathname } from "next/navigation";
import Sidebar from "@/components/Sidebar";
import {
  ChevronDownIcon,
  PlusIcon,
  ReportsIcon,
  SearchIcon,
  SettingsIcon
} from "@/components/icons";

type AppShellProps = {
  children: ReactNode;
};

const routeTitles: Record<string, string> = {
  "/dashboard": "Dashboard",
  "/customers": "Customers",
  "/vendors": "Vendors",
  "/sales-invoices": "Sales & payments",
  "/bills": "Purchases",
  "/journal-entries": "Accounting",
  "/chart-of-accounts": "Chart of accounts",
  "/reports": "Reports",
  "/settings": "Settings"
};

export default function AppShell({ children }: AppShellProps) {
  const pathname = usePathname();
  const isStandalone =
    pathname === "/login" || pathname === "/setup-wizard" || pathname.startsWith("/setup-wizard/");

  if (isStandalone) {
    return <>{children}</>;
  }

  const currentTitle =
    Object.entries(routeTitles).find(([route]) => pathname === route || pathname.startsWith(`${route}/`))
      ?.[1] ?? "Workspace";

  return (
    <div className="min-h-screen bg-[var(--surface-app)] text-slate-900">
      <div className="flex min-h-screen">
        <Sidebar />
        <div className="min-w-0 flex-1">
          <header className="sticky top-0 z-30 border-b border-[var(--line)] bg-[rgba(245,249,255,0.92)] backdrop-blur-xl">
            <div className="mx-auto flex max-w-[1700px] flex-wrap items-center gap-4 px-5 py-4 lg:px-8">
              <div className="min-w-0 flex-1">
                <p className="text-[11px] font-semibold uppercase tracking-[0.22em] text-slate-500">
                  Citus accounting
                </p>
                <p className="truncate text-sm font-medium text-slate-900">{currentTitle}</p>
              </div>

              <div className="hidden min-w-[280px] flex-[1.2] items-center justify-center xl:flex">
                <div className="flex w-full max-w-xl items-center gap-3 rounded-full border border-[var(--line)] bg-white px-4 py-2.5 text-sm text-slate-500 shadow-[0_12px_22px_rgba(15,23,42,0.05)]">
                  <SearchIcon className="h-4 w-4" />
                  <span className="truncate">Search transactions, contacts, reports, and setup</span>
                </div>
              </div>

              <div className="ml-auto flex items-center gap-3">
                <Link href="/sales-invoices/new" className="btn-primary hidden sm:inline-flex">
                  <PlusIcon className="h-4 w-4" />
                  Create invoice
                </Link>
                <Link href="/reports" className="icon-button" aria-label="Open reports">
                  <ReportsIcon className="h-4 w-4" />
                </Link>
                <Link href="/settings" className="icon-button" aria-label="Open settings">
                  <SettingsIcon className="h-4 w-4" />
                </Link>
                <button
                  type="button"
                  className="flex items-center gap-2 rounded-full border border-[var(--line)] bg-white px-3 py-2 text-sm font-semibold text-[#123772] shadow-sm"
                >
                  Default workspace
                  <ChevronDownIcon className="h-4 w-4" />
                </button>
              </div>
            </div>
          </header>

          <main className="px-5 py-6 pb-24 lg:px-8 lg:py-8 lg:pb-8">
            <div className="mx-auto w-full max-w-[1700px]">{children}</div>
          </main>
        </div>
      </div>
    </div>
  );
}
