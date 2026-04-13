"use client";

import Link from "next/link";
import { useState } from "react";
import { createBillAction } from "@/app/bills/actions";
import { PlusIcon } from "@/components/icons";
import { formatCurrency, formatDateInput } from "@/lib/format";

type Option = {
  id: string;
  label: string;
};

type TaxCodeOption = {
  id: string;
  label: string;
  ratePercent: number;
};

type BillEditorProps = {
  vendors: Option[];
  expenseAccounts: Option[];
  taxCodes: TaxCodeOption[];
};

type BillRow = {
  expenseAccountId: string;
  description: string;
  amount: string;
  taxCodeId: string;
};

const MAX_BILL_LINES = 10;

function createBlankRow(): BillRow {
  return {
    expenseAccountId: "",
    description: "",
    amount: "",
    taxCodeId: ""
  };
}

function getFutureDate(days: number) {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return formatDateInput(date);
}

function isRowUsed(row: BillRow) {
  return Boolean(
    row.expenseAccountId.trim() || row.description.trim() || row.amount.trim() || row.taxCodeId.trim()
  );
}

export default function BillEditor({
  vendors,
  expenseAccounts,
  taxCodes
}: BillEditorProps) {
  const [rows, setRows] = useState<BillRow[]>([createBlankRow(), createBlankRow()]);
  const taxById = new Map(taxCodes.map((taxCode) => [taxCode.id, taxCode]));

  const activeRows = rows.filter(isRowUsed);
  const subtotal = activeRows.reduce((sum, row) => sum + (Number(row.amount) || 0), 0);
  const taxAmount = activeRows.reduce((sum, row) => {
    const rate = taxById.get(row.taxCodeId)?.ratePercent ?? 0;
    return sum + (Number(row.amount) || 0) * (rate / 100);
  }, 0);
  const total = subtotal + taxAmount;

  return (
    <form action={createBillAction} className="space-y-6">
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="surface-panel p-6 lg:p-7">
          <div className="mb-6 flex items-start justify-between gap-4">
            <div className="space-y-2">
              <p className="eyebrow-label">Purchases</p>
              <h2 className="text-2xl font-semibold tracking-[-0.03em] text-slate-950">
                Draft a new bill
              </h2>
              <p className="max-w-2xl text-sm text-slate-600">
                Capture supplier charges in a structured way so the payable, expense split, and tax treatment are consistent when the bill is posted.
              </p>
            </div>
            <Link href="/bills" className="btn-secondary">
              Back to bills
            </Link>
          </div>

          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <div className="xl:col-span-2">
              <label htmlFor="vendorId" className="field-label">
                Supplier
              </label>
              <select id="vendorId" name="vendorId" defaultValue="" className="field-input" required>
                <option value="" disabled>
                  Choose a supplier
                </option>
                {vendors.map((vendor) => (
                  <option key={vendor.id} value={vendor.id}>
                    {vendor.label}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor="billNumber" className="field-label">
                Bill no.
              </label>
              <input id="billNumber" name="billNumber" className="field-input" placeholder="AP-2026-001" required />
            </div>
            <div>
              <label htmlFor="billDate" className="field-label">
                Bill date
              </label>
              <input
                id="billDate"
                name="billDate"
                type="date"
                defaultValue={formatDateInput(new Date())}
                className="field-input"
                required
              />
            </div>
            <div>
              <label htmlFor="dueDate" className="field-label">
                Due date
              </label>
              <input
                id="dueDate"
                name="dueDate"
                type="date"
                defaultValue={getFutureDate(30)}
                className="field-input"
                required
              />
            </div>
            <div className="md:col-span-2 xl:col-span-3">
              <label htmlFor="memo" className="field-label">
                Memo
              </label>
              <input
                id="memo"
                name="memo"
                className="field-input"
                placeholder="Optional note for AP review, supporting details, or reference"
              />
            </div>
            <div className="summary-card">
              <p className="table-head-label">Posting outcome</p>
              <p className="mt-2 text-sm text-slate-700">
                Posting will debit the selected expense or asset lines and credit Accounts Payable.
              </p>
            </div>
          </div>
        </div>

        <aside className="surface-panel h-fit p-6 lg:sticky lg:top-28">
          <p className="eyebrow-label">Summary</p>
          <h3 className="mt-2 text-xl font-semibold tracking-[-0.03em] text-slate-950">
            {formatCurrency(total)}
          </h3>
          <div className="mt-6 space-y-4">
            <div className="rounded-[22px] bg-[var(--panel-soft)] p-4">
              <div className="flex items-center justify-between text-sm text-slate-600">
                <span>Expense lines</span>
                <span>{activeRows.length}</span>
              </div>
              <div className="mt-3 flex items-center justify-between text-sm">
                <span>Subtotal</span>
                <span className="font-medium text-slate-950">{formatCurrency(subtotal)}</span>
              </div>
              <div className="mt-2 flex items-center justify-between text-sm">
                <span>Sales tax</span>
                <span className="font-medium text-slate-950">{formatCurrency(taxAmount)}</span>
              </div>
              <div className="mt-3 border-t border-[var(--line)] pt-3 text-sm font-semibold">
                <div className="flex items-center justify-between">
                  <span>Total payable</span>
                  <span>{formatCurrency(total)}</span>
                </div>
              </div>
            </div>
            <p className="text-sm text-slate-600">
              Use one line per supplier charge or allocation. Each line may use a different account and tax code.
            </p>
            <button type="submit" className="btn-primary w-full">
              Save draft bill
            </button>
          </div>
        </aside>
      </div>

      <section className="surface-panel overflow-hidden">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-[var(--line)] px-6 py-5">
          <div>
            <p className="eyebrow-label">Bill lines</p>
            <h3 className="mt-1 text-lg font-semibold text-slate-950">Supplier allocations</h3>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              className="btn-secondary"
              onClick={() =>
                setRows((current) =>
                  current.length >= MAX_BILL_LINES ? current : [...current, createBlankRow()]
                )
              }
            >
              <PlusIcon className="h-4 w-4" />
              Add line
            </button>
            <button type="button" className="btn-ghost" onClick={() => setRows([createBlankRow(), createBlankRow()])}>
              Clear all lines
            </button>
          </div>
        </div>

        <div className="overflow-x-auto">
          <div className="min-w-[960px]">
            <div className="grid grid-cols-[56px_250px_1fr_180px_180px_56px] gap-3 border-b border-[var(--line)] bg-[var(--panel-soft)] px-6 py-3">
              <div className="table-head-label">#</div>
              <div className="table-head-label">Category</div>
              <div className="table-head-label">Description</div>
              <div className="table-head-label">Amount</div>
              <div className="table-head-label">Sales tax</div>
              <div className="table-head-label text-right"> </div>
            </div>

            {rows.map((row, index) => (
              <div
                key={index}
                className="grid grid-cols-[56px_250px_1fr_180px_180px_56px] gap-3 border-b border-[var(--line)] px-6 py-3 last:border-b-0"
              >
                <div className="flex items-center text-sm font-medium text-slate-500">{index + 1}</div>
                <select
                  name={`line-${index}-expenseAccountId`}
                  value={row.expenseAccountId}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index
                          ? { ...item, expenseAccountId: event.target.value }
                          : item
                      )
                    )
                  }
                  className="field-input"
                >
                  <option value="">Choose account</option>
                  {expenseAccounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {account.label}
                    </option>
                  ))}
                </select>
                <input
                  name={`line-${index}-description`}
                  value={row.description}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, description: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                  placeholder="Describe the charge or allocation"
                />
                <input
                  name={`line-${index}-amount`}
                  type="number"
                  step="0.01"
                  min="0"
                  value={row.amount}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, amount: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                  placeholder="0.00"
                />
                <select
                  name={`line-${index}-taxCodeId`}
                  value={row.taxCodeId}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, taxCodeId: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                >
                  <option value="">No tax</option>
                  {taxCodes.map((taxCode) => (
                    <option key={taxCode.id} value={taxCode.id}>
                      {taxCode.label}
                    </option>
                  ))}
                </select>
                <div className="flex items-center justify-end">
                  <button
                    type="button"
                    className="icon-button h-11 w-11"
                    onClick={() =>
                      setRows((current) => {
                        if (current.length <= 2) {
                          return current.map((item, rowIndex) =>
                            rowIndex === index ? createBlankRow() : item
                          );
                        }

                        return current.filter((_, rowIndex) => rowIndex !== index);
                      })
                    }
                  >
                    ×
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>
    </form>
  );
}
