import { redirect } from "next/navigation";
import { hasCompanySetup, requireAuth } from "@/lib/auth";

export async function requireAuthAndSetup() {
  const user = await requireAuth();
  const setupExists = await hasCompanySetup(user.id);
  if (!setupExists) {
    redirect("/setup-wizard");
  }
  return user;
}

export async function requireAuthWithoutSetup() {
  const user = await requireAuth();
  const setupExists = await hasCompanySetup(user.id);
  if (setupExists) {
    redirect("/dashboard");
  }
  return user;
}
