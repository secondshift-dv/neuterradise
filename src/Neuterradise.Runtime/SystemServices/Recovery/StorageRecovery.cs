using Microsoft.Data.Sqlite;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Recovery;

public sealed class StorageRecovery
{
    private const string ProfileRenameSql =
        """
        SELECT p.profile_id, p.reconciliation_operation_id
        FROM profiles p
        JOIN storage_operations o ON o.operation_id = p.reconciliation_operation_id
        WHERE p.path_state IN ('PENDING', 'NEEDS_ATTENTION')
          AND o.kind = 'PROFILE_RENAME'
        ORDER BY p.profile_id;
        """;

    private const string OwnerRelocationSql =
        """
        SELECT a.asset_id, a.reconciliation_operation_id
        FROM assets a
        JOIN storage_operations o ON o.operation_id = a.reconciliation_operation_id
        WHERE a.path_state IN ('PENDING', 'NEEDS_ATTENTION')
          AND o.kind = 'OWNER_RELOCATION'
        ORDER BY a.asset_id;
        """;

    private readonly ImportCommitCoordinator _commitCoordinator;
    private readonly CatalogDb _catalog;
    private readonly PathReconciler _pathReconciler;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly SettingsOperations _settings;
    private readonly ImportSourceTimestampReconciler _timestampReconciler;
    private readonly Stage2PreparationCoordinator _stage2;

