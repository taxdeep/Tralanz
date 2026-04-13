import { requireAuthAndSetup } from "@/lib/guards";
import { prisma } from "@/lib/prisma";
import { createVendorAction } from "@/app/vendors/actions";
import { Notice, PageHeader, StatCard, StatusBadge, SurfaceSection } from "@/components/workbench";

type VendorsPageProps = {
  searchParams: Promise<{ error?: string; created?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-display-name": "Vendor display name is required."
};

export default async function VendorsPage({ searchParams }: VendorsPageProps) {
  const user = await requireAuthAndSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;
  const created = params.created === "1";

  const vendors = await prisma.vendor.findMany({
    where: { userId: user.id },
    orderBy: { createdAt: "desc" }
  });

  const activeVendors = vendors.filter((vendor: (typeof vendors)[number]) => vendor.isActive);
  const withEmail = vendors.filter((vendor: (typeof vendors)[number]) => vendor.email).length;
  const withPhone = vendors.filter((vendor: (typeof vendors)[number]) => vendor.phone).length;

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Purchases master data"
        title="Vendors"
        description="供应商页也统一成列表 + 右侧建档区，方便 AP 团队一边看主数据，一边补录新的 supplier。"
      />

      {error ? <Notice variant="danger">{error}</Notice> : null}
      {created ? <Notice variant="success">Vendor created.</Notice> : null}

      <div className="grid gap-4 md:grid-cols-3">
        <StatCard
          label="Total vendors"
          value={String(vendors.length)}
          hint="All supplier records in this company."
        />
        <StatCard
          label="Active vendors"
          value={String(activeVendors.length)}
          hint="Suppliers ready for bill creation."
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
          title="Vendor directory"
          description="Review supplier records before entering bills and AP documents."
        >
          {vendors.length === 0 ? (
            <div className="rounded-[24px] border border-dashed border-[var(--line)] bg-[var(--panel-soft)] px-6 py-10 text-center text-sm text-slate-600">
              No vendors yet. Create your first vendor on the right.
            </div>
          ) : (
            <div className="overflow-hidden rounded-[24px] border border-[var(--line)]">
              <div className="grid grid-cols-[1.2fr_1fr_180px_120px] gap-3 bg-[var(--panel-soft)] px-5 py-3">
                <div className="table-head-label">Vendor</div>
                <div className="table-head-label">Contact</div>
                <div className="table-head-label">Address</div>
                <div className="table-head-label">Status</div>
              </div>
              {vendors.map((vendor: (typeof vendors)[number]) => (
                <div
                  key={vendor.id}
                  className="grid grid-cols-[1.2fr_1fr_180px_120px] gap-3 border-t border-[var(--line)] px-5 py-4 text-sm"
                >
                  <div>
                    <p className="font-semibold text-slate-950">{vendor.displayName}</p>
                    <p className="mt-1 text-slate-500">Created {vendor.createdAt.toISOString().slice(0, 10)}</p>
                  </div>
                  <div className="text-slate-700">
                    <p>{vendor.email || "No email"}</p>
                    <p className="mt-1 text-slate-500">{vendor.phone || "No phone"}</p>
                  </div>
                  <div className="text-slate-700">{vendor.address || "No address saved"}</div>
                  <div className="self-center">
                    <StatusBadge status={vendor.isActive ? "active" : "inactive"} />
                  </div>
                </div>
              ))}
            </div>
          )}
        </SurfaceSection>

        <SurfaceSection
          title="Quick create"
          description="A compact supplier intake panel for faster AP setup."
          className="h-fit"
        >
          <form action={createVendorAction} className="space-y-4">
            <div>
              <label htmlFor="displayName" className="field-label">
                Vendor display name
              </label>
              <input id="displayName" name="displayName" className="field-input" placeholder="Supplier or company name" required />
            </div>
            <div>
              <label htmlFor="email" className="field-label">
                Email
              </label>
              <input id="email" name="email" className="field-input" placeholder="ap@supplier.com" />
            </div>
            <div>
              <label htmlFor="phone" className="field-label">
                Phone
              </label>
              <input id="phone" name="phone" className="field-input" placeholder="+1 604 555 0101" />
            </div>
            <div>
              <label htmlFor="address" className="field-label">
                Address
              </label>
              <textarea id="address" name="address" rows={4} className="field-input" placeholder="Street, city, province, postal code" />
            </div>
            <button type="submit" className="btn-primary w-full">
              Add vendor
            </button>
          </form>
        </SurfaceSection>
      </div>
    </div>
  );
}
