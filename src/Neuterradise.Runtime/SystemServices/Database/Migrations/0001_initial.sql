CREATE TABLE activity_log (
    activity_id    TEXT PRIMARY KEY,
    event_type     TEXT NOT NULL CHECK (trim(event_type) <> ''),
    profile_id     TEXT NULL,
    asset_id       TEXT NULL,
    import_unit_id TEXT NULL,
    operation_id   TEXT NULL,
    payload_json   TEXT NOT NULL DEFAULT '{}',
    occurred_at_ms INTEGER NOT NULL
);

CREATE TABLE asset_metadata (
    asset_id                TEXT PRIMARY KEY
        REFERENCES assets(asset_id) ON DELETE CASCADE,
    metadata_schema_version INTEGER NOT NULL,
    captured_at_ms          INTEGER NULL,
    width                   INTEGER NULL,
    height                  INTEGER NULL,
    duration_ms             INTEGER NULL,
    metadata_json           TEXT NOT NULL,
    updated_at_ms           INTEGER NOT NULL,
    CHECK (width IS NULL OR width > 0),
    CHECK (height IS NULL OR height > 0),
    CHECK (duration_ms IS NULL OR duration_ms >= 0)
);

CREATE TABLE assets (
    asset_id                       TEXT PRIMARY KEY,
    state                          TEXT NOT NULL
        CHECK (state IN ('CANDIDATE','ACTIVE','TRASHED','RETIRED')),
    retirement_reason              TEXT NULL,
    media_type                     TEXT NOT NULL
        CHECK (media_type IN ('IMAGE','VIDEO','MODEL')),
    is_favorite                     INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0,1)),
    bundle_sha256                   TEXT NULL CHECK (bundle_sha256 IS NULL OR (length(bundle_sha256)=64 AND bundle_sha256=lower(bundle_sha256))),
    dependency_status               TEXT NOT NULL DEFAULT 'SELF_CONTAINED' CHECK (dependency_status IN ('SELF_CONTAINED','COMPLETE','DEPENDENCIES_MISSING','DEPENDENCIES_UNKNOWN')),
    asset_storage_token            TEXT NULL UNIQUE,
    sha256                         TEXT NULL,
    byte_length                    INTEGER NULL CHECK (byte_length IS NULL OR byte_length >= 0),
    original_source_path           TEXT NULL,
    original_file_name             TEXT NULL,
    source_kind                    TEXT NULL,
    source_display_name            TEXT NULL,
    current_managed_relative_path  TEXT NULL,
    current_managed_file_name      TEXT NULL,
    target_managed_relative_path   TEXT NULL,
    target_managed_file_name       TEXT NULL,
    path_state                     TEXT NOT NULL DEFAULT 'NONE'
        CHECK (path_state IN ('NONE','PENDING','NEEDS_ATTENTION')),
    reconciliation_operation_id    TEXT NULL,
    created_at_ms                  INTEGER NOT NULL,
    added_to_library_at_ms         INTEGER NULL,
    trashed_at_ms                  INTEGER NULL,
    row_version                    INTEGER NOT NULL DEFAULT 0,
    CHECK (asset_storage_token IS NULL OR length(asset_storage_token) >= 3),
    CHECK (sha256 IS NULL OR (length(sha256) = 64 AND sha256 = lower(sha256))),
    CHECK (
        (state = 'RETIRED' AND retirement_reason IN ('SKIPPED','CANCELLED','INVALID','DEDUP_REUSED'))
        OR
        (state <> 'RETIRED' AND retirement_reason IS NULL)
    ),
    CHECK (
        (path_state = 'NONE' AND reconciliation_operation_id IS NULL)
        OR
        (path_state IN ('PENDING','NEEDS_ATTENTION') AND reconciliation_operation_id IS NOT NULL)
    )
);

CREATE TABLE categories (
    category_id     TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at_ms   INTEGER NOT NULL,
    updated_at_ms   INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 0,
    UNIQUE(normalized_name)
);

