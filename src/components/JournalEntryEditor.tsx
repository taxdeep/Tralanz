"use client";

import Link from "next/link";
import { useState } from "react";
import { createJournalEntryAction } from "@/app/journal-entries/actions";
import { PlusIcon } from "@/components/icons";
import { formatCurrency, formatDateInput } from "@/lib/format";

type AccountOption = {
  id: string;
  label: string;
};

type JournalRow = {
  accountId: string;
  debit: string;
  credit: string;
  details: string;
};

type JournalEntryEditorProps = {
  accounts: AccountOption[];
};

const MAX_JOURNAL_LINES = 12;

function createBlankRow(): JournalRow {
  return {
    accountId: "",
    debit: "",
    credit: "",
    details: ""
  };
}

function isRowUsed(row: JournalRow) {
  return Boolean(
    row.accountId.trim() || row.debit.trim() || row.credit.trim() || row.details.trim()
  );
}

export default function JournalEntryEditor({ accounts }: JournalEntryEditorProps) {
  const [rows, setRows] = useState<JournalRow[]>([
    createBlankRow(),
    createBlankRow(),
    createBlankRow(),
    createBlankRow()
  ]);

  const activeRows = rows.filter(isRowUsed);
  const totalDebit = activeRows.reduce((sum, row) => sum + (Number(row.debit) || 0), 0);
  const totalCredit = activeRows.reduce((sum, row) => sum + (Number(row.credit) || 0), 0);
  const difference = totalDebit - totalCredit;
  const isBalanced = Math.abs(difference) < 0.005 && activeRows.length >= 2;

  return (
    <form action={createJournalEntryAction} className="space-y-6">
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="surface-panel p-6 lg:p-7">
          <div className="mb-6 flex items-start justify-between gap-4">
            <div className="space-y-2">
              <p className="eyebrow-label">General ledger</p>
              <h2 className="text-2xl font-semibold tracking-[-0.03em] text-slate-950">
                Create a journal entry
              </h2>
              <p className="max-w-2xl text-sm text-slate-600">
                Manual journals should stay explicit and balanced. Use one line per debit or credit leg so the ledger remains audit-friendly.
              </p>
            </div>
            <Link href="/journal-entries" className="btn-secondary">
              Back to journal
            </Link>
          </div>

          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <div>
              <label htmlFor="entryDate" className="field-label">
                Journal date
              </label>
              <input
                id="entryDate"
                name="entryDate"
                type="date"
                defaultValue={formatDateInput(new Date())}
                className="field-input"
                required
              />
            </div>
            <div>
              <label htmlFor="entryNumber" className="field-label">
                Journal no.
              </label>
              <input id="entryNumber" name="entryNumber" className="field-input" placeholder="JE-2026-001" required />
            </div>
            <div className="md:col-span-2">
              <label htmlFor="memo" className="field-label">
                Memo
              </label>
              <input
                id="memo"
                name="memo"
                className="field-input"
                placeholder="Why this journal exists and what supporting event it reflects"
              />
            </div>
          </div>
        </div>

        <aside className="surface-panel h-fit p-6 lg:sticky lg:top-28">
          <p className="eyebrow-label">Balance check</p>
          <div className="mt-4 grid gap-3">
            <div className="summary-card">
              <p className="table-head-label">Debit</p>
              <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(totalDebit)}</p>
            </div>
            <div className="summary-card">
              <p className="table-head-label">Credit</p>
              <p className="mt-2 text-xl font-semibold text-slate-950">{formatCurrency(totalCredit)}</p>
            </div>
            <div className="summary-card">
              <p className="table-head-label">Difference</p>
              <p className={`mt-2 text-xl font-semibold ${isBalanced ? "text-emerald-700" : "text-amber-700"}`}>
                {formatCurrency(Math.abs(difference))}
              </p>
              <p className="mt-2 text-sm text-slate-600">
                {isBalanced ? "Ready to save as a balanced draft." : "Debit and credit must match before saving."}
              </p>
            </div>
            <button type="submit" className="btn-primary w-full" disabled={!isBalanced}>
              Save draft journal
            </button>
          </div>
        </aside>
      </div>

      <section className="surface-panel overflow-hidden">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-[var(--line)] px-6 py-5">
          <div>
            <p className="eyebrow-label">Journal lines</p>
            <h3 className="mt-1 text-lg font-semibold text-slate-950">Debits and credits</h3>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              className="btn-secondary"
              onClick={() =>
                setRows((current) =>
                  current.length >= MAX_JOURNAL_LINES ? current : [...current, createBlankRow()]
                )
              }
            >
              <PlusIcon className="h-4 w-4" />
              Add line
            </button>
            <button
              type="button"
              className="btn-ghost"
              onClick={() => setRows([createBlankRow(), createBlankRow(), createBlankRow(), createBlankRow()])}
            >
              Clear all lines
            </button>
          </div>
        </div>

        <div className="overflow-x-auto">
          <div className="min-w-[1100px]">
            <div className="grid grid-cols-[56px_280px_180px_180px_1fr_56px] gap-3 border-b border-[var(--line)] bg-[var(--panel-soft)] px-6 py-3">
              <div className="table-head-label">#</div>
              <div className="table-head-label">Account</div>
              <div className="table-head-label">Debit</div>
              <div className="table-head-label">Credit</div>
              <div className="table-head-label">Description</div>
              <div className="table-head-label text-right"> </div>
            </div>

            {rows.map((row, index) => (
              <div
                key={index}
                className="grid grid-cols-[56px_280px_180px_180px_1fr_56px] gap-3 border-b border-[var(--line)] px-6 py-3 last:border-b-0"
              >
                <div className="flex items-center text-sm font-medium text-slate-500">{index + 1}</div>
                <select
                  name={`line-${index}-accountId`}
                  value={row.accountId}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, accountId: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                >
                  <option value="">Choose account</option>
                  {accounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {account.label}
                    </option>
                  ))}
                </select>
                <input
                  name={`line-${index}-debit`}
                  type="number"
                  step="0.01"
                  min="0"
                  value={row.debit}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, debit: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                  placeholder="0.00"
                />
                <input
                  name={`line-${index}-credit`}
                  type="number"
                  step="0.01"
                  min="0"
                  value={row.credit}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, credit: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                  placeholder="0.00"
                />
                <input
                  name={`line-${index}-details`}
                  value={row.details}
                  onChange={(event) =>
                    setRows((current) =>
                      current.map((item, rowIndex) =>
                        rowIndex === index ? { ...item, details: event.target.value } : item
                      )
                    )
                  }
                  className="field-input"
                  placeholder="Explain the accounting intent"
                />
                <div className="flex items-center justify-end">
                  <button
                    type="button"
                    className="icon-button h-11 w-11"
                    onClick={() =>
                      setRows((current) => {
                        if (current.length <= 4) {
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
