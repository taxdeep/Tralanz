import { prisma } from "@/lib/prisma";

type DashboardWindowMonths = 12 | 24;

export type DashboardQuery = {
  window?: string;
};

type DashboardTrendPoint = {
  key: string;
  monthLabel: string;
  yearLabel: string;
  cashIn: number;
  cashOut: number;
  netChange: number;
  income: number;
  expense: number;
};

type DashboardConnectedAccount = {
  name: string;
  accountNumber: string;
  balance: number;
  lastActivity: Date | null;
  status: string;
};

type DashboardOverdueItem = {
  id: string;
  href: string;
  name: string;
  amount: number;
  dueDate: Date;
  daysOverdue: number;
};

export type DashboardData = {
  period: {
    months: DashboardWindowMonths;
    from: string;
    to: string;
  };
  company: {
    legalName: string;
    baseCurrency: string;
  };
  highlights: {
    bankBalance: number;
    receivables: number;
    payables: number;
    netCashChange: number;
  };
  connectedAccounts: DashboardConnectedAccount[];
  trends: DashboardTrendPoint[];
  overdue: {
    invoices: DashboardOverdueItem[];
    bills: DashboardOverdueItem[];
    invoiceTotal: number;
    billTotal: number;
  };
  payableAndOwing: {
    invoicesComingDue: number;
    invoicesOverdue: number;
    billsComingDue: number;
    billsOverdue: number;
  };
  stats: {
    activeCustomers: number;
    activeVendors: number;
    postedInvoices: number;
    postedBills: number;
  };
};

function formatDateInputValue(value: Date): string {
  return value.toISOString().slice(0, 10);
}

function startOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 0, 0, 0, 0);
}

function endOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 23, 59, 59, 999);
}

function addDays(value: Date, days: number): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate() + days);
}

function sum(values: number[]): number {
  return values.reduce((total, current) => total + current, 0);
}

