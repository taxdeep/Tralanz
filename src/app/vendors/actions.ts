"use server";

import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthAndSetup } from "@/lib/guards";

export async function createVendorAction(formData: FormData) {
  const user = await requireAuthAndSetup();

  const displayName = String(formData.get("displayName") ?? "").trim();
  const email = String(formData.get("email") ?? "").trim();
  const phone = String(formData.get("phone") ?? "").trim();
  const address = String(formData.get("address") ?? "").trim();

  if (!displayName) {
    redirect("/vendors?error=missing-display-name");
  }

  await prisma.vendor.create({
    data: {
      userId: user.id,
      displayName,
      email: email || null,
      phone: phone || null,
      address: address || null
    }
  });

  redirect("/vendors?created=1");
}
