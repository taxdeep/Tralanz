import Link from "next/link";
import { redirect } from "next/navigation";
import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import { postJournalEntryAction } from "@/app/journal-entries/actions";
import { formatCurrency, formatDate } from "@/lib/format";
import { Notice, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

type JournalEntryDetailPageProps = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ error?: string; posted?: string; info?: string }>;
};

const errorMessages: Record<string, string> = {
  "entry-not-balanced": "Entry cannot be posted because it is not balanced.",
  "line-must-use-active-account":
    "Entry cannot be posted because one or more lines use inactive accounts.",
  "line-account-required": "Entry has a line without account.",
  "line-amount-required": "Entry has a line without debit/credit amount.",
  "both-debit-credit-not-allowed": "Entry has a line with both debit and credit."
};

export default async function JournalEntryDetailPage({
  params,
  searchParams
}: JournalEntryDetailPageProps) {
  const user = await requireAuthAndSetup();
  const { id } = await params;
  const query = await searchParams;

  const entry = await prisma.journalEntry.findFirst({
    where: { id, userId: user.id },
    include: {
      lines: {
        orderBy: { lineNumber: "asc" },
        include: { account: true }
      }
    }
  });

  if (!entry) {
    redirect("/journal-entries");
  }

  const totalDebit = entry.lines.reduce(
    (sum: number, line: (typeof entry.lines)[number]) => sum + line.debitAmount,
    0
  );
  const totalCredit = entry.lines.reduce(
    (sum: number, line: (typeof entry.lines)[number]) => sum + line.creditAmount,
    0
  );
  const isBalanced = Number(totalDebit.toFixed(2)) === Number(totalCredit.toFixed(2));

  const error = query.error ? errorMessages[query.error] : null;
  const posted = query.posted === "1";
  const alreadyPosted = query.info === "already-posted";

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="General ledger document"
        title={`Journal entry ${entry.entryNumber}`}
        description="详情页突出分录平衡状态和每一条借贷腿，便于会计人员复核后再过账。"
        actions={
          <>
            <Link href="/journal-entries" className="btn-secondary">
              Back to journal
            </Link>
            {entry.status === "draft" ? (
              <form action={postJournalEntryAction}>
                <input type="hidden" name="journalEntryId" value={entry.id} />
                <button type="submit" className="btn-primary" disabled={!isBalanced}>
                  Post entry
                </button>
              </form>
            ) : null}
          </>
        }
      />

      {error ? <Notice variant="danger">{error}</Notice> : null}
      {posted ? <Notice variant="success">Entry posted successfully. Posted entries are now locked.</Notice> : null}
      {alreadyPosted ? <Notice variant="info">This entry is already posted.</Notice> : null}
      {!isBalanced && entry.status === "draft" ? (
        <Notice variant="warning">Entry must be balanced before posting.</Notice>
      ) : null}

      <div className="grid gap-4 md:grid-cols-4">
        <StatCard label="Journal date" value={formatDate(entry.entryDate)} hint="Posting date for the ledger." />
        <StatCard label="Status" value={entry.status.toUpperCase()} hint={entry.memo || "No memo added to this journal."} tone={entry.status === "posted" ? "positive" : "warning"} />
        <StatCard label="Total debit" value={formatCurrency(totalDebit)} hint={`${entry.lines.length} line(s) recorded.`} />
        <StatCard label="Total credit" value={formatCurrency(totalCredit)} hint={isBalanced ? "Debits and credits are balanced." : "Debits and credits do not match yet."} />
      </div>

      <SurfaceSection title="Journal lines" description={entry.memo || "No memo was added to this entry."}>
        <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
          <div className="grid grid-cols-[280px_150px_150px_1fr] gap-3 bg-[var(--panel-soft)] px-5 py-3">
            <div className="table-head-label">Account</div>
            <div className="table-head-label text-right">Debit</div>
            <div className="table-head-label text-right">Credit</div>
            <div className="table-head-label">Details</div>
          </div>
          {entry.lines.map((line: (typeof entry.lines)[number]) => (
            <div
              key={line.id}
              className="grid grid-cols-[280px_150px_150px_1fr] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
            >
              <div className="text-slate-700">
                {line.account.accountNumber} - {line.account.accountName}
              </div>
              <div className="text-right font-semibold text-slate-950">
                {line.debitAmount ? formatCurrency(line.debitAmount) : "-"}
              </div>
              <div className="text-right font-semibold text-slate-950">
                {line.creditAmount ? formatCurrency(line.creditAmount) : "-"}
              </div>
              <div className="text-slate-700">{line.details || "No details"}</div>
            </div>
          ))}
        </div>

        <div className="mt-5 flex items-center gap-3">
          <StatusBadge status={isBalanced ? "balanced" : "out of balance"} />
          <span className="text-sm text-slate-600">
            {isBalanced
              ? "This draft is balanced and can be posted."
              : "Adjust the lines until debit equals credit."}
          </span>
        </div>
      </SurfaceSection>

      {entry.status === "posted" ? (
        <Notice variant="info">Posted entries are locked and cannot be edited.</Notice>
      ) : null}
    </div>
  );
}
