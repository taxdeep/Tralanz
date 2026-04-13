import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import InvoiceEditor from "@/components/InvoiceEditor";
import { Notice, PageHeader } from "@/components/workbench";

type NewInvoicePageProps = {
  searchParams: Promise<{ error?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-required-fields": "Please fill all required fields.",
  "create-at-least-one-line": "Add at least one invoice line before saving.",
  "line-incomplete": "Every used line must include a description, quantity, and rate.",
  "invalid-quantity-price": "Quantity must be > 0 and unit price must be >= 0.",
  "invalid-customer": "Please choose a valid active customer.",
  "invoice-number-exists": "Invoice number already exists.",
  "invalid-tax-code": "Please choose a valid tax code.",
  "due-date-before-invoice-date": "Due date cannot be earlier than the invoice date."
};

export default async function NewInvoicePage({ searchParams }: NewInvoicePageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;

  const customers = await prisma.customer.findMany({
    where: { userId: user.id, isActive: true },
    orderBy: { displayName: "asc" }
  });
  const taxCodes = await prisma.taxCode.findMany({
    where: {
      userId: user.id,
      isActive: true,
      OR: [{ appliesTo: "sale" }, { appliesTo: "both" }]
    },
    orderBy: { code: "asc" }
  });

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Sales and receivables"
        title="Invoice entry workspace"
        description="发票页也从 MVP 单行表单改成了真正的业务单据结构：多行项目、逐行税码、实时汇总，更适合中型财务系统的录入习惯。"
      />
      {error ? (
        <Notice variant="danger">
          {error}
        </Notice>
      ) : null}
      {customers.length === 0 ? (
        <Notice variant="warning">
          Create at least one customer before creating invoices.
        </Notice>
      ) : (
        <InvoiceEditor
          customers={customers.map((customer: (typeof customers)[number]) => ({ id: customer.id, label: customer.displayName }))}
          taxCodes={taxCodes.map((taxCode: (typeof taxCodes)[number]) => ({
            id: taxCode.id,
            label: `${taxCode.code} (${taxCode.ratePercent}%)`,
            ratePercent: taxCode.ratePercent
          }))}
        />
      )}
    </div>
  );
}
