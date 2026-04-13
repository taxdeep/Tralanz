"use server";

import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthAndSetup } from "@/lib/guards";

export async function updateAccountAction(formData: FormData) {
  const user = await requireAuthAndSetup();

  const accountId = String(formData.get("accountId") ?? "");
  const accountName = String(formData.get("accountName") ?? "").trim();
  const isActive = formData.get("isActive") === "on";

  if (!accountId || !accountName) {
    redirect("/chart-of-accounts?error=missing-account-fields");
  }

  const account = await prisma.account.findFirst({
    where: {
      id: accountId,
      userId: user.id
    },
    select: { id: true }
  });

  if (!account) {
    redirect("/chart-of-accounts?error=account-not-found");
  }

  await prisma.account.update({
    where: { id: accountId },
    data: {
      accountName,
      isActive
    }
  });

  redirect("/chart-of-accounts?updated=1");
}
