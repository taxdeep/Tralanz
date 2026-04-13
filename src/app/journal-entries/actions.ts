"use server";

import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthAndSetup } from "@/lib/guards";
import { validateJournalLines } from "@/server/journalEntries";

const MAX_JOURNAL_LINES = 12;

const errorMap: Record<string, string> = {
  "at-least-two-lines": "create-at-least-two-lines",
  "line-account-required": "line-account-required",
  "invalid-amount": "invalid-amount",
  "both-debit-credit-not-allowed": "both-debit-credit-not-allowed",
  "line-amount-required": "line-amount-required",
  "entry-not-balanced": "entry-not-balanced",
  "line-must-use-active-account": "line-must-use-active-account"
};

export async function createJournalEntryAction(formData: FormData) {
  const user = await requireAuthAndSetup();

  const entryDate = String(formData.get("entryDate") ?? "").trim();
  const entryNumber = String(formData.get("entryNumber") ?? "").trim();
  const memo = String(formData.get("memo") ?? "").trim();

  if (!entryDate || !entryNumber) {
    redirect("/journal-entries/new?error=missing-header-fields");
  }

  const lines = Array.from({ length: MAX_JOURNAL_LINES }, (_, idx) => ({
    accountId: String(formData.get(`line-${idx}-accountId`) ?? "").trim(),
    debit: String(formData.get(`line-${idx}-debit`) ?? "").trim(),
    credit: String(formData.get(`line-${idx}-credit`) ?? "").trim(),
    details: String(formData.get(`line-${idx}-details`) ?? "").trim()
  }));

  const validation = await validateJournalLines(user.id, lines);
  if (!validation.valid) {
    redirect(`/journal-entries/new?error=${errorMap[validation.error] ?? "validation-error"}`);
  }

  const existing = await prisma.journalEntry.findFirst({
    where: { userId: user.id, entryNumber },
    select: { id: true }
  });
  if (existing) {
    redirect("/journal-entries/new?error=entry-number-exists");
  }

  const created = await prisma.journalEntry.create({
    data: {
      userId: user.id,
      entryNumber,
      entryDate: new Date(`${entryDate}T00:00:00`),
      memo: memo || null,
      status: "draft",
      lines: {
        create: validation.normalized.map((line: (typeof validation.normalized)[number], index) => ({
          lineNumber: index + 1,
          accountId: line.accountId,
          debitAmount: Number(line.debit || "0"),
          creditAmount: Number(line.credit || "0"),
          details: line.details || null
        }))
      }
    }
  });

  redirect(`/journal-entries/${created.id}`);
}

export async function postJournalEntryAction(formData: FormData) {
  const user = await requireAuthAndSetup();
  const journalEntryId = String(formData.get("journalEntryId") ?? "").trim();

  if (!journalEntryId) {
    redirect("/journal-entries?error=missing-entry-id");
  }

  const entry = await prisma.journalEntry.findFirst({
    where: { id: journalEntryId, userId: user.id },
    include: { lines: true }
  });

  if (!entry) {
    redirect("/journal-entries?error=entry-not-found");
  }

  if (entry.status === "posted") {
    redirect(`/journal-entries/${entry.id}?info=already-posted`);
  }

  const validation = await validateJournalLines(
    user.id,
    entry.lines.map((line: (typeof entry.lines)[number]) => ({
      accountId: line.accountId,
      debit: String(line.debitAmount),
      credit: String(line.creditAmount),
      details: line.details ?? ""
    }))
  );

  if (!validation.valid) {
    redirect(`/journal-entries/${entry.id}?error=${errorMap[validation.error] ?? "validation-error"}`);
  }

  await prisma.journalEntry.update({
    where: { id: entry.id },
    data: {
      status: "posted",
      postedAt: new Date()
    }
  });

  redirect(`/journal-entries/${entry.id}?posted=1`);
}
