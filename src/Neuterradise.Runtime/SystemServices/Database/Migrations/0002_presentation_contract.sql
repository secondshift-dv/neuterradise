-- Presentation Contract v1: generic, versioned presentation selection/state.
-- Additive only. One row per (scope, slot) instead of one column per visual option.
-- Presentation data never owns domain data: no foreign keys into profiles/assets, so removing a
-- binding or a pack can never cascade into Profile, Media or Vault bytes.

CREATE TABLE presentation_packs (
    pack_id          TEXT PRIMARY KEY,
    version          TEXT NOT NULL,
    name             TEXT NOT NULL,
    origin           TEXT NOT NULL CHECK (origin IN ('BUILTIN','USER')),
    content_hash     TEXT NOT NULL,
    contract_version INTEGER NOT NULL CHECK (contract_version > 0),
    state            TEXT NOT NULL CHECK (state IN ('ACTIVE','INVALID')),
    diagnostics_json TEXT NULL,
    installed_at_ms  INTEGER NOT NULL,
    updated_at_ms    INTEGER NOT NULL
);

CREATE TABLE presentation_bindings (
    scope_kind         TEXT NOT NULL CHECK (scope_kind IN ('GLOBAL','SURFACE','PROFILE','ITEM')),
    scope_id           TEXT NOT NULL CHECK (trim(scope_id) <> ''),
    slot               TEXT NOT NULL CHECK (trim(slot) <> ''),
    definition_pack_id TEXT NULL,
    definition_id      TEXT NULL,
    state_json         TEXT NULL,
    schema_version     INTEGER NOT NULL CHECK (schema_version > 0),
    updated_at_ms      INTEGER NOT NULL,
    row_version        INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope_kind, scope_id, slot),
    CHECK ((definition_pack_id IS NULL) = (definition_id IS NULL)),
    CHECK (definition_id IS NOT NULL OR state_json IS NOT NULL)
);

CREATE INDEX ix_presentation_bindings_definition
ON presentation_bindings(definition_pack_id, definition_id);
