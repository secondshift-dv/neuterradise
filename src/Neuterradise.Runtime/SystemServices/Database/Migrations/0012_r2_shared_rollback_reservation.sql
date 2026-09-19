-- R2 shared-lifetime rollback reservation guards.
-- Once an Asset Trash entry is PENDING/EXECUTING, new consumers may not attach to that Asset.
-- Existing consumers are revalidated by cancellation rollback before physical movement.

CREATE TRIGGER trg_import_interest_blocks_active_asset_trash
BEFORE INSERT ON import_asset_interests
WHEN EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id = NEW.asset_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot acquire import interest');
END;

CREATE TRIGGER trg_import_reuse_blocks_active_asset_trash
BEFORE UPDATE OF reused_asset_id ON import_items
WHEN NEW.reused_asset_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id = NEW.reused_asset_id
      AND te.state IN ('PENDING','EXECUTING')
 )
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot be selected for REUSE');
END;

CREATE TRIGGER trg_profile_relation_blocks_active_asset_trash
BEFORE INSERT ON profile_assets
WHEN EXISTS (
    SELECT 1
    FROM trash_entries te
    WHERE te.entity_type = 'ASSET'
      AND te.entity_id = NEW.asset_id
      AND te.state IN ('PENDING','EXECUTING')
)
BEGIN
    SELECT RAISE(ABORT, 'an asset with active Trash reservation cannot acquire a Profile relation');
END;
