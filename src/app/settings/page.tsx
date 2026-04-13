import { requireAuthAndSetup } from "@/lib/guards";
import { PageHeader, SurfaceSection } from "@/components/workbench";

export default async function SettingsPage() {
  await requireAuthAndSetup();

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Configuration"
        title="Settings"
        description="设置页也已经纳入统一视觉体系，后续可以继续往税码、公司资料、会计期间这些子模块展开。"
      />

      <SurfaceSection
        title="Current scope"
        description="Tax code maintenance, company profile editing, and accounting policy settings can be layered here in the next phase."
      >
        <div className="grid gap-4 md:grid-cols-3">
          {[
            "Company profile and fiscal settings",
            "Tax code maintenance and recoverable mappings",
            "Document numbering and posting controls"
          ].map((item) => (
            <div key={item} className="rounded-[24px] border border-[var(--line)] bg-[var(--panel-soft)] p-5 text-sm text-slate-700">
              {item}
            </div>
          ))}
        </div>
      </SurfaceSection>
    </div>
  );
}
