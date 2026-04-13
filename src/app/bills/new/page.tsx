import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import BillEditor from "@/components/BillEditor";
import { Notice, PageHeader } from "@/components/workbench";

type NewBillPageProps = {
  searchParams: Promise<{ error?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-required-fields": "Please fill all required fields.",
  "create-at-least-one-line": "Add at least one bill line before saving.",
  "line-incomplete": "Every used line must include an account, description, and amount.",
  "invalid-line-amount": "Each used line must have an amount greater than 0.",
  "invalid-vendor": "Please choose a valid active vendor.",
  "invalid-expense-account": "Please choose a valid active expense/asset account.",
  "bill-number-exists": "Bill number already exists.",
  "invalid-tax-code": "Please choose a valid tax code.",
  "due-date-before-bill-date": "Due date cannot be earlier than the bill date."
};

export default async function NewBillPage({ searchParams }: NewBillPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;

  const vendors = await prisma.vendor.findMany({
    where: { userId: user.id, isActive: true },
    orderBy: { displayName: "asc" }
  });
  const expenseAccounts = await prisma.account.findMany({
    where: { userId: user.id, isActive: true, accountType: { in: ["expense", "asset"] } },
    orderBy: { accountNumber: "asc" }
  });
  const taxCodes = await prisma.taxCode.findMany({
    where: {
      userId: user.id,
      isActive: true,
      OR: [{ appliesTo: "purchase" }, { appliesTo: "both" }]
    },
    orderBy: { code: "asc" }
  });

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Purchases and payables"
        title="Bill entry workspace"
        description="这里把原来单行式的 Bill 表单升级成了更符合财务逻辑的多行账单录入：同一张账单可以拆分到多个费用或资产科目，并支持逐行税码。"
      />
      {error ? (
        <Notice variant="danger">
          {error}
        </Notice>
      ) : null}

      {vendors.length === 0 ? (
        <Notice variant="warning">
          Create at least one vendor before creating bills.
        </Notice>
      ) : (
        <BillEditor
          vendors={vendors.map((vendor: (typeof vendors)[number]) => ({ id: vendor.id, label: vendor.displayName }))}
          expenseAccounts={expenseAccounts.map((account: (typeof expenseAccounts)[number]) => ({
            id: account.id,
            label: `${account.accountNumber} - ${account.accountName}`
          }))}
          taxCodes={taxCodes.map((taxCode: (typeof taxCodes)[number]) => ({
            id: taxCode.id,
            label: `${taxCode.code} (${taxCode.ratePercent}%)${taxCode.isRecoverableOnPurchase ? " recoverable" : ""}`,
            ratePercent: taxCode.ratePercent
          }))}
        />
      )}
    </div>
  );
}
