"use server";

import { redirect } from "next/navigation";
import { prisma } from "@/lib/prisma";
import { requireAuthWithoutSetup } from "@/lib/guards";
import { generateDefaultChartOfAccounts } from "@/server/chartOfAccounts";
import { generateDefaultTaxCodes } from "@/server/taxCodes";

const ALLOWED_COMPANY_TYPES = [
  "corporation",
  "llp-partnership",
  "sole-proprietorship-individual"
] as const;

const ALLOWED_TAX_REGIMES = [
  "gst-hst",
  "gst-pst",
  "hst",
  "quebec-gst-qst"
] as const;

export async function saveSetupAction(formData: FormData) {
  const user = await requireAuthWithoutSetup();

  const legalName = String(formData.get("legalName") ?? "").trim();
  const companyType = String(formData.get("companyType") ?? "").trim();
  const businessNumber = String(formData.get("businessNumber") ?? "").trim();
  const address = String(formData.get("address") ?? "").trim();
  const baseCurrency = String(formData.get("baseCurrency") ?? "CAD").trim();
  const fiscalYearEndMonth = Number(formData.get("fiscalYearEndMonth"));
  const fiscalYearEndDay = Number(formData.get("fiscalYearEndDay"));
  const gstHstRegisteredRaw = String(formData.get("gstHstRegistered") ?? "").trim();
  const taxRegistrationNumber = String(
    formData.get("taxRegistrationNumber") ?? ""
  ).trim();
  const taxRegime = String(formData.get("taxRegime") ?? "").trim();

  if (!legalName || !companyType || !businessNumber || !address || !taxRegime) {
    redirect("/setup-wizard?error=missing-required-fields");
  }

  if (!ALLOWED_COMPANY_TYPES.includes(companyType as (typeof ALLOWED_COMPANY_TYPES)[number])) {
    redirect("/setup-wizard?error=invalid-company-type");
  }

  if (!ALLOWED_TAX_REGIMES.includes(taxRegime as (typeof ALLOWED_TAX_REGIMES)[number])) {
    redirect("/setup-wizard?error=invalid-tax-regime");
  }

  if (
    !Number.isInteger(fiscalYearEndMonth) ||
    fiscalYearEndMonth < 1 ||
    fiscalYearEndMonth > 12
  ) {
    redirect("/setup-wizard?error=invalid-fiscal-month");
  }

  if (!Number.isInteger(fiscalYearEndDay) || fiscalYearEndDay < 1 || fiscalYearEndDay > 31) {
    redirect("/setup-wizard?error=invalid-fiscal-day");
  }

  const gstHstRegistered = gstHstRegisteredRaw === "yes";
  if (gstHstRegistered && !taxRegistrationNumber) {
    redirect("/setup-wizard?error=missing-tax-registration-number");
  }

  await prisma.companySetup.upsert({
    where: { userId: user.id },
    update: {
      legalName,
      companyType,
      businessNumber,
      address,
      baseCurrency: baseCurrency || "CAD",
      fiscalYearEndMonth,
      fiscalYearEndDay,
      gstHstRegistered,
      taxRegistrationNumber: gstHstRegistered ? taxRegistrationNumber : null,
      taxRegime
    },
    create: {
      userId: user.id,
      legalName,
      companyType,
      businessNumber,
      address,
      baseCurrency: baseCurrency || "CAD",
      fiscalYearEndMonth,
      fiscalYearEndDay,
      gstHstRegistered,
      taxRegistrationNumber: gstHstRegistered ? taxRegistrationNumber : null,
      taxRegime
    }
  });

  await generateDefaultChartOfAccounts({
    userId: user.id,
    companyType: companyType as (typeof ALLOWED_COMPANY_TYPES)[number],
    gstHstRegistered,
    taxRegime: taxRegime as (typeof ALLOWED_TAX_REGIMES)[number]
  });
  await generateDefaultTaxCodes({
    userId: user.id,
    taxRegime: taxRegime as (typeof ALLOWED_TAX_REGIMES)[number],
    gstHstRegistered
  });

  redirect("/dashboard");
}
