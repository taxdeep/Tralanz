"use server";
import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthAndSetup } from "@/lib/guards";

const MAX_BILL_LINES = 10;

type BillDraftLine = {
  expenseAccountId: string;
  description: string;
  amount: string;
  taxCodeId: string;
};

function round2(value: number) {
  return Math.round(value * 100) / 100;
}

export async function createBillAction(formData: FormData) {
  const user = await requireAuthAndSetup();

  const vendorId = String(formData.get("vendorId") ?? "").trim();
  const billNumber = String(formData.get("billNumber") ?? "").trim();
  const billDate = String(formData.get("billDate") ?? "").trim();
  const dueDate = String(formData.get("dueDate") ?? "").trim();
  const memo = String(formData.get("memo") ?? "").trim();

  if (!vendorId || !billNumber || !billDate || !dueDate) {
    redirect("/bills/new?error=missing-required-fields");
  }

  const normalizedLines = Array.from({ length: MAX_BILL_LINES }, (_, index): BillDraftLine => ({
    expenseAccountId: String(formData.get(`line-${index}-expenseAccountId`) ?? "").trim(),
    description: String(formData.get(`line-${index}-description`) ?? "").trim(),
    amount: String(formData.get(`line-${index}-amount`) ?? "").trim(),
    taxCodeId: String(formData.get(`line-${index}-taxCodeId`) ?? "").trim()
  })).filter((line: BillDraftLine) => line.expenseAccountId || line.description || line.amount || line.taxCodeId);

  if (normalizedLines.length === 0) {
    redirect("/bills/new?error=create-at-least-one-line");
  }

  const parsedBillDate = new Date(`${billDate}T00:00:00`);
  const parsedDueDate = new Date(`${dueDate}T00:00:00`);
  if (Number.isNaN(parsedBillDate.getTime()) || Number.isNaN(parsedDueDate.getTime())) {
    redirect("/bills/new?error=missing-required-fields");
  }
  if (parsedDueDate < parsedBillDate) {
    redirect("/bills/new?error=due-date-before-bill-date");
  }

  const vendor = await prisma.vendor.findFirst({
    where: { id: vendorId, userId: user.id, isActive: true },
    select: { id: true }
  });
  if (!vendor) {
    redirect("/bills/new?error=invalid-vendor");
  }

  const existing = await prisma.bill.findFirst({
    where: { userId: user.id, billNumber },
    select: { id: true }
  });
  if (existing) {
    redirect("/bills/new?error=bill-number-exists");
  }

  const accountIds = [...new Set(normalizedLines.map((line: (typeof normalizedLines)[number]) => line.expenseAccountId).filter(Boolean))];
  const activeAccounts = await prisma.account.findMany({
    where: {
      userId: user.id,
      isActive: true,
      id: { in: accountIds },
      accountType: { in: ["expense", "asset"] }
    },
    select: { id: true }
  });
  const activeAccountIds = new Set(activeAccounts.map((account: (typeof activeAccounts)[number]) => account.id));

  const taxCodeIds = [...new Set(normalizedLines.map((line: (typeof normalizedLines)[number]) => line.taxCodeId).filter(Boolean))];
  const taxCodes = taxCodeIds.length
    ? await prisma.taxCode.findMany({
        where: {
          userId: user.id,
          isActive: true,
          id: { in: taxCodeIds },
          OR: [{ appliesTo: "purchase" }, { appliesTo: "both" }]
        },
        select: {
          id: true,
          ratePercent: true,
          isRecoverableOnPurchase: true
        }
      })
    : [];
  const taxCodeMap = new Map<string, (typeof taxCodes)[number]>(
    taxCodes.map((taxCode: (typeof taxCodes)[number]) => [taxCode.id, taxCode])
  );

  const preparedLines = normalizedLines.map((line: (typeof normalizedLines)[number]) => {
    if (!line.expenseAccountId || !line.description || !line.amount) {
      redirect("/bills/new?error=line-incomplete");
    }

    if (!activeAccountIds.has(line.expenseAccountId)) {
      redirect("/bills/new?error=invalid-expense-account");
    }

    const amount = Number(line.amount);
    if (!Number.isFinite(amount) || amount <= 0) {
      redirect("/bills/new?error=invalid-line-amount");
    }

    const taxCode = line.taxCodeId ? taxCodeMap.get(line.taxCodeId) : null;
    if (line.taxCodeId && !taxCode) {
      redirect("/bills/new?error=invalid-tax-code");
    }

    const lineAmount = round2(amount);
    const taxAmount = round2(lineAmount * ((taxCode?.ratePercent ?? 0) / 100));

    return {
      description: line.description,
      amount: lineAmount,
      taxAmount,
      expenseAccountId: line.expenseAccountId,
      taxCodeId: taxCode?.id ?? null,
      isTaxRecoverable: taxCode?.isRecoverableOnPurchase ?? false
    };
  });

  const subtotal = round2(
    preparedLines.reduce(
      (sum: number, line: (typeof preparedLines)[number]) => sum + line.amount,
      0
    )
  );
  const taxAmount = round2(
    preparedLines.reduce(
      (sum: number, line: (typeof preparedLines)[number]) => sum + line.taxAmount,
      0
    )
  );
  const totalAmount = round2(subtotal + taxAmount);

  const bill = await prisma.bill.create({
    data: {
      userId: user.id,
      vendorId,
      billNumber,
      billDate: parsedBillDate,
      dueDate: parsedDueDate,
      memo: memo || null,
      status: "draft",
      subtotal,
      taxAmount,
      totalAmount,
      lines: {
        create: preparedLines
      }
    }
  });

  redirect(`/bills/${bill.id}`);
}

