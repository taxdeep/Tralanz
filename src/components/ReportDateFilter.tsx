type ReportDateFilterProps = {
  from: string;
  to: string;
};

export default function ReportDateFilter({ from, to }: ReportDateFilterProps) {
  return (
    <form method="get" className="mb-5 grid gap-4 md:grid-cols-2 xl:grid-cols-[1fr_1fr_auto]">
      <div>
        <label htmlFor="from" className="field-label">
          From Date
        </label>
        <input
          id="from"
          name="from"
          type="date"
          defaultValue={from}
          className="field-input"
        />
      </div>
      <div>
        <label htmlFor="to" className="field-label">
          To Date
        </label>
        <input
          id="to"
          name="to"
          type="date"
          defaultValue={to}
          className="field-input"
        />
      </div>
      <div className="flex items-end">
        <button type="submit" className="btn-primary w-full xl:w-auto">
          Apply
        </button>
      </div>
    </form>
  );
}
