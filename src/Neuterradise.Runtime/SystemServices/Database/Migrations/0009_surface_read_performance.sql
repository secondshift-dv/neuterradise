-- I01: Indexes to support Gallery, Home, Profile and MediaDetail surface read paths.

-- I01.1 Published Profile recency (Gallery/Home ordering by updated_at_ms)
CREATE INDEX IF NOT EXISTS ix_profiles_published_updated
    ON profiles (updated_at_ms DESC, profile_id DESC)
    WHERE trashed_at_ms IS NULL AND visibility = 'PUBLISHED';

-- I01.2 Published rating order (Gallery rating sort)
CREATE INDEX IF NOT EXISTS ix_profiles_published_rating
    ON profiles (rating DESC, updated_at_ms DESC, profile_id DESC)
    WHERE trashed_at_ms IS NULL AND visibility = 'PUBLISHED';

-- I01.3 Public Profile -> Asset relations (publication boundary filtered)
CREATE INDEX IF NOT EXISTS ix_profile_assets_public_profile_relation
    ON profile_assets (profile_id, relation_type, asset_id)
    WHERE publication_import_unit_id IS NULL;

-- I01.4 Public Asset -> Profile relations (publication boundary filtered)
CREATE INDEX IF NOT EXISTS ix_profile_assets_public_asset_relation
    ON profile_assets (asset_id, relation_type, profile_id)
    WHERE publication_import_unit_id IS NULL;