function getMonthKey(value: Date): string {
  return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}`;
}

function getWindowMonths(window?: string): DashboardWindowMonths {
  return window === "24" ? 24 : 12;
}

function buildTrendPoints(months: DashboardWindowMonths, now: Date): DashboardTrendPoint[] {
  const points: DashboardTrendPoint[] = [];
  const monthFormatter = new Intl.DateTimeFormat("en-CA", { month: "short" });
  const yearFormatter = new Intl.DateTimeFormat("en-CA", { year: "2-digit" });

  for (let index = months - 1; index >= 0; index -= 1) {
    const monthStart = new Date(now.getFullYear(), now.getMonth() - index, 1);
    points.push({
      key: getMonthKey(monthStart),
      monthLabel: monthFormatter.format(monthStart),
      yearLabel: yearFormatter.format(monthStart),
      cashIn: 0,
      cashOut: 0,
      netChange: 0,
      income: 0,
      expense: 0
    });
  }

  return points;
}

function calculateDaysOverdue(from: Date, to: Date): number {
  const difference = startOfDay(to).getTime() - startOfDay(from).getTime();
  return Math.max(0, Math.floor(difference / 86_400_000));
}

export async function getDashboardData(userId: string, query: DashboardQuery): Promise<DashboardData> {
  const now = new Date();
  const today = startOfDay(now);
  const rollingMonths = getWindowMonths(query.window);
  const trends = buildTrendPoints(rollingMonths, today);
  const firstTrendMonth = new Date(today.getFullYear(), today.getMonth() - (rollingMonths - 1), 1);
  const monthMap = new Map(trends.map((point) => [point.key, point]));
  const comingDueCutoff = endOfDay(addDays(today, 30));

  const [
    companySetup,
    operatingBankAccount,
    activeCustomers,
    activeVendors,
    postedInvoices,
    postedBills,
    bankLines
  ] = await Promise.all([
    prisma.companySetup.findUnique({
      where: { userId },
      select: {
        legalName: true,
        baseCurrency: true
      }
    }),
    prisma.account.findFirst({
      where: {
        userId,
        accountNumber: "1000"
      },
      select: {
        accountName: true,
        accountNumber: true
      }
    }),
    prisma.customer.count({
      where: {
        userId,
        isActive: true
      }
    }),
    prisma.vendor.count({
      where: {
        userId,
        isActive: true
      }
    }),
    prisma.invoice.findMany({
      where: {
        userId,
        status: "posted",
        invoiceDate: {
          lte: endOfDay(today)
        }
      },
      orderBy: [{ invoiceDate: "desc" }, { createdAt: "desc" }],
      select: {
        id: true,
        invoiceNumber: true,
        invoiceDate: true,
        dueDate: true,
        subtotal: true,
        totalAmount: true,
        customer: {
          select: {
            displayName: true
          }
        }
      }
    }),
    prisma.bill.findMany({
      where: {
        userId,
        status: "posted",
        billDate: {
          lte: endOfDay(today)
        }
      },
      orderBy: [{ billDate: "desc" }, { createdAt: "desc" }],
      select: {
        id: true,
        billNumber: true,
        billDate: true,
        dueDate: true,
        subtotal: true,
        totalAmount: true,
        vendor: {
          select: {
            displayName: true
          }
        }
      }
    }),
    prisma.journalEntryLine.findMany({
      where: {
        account: {
          userId,
          accountNumber: "1000"
        },
        journalEntry: {
          userId,
          status: "posted",
          entryDate: {
            lte: endOfDay(today)
          }
        }
      },
      select: {
        debitAmount: true,
        creditAmount: true,
        journalEntry: {
          select: {
            entryDate: true
          }
        }
      }
    })
  ]);

  for (const invoice of postedInvoices) {
    if (invoice.invoiceDate >= firstTrendMonth) {
      const bucket = monthMap.get(getMonthKey(invoice.invoiceDate));
      if (bucket) {
        bucket.income += invoice.subtotal;
      }
    }
  }

  for (const bill of postedBills) {
    if (bill.billDate >= firstTrendMonth) {
      const bucket = monthMap.get(getMonthKey(bill.billDate));
      if (bucket) {
        bucket.expense += bill.subtotal;
      }
    }
  }

  for (const bankLine of bankLines) {
    if (bankLine.journalEntry.entryDate >= firstTrendMonth) {
      const bucket = monthMap.get(getMonthKey(bankLine.journalEntry.entryDate));
      if (bucket) {
        bucket.cashIn += bankLine.debitAmount;
        bucket.cashOut += bankLine.creditAmount;
      }
    }
  }

  for (const point of trends) {
    point.netChange = point.cashIn - point.cashOut;
  }

  const bankBalance = sum(bankLines.map((line) => line.debitAmount - line.creditAmount));
  const receivables = sum(postedInvoices.map((invoice) => invoice.totalAmount));
  const payables = sum(postedBills.map((bill) => bill.totalAmount));
  const netCashChange = sum(trends.map((point) => point.netChange));

  const overdueInvoicesAll = postedInvoices
    .filter((invoice) => startOfDay(invoice.dueDate) < today)
    .map((invoice) => ({
      id: invoice.id,
      href: `/sales-invoices/${invoice.id}`,
      name: invoice.customer.displayName,
      amount: invoice.totalAmount,
      dueDate: invoice.dueDate,
      daysOverdue: calculateDaysOverdue(invoice.dueDate, today)
    }))
    .sort((left, right) => right.amount - left.amount || left.dueDate.getTime() - right.dueDate.getTime());

  const overdueBillsAll = postedBills
    .filter((bill) => startOfDay(bill.dueDate) < today)
    .map((bill) => ({
      id: bill.id,
      href: `/bills/${bill.id}`,
      name: bill.vendor.displayName,
      amount: bill.totalAmount,
      dueDate: bill.dueDate,
      daysOverdue: calculateDaysOverdue(bill.dueDate, today)
    }))
    .sort((left, right) => right.amount - left.amount || left.dueDate.getTime() - right.dueDate.getTime());

  const invoicesComingDue = sum(
    postedInvoices
      .filter((invoice) => {
        const dueDate = startOfDay(invoice.dueDate);
        return dueDate >= today && dueDate <= comingDueCutoff;
      })
      .map((invoice) => invoice.totalAmount)
  );

  const billsComingDue = sum(
    postedBills
      .filter((bill) => {
        const dueDate = startOfDay(bill.dueDate);
        return dueDate >= today && dueDate <= comingDueCutoff;
      })
      .map((bill) => bill.totalAmount)
  );

  const latestBankActivity = bankLines.reduce<Date | null>((latest, line) => {
    if (!latest || line.journalEntry.entryDate > latest) {
      return line.journalEntry.entryDate;
    }

    return latest;
  }, null);

  return {
    period: {
      months: rollingMonths,
      from: formatDateInputValue(firstTrendMonth),
      to: formatDateInputValue(today)
    },
    company: {
      legalName: companySetup?.legalName ?? "Citus Services Inc.",
      baseCurrency: companySetup?.baseCurrency ?? "CAD"
    },
    highlights: {
      bankBalance,
      receivables,
      payables,
      netCashChange
    },
    connectedAccounts: [
      {
        name: operatingBankAccount?.accountName ?? "Operating bank",
        accountNumber: operatingBankAccount?.accountNumber ?? "1000",
        balance: bankBalance,
        lastActivity: latestBankActivity,
        status:
          bankLines.length === 0 ? "Awaiting first bank posting" : "Live from posted journal activity"
      }
    ],
    trends,
    overdue: {
      invoices: overdueInvoicesAll.slice(0, 5),
      bills: overdueBillsAll.slice(0, 5),
      invoiceTotal: sum(overdueInvoicesAll.map((invoice) => invoice.amount)),
      billTotal: sum(overdueBillsAll.map((bill) => bill.amount))
    },
    payableAndOwing: {
      invoicesComingDue,
      invoicesOverdue: sum(overdueInvoicesAll.map((invoice) => invoice.amount)),
      billsComingDue,
      billsOverdue: sum(overdueBillsAll.map((bill) => bill.amount))
    },
    stats: {
      activeCustomers,
      activeVendors,
      postedInvoices: postedInvoices.length,
      postedBills: postedBills.length
    }
  };
}
