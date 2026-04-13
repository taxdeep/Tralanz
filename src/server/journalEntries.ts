import { prisma } from "@/lib/prisma";

export type JournalLineInput = {
  accountId: string;
  debit: string;
  credit: string;
  details: string;
};

function toAmount(value: string): number {
  if (!value.trim()) return 0;
  const parsed = Number(value);
  if (Number.isNaN(parsed) || parsed < 0) {
    return -1;
  }
  return parsed;
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

export async function validateJournalLines(
  userId: string,
  lines: JournalLineInput[]
): Promise<{ valid: true; normalized: JournalLineInput[] } | { valid: false; error: string }> {
  const normalized = lines.filter(
    (line) =>
      line.accountId.trim() ||
      line.debit.trim() ||
      line.credit.trim() ||
      line.details.trim()
  );

  if (normalized.length < 2) {
    return { valid: false, error: "at-least-two-lines" };
  }

  let totalDebit = 0;
  let totalCredit = 0;

  for (const line of normalized) {
    if (!line.accountId.trim()) {
      return { valid: false, error: "line-account-required" };
    }

    const debit = toAmount(line.debit);
    const credit = toAmount(line.credit);

    if (debit < 0 || credit < 0) {
      return { valid: false, error: "invalid-amount" };
    }

    if (debit > 0 && credit > 0) {
      return { valid: false, error: "both-debit-credit-not-allowed" };
    }

    if (debit === 0 && credit === 0) {
      return { valid: false, error: "line-amount-required" };
    }

    totalDebit += debit;
    totalCredit += credit;
  }

  if (round2(totalDebit) !== round2(totalCredit)) {
    return { valid: false, error: "entry-not-balanced" };
  }

  const accountIds = [...new Set(normalized.map((line) => line.accountId.trim()))];
  const activeAccounts = await prisma.account.findMany({
    where: {
      userId,
      id: { in: accountIds },
      isActive: true
    },
    select: { id: true }
  });

  const activeSet = new Set(activeAccounts.map((account) => account.id));
  for (const accountId of accountIds) {
    if (!activeSet.has(accountId)) {
      return { valid: false, error: "line-must-use-active-account" };
    }
  }

  return { valid: true, normalized };
}