CREATE TABLE face_detections (
    face_id                    TEXT PRIMARY KEY,
    asset_id                   TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE CASCADE,
    detection_key              TEXT NOT NULL CHECK (trim(detection_key) <> ''),
    bounding_box_json          TEXT NOT NULL,
    embedding                  BLOB NULL,
    embedding_space_key        TEXT NULL,
    suggested_identity_id      TEXT NULL REFERENCES identities(identity_id),
    confirmed_identity_id      TEXT NULL REFERENCES identities(identity_id),
    confidence                 REAL NULL,
    decision_state             TEXT NOT NULL
        CHECK (decision_state IN ('UNKNOWN','SUGGESTED','CONFIRMED','REJECTED')),
    model_id                   TEXT NOT NULL CHECK (trim(model_id) <> ''),
    model_version              TEXT NOT NULL CHECK (trim(model_version) <> ''),
    created_at_ms              INTEGER NOT NULL,
    updated_at_ms              INTEGER NOT NULL,
    row_version                INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    sampled_timestamp_ms       INTEGER NULL CHECK (sampled_timestamp_ms IS NULL OR sampled_timestamp_ms >= 0),
    suggested_candidates_json  TEXT NULL,
    UNIQUE(asset_id, detection_key, model_id, model_version),
    CHECK ((embedding IS NULL) = (embedding_space_key IS NULL)),
    CHECK (embedding IS NULL OR length(embedding)=512),
    CHECK (
        (decision_state = 'CONFIRMED' AND confirmed_identity_id IS NOT NULL)
        OR
        (decision_state <> 'CONFIRMED' AND confirmed_identity_id IS NULL)
    )
);

