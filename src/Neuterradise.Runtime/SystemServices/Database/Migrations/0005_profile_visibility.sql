ALTER TABLE profiles ADD COLUMN visibility TEXT NOT NULL DEFAULT 'PUBLISHED'
    CHECK (visibility IN ('DRAFT', 'PUBLISHED'));

CREATE INDEX ix_profiles_visibility_active
ON profiles(visibility, trashed_at_ms, updated_at_ms DESC, profile_id)
WHERE visibility = 'PUBLISHED';
