import { prisma } from "@/lib/prisma";

type ReportQuery = {
  from?: string;
  to?: string;
};

type ReportRange = {
  from: Date;
  to: Date;
  fromInput: string;
  toInput: string;
};

type AccountBalance = {
  accountId: string;
  accountNumber: string;
  accountName: string;
  accountType: string;
  reportCategory: string;
  debits: number;
  credits: number;
  net: number;
};

function startOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 0, 0, 0, 0);
}

function endOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 23, 59, 59, 999);
}

function parseDateInput(value?: string): Date | null {
  if (!value) return null;
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function toDateInput(value: Date): string {
  return value.toISOString().slice(0, 10);
}

export function getReportRange(query: ReportQuery): ReportRange {
  const now = new Date();
  const defaultFrom = new Date(now.getFullYear(), 0, 1);
  const defaultTo = endOfDay(now);

  const fromParsed = parseDateInput(query.from);
  const toParsed = parseDateInput(query.to);

  if (fromParsed && toParsed && fromParsed <= toParsed) {
    const from = startOfDay(fromParsed);
    const to = endOfDay(toParsed);
    return {
      from,
      to,
      fromInput: toDateInput(from),
      toInput: toDateInput(to)
    };
  }

  return {
    from: defaultFrom,
    to: defaultTo,
    fromInput: toDateInput(defaultFrom),
    toInput: toDateInput(defaultTo)
  };
}

export async function getAccountBalancesForPeriod(userId: string, range: ReportRange) {
  const lines = await prisma.journalEntryLine.findMany({
    where: {
      journalEntry: {
        userId,
        status: "posted",
        entryDate: {
          gte: range.from,
          lte: range.to
        }
      }
    },
    include: {
      account: true
    }
  });

  const byAccount = new Map<string, AccountBalance>();

  for (const line of lines) {
    const existing = byAccount.get(line.accountId);
    if (!existing) {
      byAccount.set(line.accountId, {
        accountId: line.accountId,
        accountNumber: line.account.accountNumber,
        accountName: line.account.accountName,
        accountType: line.account.accountType,
        reportCategory: line.account.reportCategory,
        debits: line.debitAmount,
        credits: line.creditAmount,
        net: line.debitAmount - line.creditAmount
      });
      continue;
    }

    existing.debits += line.debitAmount;
    existing.credits += line.creditAmount;
    existing.net += line.debitAmount - line.creditAmount;
  }

  return [...byAccount.values()].sort((a, b) =>
    a.accountNumber.localeCompare(b.accountNumber)
  );
}

function round2(value: number) {
  return Math.round(value * 100) / 100;
}

export function toTrialBalanceRows(balances: AccountBalance[]) {
  const rows = balances.map((item) => {
    const debitNormal = item.accountType === "asset" || item.accountType === "expense";
    const value = debitNormal ? item.debits - item.credits : item.credits - item.debits;
    const debit = value > 0 && debitNormal ? value : 0;
    const credit = value > 0 && !debitNormal ? value : 0;

    return {
      ...item,
      debit: round2(debit),
      credit: round2(credit)
    };
  });

  const totalDebit = round2(rows.reduce((sum, row) => sum + row.debit, 0));
  const totalCredit = round2(rows.reduce((sum, row) => sum + row.credit, 0));

  return { rows, totalDebit, totalCredit };
}

export function toIncomeStatement(balances: AccountBalance[]) {
  const revenueCategories = ["operating_revenue", "other_income"];
  const expenseCategories = ["operating_expense", "cost_of_sales", "other_expense"];

  const revenueRows = balances
    .filter((item) => revenueCategories.includes(item.reportCategory))
    .map((item) => ({
      ...item,
      amount: round2(item.credits - item.debits)
    }));

  const expenseRows = balances
    .filter((item) => expenseCategories.includes(item.reportCategory))
    .map((item) => ({
      ...item,
      amount: round2(item.debits - item.credits)
    }));

  const totalRevenue = round2(revenueRows.reduce((sum, row) => sum + row.amount, 0));
  const totalExpenses = round2(expenseRows.reduce((sum, row) => sum + row.amount, 0));
  const netIncome = round2(totalRevenue - totalExpenses);

  return { revenueRows, expenseRows, totalRevenue, totalExpenses, netIncome };
}

export function toBalanceSheet(balances: AccountBalance[]) {
  const assetCategories = ["current_asset", "non_current_asset"];
  const liabilityCategories = ["current_liability", "non_current_liability"];
  const equityCategories = ["equity"];

  const assetRows = balances
    .filter((item) => assetCategories.includes(item.reportCategory))
    .map((item) => ({
      ...item,
      amount: round2(item.debits - item.credits)
    }));

  const liabilityRows = balances
    .filter((item) => liabilityCategories.includes(item.reportCategory))
    .map((item) => ({
      ...item,
      amount: round2(item.credits - item.debits)
    }));

  const equityRows = balances
    .filter((item) => equityCategories.includes(item.reportCategory))
    .map((item) => ({
      ...item,
      amount: round2(item.credits - item.debits)
    }));

  const incomeStatement = toIncomeStatement(balances);
  const currentEarnings = incomeStatement.netIncome;

  const totalAssets = round2(assetRows.reduce((sum, row) => sum + row.amount, 0));
  const totalLiabilities = round2(liabilityRows.reduce((sum, row) => sum + row.amount, 0));
  const totalEquityBeforeEarnings = round2(
    equityRows.reduce((sum, row) => sum + row.amount, 0)
  );
  const totalEquity = round2(totalEquityBeforeEarnings + currentEarnings);
  const totalLiabilitiesAndEquity = round2(totalLiabilities + totalEquity);

  return {
    assetRows,
    liabilityRows,
    equityRows,
    currentEarnings,
    totalAssets,
    totalLiabilities,
    totalEquity,
    totalLiabilitiesAndEquity
  };
}
