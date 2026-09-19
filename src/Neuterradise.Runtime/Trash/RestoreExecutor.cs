using System.IO;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public sealed class RestoreExecutor
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly TimeProvider _timeProvider;

    public RestoreExecutor(
        CatalogDb catalog,
        ManagedMoveExecutor moveExecutor,
        TimeProvider? timeProvider = null,
        ManagedPathPlanner? pathPlanner = null,
        ProfileManifestWriter? manifestWriter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_catalog.Paths.Root);
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<AssetRestoreOutcome>> RestoreAssetAsync(
        Guid trashEntryId,
        Guid? targetOwnerProfileId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));

        AssetTrashPlan plan;
        AssetTrashState asset;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await TrashCoordinator.ReadTrashEntryAsync(connection, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null || entry.EntityType != TrashEntityType.Asset)
            {
                return OperationResult<AssetRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var parsed = AssetTrashPlan.FromJson(entry.PlanJson);
            if (parsed is null || entry.RecoveryRelativePath is null)
            {
                return OperationResult<AssetRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "This Trash record cannot be read, so its media cannot be restored automatically.");
            }

            plan = parsed;

            var current = await TrashCoordinator.ReadAssetTrashStateAsync(
                    connection, transaction: null, entry.EntityId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return OperationResult<AssetRestoreOutcome>.NotFound(
                    OperationErrorCode.AssetNotFound,
                    "That media item no longer exists.");
            }

            if (entry.State == TrashEntryState.Restored
                && current.State == AssetState.Active
                && current.OwnerProfileId is { } restoredOwnerId
                && current.CurrentManagedRelativePath is { } restoredPath
                && current.CurrentManagedFileName is { } restoredFileName)
            {
                var priorOutcome = OperationResult<AssetRestoreOutcome>.Success(
                    new AssetRestoreOutcome(
                        trashEntryId,
                        current.AssetId,
                        restoredOwnerId,
                        restoredPath,
                        restoredFileName,
                        current.RowVersion),
                    parsed.OperationId);
                await RefreshRelatedProjectionsAfterRestoreAsync(current.AssetId, cancellationToken)
                    .ConfigureAwait(false);
                return await RefreshManifestAfterRestoreAsync(
                        restoredOwnerId, priorOutcome, parsed.OperationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (entry.State != TrashEntryState.InTrash)
            {
                return OperationResult<AssetRestoreOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "That Trash record is not in a state that can be restored.");
            }

            if (current.State != AssetState.Trashed)
            {
                return OperationResult<AssetRestoreOutcome>.Conflict(
                    OperationErrorCode.AssetNotActive,
                    "That media item is not in Trash.");
            }

            if (current.Sha256 is null || current.ByteLength is null || current.StorageToken is null)
            {
                return OperationResult<AssetRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.AssetContentMismatch,
                    "This media item's content record is incomplete, so its recovery bytes cannot be verified.");
            }

            asset = current;
        }

        if (plan.RestoreCheckpoint is null)
        {
            var ownerId = targetOwnerProfileId ?? plan.OwnerProfileId;
            var target = await PlanCurrentRestoreTargetAsync(asset, plan, ownerId, cancellationToken)
                .ConfigureAwait(false);
            if (target.Failure is not null)
            {
                return target.Failure;
            }

            var checkpoint = new AssetRestoreCheckpoint(
                ownerId,
                VaultPathArea.TrashAssets,
                plan.RecoveryRelativePath,
                target.TargetManagedRelativePath!,
                target.TargetManagedFileName!);
            var persisted = await PersistRestoreCheckpointAsync(plan, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.IsSuccess || persisted.Value is null)
            {
                return Propagate<AssetTrashPlan, AssetRestoreOutcome>(persisted);
            }

            plan = persisted.Value;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var checkpoint = plan.RestoreCheckpoint!;
            if (plan.PackageComponents is { Count: > 0 } packageComponents)
            {
                foreach (var comp in packageComponents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceRelative = checkpoint.SourceArea == VaultPathArea.TrashAssets
                        ? comp.RecoveryRelativePath
                        : CombineRelative(checkpoint.SourceRelativePath, comp.ComponentRelativePath);

                    var move = await _moveExecutor.ExecuteRestoreCheckpointMoveAsync(
                        new ManagedRestoreCheckpointMoveRequest(
                            asset.AssetId,
                            checkpoint.SourceArea,
                            sourceRelative,
                            checkpoint.TargetManagedRelativePath,
                            comp.ComponentRelativePath,
                            comp.ByteLength,
                            comp.Sha256),
                        cancellationToken).ConfigureAwait(false);
                    if (!move.IsSuccess)
                    {
                        return TrashCoordinator.MapTrashMoveFailure<AssetRestoreOutcome>(move);
                    }
                }
            }
            else
            {
                var move = await _moveExecutor.ExecuteRestoreCheckpointMoveAsync(
                        new ManagedRestoreCheckpointMoveRequest(
                            asset.AssetId,
                            checkpoint.SourceArea,
                            checkpoint.SourceRelativePath,
                            checkpoint.TargetManagedRelativePath,
                            checkpoint.TargetManagedFileName,
                            asset.ByteLength!.Value,
                            asset.Sha256!),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!move.IsSuccess)
                {
                    return TrashCoordinator.MapTrashMoveFailure<AssetRestoreOutcome>(move);
                }
            }

            var desiredOwnerId = targetOwnerProfileId ?? checkpoint.OwnerProfileId;
            var currentTarget = await PlanCurrentRestoreTargetAsync(
                    asset, plan, desiredOwnerId, cancellationToken)
                .ConfigureAwait(false);
            if (currentTarget.Failure is not null)
            {
                return currentTarget.Failure;
            }

            if (checkpoint.OwnerProfileId != desiredOwnerId
                || checkpoint.TargetManagedRelativePath != currentTarget.TargetManagedRelativePath
                || checkpoint.TargetManagedFileName != currentTarget.TargetManagedFileName)
            {
                var next = new AssetRestoreCheckpoint(
                    desiredOwnerId,
                    VaultPathArea.Profiles,
                    $"{checkpoint.TargetManagedRelativePath}/{checkpoint.TargetManagedFileName}",
                    currentTarget.TargetManagedRelativePath!,
                    currentTarget.TargetManagedFileName!);
                var persisted = await PersistRestoreCheckpointAsync(plan, next, cancellationToken)
                    .ConfigureAwait(false);
                if (!persisted.IsSuccess || persisted.Value is null)
                {
                    return Propagate<AssetTrashPlan, AssetRestoreOutcome>(persisted);
                }

                plan = persisted.Value;
                continue;
            }

            var committed = await CommitAssetRestoreAsync(
                    trashEntryId,
                    asset,
                    plan,
                    checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return committed;
            }

            await RefreshRelatedProjectionsAfterRestoreAsync(plan.AssetId, cancellationToken)
                .ConfigureAwait(false);
            return await RefreshManifestAfterRestoreAsync(
                    checkpoint.OwnerProfileId, committed, plan.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return OperationResult<AssetRestoreOutcome>.Conflict(
            OperationErrorCode.TrashPlanStale,
            "The destination Profile kept changing while this media item was being restored. Retry when that change is finished.");
    }

    public async Task<OperationResult<ProfileRestoreOutcome>> RestoreProfileAsync(
        Guid trashEntryId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(trashEntryId, nameof(trashEntryId));

        TrashEntryRow entry;
        ProfileTrashPlan? plan;
        ProfileTrashState profile;
        await using (var readConnection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                    readConnection, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (currentEntry is null || currentEntry.EntityType != TrashEntityType.Profile)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            entry = currentEntry;
            plan = ProfileTrashPlan.FromJson(entry.PlanJson);
            var currentProfile = await TrashCoordinator.ReadProfileTrashStateAsync(
                    readConnection, transaction: null, entry.EntityId, cancellationToken)
                .ConfigureAwait(false);
            if (currentProfile is null)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            profile = currentProfile;
        }

        if (entry.State == TrashEntryState.Restored && !profile.IsTrashed)
        {
            var prior = OperationResult<ProfileRestoreOutcome>.Success(
                new ProfileRestoreOutcome(trashEntryId, profile.ProfileId, profile.RowVersion),
                plan?.OperationId);
            return await RefreshManifestAfterRestoreAsync(
                    profile.ProfileId, prior, plan?.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (entry.State != TrashEntryState.InTrash)
        {
            return OperationResult<ProfileRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "That Trash record is not in a state that can be restored.");
        }

        if (!profile.IsTrashed)
        {
            return OperationResult<ProfileRestoreOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile is not in Trash.");
        }

        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile has no current managed folder, so its recovery material cannot be restored safely.");
        }

        var recoveryRelativePath = entry.RecoveryRelativePath ?? $"_trash/profiles/{profile.ProfileId:D}";
        var move = await _moveExecutor.ExecuteProfileManifestRestoreMoveAsync(
                new ManagedProfileManifestRestoreRequest(
                    profile.ProfileId,
                    recoveryRelativePath,
                    profile.CurrentManagedRelativePath),
                cancellationToken)
            .ConfigureAwait(false);
        if (!move.IsSuccess && move.Status != StorageOperationStatus.SourceMissing)
        {
            return MapProfileManifestRestoreFailure(move);
        }

        OperationResult<ProfileRestoreOutcome> committed;
        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var currentEntry = await TrashCoordinator.ReadTrashEntryAsync(
                    connection, transaction, trashEntryId, cancellationToken)
                .ConfigureAwait(false);
            var currentProfile = await TrashCoordinator.ReadProfileTrashStateAsync(
                    connection, transaction, profile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (currentEntry is null || currentProfile is null)
            {
                return OperationResult<ProfileRestoreOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            if (!currentProfile.IsTrashed || currentProfile.RowVersion != profile.RowVersion)
            {
                return OperationResult<ProfileRestoreOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile changed while it was being restored.");
            }

            var now = DbTime.Format(_timeProvider.GetUtcNow());
            var newRowVersion = currentProfile.RowVersion + 1;
            var updated = await TrashCoordinator.ExecuteAsync(
                transaction,
                """
                UPDATE profiles
                SET trashed_at_ms = NULL,
                    updated_at_ms = $now,
                    row_version = $newRowVersion
                WHERE profile_id = $profileId AND row_version = $expectedRowVersion AND trashed_at_ms IS NOT NULL;
                """,
                cancellationToken,
                ("$now", now),
                ("$newRowVersion", newRowVersion),
                ("$profileId", DbGuid.Format(currentProfile.ProfileId)),
                ("$expectedRowVersion", currentProfile.RowVersion)).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new CatalogInvariantException(
                    $"Profile {currentProfile.ProfileId:D} changed while it was being restored.");
            }

            await TrashCoordinator.CheckpointTrashEntryAsync(
                transaction,
                trashEntryId,
                TrashEntryState.Restored,
                recoveryRelativePath,
                completedAtMs: now,
                currentEntry.RowVersion,
                now,
                cancellationToken).ConfigureAwait(false);

            await TrashCoordinator.AppendActivityAsync(
                transaction,
                ActivityEventType.ProfileRestored,
                currentProfile.ProfileId,
                assetId: null,
                plan?.OperationId,
                now,
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [currentProfile.ProfileId],
                CatalogInvalidationDomain.Profile,
                newRowVersion));
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [currentProfile.ProfileId],
                CatalogInvalidationDomain.Trash,
                0));
            transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = OperationResult<ProfileRestoreOutcome>.Success(
                new ProfileRestoreOutcome(trashEntryId, currentProfile.ProfileId, newRowVersion),
                plan?.OperationId);
        }

        return await RefreshManifestAfterRestoreAsync(
                profile.ProfileId, committed, plan?.OperationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task RefreshRelatedProjectionsAfterRestoreAsync(
        Guid assetId,
        CancellationToken cancellationToken) =>
        new RelatedEvidenceProjector(_catalog, _timeProvider)
            .RefreshRelatedProjectionsForAssetAsync(assetId, cancellationToken);

    private async Task<OperationResult<T>> RefreshManifestAfterRestoreAsync<T>(
        Guid profileId,
        OperationResult<T> committed,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        var refresh = await _manifestWriter.RegenerateManifestAsync(_catalog, profileId, cancellationToken)
            .ConfigureAwait(false);
        return refresh.IsSuccess
            ? committed
            : OperationResult<T>.NeedsAttention(
                OperationErrorCode.ProfileManifestWriteFailed,
                "The restore committed safely, but profile.json could not be refreshed from current authority. Retry the restore to repair it.",
                operationId);
    }

    private async Task<RestoreTargetResult> PlanCurrentRestoreTargetAsync(
        AssetTrashState asset,
        AssetTrashPlan plan,
        Guid ownerProfileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await TrashCoordinator.ReadProfileTrashStateAsync(
                connection, transaction: null, ownerProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            return RestoreTargetResult.Rejected(OperationResult<AssetRestoreOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "The Profile this media item would return to no longer exists. Choose another Profile."));
        }

        if (owner.IsTrashed)
        {
            return RestoreTargetResult.Rejected(OperationResult<AssetRestoreOutcome>.Conflict(
                OperationErrorCode.RestoreOwnerUnavailable,
                "The Profile this media item would return to is itself in Trash. Choose another Profile."));
        }

        return PlanRestoreTarget(asset, plan, owner);
    }

    private RestoreTargetResult PlanRestoreTarget(
        AssetTrashState asset,
        AssetTrashPlan plan,
        ProfileTrashState owner)
    {
        if (owner.StorageToken is null)
        {
            return RestoreTargetResult.Rejected(OperationResult<AssetRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile has no managed folder yet, so media cannot be restored into it."));
        }

        try
        {
            if (asset.MediaType == MediaType.Model && plan.PackageComponents is { Count: > 0 })
            {
                var primaryComponentPath = plan.PackageComponents
                    .FirstOrDefault(c => c.Role == ComponentRole.Primary)
                    ?.ComponentRelativePath
                    ?? plan.CurrentManagedFileName;

                var pkgPlan = _pathPlanner.AllocateModelPackagePlan(
                    owner.ProfileId,
                    owner.Label,
                    new ProfileStorageToken(owner.StorageToken),
                    asset.AssetId,
                    new AssetStorageToken(asset.StorageToken!),
                    primaryComponentPath,
                    candidate =>
                    {
                        try
                        {
                            return Directory.Exists(_catalog.Paths.ResolveVaultRelativePath(
                                candidate.PackageDirectoryRelativePath));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            return true;
                        }
                    });
                return RestoreTargetResult.Accepted(
                    pkgPlan.PackageDirectoryRelativePath,
                    pkgPlan.PrimaryFileName);
            }

            var planned = _pathPlanner.AllocateAssetPlan(
                owner.ProfileId,
                owner.Label,
                new ProfileStorageToken(owner.StorageToken),
                asset.AssetId,
                new AssetStorageToken(asset.StorageToken!),
                asset.MediaType,
                Path.GetExtension(plan.CurrentManagedFileName),
                candidate =>
                {
                    try
                    {
                        return File.Exists(_catalog.Paths.ResolveVaultRelativePath(
                            candidate.ManagedFileRelativePath!));
                    }
                    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        return true;
                    }
                });
            var targetFilePath = planned.ManagedFileRelativePath!;
            return RestoreTargetResult.Accepted(
                targetFilePath[..targetFilePath.LastIndexOf('/')],
                planned.ManagedFileName!);
        }
        catch (ArgumentException)
        {
            return RestoreTargetResult.Rejected(OperationResult<AssetRestoreOutcome>.NeedsAttention(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "That Profile's stored folder or this item's file naming needs repair before the restore."));
        }
    }

    private async Task<OperationResult<AssetTrashPlan>> PersistRestoreCheckpointAsync(
        AssetTrashPlan expectedPlan,
        AssetRestoreCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);
        var entry = await TrashCoordinator.ReadTrashEntryAsync(
                connection, transaction, expectedPlan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var currentPlan = AssetTrashPlan.FromJson(entry?.PlanJson);
        if (entry is null || currentPlan is null)
        {
            return OperationResult<AssetTrashPlan>.NeedsAttention(
                OperationErrorCode.TrashPlanUnreadable,
                "The durable Trash plan cannot be checkpointed safely.");
        }

        if (entry.State != TrashEntryState.InTrash)
        {
            return OperationResult<AssetTrashPlan>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "That Trash record is no longer available for restore.");
        }

        if (currentPlan.RestoreCheckpoint == checkpoint)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<AssetTrashPlan>.Success(currentPlan, currentPlan.OperationId);
        }

        if (currentPlan.RestoreCheckpoint != expectedPlan.RestoreCheckpoint)
        {
            return OperationResult<AssetTrashPlan>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "Another restore attempt advanced the physical checkpoint. Reload and retry.");
        }

        var updatedPlan = currentPlan with { RestoreCheckpoint = checkpoint };
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            entry.TrashEntryId,
            entry.State,
            entry.RecoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken,
            updatedPlan.ToJson()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult<AssetTrashPlan>.Success(updatedPlan, updatedPlan.OperationId);
    }

    private static OperationResult<ProfileRestoreOutcome> MapProfileManifestRestoreFailure(
        StorageOperationResult result) => result.Status switch
        {
            StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed =>
                OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The Profile recovery manifest does not match the Profile being restored."),
            StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision =>
                OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                    OperationErrorCode.TrashDestinationConflict,
                    "A manifest already occupies the current Profile folder, so nothing was overwritten."),
            StorageOperationStatus.PathOutsideVault => OperationResult<ProfileRestoreOutcome>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "A Profile recovery location falls outside its exact managed boundary."),
            StorageOperationStatus.Cancelled => OperationResult<ProfileRestoreOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest restore was cancelled before it completed."),
            _ => OperationResult<ProfileRestoreOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest could not be restored. The operation remains safe to retry."),
        };

    private async Task<OperationResult<AssetRestoreOutcome>> CommitAssetRestoreAsync(
        Guid trashEntryId,
        AssetTrashState asset,
        AssetTrashPlan plan,
        AssetRestoreCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await TrashCoordinator.ReadTrashEntryAsync(connection, transaction, trashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var current = await TrashCoordinator.ReadAssetTrashStateAsync(
                connection, transaction, asset.AssetId, cancellationToken).ConfigureAwait(false);
        if (entry is null || current is null)
        {
            return OperationResult<AssetRestoreOutcome>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }

        var owner = await TrashCoordinator.ReadProfileTrashStateAsync(
                connection, transaction, checkpoint.OwnerProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null || owner.IsTrashed)
        {
            return OperationResult<AssetRestoreOutcome>.Conflict(
                OperationErrorCode.RestoreOwnerUnavailable,
                "The selected destination Profile is no longer available. Choose another Profile.");
        }

        var currentTarget = PlanRestoreTarget(asset, plan, owner);
        if (currentTarget.Failure is not null
            || currentTarget.TargetManagedRelativePath != checkpoint.TargetManagedRelativePath
            || currentTarget.TargetManagedFileName != checkpoint.TargetManagedFileName)
        {
            return OperationResult<AssetRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "The destination Profile changed after the physical restore checkpoint. Retry to move the verified bytes to its current legal path.");
        }

        if (current.State == AssetState.Active && entry.State == TrashEntryState.Restored)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<AssetRestoreOutcome>.Success(
                new AssetRestoreOutcome(
                    trashEntryId,
                    asset.AssetId,
                    checkpoint.OwnerProfileId,
                    checkpoint.TargetManagedRelativePath,
                    checkpoint.TargetManagedFileName,
                    current.RowVersion),
                plan.OperationId);
        }

        if (current.State != AssetState.Trashed || current.RowVersion != asset.RowVersion)
        {
            return OperationResult<AssetRestoreOutcome>.Conflict(
                OperationErrorCode.TrashPlanStale,
                "This media item changed while it was being restored.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = current.RowVersion + 1;

        var updatedAsset = await TrashCoordinator.ExecuteAsync(
            transaction,
            """
            UPDATE assets
            SET state = 'ACTIVE',
                trashed_at_ms = NULL,
                current_managed_relative_path = $path,
                current_managed_file_name = $fileName,
                target_managed_relative_path = $path,
                target_managed_file_name = $fileName,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = $newRowVersion
            WHERE asset_id = $assetId AND row_version = $expectedRowVersion AND state = 'TRASHED';
            """,
            cancellationToken,
            ("$path", checkpoint.TargetManagedRelativePath),
            ("$fileName", checkpoint.TargetManagedFileName),
            ("$newRowVersion", newRowVersion),
            ("$assetId", DbGuid.Format(asset.AssetId)),
            ("$expectedRowVersion", current.RowVersion)).ConfigureAwait(false);
        if (updatedAsset != 1)
        {
            throw new CatalogInvariantException(
                $"Asset {asset.AssetId:D} changed while it was being restored.");
        }

        await TrashCoordinator.ExecuteAsync(
            transaction,
            """
            INSERT INTO profile_assets(profile_id, asset_id, relation_type, provenance_key, created_at_ms)
            VALUES ($profileId, $assetId, 'OWNER', NULL, $now);
            """,
            cancellationToken,
            ("$profileId", DbGuid.Format(checkpoint.OwnerProfileId)),
            ("$assetId", DbGuid.Format(asset.AssetId)),
            ("$now", now)).ConfigureAwait(false);

        await TrashCoordinator.CheckpointTrashEntryAsync(
            transaction,
            trashEntryId,
            TrashEntryState.Restored,
            entry.RecoveryRelativePath,
            completedAtMs: now,
            entry.RowVersion,
            now,
            cancellationToken).ConfigureAwait(false);

        await TrashCoordinator.AppendActivityAsync(
            transaction,
            ActivityEventType.AssetRestored,
            checkpoint.OwnerProfileId,
            asset.AssetId,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [asset.AssetId],
            CatalogInvalidationDomain.Media,
            newRowVersion));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [checkpoint.OwnerProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [asset.AssetId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<AssetRestoreOutcome>.Success(
            new AssetRestoreOutcome(
                trashEntryId,
                asset.AssetId,
                checkpoint.OwnerProfileId,
                checkpoint.TargetManagedRelativePath,
                checkpoint.TargetManagedFileName,
                newRowVersion),
            plan.OperationId);
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName.TrimStart('/', '\\')}";
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private static OperationResult<TTarget> Propagate<TSource, TTarget>(OperationResult<TSource> source) =>
        new(source.Status, default, source.ErrorCode, source.UserMessage, source.OperationId);

    private sealed record RestoreTargetResult(
        string? TargetManagedRelativePath,
        string? TargetManagedFileName,
        OperationResult<AssetRestoreOutcome>? Failure)
    {
        public static RestoreTargetResult Accepted(string targetManagedRelativePath, string targetManagedFileName) =>
            new(targetManagedRelativePath, targetManagedFileName, null);

        public static RestoreTargetResult Rejected(OperationResult<AssetRestoreOutcome> failure) =>
            new(null, null, failure);
    }
}

public sealed record AssetRestoreOutcome(
    Guid TrashEntryId,
    Guid AssetId,
    Guid OwnerProfileId,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName,
    long AssetRowVersion);

public sealed record ProfileRestoreOutcome(
    Guid TrashEntryId,
    Guid ProfileId,
    long ProfileRowVersion);
