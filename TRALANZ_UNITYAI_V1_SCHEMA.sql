-- =============================================================================
-- Citus unityAI V1 schema
--
-- Companion: AI_PRODUCT_ARCHITECTURE.md
-- Authority : CITUS_PRODUCT_ENGINEERING_AUTHORITY.md
--
-- This file defines the additive schema for the unityAI V1 batch:
--   * AI infrastructure observability (ai_job_runs, ai_request_logs)
--   * UnitySearch learning (events, usage stats, pair stats, recent queries,
--     learning profiles, ranking hints, alias suggestions, decision traces)
--   * Report usage learning (report_usage_events, report_usage_stats)
--   * Dashboard intelligence (dashboard_user_widgets, dashboard_widget_suggestions)
--   * Action Center (action_center_tasks, action_center_task_events)
--
-- Conventions match the existing Citus codebase:
--   * snake_case table and column names
--   * UUID primary keys defaulting to gen_random_uuid()
--   * timestamptz for all timestamps (NOW() default)
--   * every company-owned table indexes by company_id
--
-- This schema is applied at runtime via PostgreSqlUnityAiSchemaInitializer.
-- All statements are idempotent (CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. ai_job_runs - durable record of every AI / learning background run.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ai_job_runs (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            UUID NULL,
    job_type              TEXT NOT NULL,
    status                TEXT NOT NULL,
    trigger_type          TEXT NOT NULL,
    triggered_by_user_id  UUID NULL,
    started_at            TIMESTAMPTZ NULL,
    finished_at           TIMESTAMPTZ NULL,
    source_window_start   TIMESTAMPTZ NULL,
    source_window_end     TIMESTAMPTZ NULL,
    input_summary_json    JSONB NULL,
    output_summary_json   JSONB NULL,
    error_message         TEXT NULL,
    warnings_json         JSONB NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_ai_job_runs_company_type_created
    ON ai_job_runs (company_id, job_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ai_job_runs_company_status_created
    ON ai_job_runs (company_id, status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ai_job_runs_type_status_created
    ON ai_job_runs (job_type, status, created_at DESC);

-- -----------------------------------------------------------------------------
-- 2. ai_request_logs - one row per gateway call (incl. skipped / disabled).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ai_request_logs (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id               UUID NULL,
    job_run_id               UUID NULL,
    task_type                TEXT NOT NULL,
    provider                 TEXT NULL,
    model                    TEXT NULL,
    request_schema_version   TEXT NULL,
    response_schema_version  TEXT NULL,
    input_hash               TEXT NULL,
    input_redacted_json      JSONB NULL,
    output_redacted_json     JSONB NULL,
    status                   TEXT NOT NULL,
    error_message            TEXT NULL,
    prompt_version           TEXT NULL,
    token_input_count        INTEGER NULL,
    token_output_count       INTEGER NULL,
    estimated_cost           NUMERIC NULL,
    latency_ms               INTEGER NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_ai_request_logs_company_task_created
    ON ai_request_logs (company_id, task_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ai_request_logs_job_run
    ON ai_request_logs (job_run_id);
CREATE INDEX IF NOT EXISTS idx_ai_request_logs_status_created
    ON ai_request_logs (status, created_at DESC);

-- -----------------------------------------------------------------------------
-- 3. unitysearch_events - lightweight behavior log for picker surfaces.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_events (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           UUID NOT NULL,
    user_id              UUID NULL,
    session_id           TEXT NULL,
    context              TEXT NOT NULL,
    entity_type          TEXT NOT NULL,
    query                TEXT NULL,
    normalized_query     TEXT NULL,
    event_type           TEXT NOT NULL,
    selected_entity_id   UUID NULL,
    rank_position        INTEGER NULL,
    result_count         INTEGER NULL,
    source_route         TEXT NULL,
    anchor_context       TEXT NULL,
    anchor_entity_type   TEXT NULL,
    anchor_entity_id     UUID NULL,
    metadata_json        JSONB NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_events_company_created
    ON unitysearch_events (company_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_unitysearch_events_company_context_created
    ON unitysearch_events (company_id, context, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_unitysearch_events_company_user_context_created
    ON unitysearch_events (company_id, user_id, context, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_unitysearch_events_company_entity_selection
    ON unitysearch_events (company_id, entity_type, selected_entity_id);
CREATE INDEX IF NOT EXISTS idx_unitysearch_events_company_eventtype_created
    ON unitysearch_events (company_id, event_type, created_at DESC);

-- -----------------------------------------------------------------------------
-- 4. unitysearch_usage_stats - aggregated per (scope, user, context, entity).
--    scope_type avoids nullable-uniqueness ambiguity (company vs user rows).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_usage_stats (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           UUID NOT NULL,
    scope_type           TEXT NOT NULL,
    user_id              UUID NULL,
    context              TEXT NOT NULL,
    entity_type          TEXT NOT NULL,
    entity_id            UUID NOT NULL,
    select_count         INTEGER NOT NULL DEFAULT 0,
    select_count_7d      INTEGER NOT NULL DEFAULT 0,
    select_count_30d     INTEGER NOT NULL DEFAULT 0,
    select_count_90d     INTEGER NOT NULL DEFAULT 0,
    last_selected_at     TIMESTAMPTZ NULL,
    last_query           TEXT NULL,
    avg_rank_position    NUMERIC NULL,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- COALESCE the nullable user_id to a sentinel so the unique index actually fires
-- for company-scoped rows (where user_id IS NULL).
CREATE UNIQUE INDEX IF NOT EXISTS uq_unitysearch_usage_stats_scope
    ON unitysearch_usage_stats (
        company_id,
        scope_type,
        COALESCE(user_id, '00000000-0000-0000-0000-000000000000'),
        context,
        entity_type,
        entity_id
    );

-- -----------------------------------------------------------------------------
-- 5. unitysearch_pair_stats - anchor entity to target entity learning.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_pair_stats (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                  UUID NOT NULL,
    scope_type                  TEXT NOT NULL,
    user_id                     UUID NULL,
    source_context              TEXT NOT NULL,
    anchor_entity_type          TEXT NOT NULL,
    anchor_entity_id            UUID NOT NULL,
    target_context              TEXT NOT NULL,
    target_entity_type          TEXT NOT NULL,
    target_entity_id            UUID NOT NULL,
    select_count                INTEGER NOT NULL DEFAULT 0,
    total_anchor_select_count   INTEGER NOT NULL DEFAULT 0,
    confidence_score            NUMERIC NOT NULL DEFAULT 0,
    last_selected_at            TIMESTAMPTZ NULL,
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_unitysearch_pair_stats_scope
    ON unitysearch_pair_stats (
        company_id,
        scope_type,
        COALESCE(user_id, '00000000-0000-0000-0000-000000000000'),
        source_context,
        anchor_entity_type,
        anchor_entity_id,
        target_context,
        target_entity_type,
        target_entity_id
    );

-- -----------------------------------------------------------------------------
-- 6. unitysearch_recent_queries - per-user query history.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_recent_queries (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            UUID NOT NULL,
    user_id               UUID NULL,
    context               TEXT NOT NULL,
    query                 TEXT NOT NULL,
    normalized_query      TEXT NOT NULL,
    result_clicked        BOOLEAN NOT NULL DEFAULT FALSE,
    clicked_entity_type   TEXT NULL,
    clicked_entity_id     UUID NULL,
    result_count          INTEGER NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_recent_queries_company_user_context_created
    ON unitysearch_recent_queries (company_id, user_id, context, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_unitysearch_recent_queries_company_context_norm
    ON unitysearch_recent_queries (company_id, context, normalized_query);
CREATE INDEX IF NOT EXISTS idx_unitysearch_recent_queries_company_context_clicked_created
    ON unitysearch_recent_queries (company_id, context, result_clicked, created_at DESC);

-- -----------------------------------------------------------------------------
-- 7. unitysearch_learning_profiles - deterministic + future AI summaries.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_learning_profiles (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            UUID NOT NULL,
    user_id               UUID NULL,
    context               TEXT NOT NULL,
    profile_json          JSONB NOT NULL,
    summary_text          TEXT NULL,
    source_window_start   TIMESTAMPTZ NOT NULL,
    source_window_end     TIMESTAMPTZ NOT NULL,
    source                TEXT NOT NULL,
    model_name            TEXT NULL,
    model_version         TEXT NULL,
    confidence            NUMERIC NULL,
    job_run_id            UUID NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_learning_profiles_company_context_window
    ON unitysearch_learning_profiles (company_id, context, source_window_end DESC);

-- -----------------------------------------------------------------------------
-- 8. unitysearch_ranking_hints - reranking boosts (system / ai / admin).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_ranking_hints (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id               UUID NOT NULL,
    user_id                  UUID NULL,
    context                  TEXT NOT NULL,
    entity_type              TEXT NOT NULL,
    entity_id                UUID NOT NULL,
    boost_score              NUMERIC NOT NULL DEFAULT 0,
    confidence               NUMERIC NOT NULL DEFAULT 0,
    reason                   TEXT NULL,
    source                   TEXT NOT NULL,
    status                   TEXT NOT NULL,
    validation_status        TEXT NOT NULL,
    validation_error         TEXT NULL,
    activated_by_user_id     UUID NULL,
    rejected_by_user_id      UUID NULL,
    job_run_id               UUID NULL,
    expires_at               TIMESTAMPTZ NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_ranking_hints_company_ctx_status
    ON unitysearch_ranking_hints (company_id, context, status);
CREATE INDEX IF NOT EXISTS idx_unitysearch_ranking_hints_company_ctx_entity
    ON unitysearch_ranking_hints (company_id, context, entity_type, entity_id);

-- -----------------------------------------------------------------------------
-- 9. unitysearch_alias_suggestions - learned aliases for entities.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_alias_suggestions (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            UUID NOT NULL,
    context               TEXT NOT NULL,
    entity_type           TEXT NOT NULL,
    entity_id             UUID NOT NULL,
    alias                 TEXT NOT NULL,
    normalized_alias      TEXT NOT NULL,
    confidence            NUMERIC NOT NULL DEFAULT 0,
    reason                TEXT NULL,
    source                TEXT NOT NULL,
    status                TEXT NOT NULL,
    validation_status     TEXT NOT NULL,
    validation_error      TEXT NULL,
    approved_by_user_id   UUID NULL,
    rejected_by_user_id   UUID NULL,
    job_run_id            UUID NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_alias_company_ctx_status
    ON unitysearch_alias_suggestions (company_id, context, status);
CREATE INDEX IF NOT EXISTS idx_unitysearch_alias_company_ctx_norm
    ON unitysearch_alias_suggestions (company_id, context, normalized_alias);

-- -----------------------------------------------------------------------------
-- 10. unitysearch_decision_traces - optional ranking trace for debugging.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS unitysearch_decision_traces (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          UUID NOT NULL,
    user_id             UUID NULL,
    context             TEXT NOT NULL,
    entity_type         TEXT NOT NULL,
    query               TEXT NULL,
    normalized_query    TEXT NULL,
    returned_count      INTEGER NULL,
    trace_json          JSONB NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_unitysearch_traces_company_ctx_created
    ON unitysearch_decision_traces (company_id, context, created_at DESC);

-- -----------------------------------------------------------------------------
-- 11. report_usage_events - report-page interaction log.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS report_usage_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    user_id         UUID NULL,
    report_key      TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    date_range_key  TEXT NULL,
    filters_json    JSONB NULL,
    source_route    TEXT NULL,
    metadata_json   JSONB NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_report_usage_events_company_user_report_created
    ON report_usage_events (company_id, user_id, report_key, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_report_usage_events_company_report_event_created
    ON report_usage_events (company_id, report_key, event_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_report_usage_events_company_event_created
    ON report_usage_events (company_id, event_type, created_at DESC);

-- -----------------------------------------------------------------------------
-- 12. report_usage_stats - per (scope, report_key) aggregate.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS report_usage_stats (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id               UUID NOT NULL,
    scope_type               TEXT NOT NULL,
    user_id                  UUID NULL,
    report_key               TEXT NOT NULL,
    open_count               INTEGER NOT NULL DEFAULT 0,
    export_count             INTEGER NOT NULL DEFAULT 0,
    print_count              INTEGER NOT NULL DEFAULT 0,
    drilldown_count          INTEGER NOT NULL DEFAULT 0,
    filter_count             INTEGER NOT NULL DEFAULT 0,
    last_opened_at           TIMESTAMPTZ NULL,
    last_used_at             TIMESTAMPTZ NULL,
    common_date_range_key    TEXT NULL,
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_report_usage_stats_scope
    ON report_usage_stats (
        company_id,
        scope_type,
        COALESCE(user_id, '00000000-0000-0000-0000-000000000000'),
        report_key
    );

-- -----------------------------------------------------------------------------
-- 13. dashboard_user_widgets - widgets currently on a user's / company's dashboard.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS dashboard_user_widgets (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    UUID NOT NULL,
    user_id       UUID NULL,
    widget_key    TEXT NOT NULL,
    title         TEXT NULL,
    config_json   JSONB NULL,
    position      INTEGER NULL,
    source        TEXT NOT NULL,
    active        BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_dashboard_user_widgets_scope
    ON dashboard_user_widgets (
        company_id,
        COALESCE(user_id, '00000000-0000-0000-0000-000000000000'),
        widget_key
    );

-- -----------------------------------------------------------------------------
-- 14. dashboard_widget_suggestions - pending until accepted.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS dashboard_widget_suggestions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    user_id         UUID NULL,
    widget_key      TEXT NOT NULL,
    title           TEXT NOT NULL,
    reason          TEXT NOT NULL,
    evidence_json   JSONB NULL,
    confidence      NUMERIC NOT NULL DEFAULT 0,
    source          TEXT NOT NULL,
    status          TEXT NOT NULL,
    job_run_id      UUID NULL,
    accepted_at     TIMESTAMPTZ NULL,
    dismissed_at    TIMESTAMPTZ NULL,
    snoozed_until   TIMESTAMPTZ NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_dashboard_suggestions_company_user_status_created
    ON dashboard_widget_suggestions (company_id, user_id, status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_dashboard_suggestions_company_widget_status
    ON dashboard_widget_suggestions (company_id, widget_key, status);

-- -----------------------------------------------------------------------------
-- 15. action_center_tasks - homepage task list (rule-based + AI-assisted).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS action_center_tasks (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          UUID NOT NULL,
    assigned_user_id    UUID NULL,
    task_type           TEXT NOT NULL,
    source_engine       TEXT NOT NULL,
    source_type         TEXT NOT NULL,
    source_object_id    UUID NULL,
    title               TEXT NOT NULL,
    description         TEXT NULL,
    reason              TEXT NOT NULL,
    evidence_json       JSONB NULL,
    priority            TEXT NOT NULL,
    due_date            DATE NULL,
    action_url          TEXT NULL,
    status              TEXT NOT NULL,
    fingerprint         TEXT NOT NULL,
    ai_generated        BOOLEAN NOT NULL DEFAULT FALSE,
    confidence          NUMERIC NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ NULL,
    dismissed_at        TIMESTAMPTZ NULL,
    snoozed_until       TIMESTAMPTZ NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_action_center_tasks_fingerprint
    ON action_center_tasks (company_id, fingerprint);
CREATE INDEX IF NOT EXISTS idx_action_center_tasks_company_status_due
    ON action_center_tasks (company_id, status, due_date);
CREATE INDEX IF NOT EXISTS idx_action_center_tasks_company_assignee_status
    ON action_center_tasks (company_id, assigned_user_id, status);
CREATE INDEX IF NOT EXISTS idx_action_center_tasks_company_type_status
    ON action_center_tasks (company_id, task_type, status);

-- -----------------------------------------------------------------------------
-- 16. action_center_task_events - audit trail of task interactions.
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS action_center_task_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    task_id         UUID NOT NULL,
    user_id         UUID NULL,
    event_type      TEXT NOT NULL,
    metadata_json   JSONB NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_action_center_task_events_company_task_created
    ON action_center_task_events (company_id, task_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_action_center_task_events_company_user_created
    ON action_center_task_events (company_id, user_id, created_at DESC);