    public StorageRecovery(
        ImportCommitCoordinator commitCoordinator,
        CatalogDb catalog,
        PathReconciler pathReconciler,
        ProfileManifestWriter? manifestWriter = null)
    {
        _commitCoordinator = commitCoordinator
            ?? throw new ArgumentNullException(nameof(commitCoordinator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pathReconciler = pathReconciler ?? throw new ArgumentNullException(nameof(pathReconciler));
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _settings = new SettingsOperations(catalog);
        _timestampReconciler = new ImportSourceTimestampReconciler(catalog);
        _stage2 = new Stage2PreparationCoordinator(catalog, catalog.Paths);
    }

    public async Task<IReadOnlyList<RecoveryFinding>> RecoverPathReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RecoveryFinding>();

        foreach (var obligation in await ReadObligationsAsync(
                     ProfileRenameSql, "Profile", cancellationToken).ConfigureAwait(false))
        {
            findings.Add(await ReconcileAsync(
                    obligation,
                    "PROFILE_RENAME",
                    token => _pathReconciler.ReconcileProfileRenameAsync(obligation.EntityId, token),
                    _ => Task.FromResult<Guid?>(obligation.EntityId),
                    cancellationToken)
                .ConfigureAwait(false));
        }

        foreach (var obligation in await ReadObligationsAsync(
                     OwnerRelocationSql, "Asset", cancellationToken).ConfigureAwait(false))
        {
            findings.Add(await ReconcileAsync(
                    obligation,
                    "OWNER_RELOCATION",
                    token => _pathReconciler.ReconcileOwnerRelocationAsync(obligation.EntityId, token),
                    token => ReadOwnerProfileIdAsync(obligation.EntityId, token),
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return findings;
    }

    public async Task<RecoveryFinding> RecoverImportCommitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Recovery must preserve the normal lifecycle authority after Stage 1. In particular,
            // source cleanup is not allowed to advance the commit checkpoint beyond the Finalizer's
            // replay window before Stage 2 scheduling is durably re-established.
            var stage1 = await _commitCoordinator.MaterializeStage1Async(
                    unitId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            ImportCommitResult result;
            if (stage1.LibraryCommitted)
            {
                // Stage 2 is the canonical post-Stage-1 authority in both normal execution and
                // restart recovery. Schedule first: deterministic jobs/capabilities make this
                // replay idempotent. A crash or exception here leaves the commit checkpoint
                // recoverable instead of letting source cleanup hide a PREPARING unit.
                await _stage2.ScheduleStage2Async(
                        unitId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _stage2.TryTransitionToReadyAsync(unitId, cancellationToken)
                    .ConfigureAwait(false);

                // Preserve the visible timestamp preference on recovery. This reconciliation is
                // deliberately after Stage 2 replay so an auxiliary timestamp repair cannot prevent
                // the canonical preparation graph from being recreated after restart.
                var preferences = await _settings.GetImportPreferencesAsync(cancellationToken)
                    .ConfigureAwait(false);
                await _timestampReconciler.ReconcileAsync(
                        unitId,
                        preferences.PreserveSourceTimestamps,
                        cancellationToken)
                    .ConfigureAwait(false);

                // MaterializeStage1Async is idempotent once domain authority is committed. Stage 2
                // is now durably scheduled, so CommitAsync may safely resume post-authority cleanup.
                result = await _commitCoordinator.CommitAsync(
                        unitId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = stage1;
            }

            return result.Outcome switch
            {
                ImportCommitOutcome.Committed => Finding(
                    unitId,
                    RecoveryOutcome.Completed,
                    "IMPORT_COMMIT_RECOVERED",
                    "The persisted Import commit restored Stage 2 replay authority before resuming its terminal cleanup checkpoint."),
                ImportCommitOutcome.CommittedWithCleanupAttention => Finding(
                    unitId,
                    RecoveryOutcome.NeedsAttention,
                    "SOURCE_CLEANUP_RETRYABLE",
                    "Library and Stage 2 authorities are valid and source cleanup remains retryable."),
                ImportCommitOutcome.Blocked => BlockedFinding(unitId, result),
                ImportCommitOutcome.Conflict => Finding(
                    unitId,
                    RecoveryOutcome.NeedsAttention,
                    "IMPORT_COMMIT_CONFLICT",
                    "The persisted Import commit encountered conflicting durable authority."),
                ImportCommitOutcome.Cancelled => Finding(
                    unitId,
                    RecoveryOutcome.Completed,
                    "IMPORT_COMMIT_CANCELLED",
                    "The cancelled Import commit is durably settled and requires no recovery."),
                _ => Finding(
                    unitId,
                    RecoveryOutcome.Fatal,
                    "IMPORT_COMMIT_UNKNOWN_OUTCOME",
                    "The persisted Import commit returned an unsupported recovery outcome."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return Finding(
                unitId,
                RecoveryOutcome.Fatal,
                "IMPORT_RECOVERY_DATABASE_FAILURE",
                "The catalog failed while recovering a persisted Import commit.");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or FormatException
            or System.Text.Json.JsonException
            or CatalogInvariantException)
        {
            return Finding(
                unitId,
                RecoveryOutcome.NeedsAttention,
                "IMPORT_RECOVERY_AMBIGUOUS",
                "Persisted Import recovery facts are incomplete or inconsistent and were left unchanged.");
        }
    }

    private async Task<RecoveryFinding> ReconcileAsync(
        PathObligation obligation,
        string operationKind,
        Func<CancellationToken, Task<StorageOperationResult>> reconcile,
        Func<CancellationToken, Task<Guid?>> resolveManifestOwner,
        CancellationToken cancellationToken)
    {
        StorageOperationResult result;
        try
        {
            result = await reconcile(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return obligation.ToFinding(
                RecoveryOutcome.Fatal,
                "PATH_RECOVERY_DATABASE_FAILURE",
                "The catalog failed while resuming a persisted placement obligation.");
        }
        catch (Exception exception) when (exception is CatalogInvariantException
            or CatalogConcurrencyConflictException
            or InvalidOperationException)
        {
            return obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_AMBIGUOUS",
                "Persisted placement facts are incomplete or inconsistent and were left unchanged.");
        }

        if (result.Status == StorageOperationStatus.Success)
        {
            return await RefreshManifestAsync(
                    obligation, operationKind, resolveManifestOwner, cancellationToken)
                .ConfigureAwait(false);
        }

        return result.Status switch
        {
            StorageOperationStatus.AlreadyCompleted => obligation.ToFinding(
                RecoveryOutcome.Completed,
                operationKind + "_RECONCILED",
                "The persisted placement obligation was already at its canonical target."),
            StorageOperationStatus.Cancelled => obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_INCOMPLETE",
                "Reconciliation stopped at a safe checkpoint and remains resumable."),
            _ => obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_NEEDS_ATTENTION",
                result.SafeErrorDetail
                    ?? "Reconciliation could not continue safely from current durable facts."),
        };
    }

    private async Task<RecoveryFinding> RefreshManifestAsync(
        PathObligation obligation,
        string operationKind,
        Func<CancellationToken, Task<Guid?>> resolveManifestOwner,
        CancellationToken cancellationToken)
    {
        var converged = obligation.ToFinding(
            RecoveryOutcome.Completed,
            operationKind + "_RECONCILED",
            "The persisted placement obligation reached its canonical target.");

        var profileId = await resolveManifestOwner(cancellationToken).ConfigureAwait(false);
        if (profileId is not { } owner)
        {
            return converged;
        }

        var refresh = await _manifestWriter
            .RegenerateManifestAsync(_catalog, owner, cancellationToken)
            .ConfigureAwait(false);
        return refresh.IsSuccess
            ? converged
            : obligation.ToFinding(
                RecoveryOutcome.NeedsAttention,
                operationKind + "_MANIFEST_STALE",
                "Placement converged, but the affected profile.json could not be refreshed.");
    }

    private async Task<Guid?> ReadOwnerProfileIdAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT profile_id
            FROM profile_assets
            WHERE asset_id = $assetId AND relation_type = 'OWNER';
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var owner = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return owner is string value ? DbGuid.Parse(value) : null;
    }

    private static RecoveryFinding Finding(
        Guid unitId,
        RecoveryOutcome outcome,
        string code,
        string detail) =>
        new(unitId, "ImportUnit", unitId, outcome, code, detail);

    private static RecoveryFinding BlockedFinding(Guid unitId, ImportCommitResult result)
    {
        var blocker = result.Blockers.FirstOrDefault();
        return Finding(
            unitId,
            RecoveryOutcome.NeedsAttention,
            blocker?.Code ?? "IMPORT_COMMIT_BLOCKED",
            blocker?.Message
                ?? "The persisted Import commit could not advance safely from current durable facts.");
    }

    private async Task<IReadOnlyList<PathObligation>> ReadObligationsAsync(
        string sql,
        string entityType,
        CancellationToken cancellationToken)
    {
        var obligations = new List<PathObligation>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            obligations.Add(new PathObligation(
                entityType,
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1))));
        }

        return obligations;
    }

    private sealed record PathObligation(
        string EntityType,
        Guid EntityId,
        Guid OperationId)
    {
        public RecoveryFinding ToFinding(RecoveryOutcome outcome, string code, string safeDetail) =>
            new(OperationId, EntityType, EntityId, outcome, code, safeDetail);
    }
}
