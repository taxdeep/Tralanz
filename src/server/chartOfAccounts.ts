import { prisma } from "@/lib/prisma";

type CompanyType =
  | "corporation"
  | "llp-partnership"
  | "sole-proprietorship-individual";

type TaxRegime = "gst-hst" | "gst-pst" | "hst" | "quebec-gst-qst";

type AccountTemplate = {
  accountNumber: string;
  accountName: string;
  accountType: "asset" | "liability" | "equity" | "revenue" | "expense";
  reportCategory:
    | "current_asset"
    | "current_liability"
    | "equity"
    | "operating_revenue"
    | "operating_expense";
};

function getBaseAccounts(): AccountTemplate[] {
  return [
    {
      accountNumber: "1000",
      accountName: "Bank",
      accountType: "asset",
      reportCategory: "current_asset"
    },
    {
      accountNumber: "1100",
      accountName: "Accounts Receivable",
      accountType: "asset",
      reportCategory: "current_asset"
    },
    {
      accountNumber: "1200",
      accountName: "Sales Tax Recoverable",
      accountType: "asset",
      reportCategory: "current_asset"
    },
    {
      accountNumber: "2000",
      accountName: "Accounts Payable",
      accountType: "liability",
      reportCategory: "current_liability"
    },
    {
      accountNumber: "2100",
      accountName: "Sales Tax Payable",
      accountType: "liability",
      reportCategory: "current_liability"
    },
    {
      accountNumber: "4000",
      accountName: "Sales Revenue",
      accountType: "revenue",
      reportCategory: "operating_revenue"
    },
    {
      accountNumber: "5000",
      accountName: "Cost of Goods Sold",
      accountType: "expense",
      reportCategory: "operating_expense"
    },
    {
      accountNumber: "6100",
      accountName: "Office Expense",
      accountType: "expense",
      reportCategory: "operating_expense"
    }
  ];
}

function getCompanyTypeAccounts(companyType: CompanyType): AccountTemplate[] {
  if (companyType === "corporation") {
    return [
      {
        accountNumber: "3000",
        accountName: "Share Capital",
        accountType: "equity",
        reportCategory: "equity"
      },
      {
        accountNumber: "3100",
        accountName: "Retained Earnings",
        accountType: "equity",
        reportCategory: "equity"
      }
    ];
  }

  if (companyType === "llp-partnership") {
    return [
      {
        accountNumber: "3000",
        accountName: "Partner Capital",
        accountType: "equity",
        reportCategory: "equity"
      },
      {
        accountNumber: "3200",
        accountName: "Partner Drawings",
        accountType: "equity",
        reportCategory: "equity"
      }
    ];
  }

  return [
    {
      accountNumber: "3000",
      accountName: "Owner Equity",
      accountType: "equity",
      reportCategory: "equity"
    },
    {
      accountNumber: "3200",
      accountName: "Owner Drawings",
      accountType: "equity",
      reportCategory: "equity"
    }
  ];
}

function getTaxAccounts(gstRegistered: boolean, taxRegime: TaxRegime): AccountTemplate[] {
  if (!gstRegistered) {
    return [];
  }

  if (taxRegime === "gst-pst" || taxRegime === "quebec-gst-qst") {
    return [
      {
        accountNumber: "2110",
        accountName: "Provincial Sales Tax Payable",
        accountType: "liability",
        reportCategory: "current_liability"
      }
    ];
  }

  return [];
}

export async function generateDefaultChartOfAccounts(input: {
  userId: string;
  companyType: CompanyType;
  gstHstRegistered: boolean;
  taxRegime: TaxRegime;
}) {
  const existingCount = await prisma.account.count({
    where: { userId: input.userId }
  });

  // Duplicate prevention: only generate for first-time setup.
  if (existingCount > 0) {
    return { generated: false, reason: "accounts-already-exist" as const };
  }

  const templates = [
    ...getBaseAccounts(),
    ...getCompanyTypeAccounts(input.companyType),
    ...getTaxAccounts(input.gstHstRegistered, input.taxRegime)
  ];

  await prisma.$transaction(
    templates.map((item) =>
      prisma.account.upsert({
        where: {
          userId_accountNumber: {
            userId: input.userId,
            accountNumber: item.accountNumber
          }
        },
        create: {
          userId: input.userId,
          accountNumber: item.accountNumber,
          accountName: item.accountName,
          accountType: item.accountType,
          reportCategory: item.reportCategory,
          isActive: true,
          isSystem: true
        },
        update: {}
      })
    )
  );

  return { generated: true, count: templates.length };
}