CREATE TABLE identities (
    identity_id   TEXT PRIMARY KEY,
    profile_id    TEXT NOT NULL REFERENCES profiles(profile_id),
    is_active     INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_ms INTEGER NOT NULL,
    retired_at_ms INTEGER NULL,
    row_version   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE identity_samples (
    identity_sample_id  TEXT PRIMARY KEY,
    identity_id         TEXT NOT NULL REFERENCES identities(identity_id),
    face_id             TEXT NOT NULL REFERENCES face_detections(face_id),
    embedding           BLOB NOT NULL CHECK (length(embedding) = 512),
    embedding_space_key TEXT NOT NULL CHECK (trim(embedding_space_key) <> ''),
    model_id            TEXT NOT NULL CHECK (trim(model_id) <> ''),
    model_version       TEXT NOT NULL CHECK (trim(model_version) <> ''),
    confirmed_at_ms     INTEGER NOT NULL,
    UNIQUE(face_id)
);

CREATE TABLE import_items (
    import_item_id       TEXT PRIMARY KEY,
    import_unit_id       TEXT NOT NULL REFERENCES import_units(import_unit_id),
    candidate_asset_id   TEXT NULL UNIQUE REFERENCES assets(asset_id),
    reused_asset_id      TEXT NULL REFERENCES assets(asset_id),
    source_path          TEXT NOT NULL,
    source_file_name     TEXT NOT NULL,
    source_byte_length   INTEGER NULL CHECK (source_byte_length IS NULL OR source_byte_length >= 0),
    source_last_write_ms INTEGER NULL,
    disposition          TEXT NOT NULL DEFAULT 'INCLUDED' CHECK (disposition IN ('INCLUDED','SKIPPED','REUSED','INVALID')),
    duplicate_decision   TEXT NULL CHECK (duplicate_decision IS NULL OR duplicate_decision IN ('INCLUDE','REUSE','SKIP')),
    cleanup_policy       TEXT NOT NULL DEFAULT 'COPY' CHECK (cleanup_policy IN ('COPY','MOVE')),
    source_identity_json TEXT NULL,
    preparation_status   TEXT NOT NULL DEFAULT 'PENDING' CHECK (preparation_status IN ('PENDING','RUNNING','READY','FAILED_RETRYABLE','FAILED_TERMINAL')),
    source_cleanup_state TEXT NOT NULL DEFAULT 'SOURCE_PRESENT'
        CHECK (source_cleanup_state IN (
            'SOURCE_PRESENT',
            'DESTINATION_VERIFIED',
            'LIBRARY_COMMITTED',
            'SOURCE_DELETE_PENDING',
            'SOURCE_CONSUMED',
            'SOURCE_DELETE_FAILED',
            'SOURCE_PRESERVED',
            'SOURCE_CHANGED'
        )),
    source_cleanup_error TEXT NULL,
    created_at_ms        INTEGER NOT NULL,
    updated_at_ms        INTEGER NOT NULL,
    row_version          INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE import_sessions (
    import_session_id TEXT PRIMARY KEY,
    state             TEXT NOT NULL CHECK (state IN ('OPEN','COMPLETED','CANCELLED')),
    is_paused         INTEGER NOT NULL DEFAULT 0 CHECK (is_paused IN (0,1)),
    hidden_from_history INTEGER NOT NULL DEFAULT 0 CHECK (hidden_from_history IN (0,1)),
    created_at_ms     INTEGER NOT NULL,
    updated_at_ms     INTEGER NOT NULL,
    completed_at_ms   INTEGER NULL,
    row_version       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE import_units (
    import_unit_id           TEXT PRIMARY KEY,
    import_session_id        TEXT NOT NULL REFERENCES import_sessions(import_session_id),
    parent_import_unit_id    TEXT NULL REFERENCES import_units(import_unit_id),
    source_kind              TEXT NOT NULL,
    source_display_name      TEXT NOT NULL,
    source_path_or_reference TEXT NULL,
    state                    TEXT NOT NULL CHECK (state IN ('INTAKE','PREPARING','READY_FOR_VERIFICATION','COMMITTING','COMMITTED','COMPLETED','FAILED_RETRYABLE','FAILED_TERMINAL','CANCELLED','COMMITTED_WITH_CLEANUP_ATTENTION')),
    is_paused                INTEGER NOT NULL DEFAULT 0 CHECK (is_paused IN (0,1)),
    hidden_from_history      INTEGER NOT NULL DEFAULT 0 CHECK (hidden_from_history IN (0,1)),
    destination_kind         TEXT NULL,
    destination_profile_id   TEXT NULL REFERENCES profiles(profile_id),
    verification_step        INTEGER NOT NULL DEFAULT 1 CHECK (verification_step BETWEEN 1 AND 5),
    verification_draft_json  TEXT NOT NULL DEFAULT '{}',
    verification_version     INTEGER NOT NULL DEFAULT 0,
    commit_operation_id      TEXT NULL,
    library_commit_state     TEXT NOT NULL DEFAULT 'NOT_COMMITTED',
    created_at_ms            INTEGER NOT NULL,
    updated_at_ms            INTEGER NOT NULL,
    completed_at_ms          INTEGER NULL,
    row_version              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE job_dependencies (
    job_id            TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
    depends_on_job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
    PRIMARY KEY (job_id, depends_on_job_id),
    CHECK (job_id <> depends_on_job_id)
);

CREATE TABLE jobs (
    job_id             TEXT PRIMARY KEY,
    kind               TEXT NOT NULL CHECK (trim(kind) <> ''),
    lane               TEXT NOT NULL
        CHECK (lane IN ('FAST','IO','CPU','MEDIA','FACE')),
    state              TEXT NOT NULL
        CHECK (state IN (
            'PENDING',
            'RUNNABLE',
            'RUNNING',
            'PAUSED',
            'SUCCEEDED',
            'FAILED_RETRYABLE',
            'FAILED_TERMINAL',
            'CANCELLED'
        )),
    priority           INTEGER NOT NULL DEFAULT 50,
    owner_type         TEXT NOT NULL CHECK (trim(owner_type) <> ''),
    owner_id           TEXT NOT NULL,
    attempt            INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    max_attempts       INTEGER NOT NULL DEFAULT 5 CHECK (max_attempts > 0),
    not_before_ms      INTEGER NULL,
    progress_completed INTEGER NULL CHECK (progress_completed IS NULL OR progress_completed >= 0),
    progress_total     INTEGER NULL CHECK (progress_total IS NULL OR progress_total >= 0),
    stage              TEXT NULL,
    checkpoint_json    TEXT NOT NULL DEFAULT '{}',
    error_code         TEXT NULL,
    error_detail_safe  TEXT NULL,
    created_at_ms      INTEGER NOT NULL,
    started_at_ms      INTEGER NULL,
    completed_at_ms    INTEGER NULL,
    row_version        INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    CHECK (attempt <= max_attempts),
    CHECK (
        progress_completed IS NULL
        OR progress_total IS NULL
        OR progress_completed <= progress_total
    )
);

CREATE TABLE profile_appearance (
    profile_id       TEXT PRIMARY KEY
        REFERENCES profiles(profile_id) ON DELETE CASCADE,
    schema_version   INTEGER NOT NULL,
    layout_preset_id TEXT NULL,
    overrides_json   TEXT NOT NULL,
    updated_at_ms    INTEGER NOT NULL,
    row_version      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE profile_assets (
    profile_id     TEXT NOT NULL REFERENCES profiles(profile_id),
    asset_id       TEXT NOT NULL REFERENCES assets(asset_id),
    relation_type  TEXT NOT NULL
        CHECK (relation_type IN ('OWNER','APPEARS','MANUAL')),
    provenance_key TEXT NULL,
    created_at_ms  INTEGER NOT NULL,
    PRIMARY KEY (profile_id, asset_id, relation_type)
);

CREATE TABLE profile_tags (
    profile_id    TEXT NOT NULL REFERENCES profiles(profile_id) ON DELETE CASCADE,
    tag_id        TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
    created_at_ms INTEGER NOT NULL,
    PRIMARY KEY (profile_id, tag_id)
);

CREATE TABLE profiles (
    profile_id                    TEXT PRIMARY KEY,
    kind                          TEXT NOT NULL
        CHECK (kind IN ('NORMAL','UNKNOWN')),
    display_name                  TEXT NULL,
    unknown_sequence              INTEGER NULL UNIQUE,
    category_id                   TEXT NULL REFERENCES categories(category_id),
    rating                        INTEGER NULL CHECK (rating IS NULL OR rating BETWEEN 0 AND 5),
    is_favorite                   INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0,1)),
    overview                      TEXT NULL,
    notes                         TEXT NULL,
    profile_storage_token         TEXT NULL UNIQUE,
    current_managed_relative_path TEXT NULL,
    target_managed_relative_path  TEXT NULL,
    path_state                    TEXT NOT NULL DEFAULT 'NONE'
        CHECK (path_state IN ('NONE','PENDING','NEEDS_ATTENTION')),
    reconciliation_operation_id   TEXT NULL,
    cover_asset_id                TEXT NULL REFERENCES assets(asset_id),
    banner_asset_id               TEXT NULL REFERENCES assets(asset_id),
    created_at_ms                 INTEGER NOT NULL,
    updated_at_ms                 INTEGER NOT NULL,
    trashed_at_ms                 INTEGER NULL,
    row_version                   INTEGER NOT NULL DEFAULT 0,
    CHECK (
        (kind = 'NORMAL' AND display_name IS NOT NULL AND trim(display_name) <> '' AND unknown_sequence IS NULL)
        OR
        (kind = 'UNKNOWN' AND display_name IS NULL AND unknown_sequence IS NOT NULL AND unknown_sequence > 0)
    ),
    CHECK (
        (path_state = 'NONE' AND reconciliation_operation_id IS NULL)
        OR
        (path_state IN ('PENDING','NEEDS_ATTENTION') AND reconciliation_operation_id IS NOT NULL)
    )
);

CREATE TABLE related_profile_evidence (
    evidence_key    TEXT PRIMARY KEY,
    profile_id_low  TEXT NOT NULL REFERENCES profiles(profile_id),
    profile_id_high TEXT NOT NULL REFERENCES profiles(profile_id),
    evidence_type   TEXT NOT NULL
        CHECK (evidence_type IN ('SHARED_ASSET','CONFIRMED_FACE','MANUAL')),
    asset_id        TEXT NULL REFERENCES assets(asset_id),
    face_id         TEXT NULL REFERENCES face_detections(face_id),
    created_at_ms   INTEGER NOT NULL,
    CHECK (profile_id_low < profile_id_high),
    CHECK (
        (evidence_type = 'SHARED_ASSET' AND asset_id IS NOT NULL AND face_id IS NULL)
        OR
        (evidence_type = 'CONFIRMED_FACE' AND asset_id IS NOT NULL AND face_id IS NOT NULL)
        OR
        (evidence_type = 'MANUAL' AND asset_id IS NULL AND face_id IS NULL)
    )
);

CREATE TABLE related_profile_summary (
    profile_id_low       TEXT NOT NULL REFERENCES profiles(profile_id),
    profile_id_high      TEXT NOT NULL REFERENCES profiles(profile_id),
    shared_asset_count   INTEGER NOT NULL DEFAULT 0 CHECK (shared_asset_count >= 0),
    confirmed_face_count INTEGER NOT NULL DEFAULT 0 CHECK (confirmed_face_count >= 0),
    manual_relation      INTEGER NOT NULL DEFAULT 0 CHECK (manual_relation IN (0,1)),
    last_evidence_at_ms  INTEGER NULL,
    rank_score           INTEGER NOT NULL DEFAULT 0 CHECK (rank_score >= 0),
    updated_at_ms        INTEGER NOT NULL,
    PRIMARY KEY (profile_id_low, profile_id_high),
    CHECK (profile_id_low < profile_id_high)
);

CREATE TABLE sequences (
    name       TEXT PRIMARY KEY,
    next_value INTEGER NOT NULL CHECK (next_value > 0)
);

CREATE TABLE settings (
    key           TEXT PRIMARY KEY,
    value_json    TEXT NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE TABLE storage_operations (
    operation_id      TEXT PRIMARY KEY,
    kind              TEXT NOT NULL,
    entity_type       TEXT NOT NULL,
    entity_id         TEXT NOT NULL,
    state             TEXT NOT NULL,
    checkpoint_json   TEXT NOT NULL,
    created_at_ms     INTEGER NOT NULL,
    updated_at_ms     INTEGER NOT NULL,
    completed_at_ms   INTEGER NULL,
    error_code        TEXT NULL,
    error_detail_safe TEXT NULL,
    row_version       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE tags (
    tag_id          TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at_ms   INTEGER NOT NULL,
    updated_at_ms   INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 0,
    UNIQUE(normalized_name)
);

CREATE TABLE trash_entries (
    trash_entry_id         TEXT PRIMARY KEY,
    entity_type            TEXT NOT NULL CHECK (trim(entity_type) <> ''),
    entity_id              TEXT NOT NULL,
    state                  TEXT NOT NULL CHECK (trim(state) <> ''),
    recovery_relative_path TEXT NULL,
    plan_json              TEXT NOT NULL,
    created_at_ms          INTEGER NOT NULL,
    updated_at_ms          INTEGER NOT NULL,
    completed_at_ms        INTEGER NULL,
    row_version            INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0)
);

CREATE INDEX ix_activity_log_asset
ON activity_log(asset_id, occurred_at_ms DESC)
WHERE asset_id IS NOT NULL;

CREATE INDEX ix_activity_log_profile
ON activity_log(profile_id, occurred_at_ms DESC)
WHERE profile_id IS NOT NULL;

CREATE INDEX ix_activity_log_time
ON activity_log(occurred_at_ms DESC, activity_id);

CREATE INDEX ix_assets_current_path
ON assets(current_managed_relative_path, current_managed_file_name)
WHERE current_managed_relative_path IS NOT NULL;

CREATE INDEX ix_assets_state
ON assets(state, added_to_library_at_ms, asset_id);

CREATE INDEX ix_assets_state_type
ON assets(state, media_type, added_to_library_at_ms DESC, asset_id);

CREATE INDEX ix_assets_favorite
ON assets(is_favorite, state, added_to_library_at_ms DESC, asset_id);

CREATE INDEX ix_face_detections_asset_decision
ON face_detections(asset_id, decision_state, face_id);

CREATE INDEX ix_face_detections_decision
ON face_detections(decision_state, created_at_ms DESC, face_id);

CREATE INDEX ix_identity_samples_identity_space
ON identity_samples(identity_id, embedding_space_key);

CREATE INDEX ix_import_items_unit
ON import_items(import_unit_id, disposition, import_item_id);

CREATE INDEX ix_import_units_session_state
ON import_units(import_session_id, state, import_unit_id);

CREATE INDEX ix_jobs_owner
ON jobs(owner_type, owner_id, state);

CREATE INDEX ix_jobs_runnable
ON jobs(state, lane, priority DESC, not_before_ms, created_at_ms);

CREATE INDEX ix_profile_assets_asset_relation
ON profile_assets(asset_id, relation_type, profile_id);

CREATE INDEX ix_profile_assets_profile_relation
ON profile_assets(profile_id, relation_type, asset_id);

CREATE INDEX ix_profile_tags_tag
ON profile_tags(tag_id, profile_id);

CREATE INDEX ix_profiles_active_name
ON profiles(trashed_at_ms, display_name, profile_id);

CREATE INDEX ix_profiles_active_rating
ON profiles(trashed_at_ms, rating DESC, updated_at_ms DESC, profile_id);

CREATE INDEX ix_profiles_active_updated
ON profiles(trashed_at_ms, updated_at_ms DESC, profile_id);

CREATE INDEX ix_profiles_category
ON profiles(category_id, trashed_at_ms, profile_id)
WHERE category_id IS NOT NULL;

CREATE INDEX ix_profiles_favorite
ON profiles(is_favorite, trashed_at_ms, updated_at_ms DESC, profile_id);

CREATE INDEX ix_related_evidence_pair
ON related_profile_evidence(profile_id_low, profile_id_high, evidence_type);

CREATE INDEX ix_related_summary_high
ON related_profile_summary(profile_id_high, profile_id_low);

CREATE INDEX ix_storage_operations_entity
ON storage_operations(entity_type, entity_id, state);

CREATE INDEX ix_trash_entries_entity
ON trash_entries(entity_type, entity_id, state);

CREATE INDEX ix_trash_entries_state
ON trash_entries(state, created_at_ms DESC, trash_entry_id);

CREATE UNIQUE INDEX ux_identities_one_active_per_profile
ON identities(profile_id)
WHERE is_active = 1;

CREATE UNIQUE INDEX ux_profile_assets_one_owner
ON profile_assets(asset_id)
WHERE relation_type = 'OWNER';

CREATE TRIGGER trg_assets_nonactive_has_no_owner
BEFORE UPDATE OF state ON assets
WHEN NEW.state <> 'ACTIVE'
 AND EXISTS (
     SELECT 1 FROM profile_assets
     WHERE asset_id = OLD.asset_id AND relation_type = 'OWNER'
 )
BEGIN
    SELECT RAISE(ABORT, 'a non-ACTIVE asset cannot retain an OWNER');
END;

CREATE TRIGGER trg_assets_storage_token_immutable
BEFORE UPDATE OF asset_storage_token ON assets
WHEN OLD.asset_storage_token IS NOT NULL AND NEW.asset_storage_token IS NOT OLD.asset_storage_token
BEGIN
    SELECT RAISE(ABORT, 'asset storage token is immutable');
END;

CREATE TRIGGER trg_identities_active_normal_only
BEFORE INSERT ON identities
WHEN NEW.is_active = 1 AND NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL' AND trashed_at_ms IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'only a NORMAL profile may have an active identity');
END;

CREATE TRIGGER trg_profile_assets_owner_requires_active
BEFORE INSERT ON profile_assets
WHEN NEW.relation_type = 'OWNER'
 AND (SELECT state FROM assets WHERE asset_id = NEW.asset_id) <> 'ACTIVE'
BEGIN
    SELECT RAISE(ABORT, 'only an ACTIVE asset may have an OWNER');
END;

CREATE TRIGGER trg_profiles_storage_token_immutable
BEFORE UPDATE OF profile_storage_token ON profiles
WHEN OLD.profile_storage_token IS NOT NULL AND NEW.profile_storage_token IS NOT OLD.profile_storage_token
BEGIN
    SELECT RAISE(ABORT, 'profile storage token is immutable');
END;

CREATE TRIGGER trg_profiles_unknown_has_no_active_identity
BEFORE UPDATE OF kind ON profiles
WHEN NEW.kind = 'UNKNOWN'
 AND EXISTS (SELECT 1 FROM identities WHERE profile_id = OLD.profile_id AND is_active = 1)
BEGIN
    SELECT RAISE(ABORT, 'an UNKNOWN profile cannot have an active identity');
END;

CREATE TABLE asset_components (
    asset_id TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE CASCADE,
    component_relative_path TEXT NOT NULL CHECK (trim(component_relative_path) <> ''),
    normalized_component_path TEXT NOT NULL CHECK (trim(normalized_component_path) <> ''),
    component_role TEXT NOT NULL CHECK (component_role IN ('PRIMARY','DEPENDENCY')),
    sha256 TEXT NOT NULL CHECK (length(sha256)=64 AND sha256=lower(sha256)),
    byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
    original_source_path TEXT NULL,
    source_identity_json TEXT NULL,
    source_cleanup_state TEXT NOT NULL DEFAULT 'SOURCE_PRESENT' CHECK (source_cleanup_state IN ('SOURCE_PRESENT','DESTINATION_VERIFIED','LIBRARY_COMMITTED','SOURCE_DELETE_PENDING','SOURCE_CONSUMED','SOURCE_DELETE_FAILED','SOURCE_PRESERVED','SOURCE_CHANGED')),
    source_cleanup_error TEXT NULL,
    row_version INTEGER NOT NULL DEFAULT 0 CHECK (row_version >= 0),
    PRIMARY KEY (asset_id, component_relative_path),
    UNIQUE(asset_id, normalized_component_path)
);
CREATE UNIQUE INDEX ux_asset_components_primary ON asset_components(asset_id)
WHERE component_role='PRIMARY';

CREATE TABLE operation_receipts (
    operation_id TEXT PRIMARY KEY,
    operation_kind TEXT NOT NULL CHECK (trim(operation_kind) <> ''),
    target_id TEXT NOT NULL,
    request_hash TEXT NOT NULL CHECK (length(request_hash)=64 AND request_hash=lower(request_hash)),
    result_json TEXT NOT NULL,
    committed_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_operation_receipts_target ON operation_receipts(target_id, committed_at_ms DESC);

CREATE TABLE view_state (
    context_key TEXT PRIMARY KEY,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    state_json TEXT NOT NULL,
    updated_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_view_state_recency ON view_state(updated_at_ms DESC, context_key);

CREATE TRIGGER trg_profile_assets_update_owner_requires_active
BEFORE UPDATE OF asset_id, relation_type ON profile_assets
WHEN NEW.relation_type='OWNER'
 AND NOT EXISTS (SELECT 1 FROM assets WHERE asset_id=NEW.asset_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'only an ACTIVE asset may have an OWNER'); END;

CREATE TRIGGER trg_profile_assets_active_profile_insert
BEFORE INSERT ON profile_assets
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active relations require a nontrashed profile'); END;

CREATE TRIGGER trg_profile_assets_active_profile_update
BEFORE UPDATE OF profile_id ON profile_assets
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active relations require a nontrashed profile'); END;

CREATE TRIGGER trg_identities_update_active_normal_only
BEFORE UPDATE OF profile_id,is_active ON identities
WHEN NEW.is_active=1
 AND NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL' AND trashed_at_ms IS NULL)
BEGIN SELECT RAISE(ABORT,'active identity requires active NORMAL profile'); END;

CREATE TRIGGER trg_profiles_cover_active_update
BEFORE UPDATE OF cover_asset_id ON profiles
WHEN NEW.cover_asset_id IS NOT NULL
 AND NOT EXISTS (SELECT 1 FROM assets WHERE asset_id=NEW.cover_asset_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO'))
BEGIN SELECT RAISE(ABORT,'Cover requires active image or video'); END;

CREATE TRIGGER trg_profiles_banner_active_update
BEFORE UPDATE OF banner_asset_id ON profiles
WHEN NEW.banner_asset_id IS NOT NULL
 AND NOT EXISTS (SELECT 1 FROM assets WHERE asset_id=NEW.banner_asset_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO'))
BEGIN SELECT RAISE(ABORT,'Banner requires active image or video'); END;

CREATE TRIGGER trg_assets_nonactive_has_no_hero_reference
BEFORE UPDATE OF state ON assets
WHEN NEW.state<>'ACTIVE'
 AND EXISTS (SELECT 1 FROM profiles WHERE cover_asset_id=OLD.asset_id OR banner_asset_id=OLD.asset_id)
BEGIN SELECT RAISE(ABORT,'clear Cover/Banner references before asset leaves ACTIVE'); END;

CREATE TRIGGER trg_profiles_hero_active_insert
BEFORE INSERT ON profiles
WHEN (NEW.cover_asset_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM assets WHERE asset_id=NEW.cover_asset_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO')))
 OR (NEW.banner_asset_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM assets WHERE asset_id=NEW.banner_asset_id AND state='ACTIVE' AND media_type IN ('IMAGE','VIDEO')))
BEGIN SELECT RAISE(ABORT,'new Profile Hero requires active image or video'); END;

CREATE TRIGGER trg_profiles_unknown_hero_insert
BEFORE INSERT ON profiles
WHEN NEW.kind='UNKNOWN' AND (NEW.cover_asset_id IS NOT NULL OR NEW.banner_asset_id IS NOT NULL)
BEGIN SELECT RAISE(ABORT,'UNKNOWN profile uses system Hero'); END;

CREATE TRIGGER trg_profiles_unknown_hero_update
BEFORE UPDATE OF kind,cover_asset_id,banner_asset_id ON profiles
WHEN NEW.kind='UNKNOWN' AND (NEW.cover_asset_id IS NOT NULL OR NEW.banner_asset_id IS NOT NULL)
BEGIN SELECT RAISE(ABORT,'UNKNOWN profile uses system Hero'); END;

CREATE TRIGGER trg_profile_appearance_normal_insert
BEFORE INSERT ON profile_appearance
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL')
BEGIN SELECT RAISE(ABORT,'appearance belongs to NORMAL profile'); END;

CREATE TRIGGER trg_profile_appearance_normal_update
BEFORE UPDATE OF profile_id ON profile_appearance
WHEN NOT EXISTS (SELECT 1 FROM profiles WHERE profile_id=NEW.profile_id AND kind='NORMAL')
BEGIN SELECT RAISE(ABORT,'appearance belongs to NORMAL profile'); END;

CREATE TRIGGER trg_profile_assets_active_asset_insert
BEFORE INSERT ON profile_assets
WHEN NOT EXISTS (SELECT 1 FROM assets WHERE asset_id=NEW.asset_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'Profile relation requires ACTIVE asset'); END;

CREATE TRIGGER trg_profile_assets_active_asset_update
BEFORE UPDATE OF asset_id,relation_type ON profile_assets
WHEN NOT EXISTS (SELECT 1 FROM assets WHERE asset_id=NEW.asset_id AND state='ACTIVE')
BEGIN SELECT RAISE(ABORT,'Profile relation requires ACTIVE asset'); END;

CREATE TRIGGER trg_assets_nonactive_has_no_relations
BEFORE UPDATE OF state ON assets
WHEN NEW.state<>'ACTIVE' AND EXISTS (SELECT 1 FROM profile_assets WHERE asset_id=OLD.asset_id)
BEGIN SELECT RAISE(ABORT,'remove active relations before deactivating asset'); END;

CREATE TRIGGER trg_profiles_trash_has_no_active_relations
BEFORE UPDATE OF trashed_at_ms ON profiles
WHEN NEW.trashed_at_ms IS NOT NULL AND (
    EXISTS (SELECT 1 FROM profile_assets WHERE profile_id=OLD.profile_id)
    OR EXISTS (SELECT 1 FROM identities WHERE profile_id=OLD.profile_id AND is_active=1))
BEGIN SELECT RAISE(ABORT,'resolve Profile relations and identity before trash'); END;
