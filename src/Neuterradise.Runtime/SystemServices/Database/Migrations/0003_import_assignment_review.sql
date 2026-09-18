-- Durable grouped exceptions produced by the central import-assignment policy.
-- The cluster stores evidence, not ownership certainty.  Ownership remains authoritative in
-- profile_assets and is changed only by the existing Unknown resolution operation.

CREATE TABLE import_assignment_clusters (
    cluster_id            TEXT PRIMARY KEY,
    import_unit_id        TEXT NOT NULL REFERENCES import_units(import_unit_id),
    cluster_key           TEXT NOT NULL,
    source_directory      TEXT NULL,
    candidate_profile_id  TEXT NULL REFERENCES profiles(profile_id),
    evidence_kind         TEXT NOT NULL
        CHECK (evidence_kind IN ('HEURISTIC_CANDIDATE','CONFLICTING','INSUFFICIENT')),
    state                 TEXT NOT NULL DEFAULT 'PENDING'
        CHECK (state IN ('PENDING','ACCEPTED','KEPT_UNKNOWN')),
    decided_profile_id    TEXT NULL REFERENCES profiles(profile_id),
    created_at_ms         INTEGER NOT NULL,
    updated_at_ms         INTEGER NOT NULL,
    row_version           INTEGER NOT NULL DEFAULT 0,
    UNIQUE(import_unit_id, cluster_key),
    CHECK (
        (state = 'PENDING' AND decided_profile_id IS NULL)
        OR (state = 'ACCEPTED' AND decided_profile_id IS NOT NULL)
        OR (state = 'KEPT_UNKNOWN' AND decided_profile_id IS NULL)
    )
);

CREATE TABLE import_assignment_cluster_items (
    cluster_id      TEXT NOT NULL REFERENCES import_assignment_clusters(cluster_id) ON DELETE CASCADE,
    import_item_id  TEXT NOT NULL REFERENCES import_items(import_item_id),
    PRIMARY KEY (cluster_id, import_item_id)
);

CREATE INDEX ix_import_assignment_clusters_pending
ON import_assignment_clusters(state, import_unit_id, created_at_ms, cluster_id);

CREATE INDEX ix_import_assignment_cluster_items_item
ON import_assignment_cluster_items(import_item_id, cluster_id);
