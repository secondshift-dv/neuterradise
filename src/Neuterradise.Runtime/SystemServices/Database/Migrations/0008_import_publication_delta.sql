-- Stage 1 makes canonical media authoritative before Verify/Save, but profile projection must not
-- expose that delta until the publication boundary. NULL means the relation is published; a Unit id
-- means the relation belongs to that unpublished import delta.
ALTER TABLE profile_assets
ADD COLUMN publication_import_unit_id TEXT NULL REFERENCES import_units(import_unit_id);

CREATE INDEX ix_profile_assets_publication_import_unit
ON profile_assets(publication_import_unit_id)
WHERE publication_import_unit_id IS NOT NULL;

-- OWNER rows are created by Candidate activation, and reused MANUAL rows are created by the media
-- association authority. Mark them at insert time so there is no observable window where an
-- existing Profile sees Stage-1 media before Save.
CREATE TRIGGER trg_profile_assets_mark_import_delta
AFTER INSERT ON profile_assets
WHEN NEW.publication_import_unit_id IS NULL
BEGIN
    UPDATE profile_assets
    SET publication_import_unit_id = (
        SELECT u.import_unit_id
        FROM import_units u
        JOIN import_items i ON i.import_unit_id = u.import_unit_id
        WHERE u.destination_profile_id = NEW.profile_id
          AND u.state NOT IN (
              'CANCELLED','COMMITTED','COMPLETED',
              'COMMITTED_WITH_CLEANUP_ATTENTION','FAILED_TERMINAL')
          AND (
              (NEW.relation_type = 'OWNER' AND i.candidate_asset_id = NEW.asset_id)
              OR
              (NEW.relation_type = 'MANUAL'
               AND i.reused_asset_id = NEW.asset_id
               AND lower(COALESCE(NEW.provenance_key, '')) = 'import:' || lower(u.import_unit_id))
          )
        ORDER BY u.created_at_ms DESC, u.import_unit_id DESC
        LIMIT 1
    )
    WHERE profile_id = NEW.profile_id
      AND asset_id = NEW.asset_id
      AND relation_type = NEW.relation_type
      AND publication_import_unit_id IS NULL;
END;

-- Converge any pre-0008 Stage-1 units that were still unpublished when this migration is installed.
UPDATE profile_assets
SET publication_import_unit_id = (
    SELECT u.import_unit_id
    FROM import_units u
    JOIN import_items i ON i.import_unit_id = u.import_unit_id
    WHERE u.destination_profile_id = profile_assets.profile_id
      AND u.state IN ('PREPARING','READY_FOR_VERIFICATION','COMMITTING')
      AND (
          (profile_assets.relation_type = 'OWNER' AND i.candidate_asset_id = profile_assets.asset_id)
          OR
          (profile_assets.relation_type = 'MANUAL'
           AND i.reused_asset_id = profile_assets.asset_id
           AND lower(COALESCE(profile_assets.provenance_key, '')) = 'import:' || lower(u.import_unit_id))
      )
    ORDER BY u.created_at_ms DESC, u.import_unit_id DESC
    LIMIT 1
)
WHERE publication_import_unit_id IS NULL
  AND EXISTS (
      SELECT 1
      FROM import_units u
      JOIN import_items i ON i.import_unit_id = u.import_unit_id
      WHERE u.destination_profile_id = profile_assets.profile_id
        AND u.state IN ('PREPARING','READY_FOR_VERIFICATION','COMMITTING')
        AND (
            (profile_assets.relation_type = 'OWNER' AND i.candidate_asset_id = profile_assets.asset_id)
            OR
            (profile_assets.relation_type = 'MANUAL'
             AND i.reused_asset_id = profile_assets.asset_id
             AND lower(COALESCE(profile_assets.provenance_key, '')) = 'import:' || lower(u.import_unit_id))
        )
  );
