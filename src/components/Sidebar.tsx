"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  BillIcon,
  HomeIcon,
  InvoiceIcon,
  JournalIcon,
  LedgerIcon,
  LogoMark,
  LogoutIcon,
  PeopleIcon,
  PlusIcon,
  ReportsIcon,
  SettingsIcon
} from "@/components/icons";
import { cn } from "@/lib/cn";

const navigationItems = [
  { href: "/dashboard", label: "Dashboard", icon: HomeIcon },
  { href: "/sales-invoices", label: "Sales & payments", icon: InvoiceIcon },
  { href: "/bills", label: "Purchases", icon: BillIcon },
  { href: "/customers", label: "Customers", icon: PeopleIcon },
  { href: "/vendors", label: "Vendors", icon: PeopleIcon },
  { href: "/journal-entries", label: "Accounting", icon: JournalIcon },
  { href: "/chart-of-accounts", label: "Chart of accounts", icon: LedgerIcon },
  { href: "/reports", label: "Reports", icon: ReportsIcon },
  { href: "/settings", label: "Settings", icon: SettingsIcon }
];

export default function Sidebar() {
  const pathname = usePathname();

  return (
    <>
      <aside className="hidden min-h-screen w-[264px] shrink-0 border-r border-[var(--line)] bg-white xl:block">
        <div className="sticky top-0 flex h-screen flex-col px-5 py-5">
          <Link href="/dashboard" className="flex items-center gap-3 px-2 py-2">
            <span className="flex h-11 w-11 items-center justify-center rounded-[18px] bg-[var(--accent)] text-white shadow-[0_14px_28px_rgba(30,99,255,0.18)]">
              <LogoMark className="h-7 w-7 text-white" />
            </span>
            <div className="min-w-0">
              <p className="text-xl font-semibold tracking-[-0.04em] text-[#123772]">citus</p>
              <p className="text-xs uppercase tracking-[0.22em] text-slate-400">accounting</p>
            </div>
          </Link>

          <Link
            href="/journal-entries/new"
            className="mt-8 inline-flex items-center gap-3 rounded-full bg-[var(--accent)] px-4 py-3 text-sm font-semibold text-white shadow-[0_16px_30px_rgba(30,99,255,0.24)] transition hover:bg-[var(--accent-deep)]"
          >
            <PlusIcon className="h-4 w-4" />
            Create new
          </Link>

          <nav className="mt-8 flex-1 space-y-1 overflow-y-auto pr-1">
            {navigationItems.map((item) => {
              const isActive = pathname === item.href || pathname.startsWith(`${item.href}/`);
              const Icon = item.icon;

              return (
                <Link
                  key={item.href}
                  href={item.href}
                  className={cn(
                    "group flex items-center gap-3 rounded-[18px] px-3 py-3 text-sm font-medium transition",
                    isActive
                      ? "bg-[#eef4ff] text-[#123772]"
                      : "text-slate-600 hover:bg-[var(--panel-soft)] hover:text-slate-900"
                  )}
                >
                  <span
                    className={cn(
                      "flex h-9 w-9 items-center justify-center rounded-[14px] transition",
                      isActive
                        ? "bg-white text-[var(--accent)] ring-1 ring-inset ring-[#d9e4f5]"
                        : "bg-[var(--panel-soft)] text-slate-500 group-hover:bg-white group-hover:text-[var(--accent)] group-hover:ring-1 group-hover:ring-inset group-hover:ring-[var(--line)]"
                    )}
                  >
                    <Icon className="h-4 w-4" />
                  </span>
                  <span className="truncate">{item.label}</span>
                </Link>
              );
            })}
          </nav>

          <div className="mt-5 rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-3">
            <Link
              href="/logout"
              className="flex items-center gap-3 rounded-[18px] px-3 py-3 text-sm font-medium text-slate-700 transition hover:bg-white"
            >
              <span className="flex h-10 w-10 items-center justify-center rounded-[16px] bg-white text-slate-700 ring-1 ring-inset ring-[var(--line)]">
                <LogoutIcon className="h-4 w-4" />
              </span>
              Sign out
            </Link>
          </div>
        </div>
      </aside>

      <nav className="fixed inset-x-0 bottom-0 z-40 border-t border-[var(--line)] bg-white/95 px-2 py-2 backdrop-blur xl:hidden">
        <div className="grid grid-cols-5 gap-2">
          {[
            navigationItems[0],
            navigationItems[1],
            navigationItems[2],
            navigationItems[5],
            navigationItems[7]
          ].map((item) => {
            const isActive = pathname === item.href || pathname.startsWith(`${item.href}/`);
            const Icon = item.icon;

            return (
              <Link
                key={item.href}
                href={item.href}
                className={cn(
                  "flex flex-col items-center justify-center rounded-2xl px-2 py-2 text-[11px] font-medium transition",
                  isActive ? "bg-[var(--accent)] text-white" : "text-slate-600 hover:bg-[var(--panel-soft)]"
                )}
              >
                <Icon className="mb-1 h-4 w-4" />
                <span className="truncate">{item.label}</span>
              </Link>
            );
          })}
        </div>
      </nav>
    </>
  );
}
