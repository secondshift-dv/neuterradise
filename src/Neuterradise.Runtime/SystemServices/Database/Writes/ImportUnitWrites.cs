using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class ImportUnitWrites
{
    private const string ClearableHistoryPredicate = """
        (
            state = 'CANCELLED'
            OR (
                state IN ('COMMITTED','COMPLETED')
                AND NOT EXISTS (
                    SELECT 1 FROM import_items item
                    WHERE item.import_unit_id = import_units.import_unit_id
                      AND item.disposition = 'INVALID'
                )
                AND NOT EXISTS (
                    SELECT 1 FROM import_items item
                    WHERE item.import_unit_id = import_units.import_unit_id
                      AND item.source_cleanup_state IN ('SOURCE_DELETE_FAILED','SOURCE_CHANGED')
                )
                AND COALESCE(json_array_length(json_extract(
                    CASE WHEN json_valid(verification_draft_json) = 1
                         THEN verification_draft_json ELSE '{}' END,
                    '$.attentionItemIds')), 0) = 0
            )
        )
        """;

    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public ImportUnitWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ImportCancellationOutcome> CancelUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound);
        var state = await ReadCancellationStateAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (state is null) return new ImportCancellationOutcome(unitId, ImportCancellationResult.UnitNotFound);
        // Already cancelled: check rollback settlement.
        if (state.UnitState == ImportUnitState.Cancelled)
        {
            if (state.RollbackSettled == true)
                return new ImportCancellationOutcome(unitId, ImportCancellationResult.AlreadyCancelled);
            // Rollback not yet settled — caller must continue rollback operations.
            return new ImportCancellationOutcome(
                unitId,
                ImportCancellationResult.RollbackPending,
                assetsForTrashDisposition: GetExclusiveActiveAssetIds(state),
                destinationProfileCreated: false);
        }
        // Truly published/committed imports cannot be cancelled. The boundary is the import
        // lifecycle state (Committed/Completed), not the Stage 1 canonical checkpoint.
        // DomainAuthorityCommitted (Stage 1) does NOT prevent cancel — Stage 2 and verification
        // still occur and the import is still pre-publication.
        if (state.UnitState.IsUnitCommitted()
            || state.UnitState is ImportUnitState.Committing or ImportUnitState.FailedTerminal)
            return new ImportCancellationOutcome(unitId, ImportCancellationResult.RefusedAlreadyCommitted);
        var now = _timeProvider.GetUtcNow();
        var hasPostStage1Delta = state.LibraryCommitState.HasReachedDomainCommit();
        if (!await PersistCancellationAsync(unitId, now, hasPostStage1Delta, cancellationToken).ConfigureAwait(false))
            return new ImportCancellationOutcome(unitId, ImportCancellationResult.RefusedAlreadyCommitted);

        var jobWrites = new JobWrites(_catalog, _timeProvider);
        var importWrites = new ImportWrites(_catalog, _timeProvider);
        var cancelledJobs = 0;
        foreach (var scope in state.JobScopes)
            cancelledJobs += await jobWrites.CancelIdleAsync(null, scope.OwnerType, scope.OwnerId, now, cancellationToken).ConfigureAwait(false);
        var runningJobs = await CountRunningScopedJobsAsync(state.JobScopes, cancellationToken).ConfigureAwait(false);

        // Pre-Stage1: retire unadmitted candidate assets.
        var retiredCandidates = 0;
        foreach (var candidateAssetId in state.UnadmittedCandidateAssetIds)
        {
            await importWrites.RetireCandidateAsync(candidateAssetId, AssetRetirementReason.Cancelled, cancellationToken).ConfigureAwait(false);
            retiredCandidates++;
        }

        // Post-Stage1: read commit state for the caller to orchestrate external rollback
        // (Trash operations, Profile disposition). DB delta rollback is NOT done here —
        // the caller handles the full ordered rollback to ensure Trash plans are prepared
        // before OWNER relations are removed.
        List<Guid> assetsForTrash = [];
        var destinationProfileCreated = false;
        if (hasPostStage1Delta)
        {
            assetsForTrash = GetExclusiveActiveAssetIds(state);
            var commitOp = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (commitOp?.CheckpointJson is { } json)
            {
                try
                {
                    var cs = System.Text.Json.JsonSerializer.Deserialize<CommitStateProbe>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cs is not null)
                    {
                        destinationProfileCreated = cs.DestinationProfileCreated == true;
                    }
                }
                catch
                {
                    // Best-effort: if JSON is malformed, treat as not created.
                }
            }
        }

        return new ImportCancellationOutcome(
            unitId,
            ImportCancellationResult.Cancelled,
            cancelledJobs,
            retiredCandidates,
            runningJobs,
            assetsForTrash,
            destinationProfileCreated);
    }

    /// <summary>Minimal probe for CommitState JSON to read rollback-relevant fields.</summary>
    private sealed record CommitStateProbe(
        bool? DestinationProfileCreated,
        Guid? DestinationProfileId,
        Guid? PreviousCoverAssetId,
        Guid? PreviousBannerAssetId,
        bool? AppearanceSnapshotCaptured,
        string? PreviousAppearanceOverridesJson);

    /// <summary>
    /// Pauses or resumes one import. The unit's own flag is what the activity surface and the import
    /// finalizer honour; the queued background work for its media (owned by the media, not by the
    /// unit) is paused or resumed with it. Pausing only jobs owned by "ImportUnit" changed nothing,
    /// which is why Pause appeared to do nothing.
    /// </summary>
    public async Task<bool> SetPausedAsync(Guid unitId, bool paused, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        int changed;
        // Pause is valid while the import is still in preparation — even after Stage 1 has
        // committed canonical authority. Stage 2 preparation, verification, and finalization all
        // occur after DomainAuthorityCommitted. The boundary is the import lifecycle state
        // (Committed/Completed/Committing), not the Stage 1 checkpoint.
        await using (var unit = transaction.CreateCommand("""
            UPDATE import_units
            SET is_paused = $paused, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND is_paused <> $paused
              AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
            """))
        {
            unit.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            unit.Parameters.AddWithValue("$now", now);
            unit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            changed = await unit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var jobs = transaction.CreateCommand(paused
            ? """
              UPDATE jobs SET state = 'PAUSED', row_version = row_version + 1
              WHERE owner_type = 'Asset'
                AND state IN ('PENDING','RUNNABLE','FAILED_RETRYABLE')
                AND owner_id IN (SELECT candidate_asset_id FROM import_items
                                 WHERE import_unit_id = $unitId AND candidate_asset_id IS NOT NULL);
              """
            : """
              UPDATE jobs SET state = 'PENDING', not_before_ms = NULL, row_version = row_version + 1
              WHERE owner_type = 'Asset'
                AND state = 'PAUSED'
                AND owner_id IN (SELECT candidate_asset_id FROM import_items
                                 WHERE import_unit_id = $unitId AND candidate_asset_id IS NOT NULL);
              """))
        {
            jobs.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    /// <summary>
    /// Resumes a paused import: clears the durable paused flag and transitions Asset-owned
    /// paused jobs back to PENDING. The scheduler's dependency evaluation moves eligible jobs to
    /// RUNNABLE when prerequisites are satisfied.
    ///
    /// Does not recreate the import graph. Preserves all completed Stage 1/2 work and checkpoints.
    /// </summary>
    public async Task<bool> ResumeUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        int changed;
        // Resume is valid whenever the import lifecycle permits it — same boundary as Pause.
        await using (var unit = transaction.CreateCommand("""
            UPDATE import_units
            SET is_paused = 0, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND is_paused = 1
              AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
            """))
        {
            unit.Parameters.AddWithValue("$now", now);
            unit.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            changed = await unit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var jobs = transaction.CreateCommand("""
            UPDATE jobs SET state = 'PENDING', not_before_ms = NULL, row_version = row_version + 1
            WHERE owner_type = 'Asset'
              AND state = 'PAUSED'
              AND owner_id IN (SELECT candidate_asset_id FROM import_items
                               WHERE import_unit_id = $unitId AND candidate_asset_id IS NOT NULL);
            """))
        {
            jobs.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> RetryUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty) return false;

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        // Retry follows the durable library checkpoint instead of blindly rewinding the lifecycle.
        // Before Stage 1 authority, INTAKE is the canonical re-entry point. Once domain authority is
        // committed, the unit must remain on the post-Stage1 PREPARING path so Stage 2 can replay.
        string? libraryCommitState;
        await using (var read = transaction.CreateCommand("""
            SELECT library_commit_state
            FROM import_units
            WHERE import_unit_id = $unitId
              AND state = 'FAILED_RETRYABLE';
            """))
        {
            read.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            libraryCommitState = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (libraryCommitState is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var checkpoint = DbEnum.ParseImportCommitCheckpointOrDefault(libraryCommitState);
        var targetState = checkpoint.HasReachedDomainCommit()
            ? ImportUnitState.Preparing
            : ImportUnitState.Intake;
        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await using var command = transaction.CreateCommand("""
            UPDATE import_units
            SET state = $state,
                completed_at_ms = NULL,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND state = 'FAILED_RETRYABLE';
            """);
        command.Parameters.AddWithValue("$state", DbEnum.Format(targetState));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$now", now);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Hides one settled import from product history. This changes only presentation visibility;
    /// the import unit, items, assets, profiles and managed bytes remain authoritative and intact.
    /// </summary>
    public async Task<bool> HideFinishedFromHistoryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            return false;
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        await using var command = transaction.CreateCommand(
            $"""
            UPDATE import_units
            SET hidden_from_history = 1,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId
              AND hidden_from_history = 0
              AND {ClearableHistoryPredicate};
            """);
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Hides every currently clearable finished import with one idempotent bulk write.</summary>
    public async Task<int> HideAllFinishedFromHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        await using var command = transaction.CreateCommand(
            $"""
            UPDATE import_units
            SET hidden_from_history = 1,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE hidden_from_history = 0
              AND {ClearableHistoryPredicate};
            """);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            transaction.QueueInvalidation(CatalogInvalidationDomain.Import);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Post-Stage1 DB delta rollback. Called AFTER external Trash operations have settled, so
    /// OWNER relations for exclusively-imported assets are already handled by Trash.
    /// This method handles only the remaining DB-only cleanup:
    ///
    /// - Reused MANUAL associations with provenance "import:{unitId}".
    /// - Draft presentation overrides on import_items.
    /// - Previous Profile appearance restored for existing Profiles.
    ///
    /// Does NOT delete OWNER relations — those are handled by the Trash authority which
    /// requires them to be intact when preparing Trash plans.
    /// </summary>
    internal async Task RollbackPostStage1DeltaAsync(
        Guid unitId,
        Guid? destinationProfileId,
        CommitStateSnapshot? commitSnapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Remove reused MANUAL associations introduced by this import.
        //    Provenance "import:{unitId}" targets exactly the relation attributable to this
        //    ImportUnit. Does not delete pre-existing MANUAL/APPEARS relations or associations
        //    created by another import/user action.
        await using (var removeImportRelations = connection.CreateCommand())
        {
            removeImportRelations.CommandText =
                """
                DELETE FROM profile_assets
                WHERE provenance_key = $provenanceKey;
                """;
            removeImportRelations.Parameters.AddWithValue("$provenanceKey", $"import:{unitId:D}");
            await removeImportRelations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. Clear draft presentation overrides on import_items.
        await using (var clearAppearance = connection.CreateCommand())
        {
            clearAppearance.CommandText =
                """
                UPDATE import_items
                SET cover_asset_id = NULL, banner_asset_id = NULL,
                    row_version = row_version + 1
                WHERE import_unit_id = $unitId;
                """;
            clearAppearance.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await clearAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Restore previous Profile appearance for existing Profiles.
        //    Restores when AppearanceSnapshotCaptured is true, including null baseline
        //    (cover=NULL, banner=NULL before import).
        //    For new Profiles, the Profile will be trashed by the caller.
        if (destinationProfileId is { } profileId
            && commitSnapshot is { DestinationProfileCreated: false, AppearanceSnapshotCaptured: true })
        {
            await using (var restoreCover = connection.CreateCommand())
            {
                restoreCover.CommandText =
                    """
                    UPDATE profiles
                    SET cover_asset_id = $coverId,
                        row_version = row_version + 1
                    WHERE profile_id = $profileId;
                    """;
                restoreCover.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                restoreCover.Parameters.AddWithValue("$coverId",
                    commitSnapshot.PreviousCoverAssetId is { } cc ? DbGuid.Format(cc) : DBNull.Value);
                await restoreCover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var restoreBanner = connection.CreateCommand())
            {
                restoreBanner.CommandText =
                    """
                    UPDATE profiles
                    SET banner_asset_id = $bannerId,
                        row_version = row_version + 1
                    WHERE profile_id = $profileId;
                    """;
                restoreBanner.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                restoreBanner.Parameters.AddWithValue("$bannerId",
                    commitSnapshot.PreviousBannerAssetId is { } bb ? DbGuid.Format(bb) : DBNull.Value);
                await restoreBanner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Restore previous appearance overrides JSON (cover source kind, banner source kind,
            // video timestamps, clip start/duration, etc.).
            if (commitSnapshot.PreviousAppearanceOverridesJson is { } overridesJson)
            {
                await using (var restoreOverrides = connection.CreateCommand())
                {
                    restoreOverrides.CommandText =
                        """
                        UPDATE profile_appearance
                        SET overrides_json = $overridesJson,
                            updated_at_ms = $now,
                            row_version = row_version + 1
                        WHERE profile_id = $profileId;
                        """;
                    restoreOverrides.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                    restoreOverrides.Parameters.AddWithValue("$overridesJson", overridesJson);
                    restoreOverrides.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
                    await restoreOverrides.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Marks a cancelled import's rollback as fully settled.</summary>
    internal async Task MarkRollbackSettledAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE import_units
            SET rollback_settled = 1, updated_at_ms = $now, row_version = row_version + 1
            WHERE import_unit_id = $unitId AND state = 'CANCELLED';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the CommitState snapshot needed for appearance rollback.</summary>
    internal async Task<CommitStateSnapshot?> ReadCommitSnapshotAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var importWrites = new ImportWrites(_catalog, _timeProvider);
        var commitOp = await importWrites.ReadCommitOperationAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (commitOp?.CheckpointJson is not { } json) return null;
        try
        {
            var cs = System.Text.Json.JsonSerializer.Deserialize<CommitStateProbe>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cs is null ? null : new CommitStateSnapshot(
                cs.DestinationProfileCreated == true,
                cs.DestinationProfileId,
                cs.PreviousCoverAssetId,
                cs.PreviousBannerAssetId,
                cs.AppearanceSnapshotCaptured == true,
                cs.PreviousAppearanceOverridesJson);
        }
        catch
        {
            return null;
        }
    }

    private static List<Guid> GetExclusiveActiveAssetIds(ImportCancellationState state) =>
        state.ActivatedAssetIds
            .Where(id => !state.ReusedAssetIds.Contains(id))
            .ToList();

    /// <summary>Snapshot of CommitState fields needed for appearance rollback.</summary>
    internal sealed record CommitStateSnapshot(
        bool DestinationProfileCreated,
        Guid? DestinationProfileId,
        Guid? PreviousCoverAssetId,
        Guid? PreviousBannerAssetId,
        bool AppearanceSnapshotCaptured,
        string? PreviousAppearanceOverridesJson);

    private async Task<ImportCancellationState?> ReadCancellationStateAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string unitState;
        string libraryCommitState;
        Guid? destinationProfileId = null;
        bool? rollbackSettled = null;
        await using (var unitCommand = connection.CreateCommand())
        {
            unitCommand.CommandText = "SELECT state, library_commit_state, destination_profile_id, rollback_settled FROM import_units WHERE import_unit_id = $unitId;";
            unitCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var unitReader = await unitCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await unitReader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            unitState = unitReader.GetString(0);
            libraryCommitState = unitReader.GetString(1);
            if (!unitReader.IsDBNull(2)) destinationProfileId = DbGuid.Parse(unitReader.GetString(2));
            if (!unitReader.IsDBNull(3)) rollbackSettled = unitReader.GetInt32(3) == 1;
        }
        var unadmitted = new List<Guid>();
        var activated = new List<Guid>();
        var reused = new List<Guid>();
        var scopes = new List<ImportJobScope>();
        await using (var itemCommand = connection.CreateCommand())
        {
            itemCommand.CommandText = """
                SELECT i.import_item_id, i.candidate_asset_id, a.state, i.reused_asset_id
                FROM import_items i LEFT JOIN assets a ON a.asset_id = i.candidate_asset_id
                WHERE i.import_unit_id = $unitId ORDER BY i.import_item_id;
                """;
            itemCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(itemReader.GetString(0));
                scopes.Add(new ImportJobScope("ImportItem", itemId));
                if (itemReader.IsDBNull(1)) continue;
                var candidateAssetId = DbGuid.Parse(itemReader.GetString(1));
                scopes.Add(new ImportJobScope("Asset", candidateAssetId));
                if (!itemReader.IsDBNull(2))
                {
                    var assetState = DbEnum.ParseAssetState(itemReader.GetString(2));
                    if (assetState == AssetState.Candidate) unadmitted.Add(candidateAssetId);
                    if (assetState == AssetState.Active) activated.Add(candidateAssetId);
                }
                if (!itemReader.IsDBNull(3))
                {
                    reused.Add(DbGuid.Parse(itemReader.GetString(3)));
                }
            }
        }
        scopes.Add(new ImportJobScope("ImportUnit", unitId));
        return new ImportCancellationState(
            DbEnum.ParseImportUnitState(unitState),
            DbEnum.ParseImportCommitCheckpointOrDefault(libraryCommitState),
            unadmitted,
            activated,
            reused,
            scopes,
            destinationProfileId,
            rollbackSettled);
    }

    private async Task<int> CountRunningScopedJobsAsync(IReadOnlyList<ImportJobScope> scopes, CancellationToken cancellationToken)
    {
        if (scopes.Count == 0) return 0;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var running = 0;
        foreach (var scope in scopes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs WHERE owner_type = $ownerType AND owner_id = $ownerId AND state = 'RUNNING';";
            command.Parameters.AddWithValue("$ownerType", scope.OwnerType);
            command.Parameters.AddWithValue("$ownerId", DbGuid.Format(scope.OwnerId));
            running += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        return running;
    }

    private async Task<bool> PersistCancellationAsync(Guid unitId, DateTimeOffset now, bool hasPostStage1Delta, CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var formattedUnitId = DbGuid.Format(unitId);
        var formattedNow = DbTime.Format(now);
        int changed;
        // Cancel is accepted while the import is still pre-publication — even after Stage 1 has
        // committed canonical authority to Vault. The boundary is the import lifecycle state
        // (Committed/Completed/Committing), not the Stage 1 checkpoint. Post-Stage1 cancel rolls
        // back only the unpublished import delta; canonical bytes are preserved by existing
        // storage/trash authority. rollback_settled tracks whether external rollback (Trash,
        // Profile disposition) has completed.
        await using (var command = transaction.CreateCommand(hasPostStage1Delta
            ? """
              UPDATE import_units
              SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                  rollback_settled = 0, updated_at_ms = $now, row_version = row_version + 1
              WHERE import_unit_id = $unitId
                AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
              """
            : """
              UPDATE import_units
              SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                  updated_at_ms = $now, row_version = row_version + 1
              WHERE import_unit_id = $unitId
                AND state NOT IN ('COMMITTING','COMMITTED','COMPLETED','COMMITTED_WITH_CLEANUP_ATTENTION','CANCELLED','FAILED_TERMINAL');
              """))
        {
            command.Parameters.AddWithValue("$unitId", formattedUnitId);
            command.Parameters.AddWithValue("$now", formattedNow);
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var settleOperation = transaction.CreateCommand("""
            UPDATE storage_operations
            SET state = 'CANCELLED', completed_at_ms = COALESCE(completed_at_ms, $now),
                error_code = NULL, error_detail_safe = NULL,
                updated_at_ms = $now, row_version = row_version + 1
            WHERE operation_id = (
                SELECT commit_operation_id FROM import_units WHERE import_unit_id = $unitId)
              AND state NOT IN ('COMPLETED','FAILED','CANCELLED')
              AND EXISTS (
                  SELECT 1 FROM import_units
                  WHERE import_unit_id = $unitId AND state = 'CANCELLED');
            """))
        {
            settleOperation.Parameters.AddWithValue("$unitId", formattedUnitId);
            settleOperation.Parameters.AddWithValue("$now", formattedNow);
            await settleOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed == 1)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [unitId],
                CatalogInvalidationDomain.Import,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    private sealed record ImportCancellationState(
        ImportUnitState UnitState,
        ImportCommitCheckpoint LibraryCommitState,
        IReadOnlyList<Guid> UnadmittedCandidateAssetIds,
        IReadOnlyList<Guid> ActivatedAssetIds,
        IReadOnlyList<Guid> ReusedAssetIds,
        IReadOnlyList<ImportJobScope> JobScopes,
        Guid? DestinationProfileId = null,
        bool? RollbackSettled = null);
    private sealed record ImportJobScope(string OwnerType, Guid OwnerId);
}
