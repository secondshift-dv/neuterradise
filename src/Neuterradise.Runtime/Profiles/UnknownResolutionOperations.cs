using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Profiles;

public sealed class UnknownResolutionOperations
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly StorageTokenAllocator _tokenAllocator;
    private readonly AssetOwnerRelocationEnqueue? _ownerRelocationEnqueue;
    private readonly TimeProvider _timeProvider;

    public UnknownResolutionOperations(
        CatalogDb catalog,
        TimeProvider? timeProvider = null,
        ManagedPathPlanner? pathPlanner = null,
        AssetOwnerRelocationEnqueue? ownerRelocationEnqueue = null,
        StorageTokenAllocator? tokenAllocator = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(catalog.Paths.Root);
        _ownerRelocationEnqueue = ownerRelocationEnqueue;
        _tokenAllocator = tokenAllocator ?? new StorageTokenAllocator();
    }

    public async Task<OperationResult<UnknownResolutionOutcome>> AssignUnknownAssetsToProfileAsync(
        AssignUnknownAssetsToProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceUnknownProfileId == Guid.Empty)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "INVALID_PROFILE_ID",
                "Source UNKNOWN Profile identifier cannot be empty.");
        }

        if (request.DestinationNormalProfileId == Guid.Empty)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "INVALID_PROFILE_ID",
                "Destination NORMAL Profile identifier cannot be empty.");
        }

        if (request.SourceUnknownProfileId == request.DestinationNormalProfileId)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "SELF_PAIR_NOT_ALLOWED",
                "Source and destination cannot be the same Profile.");
        }

        if ((request.SelectedAssetIds is null || request.SelectedAssetIds.Count == 0)
            && request.AssignmentClusterId is null)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "SELECTION_EMPTY",
                "At least one media item must be selected for resolution.");
        }

        var distinctAssetIds = new List<Guid>();
        var seenAssetIds = new HashSet<Guid>();
        foreach (var assetId in request.SelectedAssetIds ?? [])
        {
            if (assetId == Guid.Empty)
            {
                return OperationResult<UnknownResolutionOutcome>.Validation(
                    "INVALID_ASSET_ID",
                    "A selected media item identifier cannot be empty.");
            }

            if (seenAssetIds.Add(assetId))
            {
                distinctAssetIds.Add(assetId);
            }
        }

        List<AssetOwnerRelocationRequest> pendingRelocations = [];
        OperationResult<UnknownResolutionOutcome> result;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            if (request.AssignmentClusterId is { } clusterId)
            {
                distinctAssetIds = await ReadAssignmentClusterAssetIdsAsync(
                    transaction,
                    clusterId,
                    request.SourceUnknownProfileId,
                    request.DestinationNormalProfileId,
                    cancellationToken).ConfigureAwait(false);
                if (distinctAssetIds.Count == 0)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        "ASSIGNMENT_CLUSTER_CHANGED",
                        "This assignment group is no longer available. Reload and try again.");
                }
            }

            var sourceProfile = await ReadProfileStateAsync(transaction, request.SourceUnknownProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (sourceProfile is null)
            {
                return OperationResult<UnknownResolutionOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "The source UNKNOWN Profile no longer exists.");
            }

            if (sourceProfile.IsTrashed)
            {
                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "The source UNKNOWN Profile is in Trash and cannot be resolved.");
            }

            if (sourceProfile.Kind != ProfileKind.Unknown)
            {
                return OperationResult<UnknownResolutionOutcome>.Validation(
                    OperationErrorCode.ProfileNotNormal,
                    "The source Profile is not an UNKNOWN Profile.");
            }

            if (request.ExpectedUnknownRowVersion.HasValue && sourceProfile.RowVersion != request.ExpectedUnknownRowVersion.Value)
            {
                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "The source UNKNOWN Profile changed concurrently. Reload and try again.");
            }

            var destinationProfile = await ReadProfileStateAsync(transaction, request.DestinationNormalProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (destinationProfile is null)
            {
                return OperationResult<UnknownResolutionOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "The destination Profile no longer exists.");
            }

            if (destinationProfile.IsTrashed)
            {
                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "The destination Profile is in Trash and cannot take ownership of media.");
            }

            if (destinationProfile.Kind != ProfileKind.Normal)
            {
                return OperationResult<UnknownResolutionOutcome>.Validation(
                    OperationErrorCode.ProfileNotNormal,
                    "The destination Profile must be an active NORMAL Profile.");
            }

            var destStorageToken = destinationProfile.StorageToken;
            if (string.IsNullOrWhiteSpace(destStorageToken))
            {
                destStorageToken = await AssignProfileStorageTokenAsync(
                    transaction, destinationProfile.ProfileId, cancellationToken).ConfigureAwait(false);
            }

            var assets = new List<AssetResolutionState>();
            var allAlreadyTransferred = true;

            foreach (var assetId in distinctAssetIds)
            {
                var asset = await ReadAssetResolutionStateAsync(transaction, assetId, cancellationToken)
                    .ConfigureAwait(false);
                if (asset is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NotFound(
                        OperationErrorCode.AssetNotFound,
                        $"Media item {assetId:D} no longer exists.");
                }

                if (asset.State != AssetState.Active || asset.IsTrashed)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.AssetNotActive,
                        $"Media item {assetId:D} is not active and cannot be resolved.");
                }

                if (asset.CurrentOwnerProfileId == request.DestinationNormalProfileId)
                {
                    assets.Add(asset);
                    continue;
                }

                allAlreadyTransferred = false;

                if (asset.CurrentOwnerProfileId != request.SourceUnknownProfileId)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.UnknownOwnershipChanged,
                        $"Media item {assetId:D} is not currently owned by source UNKNOWN Profile.");
                }

                if (request.ExpectedAssetRowVersions is not null
                    && request.ExpectedAssetRowVersions.TryGetValue(assetId, out var expectedAssetRowVersion)
                    && asset.RowVersion != expectedAssetRowVersion)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.AssetOwnerConflict,
                        $"Media item {assetId:D} changed concurrently. Reload and try again.");
                }

                if (asset.PathState != ManagedPathState.None)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.ProfilePathReconciliationBlocked,
                        $"Media item {assetId:D} is already being relocated. Wait for that to finish.");
                }

                if (asset.Sha256 is null || asset.ByteLength is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NeedsAttention(
                        OperationErrorCode.AssetContentMismatch,
                        $"Media item {assetId:D} content record is incomplete.");
                }

                if (asset.StorageToken is null
                    || asset.CurrentManagedRelativePath is null
                    || asset.CurrentManagedFileName is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NeedsAttention(
                        OperationErrorCode.CurrentPathMissing,
                        $"Media item {assetId:D} has no recorded current managed path.");
                }

                assets.Add(asset);
            }

            var now = DbTime.Format(_timeProvider.GetUtcNow());
            var resolvedAssetOutcomes = new List<UnknownResolvedAssetOutcome>();

            if (allAlreadyTransferred)
            {
                var remainingActiveCount = await CountActiveOwnedAssetsAsync(
                    transaction, request.SourceUnknownProfileId, cancellationToken).ConfigureAwait(false);

                foreach (var asset in assets)
                {
                    resolvedAssetOutcomes.Add(new UnknownResolvedAssetOutcome(
                        asset.AssetId,
                        asset.RowVersion,
                        asset.CurrentManagedRelativePath,
                        asset.CurrentManagedFileName,
                        asset.TargetManagedRelativePath,
                        asset.TargetManagedFileName,
                        asset.PathState,
                        asset.ReconciliationOperationId));
                }

                transaction.QueueInvalidation(CatalogInvalidationDomain.Profile, request.SourceUnknownProfileId, destinationProfile.ProfileId);
                transaction.QueueInvalidation(CatalogInvalidationDomain.Media, distinctAssetIds.ToArray());
                await MarkAssignmentClusterAcceptedAsync(transaction, request, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                var outcome = new UnknownResolutionOutcome(
                    request.SourceUnknownProfileId,
                    destinationProfile.ProfileId,
                    ProfileKind.Normal,
                    destinationProfile.DisplayName!,
                    distinctAssetIds.Count,
                    remainingActiveCount,
                    UnknownProfileRules.LeavesActiveUnresolved(remainingActiveCount),
                    resolvedAssetOutcomes);

                return OperationResult<UnknownResolutionOutcome>.Success(outcome);
            }

            foreach (var asset in assets)
            {
                if (asset.CurrentOwnerProfileId == request.DestinationNormalProfileId)
                {
                    resolvedAssetOutcomes.Add(new UnknownResolvedAssetOutcome(
                        asset.AssetId,
                        asset.RowVersion,
                        asset.CurrentManagedRelativePath,
                        asset.CurrentManagedFileName,
                        asset.TargetManagedRelativePath,
                        asset.TargetManagedFileName,
                        asset.PathState,
                        asset.ReconciliationOperationId));
                    continue;
                }

                string targetFilePath;
                string targetRelativePath;
                string targetFileName;
                if (asset.MediaType == MediaType.Model && asset.DependencyStatus != AssetDependencyStatus.SelfContained)
                {
                    var pkgPlan = _pathPlanner.PlanModelPackage(
                        destinationProfile.ProfileId,
                        destinationProfile.DisplayName!,
                        new ProfileStorageToken(destStorageToken),
                        asset.AssetId,
                        new AssetStorageToken(asset.StorageToken!),
                        asset.CurrentManagedFileName!);
                    targetFilePath = pkgPlan.PrimaryManagedRelativePath;
                    targetRelativePath = pkgPlan.PackageDirectoryRelativePath;
                    targetFileName = pkgPlan.PrimaryFileName;
                }
                else
                {
                    var plan = _pathPlanner.PlanAsset(
                        destinationProfile.ProfileId,
                        destinationProfile.DisplayName!,
                        new ProfileStorageToken(destStorageToken),
                        asset.AssetId,
                        new AssetStorageToken(asset.StorageToken!),
                        asset.MediaType,
                        Path.GetExtension(asset.CurrentManagedFileName!));
                    targetFilePath = plan.ManagedFileRelativePath!;
                    targetRelativePath = targetFilePath[..targetFilePath.LastIndexOf('/')];
                    targetFileName = plan.ManagedFileName!;
                }

                var currentFilePath = $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}";
                var targetFilePathText = $"{targetRelativePath}/{targetFileName}";
                var alreadyAtTarget = string.Equals(currentFilePath, targetFilePathText, StringComparison.Ordinal);

                Guid? operationId = alreadyAtTarget ? null : Guid.NewGuid();
                var newRowVersion = asset.RowVersion + 1;

                await using (var updateOwner = transaction.CreateCommand(
                    """
                    UPDATE profile_assets
                    SET profile_id = $newOwnerId,
                        created_at_ms = $now
                    WHERE asset_id = $assetId AND relation_type = 'OWNER';
                    """))
                {
                    updateOwner.Parameters.AddWithValue("$newOwnerId", DbGuid.Format(destinationProfile.ProfileId));
                    updateOwner.Parameters.AddWithValue("$now", now);
                    updateOwner.Parameters.AddWithValue("$assetId", DbGuid.Format(asset.AssetId));

                    if (await updateOwner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new CatalogInvariantException(
                            $"Ownership transfer failed for Asset {asset.AssetId:D}.");
                    }
                }

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
                    updateAsset.Parameters.AddWithValue("$assetId", DbGuid.Format(asset.AssetId));
                    updateAsset.Parameters.AddWithValue("$expectedRowVersion", asset.RowVersion);

                    if (await updateAsset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new CatalogInvariantException(
                            $"Asset {asset.AssetId:D} changed while owner transfer was being written.");
                    }
                }

                if (operationId is not null)
                {
                    await PersistOwnerRelocationOperationAsync(
                        transaction,
                        asset.AssetId,
                        operationId.Value,
                        destinationProfile.ProfileId,
                        targetRelativePath,
                        targetFileName,
                        now,
                        cancellationToken).ConfigureAwait(false);

                    pendingRelocations.Add(new AssetOwnerRelocationRequest(
                        asset.AssetId,
                        operationId.Value,
                        destinationProfile.ProfileId,
                        targetRelativePath,
                        targetFileName));
                }

                await EnsureExactlyOneOwnerAsync(transaction, asset.AssetId, cancellationToken).ConfigureAwait(false);

                resolvedAssetOutcomes.Add(new UnknownResolvedAssetOutcome(
                    asset.AssetId,
                    newRowVersion,
                    asset.CurrentManagedRelativePath,
                    asset.CurrentManagedFileName,
                    targetRelativePath,
                    targetFileName,
                    alreadyAtTarget ? ManagedPathState.None : ManagedPathState.Pending,
                    operationId));
            }

            var remainingCount = await CountActiveOwnedAssetsAsync(
                transaction, request.SourceUnknownProfileId, cancellationToken).ConfigureAwait(false);

            await using (var updateUnknown = transaction.CreateCommand(
                """
                UPDATE profiles
                SET row_version = row_version + 1,
                    updated_at_ms = $now
                WHERE profile_id = $profileId;
                """))
            {
                updateUnknown.Parameters.AddWithValue("$now", now);
                updateUnknown.Parameters.AddWithValue("$profileId", DbGuid.Format(request.SourceUnknownProfileId));
                await updateUnknown.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendActivityAsync(
                transaction,
                ActivityEventType.UnknownResolved,
                destinationProfile.ProfileId,
                assetId: distinctAssetIds[0],
                operationId: null,
                BuildUnknownResolvedPayload(
                    request.SourceUnknownProfileId,
                    destinationProfile.ProfileId,
                    destinationProfile.DisplayName!,
                    distinctAssetIds.Count,
                    remainingCount),
                DbTime.Format(_timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(CatalogInvalidationDomain.Profile, request.SourceUnknownProfileId, destinationProfile.ProfileId);
            transaction.QueueInvalidation(CatalogInvalidationDomain.Media, distinctAssetIds.ToArray());
            await MarkAssignmentClusterAcceptedAsync(transaction, request, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var finalOutcome = new UnknownResolutionOutcome(
                request.SourceUnknownProfileId,
                destinationProfile.ProfileId,
                ProfileKind.Normal,
                destinationProfile.DisplayName!,
                distinctAssetIds.Count,
                remainingCount,
                UnknownProfileRules.LeavesActiveUnresolved(remainingCount),
                resolvedAssetOutcomes);

            if (pendingRelocations.Count > 0)
            {
                result = OperationResult<UnknownResolutionOutcome>.Accepted(
                    finalOutcome,
                    pendingRelocations[0].OperationId,
                    "Media ownership transferred to Profile. File relocation is in progress in the background.");
            }
            else
            {
                result = OperationResult<UnknownResolutionOutcome>.Success(finalOutcome);
            }
        }

        if (pendingRelocations.Count > 0 && _ownerRelocationEnqueue is not null)
        {
            foreach (var relocation in pendingRelocations)
            {
                try
                {
                    await _ownerRelocationEnqueue(relocation, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = result with
                    {
                        UserMessage = "Media ownership transferred. Moving files will resume automatically.",
                    };
                }
            }
        }

        return result;
    }

    public async Task<OperationResult> KeepAssignmentClusterUnknownAsync(
        Guid sourceUnknownProfileId,
        Guid clusterId,
        long expectedClusterRowVersion,
        CancellationToken cancellationToken = default)
    {
        if (sourceUnknownProfileId == Guid.Empty || clusterId == Guid.Empty)
        {
            return OperationResult.Validation("INVALID_ASSIGNMENT_CLUSTER", "The assignment group is invalid.");
        }

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        await using var command = transaction.CreateCommand(
            """
            UPDATE import_assignment_clusters
            SET state = 'KEPT_UNKNOWN',
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE cluster_id = $clusterId
              AND state = 'PENDING'
              AND row_version = $expectedRowVersion
              AND EXISTS (
                  SELECT 1 FROM import_units iu
                  WHERE iu.import_unit_id = import_assignment_clusters.import_unit_id
                    AND iu.destination_profile_id = $sourceProfileId
              );
            """);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
        command.Parameters.AddWithValue("$expectedRowVersion", expectedClusterRowVersion);
        command.Parameters.AddWithValue("$sourceProfileId", DbGuid.Format(sourceUnknownProfileId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return OperationResult.Conflict(
                "ASSIGNMENT_CLUSTER_CHANGED",
                "This assignment group changed. Reload and try again.");
        }

        transaction.QueueInvalidation(CatalogInvalidationDomain.Profile, sourceUnknownProfileId);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Success() with { UserMessage = "Media remains safely unassigned." };
    }

    private static async Task<List<Guid>> ReadAssignmentClusterAssetIdsAsync(
        CatalogTransaction transaction,
        Guid clusterId,
        Guid sourceUnknownProfileId,
        Guid destinationProfileId,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var command = transaction.CreateCommand(
            """
            SELECT DISTINCT COALESCE(ii.reused_asset_id, ii.candidate_asset_id)
            FROM import_assignment_clusters c
            JOIN import_units iu ON iu.import_unit_id = c.import_unit_id
            JOIN import_assignment_cluster_items ci ON ci.cluster_id = c.cluster_id
            JOIN import_items ii ON ii.import_item_id = ci.import_item_id
            WHERE c.cluster_id = $clusterId
              AND iu.destination_profile_id = $sourceProfileId
              AND (c.state = 'PENDING'
                   OR (c.state = 'ACCEPTED' AND c.decided_profile_id = $destinationProfileId))
              AND COALESCE(ii.reused_asset_id, ii.candidate_asset_id) IS NOT NULL;
            """);
        command.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
        command.Parameters.AddWithValue("$sourceProfileId", DbGuid.Format(sourceUnknownProfileId));
        command.Parameters.AddWithValue("$destinationProfileId", DbGuid.Format(destinationProfileId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private async Task MarkAssignmentClusterAcceptedAsync(
        CatalogTransaction transaction,
        AssignUnknownAssetsToProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AssignmentClusterId is not { } clusterId)
        {
            return;
        }

        await using var command = transaction.CreateCommand(
            """
            UPDATE import_assignment_clusters
            SET state = 'ACCEPTED',
                decided_profile_id = $destinationProfileId,
                updated_at_ms = $now,
                row_version = CASE WHEN state = 'PENDING' THEN row_version + 1 ELSE row_version END
            WHERE cluster_id = $clusterId
              AND ((state = 'PENDING' AND ($expectedRowVersion IS NULL OR row_version = $expectedRowVersion))
                   OR (state = 'ACCEPTED' AND decided_profile_id = $destinationProfileId));
            """);
        command.Parameters.AddWithValue("$destinationProfileId", DbGuid.Format(request.DestinationNormalProfileId));
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
        command.Parameters.AddWithValue(
            "$expectedRowVersion",
            request.ExpectedAssignmentClusterRowVersion is { } version ? version : DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException("The assignment group changed while its decision was being applied.");
        }
    }

    public Task<OperationResult<UnknownResolutionOutcome>> ResolveUnknownToExistingProfileAsync(
        AssignUnknownAssetsToProfileRequest request,
        CancellationToken cancellationToken = default) =>
        AssignUnknownAssetsToProfileAsync(request, cancellationToken);

    public async Task<OperationResult<UnknownResolutionOutcome>> CreateProfileFromUnknownAssetsAsync(
        CreateProfileFromUnknownAssetsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceUnknownProfileId == Guid.Empty)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "INVALID_PROFILE_ID",
                "Source UNKNOWN Profile identifier cannot be empty.");
        }

        if (request.SelectedAssetIds is null || request.SelectedAssetIds.Count == 0)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                "SELECTION_EMPTY",
                "At least one media item must be selected for resolution.");
        }

        if (!ProfileRules.TryValidateDisplayName(request.NewDisplayName, out var normalizedDisplayName, out var nameError))
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                OperationErrorCode.ProfileNameInvalid,
                nameError!);
        }

        if (request.Rating.HasValue && (request.Rating.Value < 0 || request.Rating.Value > 5))
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                OperationErrorCode.ProfileMetadataInvalid,
                "Profile rating must be between 0 and 5.");
        }

        if (request.Overview is not null && request.Overview.Length > ProfileRules.MaximumOverviewLength)
        {
            return OperationResult<UnknownResolutionOutcome>.Validation(
                OperationErrorCode.ProfileMetadataInvalid,
                $"Profile overview cannot exceed {ProfileRules.MaximumOverviewLength} characters.");
        }

        var distinctAssetIds = new List<Guid>();
        var seenAssetIds = new HashSet<Guid>();
        foreach (var assetId in request.SelectedAssetIds)
        {
            if (assetId == Guid.Empty)
            {
                return OperationResult<UnknownResolutionOutcome>.Validation(
                    "INVALID_ASSET_ID",
                    "A selected media item identifier cannot be empty.");
            }

            if (seenAssetIds.Add(assetId))
            {
                distinctAssetIds.Add(assetId);
            }
        }

        var newProfileId = request.NewProfileId ?? Guid.NewGuid();
        var identityId = request.IdentityId ?? Guid.NewGuid();
        List<AssetOwnerRelocationRequest> pendingRelocations = [];
        OperationResult<UnknownResolutionOutcome> result;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var sourceProfile = await ReadProfileStateAsync(transaction, request.SourceUnknownProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (sourceProfile is null)
            {
                return OperationResult<UnknownResolutionOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "The source UNKNOWN Profile no longer exists.");
            }

            if (sourceProfile.IsTrashed)
            {
                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "The source UNKNOWN Profile is in Trash and cannot be resolved.");
            }

            if (sourceProfile.Kind != ProfileKind.Unknown)
            {
                return OperationResult<UnknownResolutionOutcome>.Validation(
                    OperationErrorCode.ProfileNotNormal,
                    "The source Profile is not an UNKNOWN Profile.");
            }

            if (request.ExpectedUnknownRowVersion.HasValue && sourceProfile.RowVersion != request.ExpectedUnknownRowVersion.Value)
            {
                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "The source UNKNOWN Profile changed concurrently. Reload and try again.");
            }

            var existingNewProfile = await ReadProfileStateAsync(transaction, newProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (existingNewProfile is not null)
            {
                if (existingNewProfile.Kind == ProfileKind.Normal
                    && string.Equals(existingNewProfile.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
                {

                    var allOwned = true;
                    var existingAssetOutcomes = new List<UnknownResolvedAssetOutcome>();
                    foreach (var assetId in distinctAssetIds)
                    {
                        var a = await ReadAssetResolutionStateAsync(transaction, assetId, cancellationToken).ConfigureAwait(false);
                        if (a is null || a.CurrentOwnerProfileId != newProfileId)
                        {
                            allOwned = false;
                            break;
                        }
                        existingAssetOutcomes.Add(new UnknownResolvedAssetOutcome(
                            a.AssetId,
                            a.RowVersion,
                            a.CurrentManagedRelativePath,
                            a.CurrentManagedFileName,
                            a.TargetManagedRelativePath,
                            a.TargetManagedFileName,
                            a.PathState,
                            a.ReconciliationOperationId));
                    }

                    if (allOwned)
                    {
                        var remainingActive = await CountActiveOwnedAssetsAsync(
                            transaction, request.SourceUnknownProfileId, cancellationToken).ConfigureAwait(false);

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        var idempOutcome = new UnknownResolutionOutcome(
                            request.SourceUnknownProfileId,
                            newProfileId,
                            ProfileKind.Normal,
                            normalizedDisplayName,
                            distinctAssetIds.Count,
                            remainingActive,
                            UnknownProfileRules.LeavesActiveUnresolved(remainingActive),
                            existingAssetOutcomes);

                        return OperationResult<UnknownResolutionOutcome>.Success(idempOutcome);
                    }
                }

                return OperationResult<UnknownResolutionOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    $"A Profile with identifier {newProfileId:D} already exists with different state.");
            }

            var assets = new List<AssetResolutionState>();
            foreach (var assetId in distinctAssetIds)
            {
                var asset = await ReadAssetResolutionStateAsync(transaction, assetId, cancellationToken)
                    .ConfigureAwait(false);
                if (asset is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NotFound(
                        OperationErrorCode.AssetNotFound,
                        $"Media item {assetId:D} no longer exists.");
                }

                if (asset.State != AssetState.Active || asset.IsTrashed)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.AssetNotActive,
                        $"Media item {assetId:D} is not active and cannot be resolved.");
                }

                if (asset.CurrentOwnerProfileId != request.SourceUnknownProfileId)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.UnknownOwnershipChanged,
                        $"Media item {assetId:D} is not currently owned by source UNKNOWN Profile.");
                }

                if (request.ExpectedAssetRowVersions is not null
                    && request.ExpectedAssetRowVersions.TryGetValue(assetId, out var expectedAssetRowVersion)
                    && asset.RowVersion != expectedAssetRowVersion)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.AssetOwnerConflict,
                        $"Media item {assetId:D} changed concurrently. Reload and try again.");
                }

                if (asset.PathState != ManagedPathState.None)
                {
                    return OperationResult<UnknownResolutionOutcome>.Conflict(
                        OperationErrorCode.ProfilePathReconciliationBlocked,
                        $"Media item {assetId:D} is already being relocated. Wait for that to finish.");
                }

                if (asset.Sha256 is null || asset.ByteLength is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NeedsAttention(
                        OperationErrorCode.AssetContentMismatch,
                        $"Media item {assetId:D} content record is incomplete.");
                }

                if (asset.StorageToken is null
                    || asset.CurrentManagedRelativePath is null
                    || asset.CurrentManagedFileName is null)
                {
                    return OperationResult<UnknownResolutionOutcome>.NeedsAttention(
                        OperationErrorCode.CurrentPathMissing,
                        $"Media item {assetId:D} has no recorded current managed path.");
                }

                assets.Add(asset);
            }

            if (request.CoverAssetId.HasValue && !distinctAssetIds.Contains(request.CoverAssetId.Value))
            {
                var coverAsset = await ReadAssetResolutionStateAsync(transaction, request.CoverAssetId.Value, cancellationToken).ConfigureAwait(false);
                if (coverAsset is null || coverAsset.State != AssetState.Active || coverAsset.IsTrashed)
                {
                    return OperationResult<UnknownResolutionOutcome>.Validation(
                        OperationErrorCode.AppearanceAssetInvalid,
                        "Cover media item is invalid or not active.");
                }
            }

            if (request.BannerAssetId.HasValue && !distinctAssetIds.Contains(request.BannerAssetId.Value))
            {
                var bannerAsset = await ReadAssetResolutionStateAsync(transaction, request.BannerAssetId.Value, cancellationToken).ConfigureAwait(false);
                if (bannerAsset is null || bannerAsset.State != AssetState.Active || bannerAsset.IsTrashed)
                {
                    return OperationResult<UnknownResolutionOutcome>.Validation(
                        OperationErrorCode.AppearanceAssetInvalid,
                        "Banner media item is invalid or not active.");
                }
            }

            var profileToken = await AllocateUniqueProfileStorageTokenAsync(
                transaction, newProfileId, cancellationToken).ConfigureAwait(false);

            var profilePlan = _pathPlanner.PlanProfile(
                newProfileId,
                normalizedDisplayName,
                new ProfileStorageToken(profileToken));
            var profileFolder = profilePlan.ProfileFolderRelativePath;

            var now = DbTime.Format(_timeProvider.GetUtcNow());

            await using (var insertProfile = transaction.CreateCommand(
                """
                INSERT INTO profiles(
                    profile_id, kind, display_name, unknown_sequence,
                    category_id, rating, is_favorite, overview,
                    profile_storage_token, current_managed_relative_path,
                    target_managed_relative_path, path_state,
                    cover_asset_id, banner_asset_id,
                    created_at_ms, updated_at_ms, row_version)
                VALUES (
                    $profileId, 'NORMAL', $displayName, NULL,
                    $categoryId, $rating, $isFavorite, $overview,
                    $storageToken, $profileFolder,
                    $profileFolder, 'NONE',
                    $coverId, $bannerId,
                    $now, $now, 0);
                """))
            {
                insertProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(newProfileId));
                insertProfile.Parameters.AddWithValue("$displayName", normalizedDisplayName);
                insertProfile.Parameters.AddWithValue("$categoryId", request.CategoryId.HasValue ? (object)DbGuid.Format(request.CategoryId.Value) : DBNull.Value);
                insertProfile.Parameters.AddWithValue("$rating", (object?)request.Rating ?? DBNull.Value);
                insertProfile.Parameters.AddWithValue("$isFavorite", request.IsFavorite == true ? 1 : 0);
                insertProfile.Parameters.AddWithValue("$overview", (object?)request.Overview ?? DBNull.Value);
                insertProfile.Parameters.AddWithValue("$storageToken", profileToken);
                insertProfile.Parameters.AddWithValue("$profileFolder", profileFolder);
                insertProfile.Parameters.AddWithValue("$coverId", request.CoverAssetId.HasValue ? (object)DbGuid.Format(request.CoverAssetId.Value) : DBNull.Value);
                insertProfile.Parameters.AddWithValue("$bannerId", request.BannerAssetId.HasValue ? (object)DbGuid.Format(request.BannerAssetId.Value) : DBNull.Value);
                insertProfile.Parameters.AddWithValue("$now", now);

                await insertProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (request.Tags is not null && request.Tags.Count > 0)
            {
                foreach (var tag in request.Tags.Distinct())
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    await using var insertTag = transaction.CreateCommand(
                        """
                        INSERT OR IGNORE INTO profile_tags(profile_id, tag_id, created_at_ms)
                        VALUES ($profileId, $tagId, $now);
                        """);
                    insertTag.Parameters.AddWithValue("$profileId", DbGuid.Format(newProfileId));
                    insertTag.Parameters.AddWithValue("$tagId", tag.Trim());
                    insertTag.Parameters.AddWithValue("$now", now);
                    await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (request.CoverShape is not null
                || request.CoverFrameId is not null
                || request.LayoutPresetId is not null
                || request.InitialAppearanceOverrides is not null)
            {
                var overrides = request.InitialAppearanceOverrides ?? ProfileAppearanceOverrides.Default with
                {
                    CoverShape = request.CoverShape ?? ProfileAppearanceOverrides.Default.CoverShape,
                    CoverFrameId = request.CoverFrameId ?? ProfileAppearanceOverrides.Default.CoverFrameId,
                };

                await using var insertAppearance = transaction.CreateCommand(
                    """
                    INSERT INTO profile_appearance(
                        profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
                    VALUES (
                        $profileId, $schemaVersion, $layoutPresetId, $overridesJson, $now, 1);
                    """);
                insertAppearance.Parameters.AddWithValue("$profileId", DbGuid.Format(newProfileId));
                insertAppearance.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
                insertAppearance.Parameters.AddWithValue("$layoutPresetId", (object?)request.LayoutPresetId ?? DBNull.Value);
                insertAppearance.Parameters.AddWithValue("$overridesJson", overrides.ToJson());
                insertAppearance.Parameters.AddWithValue("$now", now);
                await insertAppearance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insertIdentity = transaction.CreateCommand(
                """
                INSERT INTO identities(identity_id, profile_id, is_active, created_at_ms)
                VALUES ($identityId, $profileId, 1, $now);
                """))
            {
                insertIdentity.Parameters.AddWithValue("$identityId", DbGuid.Format(identityId));
                insertIdentity.Parameters.AddWithValue("$profileId", DbGuid.Format(newProfileId));
                insertIdentity.Parameters.AddWithValue("$now", now);
                await insertIdentity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var countIdentity = transaction.CreateCommand(
                "SELECT COUNT(*) FROM identities WHERE profile_id = $profileId AND is_active = 1;"))
            {
                countIdentity.Parameters.AddWithValue("$profileId", DbGuid.Format(newProfileId));
                var identityCount = Convert.ToInt32(
                    await countIdentity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (identityCount != 1)
                {
                    throw new CatalogInvariantException(
                        $"NORMAL Profile {newProfileId:D} must have exactly one active Identity.");
                }
            }

            var resolvedAssetOutcomes = new List<UnknownResolvedAssetOutcome>();

            foreach (var asset in assets)
            {
                string targetFilePath;
                string targetRelativePath;
                string targetFileName;
                if (asset.MediaType == MediaType.Model && asset.DependencyStatus != AssetDependencyStatus.SelfContained)
                {
                    var pkgPlan = _pathPlanner.PlanModelPackage(
                        newProfileId,
                        normalizedDisplayName,
                        new ProfileStorageToken(profileToken),
                        asset.AssetId,
                        new AssetStorageToken(asset.StorageToken!),
                        asset.CurrentManagedFileName!);
                    targetFilePath = pkgPlan.PrimaryManagedRelativePath;
                    targetRelativePath = pkgPlan.PackageDirectoryRelativePath;
                    targetFileName = pkgPlan.PrimaryFileName;
                }
                else
                {
                    var plan = _pathPlanner.PlanAsset(
                        newProfileId,
                        normalizedDisplayName,
                        new ProfileStorageToken(profileToken),
                        asset.AssetId,
                        new AssetStorageToken(asset.StorageToken!),
                        asset.MediaType,
                        Path.GetExtension(asset.CurrentManagedFileName!));
                    targetFilePath = plan.ManagedFileRelativePath!;
                    targetRelativePath = targetFilePath[..targetFilePath.LastIndexOf('/')];
                    targetFileName = plan.ManagedFileName!;
                }

                var currentFilePath = $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}";
                var targetFilePathText = $"{targetRelativePath}/{targetFileName}";
                var alreadyAtTarget = string.Equals(currentFilePath, targetFilePathText, StringComparison.Ordinal);

                Guid? operationId = alreadyAtTarget ? null : Guid.NewGuid();
                var newRowVersion = asset.RowVersion + 1;

                await using (var updateOwner = transaction.CreateCommand(
                    """
                    UPDATE profile_assets
                    SET profile_id = $newOwnerId,
                        created_at_ms = $now
                    WHERE asset_id = $assetId AND relation_type = 'OWNER';
                    """))
                {
                    updateOwner.Parameters.AddWithValue("$newOwnerId", DbGuid.Format(newProfileId));
                    updateOwner.Parameters.AddWithValue("$now", now);
                    updateOwner.Parameters.AddWithValue("$assetId", DbGuid.Format(asset.AssetId));

                    if (await updateOwner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new CatalogInvariantException(
                            $"Ownership transfer failed for Asset {asset.AssetId:D}.");
                    }
                }

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
                    updateAsset.Parameters.AddWithValue("$assetId", DbGuid.Format(asset.AssetId));
                    updateAsset.Parameters.AddWithValue("$expectedRowVersion", asset.RowVersion);

                    if (await updateAsset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new CatalogInvariantException(
                            $"Asset {asset.AssetId:D} changed while owner transfer was being written.");
                    }
                }

                if (operationId is not null)
                {
                    await PersistOwnerRelocationOperationAsync(
                        transaction,
                        asset.AssetId,
                        operationId.Value,
                        newProfileId,
                        targetRelativePath,
                        targetFileName,
                        now,
                        cancellationToken).ConfigureAwait(false);

                    pendingRelocations.Add(new AssetOwnerRelocationRequest(
                        asset.AssetId,
                        operationId.Value,
                        newProfileId,
                        targetRelativePath,
                        targetFileName));
                }

                await EnsureExactlyOneOwnerAsync(transaction, asset.AssetId, cancellationToken).ConfigureAwait(false);

                resolvedAssetOutcomes.Add(new UnknownResolvedAssetOutcome(
                    asset.AssetId,
                    newRowVersion,
                    asset.CurrentManagedRelativePath,
                    asset.CurrentManagedFileName,
                    targetRelativePath,
                    targetFileName,
                    alreadyAtTarget ? ManagedPathState.None : ManagedPathState.Pending,
                    operationId));
            }

            var remainingCount = await CountActiveOwnedAssetsAsync(
                transaction, request.SourceUnknownProfileId, cancellationToken).ConfigureAwait(false);

            await using (var updateUnknown = transaction.CreateCommand(
                """
                UPDATE profiles
                SET row_version = row_version + 1,
                    updated_at_ms = $now
                WHERE profile_id = $profileId;
                """))
            {
                updateUnknown.Parameters.AddWithValue("$now", now);
                updateUnknown.Parameters.AddWithValue("$profileId", DbGuid.Format(request.SourceUnknownProfileId));
                await updateUnknown.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendActivityAsync(
                transaction,
                ActivityEventType.UnknownResolved,
                newProfileId,
                assetId: distinctAssetIds[0],
                operationId: null,
                BuildUnknownResolvedPayload(
                    request.SourceUnknownProfileId,
                    newProfileId,
                    normalizedDisplayName,
                    distinctAssetIds.Count,
                    remainingCount),
                DbTime.Format(_timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            transaction.QueueInvalidation(CatalogInvalidationDomain.Profile, request.SourceUnknownProfileId, newProfileId);
            transaction.QueueInvalidation(CatalogInvalidationDomain.Media, distinctAssetIds.ToArray());
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var finalOutcome = new UnknownResolutionOutcome(
                request.SourceUnknownProfileId,
                newProfileId,
                ProfileKind.Normal,
                normalizedDisplayName,
                distinctAssetIds.Count,
                remainingCount,
                UnknownProfileRules.LeavesActiveUnresolved(remainingCount),
                resolvedAssetOutcomes);

            if (pendingRelocations.Count > 0)
            {
                result = OperationResult<UnknownResolutionOutcome>.Accepted(
                    finalOutcome,
                    pendingRelocations[0].OperationId,
                    "New Profile created. File relocation is in progress in the background.");
            }
            else
            {
                result = OperationResult<UnknownResolutionOutcome>.Success(finalOutcome);
            }
        }

        if (pendingRelocations.Count > 0 && _ownerRelocationEnqueue is not null)
        {
            foreach (var relocation in pendingRelocations)
            {
                try
                {
                    await _ownerRelocationEnqueue(relocation, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = result with
                    {
                        UserMessage = "New Profile created. Moving files will resume automatically.",
                    };
                }
            }
        }

        return result;
    }

    public Task<OperationResult<UnknownResolutionOutcome>> ResolveUnknownToNewProfileAsync(
        CreateProfileFromUnknownAssetsRequest request,
        CancellationToken cancellationToken = default) =>
        CreateProfileFromUnknownAssetsAsync(request, cancellationToken);

    private static async Task<ProfileState?> ReadProfileStateAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT profile_id, kind, display_name, unknown_sequence, profile_storage_token,
                   row_version, trashed_at_ms
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kindStr = reader.GetString(1);
        var kind = DbEnum.ParseProfileKind(kindStr);

        return new ProfileState(
            DbGuid.Parse(reader.GetString(0)),
            kind,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            !reader.IsDBNull(6));
    }

    private static async Task<AssetResolutionState?> ReadAssetResolutionStateAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT a.asset_id, a.state, a.media_type, a.asset_storage_token, a.sha256,
                   a.byte_length, a.current_managed_relative_path, a.current_managed_file_name,
                   a.target_managed_relative_path, a.target_managed_file_name, a.path_state,
                   a.reconciliation_operation_id, a.row_version, a.trashed_at_ms,
                   (SELECT pa.profile_id
                    FROM profile_assets pa
                    WHERE pa.asset_id = a.asset_id AND pa.relation_type = 'OWNER') AS owner_profile_id,
                   a.dependency_status
            FROM assets a
            WHERE a.asset_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var state = DbEnum.ParseAssetState(reader.GetString(1));
        var mediaType = DbEnum.ParseMediaType(reader.GetString(2));
        var pathState = DbEnum.ParseManagedPathState(reader.GetString(10));

        return new AssetResolutionState(
            DbGuid.Parse(reader.GetString(0)),
            state,
            mediaType,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            pathState,
            reader.IsDBNull(11) ? null : DbGuid.Parse(reader.GetString(11)),
            reader.GetInt64(12),
            !reader.IsDBNull(13),
            reader.IsDBNull(14) ? null : DbGuid.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? AssetDependencyStatus.SelfContained : DbEnum.ParseAssetDependencyStatus(reader.GetString(15)));
    }

    private static async Task<int> CountActiveOwnedAssetsAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*)
            FROM profile_assets pa
            JOIN assets a ON a.asset_id = pa.asset_id
            WHERE pa.profile_id = $profileId
              AND pa.relation_type = 'OWNER'
              AND a.state = 'ACTIVE'
              AND a.trashed_at_ms IS NULL;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    private async Task<string> AssignProfileStorageTokenAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var candidate = _tokenAllocator.GetProfileCandidate(profileId, attempt).Value;
            if (await TokenExistsAsync(transaction, candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using var update = transaction.CreateCommand(
                """
                UPDATE profiles
                SET profile_storage_token = $token
                WHERE profile_id = $profileId AND profile_storage_token IS NULL;
                """);
            update.Parameters.AddWithValue("$token", candidate);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

            try
            {
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    return candidate;
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                continue;
            }
        }

        throw new CatalogInvariantException(
            $"No unique Profile storage token candidate remained for {profileId:D}.");
    }

    private async Task<string> AllocateUniqueProfileStorageTokenAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var candidate = _tokenAllocator.GetProfileCandidate(profileId, attempt).Value;
            if (!await TokenExistsAsync(transaction, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new CatalogInvariantException(
            $"No unique Profile storage token candidate remained for {profileId:D}.");
    }

    private static async Task<bool> TokenExistsAsync(
        CatalogTransaction transaction,
        string token,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT 1 FROM profiles WHERE profile_storage_token = $token LIMIT 1;");
        command.Parameters.AddWithValue("$token", token);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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
                $operationId, 'OWNER_RELOCATION', 'ASSET', $assetId, 'PENDING', $checkpoint,
                $now, $now);
            """);
        insert.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        insert.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        insert.Parameters.AddWithValue(
            "$checkpoint",
            BuildOwnerRelocationCheckpoint(newOwnerProfileId, targetRelativePath, targetFileName));
        insert.Parameters.AddWithValue("$now", nowMilliseconds);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureExactlyOneOwnerAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT COUNT(*)
            FROM profile_assets
            WHERE asset_id = $assetId AND relation_type = 'OWNER';
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new CatalogInvariantException(
                $"Asset {assetId:D} must have exactly one OWNER; observed {count}.");
        }
    }

    private static async Task AppendActivityAsync(
        CatalogTransaction transaction,
        string eventType,
        Guid profileId,
        Guid? assetId,
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
        insert.Parameters.AddWithValue(
            "$assetId",
            assetId is null ? DBNull.Value : DbGuid.Format(assetId.Value));
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

    private static string BuildUnknownResolvedPayload(
        Guid sourceUnknownProfileId,
        Guid destinationProfileId,
        string destinationDisplayName,
        int resolvedAssetCount,
        int remainingUnknownAssetCount)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceUnknownProfileId", DbGuid.Format(sourceUnknownProfileId));
            writer.WriteString("destinationProfileId", DbGuid.Format(destinationProfileId));
            writer.WriteString("destinationDisplayName", destinationDisplayName);
            writer.WriteNumber("resolvedAssetCount", resolvedAssetCount);
            writer.WriteNumber("remainingUnknownAssetCount", remainingUnknownAssetCount);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record ProfileState(
        Guid ProfileId,
        ProfileKind Kind,
        string? DisplayName,
        long? UnknownSequence,
        string? StorageToken,
        long RowVersion,
        bool IsTrashed);

    private sealed record AssetResolutionState(
        Guid AssetId,
        AssetState State,
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
        long RowVersion,
        bool IsTrashed,
        Guid? CurrentOwnerProfileId,
        AssetDependencyStatus DependencyStatus = AssetDependencyStatus.SelfContained);
}

public sealed record AssignUnknownAssetsToProfileRequest(
    Guid SourceUnknownProfileId,
    IReadOnlyList<Guid> SelectedAssetIds,
    Guid DestinationNormalProfileId,
    long? ExpectedUnknownRowVersion = null,
    IReadOnlyDictionary<Guid, long>? ExpectedAssetRowVersions = null,
    Guid? AssignmentClusterId = null,
    long? ExpectedAssignmentClusterRowVersion = null);

public sealed record CreateProfileFromUnknownAssetsRequest(
    Guid SourceUnknownProfileId,
    IReadOnlyList<Guid> SelectedAssetIds,
    string NewDisplayName,
    Guid? NewProfileId = null,
    Guid? IdentityId = null,
    Guid? CategoryId = null,
    IReadOnlyList<string>? Tags = null,
    short? Rating = null,
    bool? IsFavorite = null,
    string? Overview = null,
    Guid? CoverAssetId = null,
    Guid? BannerAssetId = null,
    string? CoverShape = null,
    string? CoverFrameId = null,
    string? LayoutPresetId = null,
    ProfileAppearanceOverrides? InitialAppearanceOverrides = null,
    long? ExpectedUnknownRowVersion = null,
    IReadOnlyDictionary<Guid, long>? ExpectedAssetRowVersions = null,
    Guid? OperationId = null);

public sealed record UnknownResolutionOutcome(
    Guid SourceUnknownProfileId,
    Guid DestinationProfileId,
    ProfileKind DestinationKind,
    string DestinationDisplayName,
    int ResolvedAssetCount,
    int RemainingUnknownAssetCount,
    bool SourceUnknownLeavesActiveUnresolved,
    IReadOnlyList<UnknownResolvedAssetOutcome> ResolvedAssets);

public sealed record UnknownResolvedAssetOutcome(
    Guid AssetId,
    long NewRowVersion,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    string? TargetManagedRelativePath,
    string? TargetManagedFileName,
    ManagedPathState PathState,
    Guid? ReconciliationOperationId);
