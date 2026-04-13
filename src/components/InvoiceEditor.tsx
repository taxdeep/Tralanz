"use client";

import Link from "next/link";
import { useState } from "react";
import { createInvoiceAction } from "@/app/sales-invoices/actions";
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

type InvoiceRow = {
  description: string;
  quantity: string;
  unitPrice: string;
  taxCodeId: string;
};

type InvoiceEditorProps = {
  customers: Option[];
  taxCodes: TaxCodeOption[];
};

const MAX_INVOICE_LINES = 10;

function createBlankRow(): InvoiceRow {
  return {
    description: "",
    quantity: "",
    unitPrice: "",
    taxCodeId: ""
  };
}

function getFutureDate(days: number) {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return formatDateInput(date);
}

function isRowUsed(row: InvoiceRow) {
  return Boolean(
    row.description.trim() || row.quantity.trim() || row.unitPrice.trim() || row.taxCodeId.trim()
  );
}

export default function InvoiceEditor({ customers, taxCodes }: InvoiceEditorProps) {
  const [rows, setRows] = useState<InvoiceRow[]>([createBlankRow(), createBlankRow()]);
  const taxById = new Map(taxCodes.map((taxCode) => [taxCode.id, taxCode]));

  const activeRows = rows.filter(isRowUsed);
  const subtotal = activeRows.reduce(
    (sum, row) => sum + (Number(row.quantity) || 0) * (Number(row.unitPrice) || 0),
    0
  );
  const taxAmount = activeRows.reduce((sum, row) => {
    const amount = (Number(row.quantity) || 0) * (Number(row.unitPrice) || 0);
    const rate = taxById.get(row.taxCodeId)?.ratePercent ?? 0;
    return sum + amount * (rate / 100);
  }, 0);
  const total = subtotal + taxAmount;

  return (
    <form action={createInvoiceAction} className="space-y-6">
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="surface-panel p-6 lg:p-7">
          <div className="mb-6 flex items-start justify-between gap-4">
            <div className="space-y-2">
              <p className="eyebrow-label">Sales</p>
              <h2 className="text-2xl font-semibold tracking-[-0.03em] text-slate-950">
                Draft a new invoice
              </h2>
              <p className="max-w-2xl text-sm text-slate-600">
                Build invoices around line items, quantities, and tax handling so the receivable and revenue entry can be posted cleanly.
              </p>
            </div>
            <Link href="/sales-invoices" className="btn-secondary">
              Back to invoices
            </Link>
          </div>

          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <div className="xl:col-span-2">
              <label htmlFor="customerId" className="field-label">
                Customer
              </label>
              <select id="customerId" name="customerId" defaultValue="" className="field-input" required>
                <option value="" disabled>
                  Choose a customer
                </option>
                {customers.map((customer) => (
                  <option key={customer.id} value={customer.id}>
                    {customer.label}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor="invoiceNumber" className="field-label">
                Invoice no.
              </label>
              <input
                id="invoiceNumber"
                name="invoiceNumber"
                className="field-input"
                placeholder="INV-2026-001"
                required
              />
            </div>
            <div>
              <label htmlFor="invoiceDate" className="field-label">
                Invoice date
              </label>
              <input
                id="invoiceDate"
                name="invoiceDate"
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
                Message or memo
              </label>
              <input
                id="memo"
                name="memo"
                className="field-input"
                placeholder="Optional internal note or message summary"
              />
            </div>
            <div className="summary-card">
              <p className="table-head-label">Posting outcome</p>
              <p className="mt-2 text-sm text-slate-700">
                Posting will debit Accounts Receivable and credit Sales Revenue plus Sales Tax Payable when needed.
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
                <span>Billing lines</span>
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
                  <span>Total receivable</span>
                  <span>{formatCurrency(total)}</span>
                </div>
              </div>
            </div>
            <p className="text-sm text-slate-600">
              Each line can use its own tax treatment. Keep one deliverable or billing concept per row for cleaner audit history.
            </p>
            <button type="submit" className="btn-primary w-full">
              Save draft invoice
            </button>
          </div>
        </aside>
      </div>

      <section className="surface-panel overflow-hidden">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-[var(--line)] px-6 py-5">
          <div>
            <p className="eyebrow-label">Invoice lines</p>
            <h3 className="mt-1 text-lg font-semibold text-slate-950">Billable items</h3>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              className="btn-secondary"
              onClick={() =>
                setRows((current) =>
                  current.length >= MAX_INVOICE_LINES ? current : [...current, createBlankRow()]
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
          <div className="min-w-[1080px]">
            <div className="grid grid-cols-[56px_1.3fr_150px_150px_180px_180px_56px] gap-3 border-b border-[var(--line)] bg-[var(--panel-soft)] px-6 py-3">
              <div className="table-head-label">#</div>
              <div className="table-head-label">Description</div>
              <div className="table-head-label">Qty</div>
              <div className="table-head-label">Rate</div>
              <div className="table-head-label">Sales tax</div>
              <div className="table-head-label">Line total</div>
              <div className="table-head-label text-right"> </div>
            </div>

            {rows.map((row, index) => {
              const lineTotal = (Number(row.quantity) || 0) * (Number(row.unitPrice) || 0);

              return (
                <div
                  key={index}
                  className="grid grid-cols-[56px_1.3fr_150px_150px_180px_180px_56px] gap-3 border-b border-[var(--line)] px-6 py-3 last:border-b-0"
                >
                  <div className="flex items-center text-sm font-medium text-slate-500">{index + 1}</div>
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
                    placeholder="What is being billed"
                  />
                  <input
                    name={`line-${index}-quantity`}
                    type="number"
                    step="0.01"
                    min="0"
                    value={row.quantity}
                    onChange={(event) =>
                      setRows((current) =>
                        current.map((item, rowIndex) =>
                          rowIndex === index ? { ...item, quantity: event.target.value } : item
                        )
                      )
                    }
                    className="field-input"
                    placeholder="0.00"
                  />
                  <input
                    name={`line-${index}-unitPrice`}
                    type="number"
                    step="0.01"
                    min="0"
                    value={row.unitPrice}
                    onChange={(event) =>
                      setRows((current) =>
                        current.map((item, rowIndex) =>
                          rowIndex === index ? { ...item, unitPrice: event.target.value } : item
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
                  <div className="flex items-center rounded-2xl border border-[var(--line)] bg-[var(--panel-soft)] px-4 text-sm font-medium text-slate-900">
                    {formatCurrency(lineTotal)}
                  </div>
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
              );
            })}
          </div>
        </div>
      </section>
    </form>
  );
}
