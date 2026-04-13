import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import { createCustomerAction } from "@/app/customers/actions";
import { Notice, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

type CustomersPageProps = {
  searchParams: Promise<{ error?: string; created?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-display-name": "Customer display name is required."
};

export default async function CustomersPage({ searchParams }: CustomersPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;
  const created = params.created === "1";

  const customers = await prisma.customer.findMany({
    where: { userId: user.id },
    orderBy: { createdAt: "desc" }
  });

  const activeCustomers = customers.filter((customer: (typeof customers)[number]) => customer.isActive);
  const withEmail = customers.filter((customer: (typeof customers)[number]) => customer.email).length;
  const withPhone = customers.filter((customer: (typeof customers)[number]) => customer.phone).length;

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Sales master data"
        title="Customers"
        description="这里参考了你截图里的客户列表 + 右侧录入区思路，把客户页调整成‘列表管理 + 快速建档’的并行工作方式。"
      />

      {error ? <Notice variant="danger">{error}</Notice> : null}
      {created ? <Notice variant="success">Customer created.</Notice> : null}

      <div className="grid gap-4 md:grid-cols-3">
        <StatCard
          label="Total customers"
          value={String(customers.length)}
          hint="All customer records in this company."
        />
        <StatCard
          label="Active customers"
          value={String(activeCustomers.length)}
          hint="Customers available for invoicing."
          tone="positive"
        />
        <StatCard
          label="Contact completeness"
          value={`${withEmail}/${withPhone}`}
          hint="Email records over phone records currently saved."
        />
      </div>

      <div className="grid gap-6 2xl:grid-cols-[minmax(0,1fr)_380px]">
        <SurfaceSection
          title="Customer directory"
          description="Use this list to review who is ready for invoicing and whether a record has enough contact detail."
        >
          {customers.length === 0 ? (
            <div className="rounded-[24px] border border-dashed border-[var(--line)] bg-[var(--panel-soft)] px-6 py-10 text-center text-sm text-slate-600">
              No customers yet. Create your first customer on the right.
            </div>
          ) : (
            <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
              <div className="grid grid-cols-[1.2fr_1fr_180px_120px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
                <div className="table-head-label">Customer</div>
                <div className="table-head-label">Contact</div>
                <div className="table-head-label">Address</div>
                <div className="table-head-label">Status</div>
              </div>
              {customers.map((customer: (typeof customers)[number]) => (
                <div
                  key={customer.id}
                  className="grid grid-cols-[1.2fr_1fr_180px_120px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
                >
                  <div>
                    <p className="font-semibold text-slate-950">{customer.displayName}</p>
                    <p className="mt-1 text-slate-500">Created {customer.createdAt.toISOString().slice(0, 10)}</p>
                  </div>
                  <div className="text-slate-700">
                    <p>{customer.email || "No email"}</p>
                    <p className="mt-1 text-slate-500">{customer.phone || "No phone"}</p>
                  </div>
                  <div className="text-slate-700">{customer.address || "No address saved"}</div>
                  <div className="self-center">
                    <StatusBadge status={customer.isActive ? "active" : "inactive"} />
                  </div>
                </div>
              ))}
            </div>
          )}
        </SurfaceSection>

        <SurfaceSection
          title="Quick create"
          description="A compact right-side intake form, similar to the customer drawer flow in your screenshot."
          className="h-fit"
        >
          <form action={createCustomerAction} className="space-y-4">
            <div>
              <label htmlFor="displayName" className="field-label">
                Customer display name
              </label>
              <input id="displayName" name="displayName" className="field-input" placeholder="Customer or company name" required />
            </div>
            <div>
              <label htmlFor="email" className="field-label">
                Email
              </label>
              <input id="email" name="email" className="field-input" placeholder="billing@customer.com" />
            </div>
            <div>
              <label htmlFor="phone" className="field-label">
                Phone
              </label>
              <input id="phone" name="phone" className="field-input" placeholder="+1 604 555 0100" />
            </div>
            <div>
              <label htmlFor="address" className="field-label">
                Address
              </label>
              <textarea id="address" name="address" rows={4} className="field-input" placeholder="Street, city, province, postal code" />
            </div>
            <button type="submit" className="btn-primary w-full">
              Add customer
            </button>
          </form>
        </SurfaceSection>
      </div>
    </div>
  );
}