export async function postBillAction(formData: FormData) {
  const user = await requireAuthAndSetup();
  const billId = String(formData.get("billId") ?? "").trim();
  if (!billId) {
    redirect("/bills?error=missing-bill-id");
  }

  const bill = await prisma.bill.findFirst({
    where: { id: billId, userId: user.id },
    include: {
      lines: {
        include: {
          expenseAccount: true,
          taxCode: true
        }
      }
    }
  });
  if (!bill) {
    redirect("/bills?error=bill-not-found");
  }
  if (bill.status === "posted") {
    redirect(`/bills/${bill.id}?info=already-posted`);
  }

  const apAccount = await prisma.account.findFirst({
    where: { userId: user.id, accountNumber: "2000", isActive: true }
  });
  if (!apAccount) {
    redirect(`/bills/${bill.id}?error=missing-ap-account`);
  }

  const subtotal = round2(
    bill.lines.reduce((sum: number, line: (typeof bill.lines)[number]) => sum + line.amount, 0)
  );
  const totalTax = round2(
    bill.lines.reduce(
      (sum: number, line: (typeof bill.lines)[number]) => sum + line.taxAmount,
      0
    )
  );
  const total = round2(subtotal + totalTax);
  const recoverableMissing = bill.lines.some(
    (line: (typeof bill.lines)[number]) =>
      line.taxAmount > 0 && line.isTaxRecoverable && !line.taxCode?.recoverableAccountId
  );
  if (recoverableMissing) {
    redirect(`/bills/${bill.id}?error=missing-recoverable-tax-account`);
  }

  await prisma.$transaction(async (tx: any) => {
    const entry = await tx.journalEntry.create({
      data: {
        userId: user.id,
        entryNumber: `BILL-${bill.billNumber}`,
        entryDate: bill.billDate,
        memo: `Bill ${bill.billNumber}`,
        status: "posted",
        postedAt: new Date()
      }
    });

    let lineNo = 1;
    for (const line of bill.lines) {
      const expenseDebit = line.isTaxRecoverable ? line.amount : line.amount + line.taxAmount;
      await tx.journalEntryLine.create({
        data: {
          journalEntryId: entry.id,
          lineNumber: lineNo++,
          accountId: line.expenseAccountId,
          debitAmount: round2(expenseDebit),
          creditAmount: 0,
          details: `Bill expense: ${line.description}`
        }
      });

      if (line.taxAmount > 0 && line.isTaxRecoverable) {
        const recoverableAccountId = line.taxCode?.recoverableAccountId;
        await tx.journalEntryLine.create({
          data: {
            journalEntryId: entry.id,
            lineNumber: lineNo++,
            accountId: recoverableAccountId,
            debitAmount: round2(line.taxAmount),
            creditAmount: 0,
            details: "Recoverable purchase tax"
          }
        });
      }
    }

    await tx.journalEntryLine.create({
      data: {
        journalEntryId: entry.id,
        lineNumber: lineNo,
        accountId: apAccount.id,
        debitAmount: 0,
        creditAmount: total,
        details: "Accounts Payable"
      }
    });

    await tx.bill.update({
      where: { id: bill.id },
      data: {
        status: "posted",
        postedAt: new Date(),
        subtotal,
        taxAmount: totalTax,
        totalAmount: total
      }
    });
  });

  redirect(`/bills/${bill.id}?posted=1`);
}
