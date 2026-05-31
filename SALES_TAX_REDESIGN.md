# Sales Tax — Rule / Code redesign

Status: **DRAFT for review** (2026-05-31). No code changed yet.
Supersedes the multi-component model in `SALES_TAX_MODULE_DESIGN.md` (S1–S5).

Confirmed direction (user, 2026-05-31):
- **Tax Rule = a single tax** = the existing legacy `tax_codes` row (e.g. `G` = GST 5%). Kept as-is.
- **Tax Code = a user-defined bundle** that references several Tax Rules (e.g. `BC` = GST 5% + PST 7%). New.

---

## 1. Two-layer model

```
Tax Rule  (layer 1, EXISTING)      Tax Code (layer 2, NEW)
= one tax, one rate                = an ordered bundle of Rules
  G   GST 5%                         BC   -> [G, PST-BC]
  PST-BC  PST 7%                     ON   -> [HST-ON]
  HST-ON  HST 13%                    GST  -> [G]   (single-rule code)
```

A document line selects ONE **Tax Code**. The engine expands it to its Rules and
produces one tax leg per Rule. A "single tax" is just a Code with one Rule.

## 2. Naming collision (must resolve)

Today the table literally named `tax_codes` IS the **Rule**. The user's new
"Tax Code" is a different thing (the bundle). Proposed technical names
(branding-strangler: keep stable tech names, map UI labels):

| Concept (UI label) | Table (technical) | Status |
|---|---|---|
| **Tax Rule** | `tax_codes` *(unchanged)* | exists |
| **Tax Code** (bundle) | `tax_code_sets` | NEW |
| Code→Rule membership | `tax_code_set_rules` | NEW |

(Table names are negotiable — calling out the collision so we pick deliberately.)

## 3. Schema

**Tax Rule = `tax_codes` — UNCHANGED.** Already carries everything a single tax
needs: `rate_percent`, `applies_to`, `recoverability_mode`,
`is_recoverable_on_purchase`, `payable_account_id`, `recoverable_account_id`,
`is_active`. No new columns.

**`tax_code_sets` (NEW — the bundle):**
- `id`, `company_id`, `code`, `name`, `applies_to` (sales/purchase/both), `is_active`

**`tax_code_set_rules` (NEW — ordered membership, many-to-many):**
- `tax_code_set_id` → tax_code_sets.id
- `tax_rule_id` → tax_codes.id
- `sequence` (leg order on the JE)
- `is_compound` (default false; tax-on-tax, e.g. legacy QST — rarely needed today)

A Rule can belong to many Codes (shared/reusable — define `G` once, reference everywhere).

## 4. Engine

Replace the current `legacy_id → sales_tax_codes → components` bridge with a
direct, simpler path:

```
line.tax_code_id
  → is it a tax_code_set?  yes → expand to its ordered Rules
                           no  → it's a single Rule (today's behaviour) → [itself]
  → for each Rule: tax = base × rate_percent
                   split recoverable / non-recoverable per the Rule's mode
                   route to the Rule's payable / recoverable accounts
  → N tax legs  (this is the multi-tax GST+PST JE that S5.2 was for)
```

The per-Rule math (rate, recoverability, GL routing) is exactly today's logic,
just sourced from `tax_codes` directly instead of the component tables.

## 5. Posting & snapshots

- One tax leg per Rule (ordered by `sequence`) → the multi-tax JE.
- Snapshots (decision A: store the GL accounts) re-key from per-component to
  **per-Rule** — same idea, simpler source (`tax_codes` accounts).
- Immutability still applies (snapshot frozen at post time).

## 6. Migration & live-safety (v2 is in production, flag ON)

- **No rewrite of existing document lines.** They store a Rule id today; the
  engine's "is it a set? else single Rule" check keeps them computing exactly as
  now. New lines can store either a Rule or a Code id.
- **Roll out behind the existing flag / a new path** — keep the current compute
  path until the new one is verified against the same documents.
- Snapshots are written fresh on each post, so re-keying affects only new posts.

## 7. What this supersedes

The v2 `sales_tax_codes` + `sales_tax_code_components` + `sales_tax_code_component_rates`
model becomes **redundant** (the bundle does what "code owns components" did).
Recommend: stop using them in the engine first; **drop the tables in a later
cleanup phase**, not now.

## 8. Open decisions (my recommendation in **bold** — please confirm/adjust)

1. **Reusability** — Rules shared across Codes (many-to-many). **Yes.**
2. **Keep Tax Rule simple** — no new columns. The legacy `tax_codes` lacks
   *jurisdiction/regime type*, *date-banded rates*, and *GST34 box mappings*
   (the component model added these). **Defer all three to the GST34 reporting
   phase** — posting only needs rate + recoverability + accounts, which Rules
   already have.
3. **Line reference** — polymorphic (a line's `tax_code_id` may point to a Rule
   or a Code; engine resolves). **Yes** — safest for the live data, no line rewrite.
4. **Retire v2 component tables** — **later cleanup phase, not now.**
5. **Compound (tax-on-tax)** — supported via `is_compound` on the membership,
   **default off**. Confirm you don't need it on day one.

## 9. Phased plan (after sign-off)

- **R1** Schema: `tax_code_sets` + `tax_code_set_rules` (migration). Verify on remote.
- **R2** UI: rename current "Tax codes" page → **Tax Rules**; new **Tax Code**
  editor (pick + order Rules). Line picker lists Rules + Codes.
- **R3** Engine: expand Code→Rules, compute each Rule directly (behind flag).
  Verify GST+PST = two legs on a real document.
- **R4** Posting/snapshots per Rule; immutability.
- **R5** Cleanup: retire the v2 component tables + bridge.

---

### Decision needed to start R1
Confirm §8 items 1–5 (or adjust). Then I build R1.
