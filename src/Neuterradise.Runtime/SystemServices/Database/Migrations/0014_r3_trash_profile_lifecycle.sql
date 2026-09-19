-- R3 Trash/Profile lifecycle authority.
-- Freeze mutable graph edges while Trash/Purge owns their lifecycle so the persisted snapshot
-- cannot be invalidated between physical work and the terminal database transition.

CREATE TRIGGER trg_profile_relation_update_blocks_active_asset_trash
BEFORE UPDATE ON profile_assets
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id IN (OLD.asset_id, NEW.asset_id)
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot mutate Profile relations');
END;

CREATE TRIGGER trg_profile_hero_insert_blocks_active_asset_trash
BEFORE INSERT ON profiles
WHEN (NEW.cover_asset_id IS NOT NULL OR NEW.banner_asset_id IS NOT NULL)
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id IN (NEW.cover_asset_id, NEW.banner_asset_id)
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot acquire Hero references');
END;

CREATE TRIGGER trg_profile_hero_update_blocks_active_asset_trash
BEFORE UPDATE OF cover_asset_id, banner_asset_id ON profiles
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id IN (NEW.cover_asset_id, NEW.banner_asset_id)
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot acquire Hero references');
END;

CREATE TRIGGER trg_profile_relation_blocks_active_profile_trash
BEFORE INSERT ON profile_assets
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire relations');
END;

CREATE TRIGGER trg_profile_relation_update_blocks_active_profile_trash
BEFORE UPDATE ON profile_assets
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot retain or acquire mutated relations');
END;

CREATE TRIGGER trg_identity_insert_blocks_active_profile_trash
BEFORE INSERT ON identities
WHEN NEW.is_active = 1
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire an active identity');
END;

CREATE TRIGGER trg_identity_update_blocks_active_profile_trash
BEFORE UPDATE OF profile_id, is_active ON identities
WHEN NEW.is_active = 1
 AND EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PROFILE'
      AND te.entity_id = NEW.profile_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Trash lifecycle cannot acquire an active identity');
END;

CREATE TRIGGER trg_assignment_cluster_insert_blocks_active_profile_purge
BEFORE INSERT ON import_assignment_clusters
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PURGE_PROFILE'
      AND te.entity_id IN (NEW.candidate_profile_id, NEW.decided_profile_id)
      AND te.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Purge authorization cannot acquire assignment references');
END;

CREATE TRIGGER trg_assignment_cluster_update_blocks_active_profile_purge
BEFORE UPDATE OF candidate_profile_id, decided_profile_id ON import_assignment_clusters
WHEN EXISTS (
    SELECT 1 FROM trash_entries te
    WHERE te.entity_type = 'PURGE_PROFILE'
      AND te.entity_id IN (NEW.candidate_profile_id, NEW.decided_profile_id)
      AND te.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
)
BEGIN
    SELECT RAISE(ABORT, 'a Profile with active Purge authorization cannot acquire assignment references');
END;
