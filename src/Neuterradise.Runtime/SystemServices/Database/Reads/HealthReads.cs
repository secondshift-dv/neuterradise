using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Import;
using Neuterradise.App.Settings;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class HealthReads
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public HealthReads(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HealthReads(CatalogConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<LibraryHealthSummary> GetHealthSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM profiles WHERE trashed_at_ms IS NULL) AS total_profiles,
                (SELECT COUNT(*) FROM profiles WHERE kind = 'NORMAL' AND trashed_at_ms IS NULL) AS normal_profiles,
                (SELECT COUNT(*) FROM profiles WHERE kind = 'UNKNOWN' AND trashed_at_ms IS NULL) AS unknown_profiles,
                (SELECT COUNT(*) FROM assets) AS total_assets,
                (SELECT COUNT(*) FROM assets WHERE state = 'ACTIVE') AS active_assets,
                (SELECT COUNT(*) FROM assets WHERE state = 'CANDIDATE') AS candidate_assets,
                (SELECT COUNT(*) FROM assets WHERE state = 'TRASHED') AS trashed_assets,
                (SELECT COUNT(*) FROM assets WHERE state = 'RETIRED') AS retired_assets,
                (
                    (SELECT COUNT(*) FROM profiles WHERE path_state = 'PENDING') +
                    (SELECT COUNT(*) FROM assets WHERE path_state = 'PENDING')
                ) AS pending_reconciliation,
                (
                    (SELECT COUNT(*) FROM profiles WHERE path_state = 'NEEDS_ATTENTION') +
                    (SELECT COUNT(*) FROM assets WHERE path_state = 'NEEDS_ATTENTION')
                ) AS needs_attention_reconciliation,
                (SELECT COUNT(*)
                 FROM face_detections fd
                 JOIN assets a ON a.asset_id = fd.asset_id
                 WHERE a.state = 'ACTIVE'
                   AND fd.decision_state IN ('UNKNOWN', 'SUGGESTED')) AS unresolved_faces,
                (SELECT COUNT(*) FROM jobs WHERE state IN ('PENDING', 'RUNNABLE', 'RUNNING')) AS active_jobs,
                (SELECT COUNT(*) FROM jobs WHERE state IN ('FAILED_RETRYABLE', 'FAILED_TERMINAL')) AS failed_jobs,
                (SELECT coalesce(SUM(byte_length), 0) FROM assets WHERE state = 'ACTIVE') AS active_bytes;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new LibraryHealthSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, _timeProvider.GetUtcNow(), 0);
        }

        return new LibraryHealthSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            _timeProvider.GetUtcNow(),
            reader.GetInt64(13));
    }

    public async Task<StorageMetricsAuthorityCounts> GetStorageMetricsCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM trash_entries WHERE state IN ('PENDING', 'IN_TRASH', 'EXECUTING')) AS trash_entry_count,
                (SELECT coalesce(SUM(byte_length), 0) FROM assets WHERE state = 'TRASHED') AS trashed_asset_bytes,
                (SELECT COUNT(*) FROM import_items WHERE source_cleanup_state IN ('SOURCE_DELETE_PENDING', 'SOURCE_DELETE_FAILED', 'SOURCE_CHANGED')) AS pending_cleanup_count;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StorageMetricsAuthorityCounts(0, 0, 0);
        }

        return new StorageMetricsAuthorityCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    public async Task<IReadOnlyList<HealthProfileItem>> GetHealthProfileItemsAsync(
        Guid? profileId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (profileId.HasValue)
        {
            command.CommandText = """
                SELECT profile_id, kind, display_name, profile_storage_token, current_managed_relative_path,
                       path_state, (trashed_at_ms IS NOT NULL) AS is_trashed, row_version
                FROM profiles
                WHERE profile_id = $profileId;
                """;
            command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId.Value));
        }
        else
        {
            command.CommandText = """
                SELECT profile_id, kind, display_name, profile_storage_token, current_managed_relative_path,
                       path_state, (trashed_at_ms IS NOT NULL) AS is_trashed, row_version
                FROM profiles;
                """;
        }

        var results = new List<HealthProfileItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new HealthProfileItem(
                ProfileId: DbGuid.Parse(reader.GetString(0)),
                Kind: DbEnum.ParseProfileKind(reader.GetString(1)),
                DisplayName: reader.GetString(2),
                StorageToken: reader.IsDBNull(3) ? null : reader.GetString(3),
                CurrentManagedRelativePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                PathState: DbEnum.ParseManagedPathState(reader.GetString(5)),
                IsTrashed: reader.GetInt32(6) != 0,
                RowVersion: reader.GetInt64(7)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthAssetItem>> GetHealthAssetItemsAsync(
        Guid? assetId = null,
        Guid? ownerProfileId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var sql = """
            SELECT a.asset_id, a.media_type,
                   coalesce(a.current_managed_file_name, a.original_file_name, '') AS file_name,
                   a.asset_storage_token, a.sha256, coalesce(a.byte_length, 0),
                   a.current_managed_relative_path, a.state, (a.trashed_at_ms IS NOT NULL) AS is_trashed,
                   pa.profile_id AS owner_profile_id, p.display_name AS owner_display_name, p.profile_storage_token AS owner_storage_token,
                   a.current_managed_file_name
            FROM assets a
            LEFT JOIN profile_assets pa ON pa.asset_id = a.asset_id AND pa.relation_type = $ownerRelation
            LEFT JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE 1=1
            """;

        if (assetId.HasValue)
        {
            sql += " AND a.asset_id = $assetId";
            command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId.Value));
        }

        command.Parameters.AddWithValue("$ownerRelation", DbEnum.Format(ProfileAssetRelation.Owner));

        if (ownerProfileId.HasValue)
        {
            sql += " AND pa.profile_id = $ownerProfileId";
            command.Parameters.AddWithValue("$ownerProfileId", DbGuid.Format(ownerProfileId.Value));
        }

        command.CommandText = sql + ";";

        var results = new List<HealthAssetItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rawFileName = reader.GetString(2);
            var extension = Path.GetExtension(rawFileName).TrimStart('.');

            results.Add(new HealthAssetItem(
                AssetId: DbGuid.Parse(reader.GetString(0)),
                MediaType: DbEnum.ParseMediaType(reader.GetString(1)),
                Extension: extension,
                StorageToken: reader.IsDBNull(3) ? null : reader.GetString(3),
                Sha256: reader.IsDBNull(4) ? null : reader.GetString(4),
                ByteLength: reader.GetInt64(5),
                CurrentManagedRelativePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                State: DbEnum.ParseAssetState(reader.GetString(7)),
                IsTrashed: reader.GetInt32(8) != 0,
                OwnerProfileId: reader.IsDBNull(9) ? null : DbGuid.Parse(reader.GetString(9)),
                OwnerDisplayName: reader.IsDBNull(10) ? null : reader.GetString(10),
                OwnerStorageToken: reader.IsDBNull(11) ? null : reader.GetString(11),
                CurrentManagedFileName: reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthSourceCleanupItem>> GetSourceCleanupRepairItemsAsync(
        Guid? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id,
                   i.candidate_asset_id,
                   i.row_version,
                   i.source_cleanup_state,
                   i.source_path,
                   candidate.sha256,
                   candidate.byte_length,
                   managed.current_managed_relative_path,
                   managed.current_managed_file_name
            FROM import_items i
            JOIN import_units u ON u.import_unit_id = i.import_unit_id
            JOIN assets candidate ON candidate.asset_id = i.candidate_asset_id
            JOIN assets managed ON managed.asset_id = coalesce(i.reused_asset_id, i.candidate_asset_id)
            WHERE i.source_cleanup_state IN ('SOURCE_DELETE_PENDING','SOURCE_DELETE_FAILED','SOURCE_CHANGED')
              AND u.library_commit_state IN (
                  'DOMAIN_AUTHORITY_COMMITTED','SOURCE_CLEANUP_PENDING',
                  'SOURCE_CLEANUP_COMPLETE','TERMINAL')
              AND ($subjectId IS NULL
                   OR i.candidate_asset_id = $subjectId
                   OR i.import_item_id = $subjectId)
              AND candidate.sha256 IS NOT NULL
              AND candidate.byte_length IS NOT NULL
              AND managed.current_managed_relative_path IS NOT NULL
              AND managed.current_managed_file_name IS NOT NULL
            ORDER BY i.import_item_id;
            """;
        command.Parameters.AddWithValue(
            "$subjectId",
            subjectId is null ? DBNull.Value : DbGuid.Format(subjectId.Value));

        var results = new List<HealthSourceCleanupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new HealthSourceCleanupItem(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                DbEnum.ParseSourceCleanupState(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthFinding>> GetDatabaseIntegrityFindingsAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<HealthFinding>();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var check in DatabaseIntegrityChecks)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = check.Sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var subject = reader.IsDBNull(0) ? null : reader.GetString(0);
                var detail = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetString(1) : null;

                findings.Add(new HealthFinding(
                    check.Code,
                    check.Severity,
                    ProfileId: check.Entity == IntegrityEntity.Profile ? TryParse(subject) : null,
                    AssetId: check.Entity == IntegrityEntity.Asset ? TryParse(subject) : null,
                    JobId: check.Entity == IntegrityEntity.Job ? TryParse(subject) : null,
                    OperationId: check.Entity == IntegrityEntity.Operation ? subject : null,
                    Summary: detail is null
                        ? $"{check.Summary} ({subject ?? "unidentified"})"
                        : $"{check.Summary} ({subject ?? "unidentified"}): {detail}",
                    RepairAvailable: IsConservativeRepairAvailable(check.Code)));
            }
        }

        findings.AddRange(
            await FindTaxonomyNameCollisionsAsync(connection, cancellationToken).ConfigureAwait(false));

        return findings;
    }

    private static async Task<IReadOnlyList<HealthFinding>> FindTaxonomyNameCollisionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var findings = new List<HealthFinding>();

        foreach (var (table, label) in new[] { ("categories", "Category"), ("tags", "Tag") })
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM {table} ORDER BY created_at_ms;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var canonical = TaxonomyNamePolicy.TryNormalize(name);
                if (canonical is null)
                {
                    continue;
                }

                if (seen.TryGetValue(canonical, out var existing))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TaxonomyNameCollision,
                        HealthSeverity.Warning,
                        ProfileId: null,
                        AssetId: null,
                        JobId: null,
                        OperationId: null,
                        Summary: $"{label} '{name}' and '{existing}' are the same name under the current"
                            + " naming rules. Both are kept; merge them from Settings when you decide which to keep.",
                        RepairAvailable: false));
                    continue;
                }

                seen[canonical] = name;
            }
        }

        return findings;
    }

    private static bool IsConservativeRepairAvailable(string code) =>
        code is HealthFindingCode.SourceDeletePending or HealthFindingCode.SourceDeleteFailed;

    private static Guid? TryParse(string? value) =>
        value is not null && DomainId.TryParse(value, out var parsed) ? parsed : null;

    private enum IntegrityEntity
    {
        Profile,
        Asset,
        Job,
        Operation,
    }

    private sealed record IntegrityCheck(
        string Code,
        HealthSeverity Severity,
        IntegrityEntity Entity,
        string Summary,
        string Sql);

    private static readonly IntegrityCheck[] DatabaseIntegrityChecks =
    [
        new(
            HealthFindingCode.StorageTokenMissing,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "An active Profile with a managed folder has no ProfileStorageToken",
            """
            SELECT profile_id, current_managed_relative_path
            FROM profiles
            WHERE trashed_at_ms IS NULL
              AND current_managed_relative_path IS NOT NULL
              AND profile_storage_token IS NULL;
            """),
        new(
            HealthFindingCode.StorageTokenMissing,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "An ACTIVE Asset with managed bytes has no AssetStorageToken",
            """
            SELECT asset_id, current_managed_file_name
            FROM assets
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND current_managed_file_name IS NOT NULL
              AND asset_storage_token IS NULL;
            """),
        new(
            HealthFindingCode.StorageTokenPathMismatch,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's managed folder no longer carries its persisted ProfileStorageToken",
            """
            -- Mutation evidence, not a naming convention check: the finding fires only
            -- when the managed folder already carries a bracketed Profile token marker
            -- and that marker is not this Profile's persisted token. A path with no
            -- marker at all is a different concern and is not evidence of mutation.
            SELECT profile_id, current_managed_relative_path
            FROM profiles
            WHERE trashed_at_ms IS NULL
              AND profile_storage_token IS NOT NULL
              AND current_managed_relative_path IS NOT NULL
              AND instr(current_managed_relative_path, '[P-') > 0
              AND instr(current_managed_relative_path, '[' || profile_storage_token || ']') = 0;
            """),
        new(
            HealthFindingCode.StorageTokenPathMismatch,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "An Asset's managed file name no longer carries its persisted AssetStorageToken",
            """
            -- Master Specification 03 names a managed file "{safe name} - {token}.{ext}",
            -- so the Asset marker is the " - " trailer, not the bracketed form a Profile
            -- folder uses. The finding fires only when the name already carries an Asset
            -- token marker and that marker is not this Asset's own token.
            SELECT asset_id, current_managed_file_name
            FROM assets
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND asset_storage_token IS NOT NULL
              AND current_managed_file_name IS NOT NULL
              AND instr(current_managed_file_name, ' - A-') > 0
              AND instr(current_managed_file_name, ' - ' || asset_storage_token || '.') = 0;
            """),
        new(
            HealthFindingCode.OwnerCountInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Asset,
            "An ACTIVE Asset does not have exactly one OWNER",
            """
            SELECT a.asset_id, 'owners=' || COUNT(pa.profile_id)
            FROM assets a
            LEFT JOIN profile_assets pa
              ON pa.asset_id = a.asset_id AND pa.relation_type = 'OWNER'
            WHERE a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL
            GROUP BY a.asset_id
            HAVING COUNT(pa.profile_id) <> 1;
            """),
        new(
            HealthFindingCode.OwnerCountInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Asset,
            "A CANDIDATE or RETIRED Asset still holds an OWNER relation",
            """
            SELECT a.asset_id, a.state
            FROM assets a
            JOIN profile_assets pa
              ON pa.asset_id = a.asset_id AND pa.relation_type = 'OWNER'
            WHERE a.state IN ('CANDIDATE', 'RETIRED');
            """),
        new(
            HealthFindingCode.ActiveFingerprintMissing,
            HealthSeverity.Critical,
            IntegrityEntity.Asset,
            "An ACTIVE Asset is missing a trusted fingerprint or a resolved current path",
            """
            SELECT asset_id,
                   'sha256=' || CASE WHEN sha256 IS NULL THEN 'missing' ELSE 'present' END
                   || ' path_state=' || path_state
            FROM assets
            WHERE state = 'ACTIVE'
              AND trashed_at_ms IS NULL
              AND (
                    sha256 IS NULL
                    OR byte_length IS NULL
                    OR current_managed_relative_path IS NULL
                    OR current_managed_file_name IS NULL
                  );
            """),
        new(
            HealthFindingCode.IdentityCardinalityInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Profile,
            "An active NORMAL Profile does not have exactly one active Identity",
            """
            SELECT p.profile_id, 'identities=' || COUNT(i.identity_id)
            FROM profiles p
            LEFT JOIN identities i ON i.profile_id = p.profile_id AND i.is_active = 1
            WHERE p.kind = 'NORMAL' AND p.trashed_at_ms IS NULL
            GROUP BY p.profile_id
            HAVING COUNT(i.identity_id) <> 1;
            """),
        new(
            HealthFindingCode.IdentityCardinalityInvalid,
            HealthSeverity.Critical,
            IntegrityEntity.Profile,
            "An active UNKNOWN Profile holds an active Identity",
            """
            SELECT p.profile_id, i.identity_id
            FROM profiles p
            JOIN identities i ON i.profile_id = p.profile_id AND i.is_active = 1
            WHERE p.kind = 'UNKNOWN' AND p.trashed_at_ms IS NULL;
            """),
        new(
            HealthFindingCode.UnknownSequenceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's UnknownSequence does not match its Kind or is not a positive value",
            """
            SELECT profile_id,
                   'kind=' || kind || ' sequence='
                   || CASE WHEN unknown_sequence IS NULL THEN 'null'
                           ELSE CAST(unknown_sequence AS TEXT) END
            FROM profiles
            WHERE (kind = 'UNKNOWN' AND (unknown_sequence IS NULL OR unknown_sequence <= 0))
               OR (kind = 'NORMAL' AND unknown_sequence IS NOT NULL);
            """),
        new(
            HealthFindingCode.AppearanceReferenceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A Profile's Cover or Banner points at media that cannot legally serve that role",
            """
            SELECT p.profile_id,
                   'cover=' || coalesce(p.cover_asset_id, '-')
                   || ' banner=' || coalesce(p.banner_asset_id, '-')
            FROM profiles p
            LEFT JOIN assets cover ON cover.asset_id = p.cover_asset_id
            LEFT JOIN assets banner ON banner.asset_id = p.banner_asset_id
            WHERE p.trashed_at_ms IS NULL
              AND (
                    (p.cover_asset_id IS NOT NULL
                     AND (cover.asset_id IS NULL
                          OR cover.state <> 'ACTIVE'
                          OR cover.trashed_at_ms IS NOT NULL
                          OR cover.media_type <> 'IMAGE'))
                    OR (p.banner_asset_id IS NOT NULL
                        AND (banner.asset_id IS NULL
                             OR banner.state <> 'ACTIVE'
                             OR banner.trashed_at_ms IS NOT NULL
                             OR banner.media_type NOT IN ('IMAGE', 'VIDEO')))
                    OR (p.kind = 'UNKNOWN'
                        AND (p.cover_asset_id IS NOT NULL OR p.banner_asset_id IS NOT NULL))
                  );
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A Profile's current/target placement state is internally inconsistent",
            """
            SELECT profile_id, 'path_state=' || path_state
            FROM profiles
            WHERE (path_state <> 'NONE' AND reconciliation_operation_id IS NULL)
               OR (path_state = 'NONE'
                   AND target_managed_relative_path IS NOT NULL
                   AND current_managed_relative_path IS NOT NULL
                   AND current_managed_relative_path <> target_managed_relative_path);
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Asset,
            "An Asset's current/target placement state is internally inconsistent",
            """
            SELECT asset_id, 'path_state=' || path_state
            FROM assets
            WHERE (path_state <> 'NONE' AND reconciliation_operation_id IS NULL)
               OR (path_state = 'NONE'
                   AND target_managed_file_name IS NOT NULL
                   AND current_managed_file_name IS NOT NULL
                   AND (current_managed_file_name <> target_managed_file_name
                        OR coalesce(current_managed_relative_path, '')
                           <> coalesce(target_managed_relative_path, '')));
            """),
        new(
            HealthFindingCode.SourceDeletePending,
            HealthSeverity.Warning,
            IntegrityEntity.Asset,
            "An Import item is stuck waiting for source cleanup",
            """
            SELECT coalesce(ii.candidate_asset_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_DELETE_PENDING';
            """),
        new(
            HealthFindingCode.SourceDeleteFailed,
            HealthSeverity.Warning,
            IntegrityEntity.Asset,
            "An Import item's source cleanup failed and remains unresolved",
            """
            SELECT coalesce(ii.candidate_asset_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_DELETE_FAILED';
            """),
        new(
            HealthFindingCode.SourceChanged,
            HealthSeverity.Warning,
            IntegrityEntity.Asset,
            "A committed import item's source file changed before cleanup completed",
            """
            SELECT coalesce(ii.candidate_asset_id, ii.import_item_id), ii.source_cleanup_state
            FROM import_items ii
            WHERE ii.source_cleanup_state = 'SOURCE_CHANGED';
            """),
        new(
            HealthFindingCode.FaceRecordOrphaned,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "A FaceDetection points at an Identity that is no longer active",
            """
            SELECT fd.asset_id, 'face=' || fd.face_id
            FROM face_detections fd
            LEFT JOIN identities confirmed ON confirmed.identity_id = fd.confirmed_identity_id
            LEFT JOIN identities suggested ON suggested.identity_id = fd.suggested_identity_id
            WHERE (fd.confirmed_identity_id IS NOT NULL
                   AND (confirmed.identity_id IS NULL OR confirmed.is_active = 0))
               OR (fd.suggested_identity_id IS NOT NULL AND suggested.identity_id IS NULL);
            """),
        new(
            HealthFindingCode.FaceRecordOrphaned,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "An IdentitySample references a face detection that is no longer confirmed to it",
            """
            SELECT fd.asset_id, 'sample=' || s.identity_sample_id
            FROM identity_samples s
            JOIN face_detections fd ON fd.face_id = s.face_id
            WHERE fd.decision_state <> 'CONFIRMED'
               OR fd.confirmed_identity_id IS NULL
               OR fd.confirmed_identity_id <> s.identity_id;
            """),
        new(
            HealthFindingCode.EmbeddingProvenanceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "A stored embedding has missing or inconsistent space provenance",
            """
            SELECT fd.asset_id, 'face=' || fd.face_id
            FROM face_detections fd
            WHERE (fd.embedding IS NOT NULL
                   AND (fd.embedding_space_key IS NULL OR trim(fd.embedding_space_key) = ''))
               OR (fd.embedding IS NULL AND fd.embedding_space_key IS NOT NULL);
            """),
        new(
            HealthFindingCode.EmbeddingProvenanceInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Asset,
            "An IdentitySample embedding space disagrees with the detection it came from",
            """
            SELECT fd.asset_id, 'sample=' || s.identity_sample_id
            FROM identity_samples s
            JOIN face_detections fd ON fd.face_id = s.face_id
            WHERE fd.embedding_space_key IS NOT NULL
              AND fd.embedding_space_key <> s.embedding_space_key;
            """),
        new(
            HealthFindingCode.ManualRelatedPairInvalid,
            HealthSeverity.Error,
            IntegrityEntity.Profile,
            "A manual Related pair names an endpoint that cannot legally be related",
            """
            SELECT e.profile_id_low, 'other=' || e.profile_id_high
            FROM related_profile_evidence e
            LEFT JOIN profiles low ON low.profile_id = e.profile_id_low
            LEFT JOIN profiles high ON high.profile_id = e.profile_id_high
            WHERE e.evidence_type = 'MANUAL'
              AND (low.profile_id IS NULL
                   OR high.profile_id IS NULL
                   OR low.kind <> 'NORMAL'
                   OR high.kind <> 'NORMAL'
                   OR e.profile_id_low = e.profile_id_high);
            """),
        new(
            HealthFindingCode.RelatedSummaryMismatch,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A Related summary disagrees with the evidence it is derived from",
            """
            SELECT s.profile_id_low,
                   'other=' || s.profile_id_high
                   || ' stored=' || s.shared_asset_count || '/' || s.confirmed_face_count
                   || ' actual=' || coalesce(e.shared_assets, 0) || '/' || coalesce(e.confirmed_faces, 0)
            FROM related_profile_summary s
            LEFT JOIN (
                SELECT ev.profile_id_low,
                       ev.profile_id_high,
                       COUNT(DISTINCT CASE WHEN ev.evidence_type = 'SHARED_ASSET' THEN ev.asset_id END)
                           AS shared_assets,
                       COUNT(DISTINCT CASE WHEN ev.evidence_type = 'CONFIRMED_FACE' THEN ev.face_id END)
                           AS confirmed_faces
                FROM related_profile_evidence ev
                LEFT JOIN assets a ON a.asset_id = ev.asset_id
                WHERE ev.asset_id IS NULL OR (a.state = 'ACTIVE' AND a.trashed_at_ms IS NULL)
                GROUP BY ev.profile_id_low, ev.profile_id_high
            ) e ON e.profile_id_low = s.profile_id_low AND e.profile_id_high = s.profile_id_high
            WHERE s.shared_asset_count <> coalesce(e.shared_assets, 0)
               OR s.confirmed_face_count <> coalesce(e.confirmed_faces, 0);
            """),
        new(
            HealthFindingCode.JobStuck,
            HealthSeverity.Warning,
            IntegrityEntity.Job,
            "A durable job is claimed but no process can be holding its lease",
            """
            SELECT job_id, 'state=' || state || ' attempt=' || attempt || '/' || max_attempts
            FROM jobs
            WHERE state = 'RUNNING'
               OR (state = 'FAILED_RETRYABLE' AND attempt >= max_attempts);
            """),
        new(
            HealthFindingCode.ReconciliationInconsistent,
            HealthSeverity.Warning,
            IntegrityEntity.Operation,
            "A storage operation is nonterminal but no entity still owes it work",
            """
            SELECT o.operation_id, 'kind=' || o.kind || ' state=' || o.state
            FROM storage_operations o
            WHERE o.state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED')
              AND o.kind <> 'LIBRARY_REPAIR'
              AND NOT EXISTS (
                    SELECT 1 FROM profiles p WHERE p.reconciliation_operation_id = o.operation_id)
              AND NOT EXISTS (
                    SELECT 1 FROM assets a WHERE a.reconciliation_operation_id = o.operation_id);
            """),
        new(
            HealthFindingCode.ProfileManifestMalformed,
            HealthSeverity.Warning,
            IntegrityEntity.Profile,
            "A persisted appearance or layout setting is not valid JSON",
            """
            SELECT pa.profile_id, 'overrides_json'
            FROM profile_appearance pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE p.trashed_at_ms IS NULL
              AND json_valid(pa.overrides_json) = 0;
            """),
    ];
}
