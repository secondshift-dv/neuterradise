using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Media;

public sealed class MediaOperations
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly VaultPaths _paths;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly ExplorerLocationProvider _explorerLocations;
    private readonly MediaClipboardWriter _clipboardWriter;
    private readonly TimeProvider _timeProvider;
    private readonly OperationExecution _execution;

    public MediaOperations(
        CatalogDb catalog,
        TimeProvider? timeProvider = null,
        ManagedPathPlanner? pathPlanner = null,
        ExplorerLocationProvider? explorerLocations = null,
        AssetOwnerRelocationEnqueue? ownerRelocationEnqueue = null,
        MediaClipboardWriter? clipboardWriter = null,
        StructuredDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _paths = catalog.Paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_paths.Root);
        _explorerLocations = explorerLocations ?? new ExplorerLocationProvider(catalog.Paths);
        _clipboardWriter = clipboardWriter ?? WriteToSystemClipboard;
        _execution = new OperationExecution(diagnostics, _timeProvider.AsClock());
    }

    /// <summary>Canonical operation kinds for the media command boundary (Section 7.1).</summary>
    public static class Kinds
    {
        public const string ChangePrimaryProfile = "MEDIA_CHANGE_PRIMARY_PROFILE";
        public const string AddProfileAssetAssociation = "MEDIA_ADD_PROFILE_ASSOCIATION";
        public const string RemoveProfileAssetAssociation = "MEDIA_REMOVE_PROFILE_ASSOCIATION";
        public const string OpenInDefaultApp = "MEDIA_OPEN_IN_DEFAULT_APP";
        public const string ShowInFolder = "MEDIA_SHOW_IN_FOLDER";
        public const string ResolveCurrentLocation = "MEDIA_RESOLVE_CURRENT_LOCATION";
    }

    public Task<OperationResult<ChangePrimaryProfileOutcome>> ChangePrimaryProfileAsync(
        ChangePrimaryProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _execution.RunAsync<ChangePrimaryProfileOutcome>(
            OperationContext.Start(Kinds.ChangePrimaryProfile, _timeProvider, cancellationToken)
                with { AssetId = request.AssetId, ProfileId = request.NewOwnerProfileId },
            context => ChangePrimaryProfileCoreAsync(request, context.CancellationToken));
    }

    private async Task<OperationResult<ChangePrimaryProfileOutcome>> ChangePrimaryProfileCoreAsync(
        ChangePrimaryProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.AssetId, nameof(request.AssetId));
        EnsureNonEmpty(request.NewOwnerProfileId, nameof(request.NewOwnerProfileId));

        AssetOwnerRelocationRequest? relocation = null;
        OperationResult<ChangePrimaryProfileOutcome> result;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var asset = await ReadAssetOwnershipStateAsync(transaction, request.AssetId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            { 
                return OperationResult<ChangePrimaryProfileOutcome>.NotFound(
                    OperationErrorCode.AssetNotFound,
                    "That media item no longer exists.");
            }

            if (asset.State != AssetState.Active || asset.IsTrashed)
            { 
                return OperationResult<ChangePrimaryProfileOutcome>.Conflict(
                    OperationErrorCode.AssetNotActive,
                    "Only active media can change its primary Profile.");
            }

            if (asset.CurrentOwnerProfileId is null)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.NeedsAttention(
                    OperationErrorCode.AssetOwnerConflict,
                    "This media item has no recorded owner and needs attention before it can be moved.");
            }

            if (asset.CurrentOwnerProfileId == request.NewOwnerProfileId)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return asset.PathState == ManagedPathState.None
                    ? OperationResult<ChangePrimaryProfileOutcome>.Success(
                        new ChangePrimaryProfileOutcome(
                            asset.AssetId,
                            request.NewOwnerProfileId,
                            asset.RowVersion,
                            asset.CurrentManagedRelativePath,
                            asset.CurrentManagedFileName,
                            asset.TargetManagedRelativePath,
                            asset.TargetManagedFileName,
                            ManagedPathState.None,
                            null))
                    : OperationResult<ChangePrimaryProfileOutcome>.Accepted(
                        new ChangePrimaryProfileOutcome(
                            asset.AssetId,
                            request.NewOwnerProfileId,
                            asset.RowVersion,
                            asset.CurrentManagedRelativePath,
                            asset.CurrentManagedFileName,
                            asset.TargetManagedRelativePath,
                            asset.TargetManagedFileName,
                            asset.PathState,
                            asset.ReconciliationOperationId),
                        asset.ReconciliationOperationId,
                        "This media item is already moving to that Profile.");
            }

            if (asset.RowVersion != request.ExpectedAssetRowVersion)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.Conflict(
                    OperationErrorCode.AssetOwnerConflict,
                    "This media item changed somewhere else. Reload it and try the move again.");
            }

            if (asset.PathState != ManagedPathState.None)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.Conflict(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "This media item is already being moved. Wait for that to finish, then try again.");
            }

            if (asset.Sha256 is null || asset.ByteLength is null)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.NeedsAttention(
                    OperationErrorCode.AssetContentMismatch,
                    "This media item's content record is incomplete and must be repaired before it can be moved.");
            }

            if (asset.StorageToken is null
                || asset.CurrentManagedRelativePath is null
                || asset.CurrentManagedFileName is null)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.NeedsAttention(
                    OperationErrorCode.CurrentPathMissing,
                    "This media item has no recorded managed file yet, so it cannot be moved to another Profile.");
            }

            var destination = await ReadDestinationProfileAsync(
                transaction, request.NewOwnerProfileId, cancellationToken).ConfigureAwait(false);
            if (destination is null)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (destination.IsTrashed)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "That Profile was moved to Trash and cannot take ownership of media.");
            }

            if (destination.StorageToken is null)
            {
                return OperationResult<ChangePrimaryProfileOutcome>.NeedsAttention(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "That Profile has no managed folder yet, so media cannot be moved into it.");
            }

            string targetRelativePath;
            string targetFileName;
            try
            {
                var plan = _pathPlanner.PlanAsset(
                    destination.ProfileId,
                    destination.Label,
                    new ProfileStorageToken(destination.StorageToken),
                    asset.AssetId,
                    new AssetStorageToken(asset.StorageToken),
                    asset.MediaType,
                    Path.GetExtension(asset.CurrentManagedFileName));

                var targetFilePath = plan.ManagedFileRelativePath!;
                targetRelativePath = targetFilePath[..targetFilePath.LastIndexOf('/')];
                targetFileName = plan.ManagedFileName!;
            }
            catch (ArgumentException)
            {

                return OperationResult<ChangePrimaryProfileOutcome>.NeedsAttention(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "That Profile's stored folder or this item's file naming needs repair before the move.");
            }

            var oldOwnerProfileId = asset.CurrentOwnerProfileId.Value;
            var now = DbTime.Format(_timeProvider.GetUtcNow());
            var newRowVersion = asset.RowVersion + 1;

            await using (var updateOwner = transaction.CreateCommand(
                """
                UPDATE profile_assets
                SET profile_id = $newOwnerId,
                    created_at_ms = $now
                WHERE asset_id = $assetId AND relation_type = 'OWNER';
                """))
            {
                updateOwner.Parameters.AddWithValue("$newOwnerId", DbGuid.Format(request.NewOwnerProfileId));
                updateOwner.Parameters.AddWithValue("$now", now);
                updateOwner.Parameters.AddWithValue("$assetId", DbGuid.Format(request.AssetId));

                if (await updateOwner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CatalogInvariantException(
                        $"Ownership transfer failed for Asset {request.AssetId:D}.");
                }
            }

            await ClearAppearanceIfNoRemainingRelationAsync(
                transaction, oldOwnerProfileId, request.AssetId, cancellationToken).ConfigureAwait(false);

            var currentFilePath = $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}";
            var targetFilePathText = $"{targetRelativePath}/{targetFileName}";
            var alreadyAtTarget = string.Equals(currentFilePath, targetFilePathText, StringComparison.Ordinal);

            Guid? operationId = alreadyAtTarget ? null : Guid.NewGuid();

            await using (var updateAsset = transaction.CreateCommand(
                """
                UPDATE assets
                SET target_managed_relative_path = $targetPath,
                    target_managed_file_name = $targetFileName,
                    path_state = $pathState,
                    reconciliation_operation_id = $operationId,
                    row_version = $newRowVersion
                WHERE asset_id = $assetId AND row_version = $expectedRowVersion;
                """))
            {
                updateAsset.Parameters.AddWithValue("$targetPath", targetRelativePath);
                updateAsset.Parameters.AddWithValue("$targetFileName", targetFileName);
                updateAsset.Parameters.AddWithValue(
                    "$pathState",
                    DbEnum.Format(alreadyAtTarget ? ManagedPathState.None : ManagedPathState.Pending));
                updateAsset.Parameters.AddWithValue(
                    "$operationId",
                    operationId is null ? DBNull.Value : DbGuid.Format(operationId.Value));
                updateAsset.Parameters.AddWithValue("$newRowVersion", newRowVersion);
                updateAsset.Parameters.AddWithValue("$assetId", DbGuid.Format(request.AssetId));
                updateAsset.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedAssetRowVersion);

                if (await updateAsset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CatalogInvariantException(
                        $"Asset {request.AssetId:D} changed while its owner change was being written.");
                }
            }

            if (operationId is not null)
            {
                await PersistOwnerRelocationOperationAsync(
                    transaction,
                    request.AssetId,
                    operationId.Value,
                    request.NewOwnerProfileId,
                    targetRelativePath,
                    targetFileName,
                    now,
                    cancellationToken).ConfigureAwait(false);
                await ReconciliationJobAuthority.EnsureOwnerRelocationJobAsync(
                    transaction,
                    operationId.Value,
                    request.AssetId,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }

            await EnsureExactlyOneOwnerAsync(transaction, request.AssetId, cancellationToken).ConfigureAwait(false);

            await AppendActivityAsync(
                transaction,
                ActivityEventType.PrimaryProfileChanged,
                request.NewOwnerProfileId,
                request.AssetId,
                operationId,
                BuildOwnerChangePayload(oldOwnerProfileId, request.NewOwnerProfileId, targetFilePathText),
                DbTime.Format(_timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.AssetId],
                CatalogInvalidationDomain.Media,
                newRowVersion));
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [oldOwnerProfileId, request.NewOwnerProfileId],
                CatalogInvalidationDomain.Profile,
                0));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var outcome = new ChangePrimaryProfileOutcome(
                request.AssetId,
                request.NewOwnerProfileId,
                newRowVersion,
                asset.CurrentManagedRelativePath,
                asset.CurrentManagedFileName,
                targetRelativePath,
                targetFileName,
                alreadyAtTarget ? ManagedPathState.None : ManagedPathState.Pending,
                operationId);

            if (alreadyAtTarget)
            {

                return OperationResult<ChangePrimaryProfileOutcome>.Success(outcome);
            }

            relocation = new AssetOwnerRelocationRequest(
                request.AssetId,
                operationId!.Value,
                request.NewOwnerProfileId,
                targetRelativePath,
                targetFileName);
            result = OperationResult<ChangePrimaryProfileOutcome>.Accepted(
                outcome,
                operationId,
                "The new owner is saved. The file is being moved into that Profile's folder in the background.");
        }

        if (relocation is not null)
        {
            JobSignals.Raise();
        }

        return result;
    }

    public Task<OperationResult<ProfileAssetAssociationOutcome>> AddProfileAssetAssociationAsync(
        ProfileAssetAssociationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _execution.RunAsync<ProfileAssetAssociationOutcome>(
            OperationContext.Start(Kinds.AddProfileAssetAssociation, _timeProvider, cancellationToken)
                with { AssetId = request.AssetId, ProfileId = request.ProfileId },
            context => AddProfileAssetAssociationCoreAsync(request, context.CancellationToken));
    }

    private async Task<OperationResult<ProfileAssetAssociationOutcome>> AddProfileAssetAssociationCoreAsync(
        ProfileAssetAssociationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.AssetId, nameof(request.AssetId));

        if (!IsAssociableRelation(request.RelationType))
        {
            return OperationResult<ProfileAssetAssociationOutcome>.Validation(
                OperationErrorCode.AssetOwnerConflict,
                "Ownership is changed with Change Primary Profile, not by adding an association.");
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var asset = await ReadAssetOwnershipStateAsync(transaction, request.AssetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return OperationResult<ProfileAssetAssociationOutcome>.NotFound(
                OperationErrorCode.AssetNotFound,
                "That media item no longer exists.");
        }

        if (asset.State != AssetState.Active || asset.IsTrashed)
        {
            return OperationResult<ProfileAssetAssociationOutcome>.Conflict(
                OperationErrorCode.AssetNotActive,
                "Only active media can be linked to a Profile.");
        }

        var profile = await ReadDestinationProfileAsync(transaction, request.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult<ProfileAssetAssociationOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "That Profile no longer exists.");
        }

        if (profile.IsTrashed)
        {
            return OperationResult<ProfileAssetAssociationOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile was moved to Trash and cannot be linked to media.");
        }

        var relationText = DbEnum.Format(request.RelationType);

        var alreadyPresent = await RelationExistsAsync(
            transaction, request.ProfileId, request.AssetId, relationText, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyPresent)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<ProfileAssetAssociationOutcome>.Success(
                new ProfileAssetAssociationOutcome(
                    request.ProfileId, request.AssetId, request.RelationType, WasChanged: false));
        }

        await using (var insert = transaction.CreateCommand(
            """
            INSERT INTO profile_assets(profile_id, asset_id, relation_type, provenance_key, created_at_ms)
            VALUES ($profileId, $assetId, $relationType, $provenanceKey, $now);
            """))
        {
            insert.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            insert.Parameters.AddWithValue("$assetId", DbGuid.Format(request.AssetId));
            insert.Parameters.AddWithValue("$relationType", relationText);
            insert.Parameters.AddWithValue(
                "$provenanceKey",
                request.ProvenanceKey is null ? DBNull.Value : request.ProvenanceKey);
            insert.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendActivityAsync(
            transaction,
            ActivityEventType.AssetAssociationAdded,
            request.ProfileId,
            request.AssetId,
            operationId: null,
            BuildAssociationPayload(request.RelationType),
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        // Check whether this new relation is import-attributed (unpublished Task-G delta).
        var isImportAttributed = await IsRelationImportAttributedAsync(
            transaction, request.ProfileId, request.AssetId, relationText, cancellationToken).ConfigureAwait(false);

        if (!isImportAttributed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.AssetId],
                CatalogInvalidationDomain.Media,
                0));
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.ProfileId],
                CatalogInvalidationDomain.Profile,
                0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileAssetAssociationOutcome>.Success(
            new ProfileAssetAssociationOutcome(
                request.ProfileId, request.AssetId, request.RelationType, WasChanged: true));
    }

    public Task<OperationResult<ProfileAssetAssociationOutcome>> RemoveProfileAssetAssociationAsync(
        ProfileAssetAssociationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _execution.RunAsync<ProfileAssetAssociationOutcome>(
            OperationContext.Start(Kinds.RemoveProfileAssetAssociation, _timeProvider, cancellationToken)
                with { AssetId = request.AssetId, ProfileId = request.ProfileId },
            context => RemoveProfileAssetAssociationCoreAsync(request, context.CancellationToken));
    }

    private async Task<OperationResult<ProfileAssetAssociationOutcome>> RemoveProfileAssetAssociationCoreAsync(
        ProfileAssetAssociationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request.ProfileId));
        EnsureNonEmpty(request.AssetId, nameof(request.AssetId));

        if (!IsAssociableRelation(request.RelationType))
        {
            return OperationResult<ProfileAssetAssociationOutcome>.Validation(
                OperationErrorCode.AssetOwnerConflict,
                "Ownership cannot be removed. Move it to another Profile with Change Primary Profile instead.");
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var relationText = DbEnum.Format(request.RelationType);

        // Capture whether the relation to be removed is import-attributed before deleting it.
        var wasImportAttributed = await IsRelationImportAttributedAsync(
            transaction, request.ProfileId, request.AssetId, relationText, cancellationToken).ConfigureAwait(false);

        int removed;
        await using (var delete = transaction.CreateCommand(
            """
            DELETE FROM profile_assets
            WHERE profile_id = $profileId AND asset_id = $assetId AND relation_type = $relationType;
            """))
        {
            delete.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            delete.Parameters.AddWithValue("$assetId", DbGuid.Format(request.AssetId));
            delete.Parameters.AddWithValue("$relationType", relationText);
            removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (removed == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<ProfileAssetAssociationOutcome>.Success(
                new ProfileAssetAssociationOutcome(
                    request.ProfileId, request.AssetId, request.RelationType, WasChanged: false));
        }

        await ClearAppearanceIfNoRemainingRelationAsync(
            transaction, request.ProfileId, request.AssetId, cancellationToken).ConfigureAwait(false);

        await AppendActivityAsync(
            transaction,
            ActivityEventType.AssetAssociationRemoved,
            request.ProfileId,
            request.AssetId,
            operationId: null,
            BuildAssociationPayload(request.RelationType),
            DbTime.Format(_timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        if (!wasImportAttributed)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.AssetId],
                CatalogInvalidationDomain.Media,
                0));
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.ProfileId],
                CatalogInvalidationDomain.Profile,
                0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileAssetAssociationOutcome>.Success(
            new ProfileAssetAssociationOutcome(
                request.ProfileId, request.AssetId, request.RelationType, WasChanged: true));
    }

    public Task<OperationResult> OpenInDefaultAppAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        _execution.RunAsync(
            OperationContext.Start(Kinds.OpenInDefaultApp, _timeProvider, cancellationToken)
                with { AssetId = assetId },
            context => OpenInDefaultAppCoreAsync(assetId, context.CancellationToken));

    private async Task<OperationResult> OpenInDefaultAppCoreAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var located = await ResolveCurrentLocationCoreAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return ToNonGeneric(located);
        }

        var launched = await _explorerLocations
            .OpenManagedFileInDefaultAppAsync(located.Value!.VaultRelativeFilePath, cancellationToken)
            .ConfigureAwait(false);

        return MapExternalActionStatus(
            launched.Status,
            missingMessage: "This media file is recorded but is not where it should be.",
            untrustedMessage: "This media item's recorded location could not be trusted, so nothing was opened.",
            cancelledMessage: "Opening this media item was cancelled.",
            failedMessage: "Windows could not open this media item with its default app.");
    }

    public Task<OperationResult> ShowInFolderAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        _execution.RunAsync(
            OperationContext.Start(Kinds.ShowInFolder, _timeProvider, cancellationToken)
                with { AssetId = assetId },
            context => ShowInFolderCoreAsync(assetId, context.CancellationToken));

    private async Task<OperationResult> ShowInFolderCoreAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var located = await ResolveCurrentLocationCoreAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return ToNonGeneric(located);
        }

        var shown = await _explorerLocations
            .ShowAssetInFolderAsync(located.Value!.VaultRelativeFilePath, cancellationToken)
            .ConfigureAwait(false);

        return MapExternalActionStatus(
            shown.Status,
            missingMessage: "This media file is recorded but is not where it should be.",
            untrustedMessage: "This media item's recorded location could not be trusted, so nothing was opened.",
            cancelledMessage: "Showing this media item was cancelled.",
            failedMessage: "Windows Explorer could not show this media item.");
    }

    public Task<OperationResult<MediaCurrentLocation>> ResolveCurrentLocationAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        _execution.RunAsync<MediaCurrentLocation>(
            OperationContext.Start(Kinds.ResolveCurrentLocation, _timeProvider, cancellationToken)
                with { AssetId = assetId },
            context => ResolveCurrentLocationCoreAsync(assetId, context.CancellationToken));

    private async Task<OperationResult<MediaCurrentLocation>> ResolveCurrentLocationCoreAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection);

        var asset = await ReadAssetOwnershipStateAsync(transaction, assetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return OperationResult<MediaCurrentLocation>.NotFound(
                OperationErrorCode.AssetNotFound,
                "That media item no longer exists.");
        }

        if (asset.PathState == ManagedPathState.NeedsAttention)
        {
            return OperationResult<MediaCurrentLocation>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "This media item's location needs attention before it can be opened.",
                asset.ReconciliationOperationId);
        }

        if (asset.CurrentManagedRelativePath is null || asset.CurrentManagedFileName is null)
        {
            return OperationResult<MediaCurrentLocation>.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "This media item has no managed file on disk yet.");
        }

        var vaultRelativeFilePath = $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}";

        string absolutePath;
        try
        {
            absolutePath = _paths.ResolveVaultRelativePath(vaultRelativeFilePath);
        }
        catch (ArgumentException)
        {
            return OperationResult<MediaCurrentLocation>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "This media item's recorded location is not inside the managed profiles area.");
        }

        return OperationResult<MediaCurrentLocation>.Success(
            new MediaCurrentLocation(
                assetId,
                vaultRelativeFilePath,
                absolutePath,
                asset.CurrentManagedFileName));
    }

    public OperationResult CopyCurrentPath(MediaCurrentLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return CopyToClipboard(location.AbsoluteFilePath);
    }

    public OperationResult CopyCurrentFilename(MediaCurrentLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return CopyToClipboard(location.CurrentManagedFileName);
    }

    private OperationResult CopyToClipboard(string text)
    {
        try
        {
            return _clipboardWriter(text)
                ? OperationResult.Success()
                : OperationResult.Failed(
                    OperationErrorCode.CurrentPathAmbiguous,
                    "The clipboard was not available, so nothing was copied.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return OperationResult.Failed(
                OperationErrorCode.CurrentPathAmbiguous,
                "The clipboard was not available, so nothing was copied.");
        }
    }

    private static bool WriteToSystemClipboard(string text)
    {
        Win32Clipboard.SetText(text);
        return true;
    }

    private static OperationResult ToNonGeneric(OperationResult<MediaCurrentLocation> result) =>
        result.WithoutValue();

    private static OperationResult MapExternalActionStatus(
        StorageOperationStatus status,
        string missingMessage,
        string untrustedMessage,
        string cancelledMessage,
        string failedMessage) => status switch
    {
        StorageOperationStatus.Success or StorageOperationStatus.AlreadyCompleted =>
            OperationResult.Success(),
        StorageOperationStatus.SourceMissing =>
            OperationResult.NeedsAttention(OperationErrorCode.CurrentPathMissing, missingMessage),
        StorageOperationStatus.PathOutsideVault or StorageOperationStatus.NeedsAttention =>
            OperationResult.NeedsAttention(OperationErrorCode.CurrentPathAmbiguous, untrustedMessage),
        StorageOperationStatus.Cancelled =>
            OperationResult.Cancelled(cancelledMessage),
        _ => OperationResult.Failed(OperationErrorCode.CurrentPathAmbiguous, failedMessage),
    };

    private static bool IsAssociableRelation(ProfileAssetRelation relation) =>
        relation is ProfileAssetRelation.Appears or ProfileAssetRelation.Manual;

    private static async Task<bool> IsRelationImportAttributedAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        string relationText,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT publication_import_unit_id
            FROM profile_assets
            WHERE profile_id = $profileId AND asset_id = $assetId AND relation_type = $relationType;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$relationType", relationText);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task<AssetOwnershipState?> ReadAssetOwnershipStateAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT a.state, a.trashed_at_ms, a.row_version, a.media_type, a.asset_storage_token,
                   a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.target_managed_relative_path, a.target_managed_file_name,
                   a.path_state, a.reconciliation_operation_id,
                   (SELECT pa.profile_id FROM profile_assets pa
                    WHERE pa.asset_id = a.asset_id AND pa.relation_type = 'OWNER')
            FROM assets a
            WHERE a.asset_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AssetOwnershipState(
            assetId,
            DbEnum.ParseAssetState(reader.GetString(0)),
            !reader.IsDBNull(1),
            reader.GetInt64(2),
            DbEnum.ParseMediaType(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DbEnum.ParseManagedPathState(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DbGuid.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DbGuid.Parse(reader.GetString(13)));
    }

    private static async Task<DestinationProfile?> ReadDestinationProfileAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, display_name, unknown_sequence, profile_storage_token, trashed_at_ms
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(0));

        var label = kind == ProfileKind.Unknown
            ? UnknownProfileRules.FormatDerivedLabel(reader.GetInt64(2))
            : reader.GetString(1);

        return new DestinationProfile(
            profileId,
            kind,
            label,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            !reader.IsDBNull(4));
    }

    private static async Task<bool> RelationExistsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        string relationText,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profile_assets
                WHERE profile_id = $profileId AND asset_id = $assetId AND relation_type = $relationType
            );
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$relationType", relationText);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ClearAppearanceIfNoRemainingRelationAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var checkRemaining = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profile_assets
                WHERE profile_id = $profileId AND asset_id = $assetId
            );
            """);
        checkRemaining.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        checkRemaining.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var hasRemainingRelation = Convert.ToInt32(
            await checkRemaining.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
        if (hasRemainingRelation)
        {
            return;
        }

        await using var clearAppearance = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_asset_id = CASE WHEN cover_asset_id = $assetId THEN NULL ELSE cover_asset_id END,
                banner_asset_id = CASE WHEN banner_asset_id = $assetId THEN NULL ELSE banner_asset_id END,
                row_version = row_version + 1
            WHERE profile_id = $profileId AND (cover_asset_id = $assetId OR banner_asset_id = $assetId);
            """);
        clearAppearance.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        clearAppearance.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        await clearAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureExactlyOneOwnerAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*) FROM profile_assets
            WHERE asset_id = $assetId AND relation_type = 'OWNER';
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var owners = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (owners != 1)
        {
            throw new CatalogInvariantException(
                $"ACTIVE Asset {assetId:D} postcondition failed: must have exactly one OWNER, found {owners}.");
        }
    }

    private static async Task PersistOwnerRelocationOperationAsync(
        CatalogTransaction transaction,
        Guid assetId,
        Guid operationId,
        Guid newOwnerProfileId,
        string targetRelativePath,
        string targetFileName,
        long nowMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO storage_operations(
                operation_id, kind, entity_type, entity_id, state, checkpoint_json,
                created_at_ms, updated_at_ms)
            VALUES (
                $operationId, 'OWNER_RELOCATION', 'ASSET', $entityId, 'PENDING', $checkpoint,
                $now, $now);
            """);
        insert.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        insert.Parameters.AddWithValue("$entityId", DbGuid.Format(assetId));
        insert.Parameters.AddWithValue(
            "$checkpoint",
            BuildOwnerRelocationCheckpoint(newOwnerProfileId, targetRelativePath, targetFileName));
        insert.Parameters.AddWithValue("$now", nowMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendActivityAsync(
        CatalogTransaction transaction,
        string eventType,
        Guid profileId,
        Guid assetId,
        Guid? operationId,
        string payloadJson,
        long occurredAtMs,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, asset_id, operation_id, payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $assetId, $operationId, $payloadJson, $occurredAtMs);
            """);
        insert.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$eventType", eventType);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        insert.Parameters.AddWithValue(
            "$operationId",
            operationId is null ? DBNull.Value : DbGuid.Format(operationId.Value));
        insert.Parameters.AddWithValue("$payloadJson", payloadJson);
        insert.Parameters.AddWithValue("$occurredAtMs", occurredAtMs);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildOwnerRelocationCheckpoint(
        Guid newOwnerProfileId,
        string targetRelativePath,
        string targetFileName)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("newOwnerProfileId", DbGuid.Format(newOwnerProfileId));
            writer.WriteString("targetManagedRelativePath", targetRelativePath);
            writer.WriteString("targetManagedFileName", targetFileName);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildOwnerChangePayload(
        Guid previousOwnerProfileId,
        Guid newOwnerProfileId,
        string targetManagedFilePath)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("previousOwnerProfileId", DbGuid.Format(previousOwnerProfileId));
            writer.WriteString("newOwnerProfileId", DbGuid.Format(newOwnerProfileId));
            writer.WriteString("targetManagedFilePath", targetManagedFilePath);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildAssociationPayload(ProfileAssetRelation relation)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("relationType", DbEnum.Format(relation));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private sealed record AssetOwnershipState(
        Guid AssetId,
        AssetState State,
        bool IsTrashed,
        long RowVersion,
        MediaType MediaType,
        string? StorageToken,
        string? Sha256,
        long? ByteLength,
        string? CurrentManagedRelativePath,
        string? CurrentManagedFileName,
        string? TargetManagedRelativePath,
        string? TargetManagedFileName,
        ManagedPathState PathState,
        Guid? ReconciliationOperationId,
        Guid? CurrentOwnerProfileId);

    private sealed record DestinationProfile(
        Guid ProfileId,
        ProfileKind Kind,
        string Label,
        string? StorageToken,
        bool IsTrashed);
}

