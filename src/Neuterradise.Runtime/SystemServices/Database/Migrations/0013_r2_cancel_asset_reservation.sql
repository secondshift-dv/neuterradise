-- R2 cancellation rollback reservation authority.
-- A cancellation obtains this reservation only after proving the Asset has no competing live
-- import consumer or published/external Profile relation. The reservation then closes the TOCTOU
-- window until rollback_settled releases it.

CREATE TABLE import_cancel_asset_reservations (
    import_unit_id TEXT NOT NULL REFERENCES import_units(import_unit_id) ON DELETE CASCADE,
    asset_id       TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE CASCADE,
    created_at_ms  INTEGER NOT NULL,
    PRIMARY KEY (import_unit_id, asset_id)
);

CREATE UNIQUE INDEX ux_import_cancel_asset_reservations_asset
ON import_cancel_asset_reservations(asset_id);

CREATE TRIGGER trg_reserved_asset_blocks_import_interest
BEFORE INSERT ON import_asset_interests
WHEN EXISTS (
    SELECT 1
    FROM import_cancel_asset_reservations reservation
    WHERE reservation.asset_id = NEW.asset_id
)
BEGIN
    SELECT RAISE(ABORT, 'an asset reserved for cancellation rollback cannot acquire import interest');
END;

CREATE TRIGGER trg_reserved_asset_blocks_import_reuse_insert
BEFORE INSERT ON import_items
WHEN NEW.reused_asset_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM import_cancel_asset_reservations reservation
    WHERE reservation.asset_id = NEW.reused_asset_id
 )
BEGIN
    SELECT RAISE(ABORT, 'an asset reserved for cancellation rollback cannot be selected for REUSE');
END;

CREATE TRIGGER trg_reserved_asset_blocks_import_reuse_update
BEFORE UPDATE OF reused_asset_id ON import_items
WHEN NEW.reused_asset_id IS NOT NULL
 AND EXISTS (
    SELECT 1
    FROM import_cancel_asset_reservations reservation
    WHERE reservation.asset_id = NEW.reused_asset_id
 )
BEGIN
    SELECT RAISE(ABORT, 'an asset reserved for cancellation rollback cannot be selected for REUSE');
END;

CREATE TRIGGER trg_reserved_asset_blocks_profile_relation
BEFORE INSERT ON profile_assets
WHEN EXISTS (
    SELECT 1
    FROM import_cancel_asset_reservations reservation
    WHERE reservation.asset_id = NEW.asset_id
)
BEGIN
    SELECT RAISE(ABORT, 'an asset reserved for cancellation rollback cannot acquire a Profile relation');
END;
