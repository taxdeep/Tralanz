import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import JournalEntryEditor from "@/components/JournalEntryEditor";
import { Notice, PageHeader } from "@/components/workbench";

type NewJournalEntryPageProps = {
  searchParams: Promise<{ error?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-header-fields": "Date and entry number are required.",
  "create-at-least-two-lines": "Minimum 2 lines are required.",
  "line-account-required": "Every used line must include an account.",
  "invalid-amount": "Debit and credit must be valid positive numbers.",
  "both-debit-credit-not-allowed": "A line cannot have both debit and credit.",
  "line-amount-required": "Each used line must have a debit or credit amount.",
  "entry-not-balanced": "Entry must balance (total debit equals total credit).",
  "line-must-use-active-account": "Each line must use an active account.",
  "entry-number-exists": "Entry number already exists."
};

export default async function NewJournalEntryPage({ searchParams }: NewJournalEntryPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;

  const activeAccounts = await prisma.account.findMany({
    where: { userId: user.id, isActive: true },
    orderBy: [{ accountNumber: "asc" }]
  });

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="General journal"
        title="Journal entry workspace"
        description="把原来简单的 8 行录入表单改成了更像财务系统的工作台：顶部是单据头，主体是可扩展分录表，右侧实时展示借贷平衡。"
      />

      {error ? (
        <Notice variant="danger">
          {error}
        </Notice>
      ) : null}

      {activeAccounts.length === 0 ? (
        <Notice variant="warning">
          You need at least one active account before creating a journal entry.
        </Notice>
      ) : (
        <JournalEntryEditor
          accounts={activeAccounts.map((account: (typeof activeAccounts)[number]) => ({
            id: account.id,
            label: `${account.accountNumber} - ${account.accountName}`
          }))}
        />
      )}
    </div>
  );
}
