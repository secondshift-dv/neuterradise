-- Stage 2 capability readiness tracking.
-- Each asset has one row per applicable capability. Stage 2 is complete when every
-- required/applicable capability reaches a terminal state (READY or NOT_APPLICABLE).

CREATE TABLE IF NOT EXISTS asset_capability_readiness (
    asset_id        TEXT NOT NULL,
    capability      TEXT NOT NULL,
    state           TEXT NOT NULL DEFAULT 'QUEUED'
                    CHECK (state IN ('QUEUED', 'PROCESSING', 'READY', 'NOT_APPLICABLE', 'FAILED')),
    job_id          TEXT,
    source_fingerprint TEXT,
    updated_at_ms   INTEGER NOT NULL,
    PRIMARY KEY (asset_id, capability)
);

CREATE INDEX IF NOT EXISTS ix_asset_capability_state
ON asset_capability_readiness(state, asset_id);

-- Add stage_2_started_at_ms to import_units so resume can distinguish:
--   NULL   = Stage 2 never started
--   NOT NULL = Stage 2 was scheduled (may be incomplete)
ALTER TABLE import_units ADD COLUMN stage_2_started_at_ms INTEGER;
