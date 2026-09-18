-- Restore the allocation authority for Vaults created before the unknown Profile
-- sequence seed was introduced. Existing counters are preserved, while a missing
-- counter resumes after the greatest UnknownSequence already committed.
INSERT OR IGNORE INTO sequences(name, next_value)
SELECT
    'unknown_profile',
    MAX(1, COALESCE(MAX(unknown_sequence), 0) + 1)
FROM profiles
WHERE kind = 'UNKNOWN';

-- Cancellation is terminal for a pre-authority import commit. Older builds could
-- leave the unit cancelled while its durable operation remained at a live checkpoint.
UPDATE storage_operations
SET state = 'CANCELLED',
    completed_at_ms = COALESCE(completed_at_ms, updated_at_ms),
    error_code = NULL,
    error_detail_safe = NULL,
    row_version = row_version + 1
WHERE kind = 'IMPORT_COMMIT'
  AND state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED')
  AND operation_id IN (
      SELECT commit_operation_id
      FROM import_units
      WHERE state = 'CANCELLED'
        AND commit_operation_id IS NOT NULL
        AND library_commit_state NOT IN (
            'DOMAIN_AUTHORITY_COMMITTED',
            'SOURCE_CLEANUP_PENDING',
            'SOURCE_CLEANUP_COMPLETE',
            'TERMINAL'));
