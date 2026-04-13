import { prisma } from "@/lib/prisma";

type TaxRegime = "gst-hst" | "gst-pst" | "hst" | "quebec-gst-qst";

export async function generateDefaultTaxCodes(input: {
  userId: string;
  taxRegime: TaxRegime;
  gstHstRegistered: boolean;
}) {
  const existing = await prisma.taxCode.count({
    where: { userId: input.userId }
  });
  if (existing > 0) {
    return { generated: false, reason: "tax-codes-already-exist" as const };
  }

  const taxPayable = await prisma.account.findFirst({
    where: { userId: input.userId, accountNumber: "2100" },
    select: { id: true }
  });
  const taxRecoverable = await prisma.account.findFirst({
    where: { userId: input.userId, accountNumber: "1200" },
    select: { id: true }
  });

  const codes: Array<{
    code: string;
    name: string;
    ratePercent: number;
    appliesTo: string;
    isRecoverableOnPurchase: boolean;
  }> = [
    { code: "EXEMPT", name: "Exempt / Zero Tax", ratePercent: 0, appliesTo: "both", isRecoverableOnPurchase: false }
  ];

  if (input.gstHstRegistered) {
    if (input.taxRegime === "hst") {
      codes.push({ code: "HST13", name: "HST 13%", ratePercent: 13, appliesTo: "both", isRecoverableOnPurchase: true });
      codes.push({ code: "HST13-NR", name: "HST 13% Non-Recoverable", ratePercent: 13, appliesTo: "purchase", isRecoverableOnPurchase: false });
    } else {
      codes.push({ code: "GST5", name: "GST 5%", ratePercent: 5, appliesTo: "both", isRecoverableOnPurchase: true });
      codes.push({ code: "GST5-NR", name: "GST 5% Non-Recoverable", ratePercent: 5, appliesTo: "purchase", isRecoverableOnPurchase: false });
    }
  }

  await prisma.$transaction(
    codes.map((code) =>
      prisma.taxCode.create({
        data: {
          userId: input.userId,
          code: code.code,
          name: code.name,
          ratePercent: code.ratePercent,
          appliesTo: code.appliesTo,
          isRecoverableOnPurchase: code.isRecoverableOnPurchase,
          payableAccountId: taxPayable?.id ?? null,
          recoverableAccountId: taxRecoverable?.id ?? null
        }
      })
    )
  );

  return { generated: true, count: codes.length };
}
