import { requireAuthWithoutSetup } from "@/lib/guards";
import { saveSetupAction } from "@/app/setup-wizard/actions";

type SetupPageProps = {
  searchParams: Promise<{ error?: string }>;
};

const errorMessages: Record<string, string> = {
  "missing-required-fields": "Please fill in all required fields.",
  "invalid-company-type": "Please select a valid company type.",
  "invalid-tax-regime": "Please select a valid tax regime.",
  "invalid-fiscal-month": "Fiscal year end month must be between 1 and 12.",
  "invalid-fiscal-day": "Fiscal year end day must be between 1 and 31.",
  "missing-tax-registration-number":
    "Tax registration number is required when GST/HST is registered."
};

export default async function SetupWizardPage({ searchParams }: SetupPageProps) {
  await requireAuthWithoutSetup();
  const params = await searchParams;
  const error = params.error ? errorMessages[params.error] : null;

  return (
    <div className="rounded-lg bg-white border border-slate-200 p-6">
      <h1 className="text-xl font-semibold mb-2">Setup Wizard</h1>
      <p className="text-sm text-slate-600 mb-4">
        Complete your company profile to start using the system.
      </p>
      {error ? (
        <p className="mb-3 rounded bg-red-50 border border-red-200 text-red-700 text-sm px-3 py-2">
          {error}
        </p>
      ) : null}
      <form action={saveSetupAction} className="space-y-3 max-w-xl">
        <div>
          <label
            htmlFor="legalName"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Company Legal Name
          </label>
          <input
            id="legalName"
            name="legalName"
            type="text"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            required
          />
        </div>
        <div>
          <label
            htmlFor="companyType"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Company Type
          </label>
          <select
            id="companyType"
            name="companyType"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            required
            defaultValue=""
          >
            <option value="" disabled>
              Select company type
            </option>
            <option value="corporation">Corporation</option>
            <option value="llp-partnership">LLP / Partnership</option>
            <option value="sole-proprietorship-individual">
              Sole Proprietorship / Individual
            </option>
          </select>
        </div>
        <div>
          <label
            htmlFor="businessNumber"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Business Number
          </label>
          <input
            id="businessNumber"
            name="businessNumber"
            type="text"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            required
          />
        </div>
        <div>
          <label
            htmlFor="address"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Address
          </label>
          <textarea
            id="address"
            name="address"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm min-h-24"
            required
          />
        </div>
        <div>
          <label
            htmlFor="baseCurrency"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Base Currency
          </label>
          <input
            id="baseCurrency"
            name="baseCurrency"
            type="text"
            defaultValue="CAD"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
          />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label
              htmlFor="fiscalYearEndMonth"
              className="block text-sm font-medium text-slate-700 mb-1"
            >
              Fiscal Year End Month
            </label>
            <input
              id="fiscalYearEndMonth"
              name="fiscalYearEndMonth"
              type="number"
              min={1}
              max={12}
              className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
              required
            />
          </div>
          <div>
            <label
              htmlFor="fiscalYearEndDay"
              className="block text-sm font-medium text-slate-700 mb-1"
            >
              Fiscal Year End Day
            </label>
            <input
              id="fiscalYearEndDay"
              name="fiscalYearEndDay"
              type="number"
              min={1}
              max={31}
              className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
              required
            />
          </div>
        </div>
        <div>
          <label
            htmlFor="gstHstRegistered"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            GST/HST Registered
          </label>
          <select
            id="gstHstRegistered"
            name="gstHstRegistered"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            defaultValue="no"
          >
            <option value="yes">Yes</option>
            <option value="no">No</option>
          </select>
        </div>
        <div>
          <label
            htmlFor="taxRegistrationNumber"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Tax Registration Number
          </label>
          <input
            id="taxRegistrationNumber"
            name="taxRegistrationNumber"
            type="text"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            placeholder="Required if GST/HST registered = Yes"
          />
        </div>
        <div>
          <label
            htmlFor="taxRegime"
            className="block text-sm font-medium text-slate-700 mb-1"
          >
            Province / Tax Regime
          </label>
          <select
            id="taxRegime"
            name="taxRegime"
            className="w-full rounded border border-slate-300 px-3 py-2 text-sm"
            required
            defaultValue=""
          >
            <option value="" disabled>
              Select tax regime
            </option>
            <option value="gst-hst">GST/HST (federal)</option>
            <option value="gst-pst">GST + PST</option>
            <option value="hst">HST province</option>
            <option value="quebec-gst-qst">Quebec GST + QST</option>
          </select>
        </div>
        <button
          type="submit"
          className="rounded bg-slate-900 text-white py-2 px-4 text-sm font-medium hover:bg-slate-800"
        >
          Save Setup
        </button>
      </form>
    </div>
  );
}
