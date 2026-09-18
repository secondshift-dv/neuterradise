DELETE FROM asset_capability_readiness
WHERE NOT EXISTS (
    SELECT 1
    FROM assets
    WHERE assets.asset_id = asset_capability_readiness.asset_id
);
