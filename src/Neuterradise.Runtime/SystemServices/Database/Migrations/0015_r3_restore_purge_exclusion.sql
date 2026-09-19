-- R3 corrective authority: Profile Restore and Profile Purge are mutually exclusive.
-- One durable lifecycle must win before either operation can touch recovery material.

CREATE TRIGGER trg_profile_purge_insert_blocks_active_restore
BEFORE INSERT ON trash_entries
WHEN NEW.entity_type = 'PURGE_PROFILE'
 AND NEW.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 AND EXISTS (
    SELECT 1
    FROM trash_entries source
    WHERE source.entity_type = 'PROFILE'
      AND source.entity_id = NEW.entity_id
      AND source.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Purge cannot start while Restore is active');
END;

CREATE TRIGGER trg_profile_purge_update_blocks_active_restore
BEFORE UPDATE OF state ON trash_entries
WHEN NEW.entity_type = 'PURGE_PROFILE'
 AND NEW.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 AND EXISTS (
    SELECT 1
    FROM trash_entries source
    WHERE source.entity_type = 'PROFILE'
      AND source.entity_id = NEW.entity_id
      AND source.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Purge cannot advance while Restore is active');
END;

CREATE TRIGGER trg_profile_restore_update_blocks_active_purge
BEFORE UPDATE OF state ON trash_entries
WHEN NEW.entity_type = 'PROFILE'
 AND NEW.state IN ('RESTORE_EXECUTING','RESTORE_FINALIZING')
 AND EXISTS (
    SELECT 1
    FROM trash_entries purge
    WHERE purge.entity_type = 'PURGE_PROFILE'
      AND purge.entity_id = NEW.entity_id
      AND purge.state IN ('PURGE_PENDING','PURGE_EXECUTING','PURGE_RETRY_REQUIRED')
 )
BEGIN
    SELECT RAISE(ABORT, 'Profile Restore cannot start while Purge is active');
END;
