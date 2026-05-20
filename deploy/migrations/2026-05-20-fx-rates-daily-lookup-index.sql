-- =====================================================================
-- H-16: fx_rates_daily lookup index
-- =====================================================================
--
-- Audit found PostgresFxRateCacheRepository.GetAsync (and the same
-- shape in PostgreSqlTaskMarginReportService's LATERAL JOIN) executes
-- a four-column equality predicate on fx_rates_daily for every FX
-- resolution:
--
--   where rate_date  = @rate_date
--     and base_code  = @base_code
--     and quote_code = @quote_code
--     and value_basis = @value_basis
--
-- No supporting index existed, so each lookup was a seq-scan that
-- degrades linearly as the table grows. A unique index on the four
-- columns serves both `GetAsync` (exact match) and
-- `GetLatestBeforeAsync` (range scan on rate_date) — putting
-- rate_date last + DESC lets the descending range scan terminate
-- early without sorting.
--
-- Also serves as a uniqueness guard: the cache repository inserts a
-- single row per (base, quote, value_basis, date). Without the
-- index a race between two writers could land duplicate rows.
-- =====================================================================

begin;

create unique index if not exists ux_fx_rates_daily_lookup
  on fx_rates_daily (base_code, quote_code, value_basis, rate_date desc);

comment on index ux_fx_rates_daily_lookup is
  'H-16 (PR-H1): supports PostgresFxRateCacheRepository.GetAsync + GetLatestBeforeAsync and serves as uniqueness guard against double-insert races.';

commit;
