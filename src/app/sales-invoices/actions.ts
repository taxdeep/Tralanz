"use server";
import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthAndSetup } from "@/lib/guards";

const MAX_INVOICE_LINES = 10;

type InvoiceDraftLine = {
  description: string;
  quantity: string;
  unitPrice: string;
  taxCodeId: string;
};

function round2(value: number) {
  return Math.round(value * 100) / 100;
}

export async function createInvoiceAction(formData: FormData) {
  const user = await requireAuthAndSetup();

  const customerId = String(formData.get("customerId") ?? "").trim();
  const invoiceNumber = String(formData.get("invoiceNumber") ?? "").trim();
  const invoiceDate = String(formData.get("invoiceDate") ?? "").trim();
  const dueDate = String(formData.get("dueDate") ?? "").trim();
  const memo = String(formData.get("memo") ?? "").trim();

  if (!customerId || !invoiceNumber || !invoiceDate || !dueDate) {
    redirect("/sales-invoices/new?error=missing-required-fields");
  }

  const normalizedLines = Array.from({ length: MAX_INVOICE_LINES }, (_, index): InvoiceDraftLine => ({
    description: String(formData.get(`line-${index}-description`) ?? "").trim(),
    quantity: String(formData.get(`line-${index}-quantity`) ?? "").trim(),
    unitPrice: String(formData.get(`line-${index}-unitPrice`) ?? "").trim(),
    taxCodeId: String(formData.get(`line-${index}-taxCodeId`) ?? "").trim()
  })).filter((line: InvoiceDraftLine) => line.description || line.quantity || line.unitPrice || line.taxCodeId);

  if (normalizedLines.length === 0) {
    redirect("/sales-invoices/new?error=create-at-least-one-line");
  }

  const parsedInvoiceDate = new Date(`${invoiceDate}T00:00:00`);
  const parsedDueDate = new Date(`${dueDate}T00:00:00`);
  if (Number.isNaN(parsedInvoiceDate.getTime()) || Number.isNaN(parsedDueDate.getTime())) {
    redirect("/sales-invoices/new?error=missing-required-fields");
  }
  if (parsedDueDate < parsedInvoiceDate) {
    redirect("/sales-invoices/new?error=due-date-before-invoice-date");
  }

  const customer = await prisma.customer.findFirst({
    where: { id: customerId, userId: user.id, isActive: true },
    select: { id: true }
  });
  if (!customer) {
    redirect("/sales-invoices/new?error=invalid-customer");
  }

  const existing = await prisma.invoice.findFirst({
    where: { userId: user.id, invoiceNumber },
    select: { id: true }
  });
  if (existing) {
    redirect("/sales-invoices/new?error=invoice-number-exists");
  }

  const taxCodeIds = [...new Set(normalizedLines.map((line: (typeof normalizedLines)[number]) => line.taxCodeId).filter(Boolean))];
  const taxCodes = taxCodeIds.length
    ? await prisma.taxCode.findMany({
        where: {
          userId: user.id,
          isActive: true,
          id: { in: taxCodeIds },
          OR: [{ appliesTo: "sale" }, { appliesTo: "both" }]
        },
        select: {
          id: true,
          ratePercent: true
        }
      })
    : [];
  const taxCodeMap = new Map<string, (typeof taxCodes)[number]>(
    taxCodes.map((taxCode: (typeof taxCodes)[number]) => [taxCode.id, taxCode])
  );

  const preparedLines = normalizedLines.map((line: (typeof normalizedLines)[number]) => {
    if (!line.description || !line.quantity || !line.unitPrice) {
      redirect("/sales-invoices/new?error=line-incomplete");
    }

    const quantity = Number(line.quantity);
    const unitPrice = Number(line.unitPrice);
    if (!Number.isFinite(quantity) || quantity <= 0 || !Number.isFinite(unitPrice) || unitPrice < 0) {
      redirect("/sales-invoices/new?error=invalid-quantity-price");
    }

    const taxCode = line.taxCodeId ? taxCodeMap.get(line.taxCodeId) : null;
    if (line.taxCodeId && !taxCode) {
      redirect("/sales-invoices/new?error=invalid-tax-code");
    }

    const lineAmount = round2(quantity * unitPrice);
    const taxAmount = round2(lineAmount * ((taxCode?.ratePercent ?? 0) / 100));

    return {
      description: line.description,
      quantity,
      unitPrice,
      lineAmount,
      taxAmount,
      taxCodeId: taxCode?.id ?? null
    };
  });

  const subtotal = round2(
    preparedLines.reduce(
      (sum: number, line: (typeof preparedLines)[number]) => sum + line.lineAmount,
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

  const invoice = await prisma.invoice.create({
    data: {
      userId: user.id,
      customerId,
      invoiceNumber,
      invoiceDate: parsedInvoiceDate,
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

  redirect(`/sales-invoices/${invoice.id}`);
}

export async function postInvoiceAction(formData: FormData) {
  const user = await requireAuthAndSetup();
  const invoiceId = String(formData.get("invoiceId") ?? "").trim();
  if (!invoiceId) {
    redirect("/sales-invoices?error=missing-invoice-id");
  }

  const invoice = await prisma.invoice.findFirst({
    where: { id: invoiceId, userId: user.id },
    include: {
      lines: { include: { taxCode: true } }
    }
  });
  if (!invoice) {
    redirect("/sales-invoices?error=invoice-not-found");
  }
  if (invoice.status === "posted") {
    redirect(`/sales-invoices/${invoice.id}?info=already-posted`);
  }

  const arAccount = await prisma.account.findFirst({
    where: { userId: user.id, accountNumber: "1100", isActive: true }
  });
  const salesAccount = await prisma.account.findFirst({
    where: { userId: user.id, accountNumber: "4000", isActive: true }
  });
  const taxPayableAccount = await prisma.account.findFirst({
    where: { userId: user.id, accountNumber: "2100", isActive: true }
  });

  if (!arAccount || !salesAccount) {
    redirect(`/sales-invoices/${invoice.id}?error=missing-required-accounts`);
  }

  const subtotal = round2(
    invoice.lines.reduce(
      (sum: number, line: (typeof invoice.lines)[number]) => sum + line.lineAmount,
      0
    )
  );
  const taxAmount = round2(
    invoice.lines.reduce(
      (sum: number, line: (typeof invoice.lines)[number]) => sum + line.taxAmount,
      0
    )
  );
  const total = round2(subtotal + taxAmount);
  if (taxAmount > 0 && !taxPayableAccount) {
    redirect(`/sales-invoices/${invoice.id}?error=missing-required-accounts`);
  }
  const taxPayableAccountId = taxPayableAccount?.id;

  await prisma.$transaction(async (tx: any) => {
    const entry = await tx.journalEntry.create({
      data: {
        userId: user.id,
        entryNumber: `INV-${invoice.invoiceNumber}`,
        entryDate: invoice.invoiceDate,
        memo: `Invoice ${invoice.invoiceNumber}`,
        status: "posted",
        postedAt: new Date()
      }
    });

    await tx.journalEntryLine.create({
      data: {
        journalEntryId: entry.id,
        lineNumber: 1,
        accountId: arAccount.id,
        debitAmount: total,
        creditAmount: 0,
        details: "Accounts Receivable"
      }
    });

    await tx.journalEntryLine.create({
      data: {
        journalEntryId: entry.id,
        lineNumber: 2,
        accountId: salesAccount.id,
        debitAmount: 0,
        creditAmount: subtotal,
        details: "Sales Revenue"
      }
    });

    if (taxAmount > 0) {
      await tx.journalEntryLine.create({
        data: {
          journalEntryId: entry.id,
          lineNumber: 3,
          accountId: taxPayableAccountId!,
          debitAmount: 0,
          creditAmount: taxAmount,
          details: "Sales Tax Payable"
        }
      });
    }

    await tx.invoice.update({
      where: { id: invoice.id },
      data: {
        status: "posted",
        postedAt: new Date(),
        subtotal,
        taxAmount,
        totalAmount: total
      }
    });
  });

  redirect(`/sales-invoices/${invoice.id}?posted=1`);
}
