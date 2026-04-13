"use server";

import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { createSession, hasCompanySetup } from "@/lib/auth";

export async function loginAction(formData: FormData) {
  const emailOrUsername = String(formData.get("emailOrUsername") ?? "").trim();
  const password = String(formData.get("password") ?? "");

  if (!emailOrUsername || !password) {
    redirect("/login?error=missing-fields");
  }

  const user = await prisma.user.findFirst({
    where: {
      OR: [{ email: emailOrUsername }, { username: emailOrUsername }]
    }
  });

  // MVP-simple auth: compare password directly with stored value.
  // In production, replace with secure password hashing.
  if (!user || !user.isActive || user.passwordHash !== password) {
    redirect("/login?error=invalid-credentials");
  }

  await createSession(user.id);
  const setupExists = await hasCompanySetup(user.id);

  if (setupExists) {
    redirect("/dashboard");
  }

  redirect("/setup-wizard");
}

export async function logoutAction() {
  const { clearSession } = await import("@/lib/auth");
  await clearSession();
  redirect("/login");
}