public sealed record AssetOwnerRelocationRequest(
    Guid AssetId,
    Guid OperationId,
    Guid NewOwnerProfileId,
    string TargetManagedRelativePath,
    string TargetManagedFileName);

public delegate Task AssetOwnerRelocationEnqueue(
    AssetOwnerRelocationRequest request,
    CancellationToken cancellationToken);

public delegate bool MediaClipboardWriter(string text);

public sealed record MediaCurrentLocation(
    Guid AssetId,
    string VaultRelativeFilePath,
    string AbsoluteFilePath,
    string CurrentManagedFileName);

public sealed record ChangePrimaryProfileRequest(
    Guid AssetId,
    Guid NewOwnerProfileId,
    long ExpectedAssetRowVersion);

public sealed record ChangePrimaryProfileOutcome(
    Guid AssetId,
    Guid OwnerProfileId,
    long RowVersion,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string? TargetManagedRelativePath,
    string? TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId);

public sealed record ProfileAssetAssociationRequest(
    Guid ProfileId,
    Guid AssetId,
    ProfileAssetRelation RelationType,
    string? ProvenanceKey = null);

public sealed record ProfileAssetAssociationOutcome(
    Guid ProfileId,
    Guid AssetId,
    ProfileAssetRelation RelationType,
    bool WasChanged);
