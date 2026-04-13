import type { ReactNode } from "react";
import { cn } from "@/lib/cn";

type IconProps = {
  className?: string;
};

function IconBase({
  className,
  children,
  viewBox = "0 0 24 24"
}: IconProps & { children: ReactNode; viewBox?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox={viewBox}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={cn("h-5 w-5 shrink-0", className)}
    >
      {children}
    </svg>
  );
}

export function LogoMark({ className }: IconProps) {
  return (
    <svg aria-hidden="true" viewBox="0 0 48 48" className={cn("h-9 w-9 shrink-0", className)}>
      <circle cx="24" cy="24" r="22" fill="currentColor" opacity="0.1" />
      <path
        d="M12 26c4.6-8.5 11.1-13.2 19.5-14 2.5-.2 4.3 2.1 3.4 4.4-2.3 5.9-7.6 11.3-15.8 16-2 1.1-4.5-.4-4.6-2.6L14 26h-2Z"
        fill="currentColor"
      />
      <path
        d="M17 18c2.3 3.4 5.9 5.1 10.9 5"
        stroke="currentColor"
        strokeWidth="2.1"
        strokeLinecap="round"
      />
    </svg>
  );
}

export function SearchIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </IconBase>
  );
}

export function HomeIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M3 10.5 12 4l9 6.5" />
      <path d="M5 9.5V20h14V9.5" />
      <path d="M10 20v-6h4v6" />
    </IconBase>
  );
}

export function JournalIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M6 4h9l3 3v13H6z" />
      <path d="M15 4v4h4" />
      <path d="M9 12h6" />
      <path d="M9 16h6" />
    </IconBase>
  );
}

export function ReportsIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M5 19h14" />
      <path d="M7 16V9" />
      <path d="M12 16V5" />
      <path d="M17 16v-4" />
    </IconBase>
  );
}

export function PeopleIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M15 19v-1a3 3 0 0 0-3-3H7a3 3 0 0 0-3 3v1" />
      <circle cx="9.5" cy="8" r="3" />
      <path d="M20 19v-1a3 3 0 0 0-2.2-2.9" />
      <path d="M15.6 5.2a3 3 0 1 1 0 5.7" />
    </IconBase>
  );
}

export function InvoiceIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M7 3h10l3 3v15H7z" />
      <path d="M12 10h5" />
      <path d="M10 14h7" />
      <path d="M10 18h4" />
      <path d="M17 3v4h4" />
    </IconBase>
  );
}

export function BillIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <rect x="5" y="4" width="14" height="16" rx="2" />
      <path d="M9 9h6" />
      <path d="M9 13h6" />
      <path d="M9 17h3" />
    </IconBase>
  );
}

export function LedgerIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <rect x="4" y="5" width="16" height="14" rx="2" />
      <path d="M8 9h8" />
      <path d="M8 13h8" />
      <path d="M8 17h4" />
    </IconBase>
  );
}

export function SettingsIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1 1 0 0 0 .2 1.1l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1 1 0 0 0-1.1-.2 1 1 0 0 0-.6.9V20a2 2 0 1 1-4 0v-.2a1 1 0 0 0-.6-.9 1 1 0 0 0-1.1.2l-.1.1a2 2 0 0 1-2.8-2.8l.1-.1a1 1 0 0 0 .2-1.1 1 1 0 0 0-.9-.6H4a2 2 0 1 1 0-4h.2a1 1 0 0 0 .9-.6 1 1 0 0 0-.2-1.1l-.1-.1a2 2 0 0 1 2.8-2.8l.1.1a1 1 0 0 0 1.1.2 1 1 0 0 0 .6-.9V4a2 2 0 1 1 4 0v.2a1 1 0 0 0 .6.9 1 1 0 0 0 1.1-.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1 1 0 0 0-.2 1.1 1 1 0 0 0 .9.6H20a2 2 0 1 1 0 4h-.2a1 1 0 0 0-.9.6Z" />
    </IconBase>
  );
}

export function FilterIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M4 6h16" />
      <path d="M7 12h10" />
      <path d="M10 18h4" />
    </IconBase>
  );
}

export function PlusIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M12 5v14" />
      <path d="M5 12h14" />
    </IconBase>
  );
}

export function ChevronDownIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="m6 9 6 6 6-6" />
    </IconBase>
  );
}

export function LogoutIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4" />
      <path d="M10 17 15 12 10 7" />
      <path d="M15 12H4" />
    </IconBase>
  );
}

export function ArrowUpRightIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M7 17 17 7" />
      <path d="M8 7h9v9" />
    </IconBase>
  );
}

export function CheckCircleIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <circle cx="12" cy="12" r="9" />
      <path d="m8.5 12.3 2.3 2.3 4.7-5.4" />
    </IconBase>
  );
}

export function AlertTriangleIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="M12 3 2.8 19h18.4L12 3Z" />
      <path d="M12 9v4" />
      <path d="M12 17h.01" />
    </IconBase>
  );
}

export function SparklesIcon({ className }: IconProps) {
  return (
    <IconBase className={className}>
      <path d="m12 3 1.5 4.5L18 9l-4.5 1.5L12 15l-1.5-4.5L6 9l4.5-1.5L12 3Z" />
      <path d="m5 16 .8 2.2L8 19l-2.2.8L5 22l-.8-2.2L2 19l2.2-.8L5 16Z" />
      <path d="m19 14 .8 2.2L22 17l-2.2.8L19 20l-.8-2.2L16 17l2.2-.8L19 14Z" />
    </IconBase>
  );
}
