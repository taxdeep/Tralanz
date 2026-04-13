import { redirect } from "next/navigation";
import { getAuthUser, hasCompanySetup } from "@/lib/auth";

export default async function HomePage() {
  const user = await getAuthUser();
  if (!user) {
    redirect("/login");
  }

  const setupExists = await hasCompanySetup(user.id);
  redirect(setupExists ? "/dashboard" : "/setup-wizard");
}
