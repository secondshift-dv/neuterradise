-- R2 storage/import durability authority.
ALTER TABLE assets
ADD COLUMN dependency_discovery_state TEXT NOT NULL DEFAULT 'COMPLETE'
    CHECK (dependency_discovery_state IN (
        'COMPLETE',
        'MISSING_DEPENDENCIES',
        'UNKNOWN',
        'FAILED_RETRYABLE',
        'FAILED_TERMINAL',
        'UNSUPPORTED'
    ));

UPDATE assets
SET dependency_discovery_state = CASE dependency_status
    WHEN 'DEPENDENCIES_MISSING' THEN 'MISSING_DEPENDENCIES'
    WHEN 'DEPENDENCIES_UNKNOWN' THEN 'UNKNOWN'
    ELSE 'COMPLETE'
END;

CREATE TABLE import_asset_interests (
    import_unit_id   TEXT NOT NULL REFERENCES import_units(import_unit_id) ON DELETE CASCADE,
    asset_id         TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE CASCADE,
    desired_priority INTEGER NOT NULL DEFAULT 50 CHECK (desired_priority BETWEEN 0 AND 100),
    created_at_ms    INTEGER NOT NULL,
    updated_at_ms    INTEGER NOT NULL,
    PRIMARY KEY (import_unit_id, asset_id)
);

CREATE INDEX ix_import_asset_interests_asset
ON import_asset_interests(asset_id, import_unit_id);
