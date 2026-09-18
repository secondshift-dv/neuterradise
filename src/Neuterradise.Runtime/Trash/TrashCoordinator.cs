using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public sealed class TrashCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly ManagedMoveExecutor _moveExecutor;
    private readonly MediaOperations _mediaOperations;
    private readonly ManagedNamePolicy _namePolicy;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly RuntimeMediaResourceCache? _runtimeResources;
    private readonly TimeProvider _timeProvider;

    public TrashCoordinator(
        CatalogDb catalog,
        ManagedMoveExecutor moveExecutor,
        MediaOperations mediaOperations,
        TimeProvider? timeProvider = null,
        ManagedNamePolicy? namePolicy = null,
        RuntimeMediaResourceCache? runtimeResources = null,
        ProfileManifestWriter? manifestWriter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _moveExecutor = moveExecutor ?? throw new ArgumentNullException(nameof(moveExecutor));
        _mediaOperations = mediaOperations ?? throw new ArgumentNullException(nameof(mediaOperations));
        _namePolicy = namePolicy ?? new ManagedNamePolicy();
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _runtimeResources = runtimeResources;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<AssetTrashPlan>> PrepareAssetTrashAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(assetId, nameof(assetId));

        AssetTrashPlan plan;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var asset = await ReadAssetTrashStateAsync(connection, transaction: null, assetId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return OperationResult<AssetTrashPlan>.NotFound(
                    OperationErrorCode.AssetNotFound,
                    "That media item no longer exists.");
            }

            var gate = ValidateAssetIsTrashable(asset);
            if (gate is not null)
            {
                return gate;
            }

            var existing = await ReadActiveTrashEntryAsync(
                    connection, TrashEntityType.Asset, assetId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {

                var existingPlan = AssetTrashPlan.FromJson(existing.PlanJson);
                return existingPlan is null
                    ? OperationResult<AssetTrashPlan>.NeedsAttention(
                        OperationErrorCode.TrashPlanUnreadable,
                        "An earlier Trash record for this media item cannot be read and needs attention.")
                    : OperationResult<AssetTrashPlan>.Success(existingPlan);
            }

            var appearanceReferences = await ReadAffectedAppearanceReferencesAsync(
                    connection, transaction: null, assetId, cancellationToken).ConfigureAwait(false);

            string extension;
            try
            {
                extension = _namePolicy.NormalizeExtension(Path.GetExtension(asset.CurrentManagedFileName!));
            }
            catch (ArgumentException)
            {
                return OperationResult<AssetTrashPlan>.NeedsAttention(
                    OperationErrorCode.CurrentPathAmbiguous,
                    "This media item's stored filename cannot be interpreted and needs repair before it can be moved to Trash.");
            }

            IReadOnlyList<AssetTrashComponentPlan>? packageComponents = null;
            if (asset.MediaType == MediaType.Model)
            {
                var components = await _catalog.AssetWrites.GetAssetComponentsAsync(assetId, cancellationToken)
                    .ConfigureAwait(false);
                if (components.Count > 1 || (components.Count == 1 && components.Any(c => c.ComponentRole == ComponentRole.Dependency)))
                {
                    packageComponents = components.Select(c => new AssetTrashComponentPlan(
                        c.ComponentRelativePath,
                        CombineRelative(asset.CurrentManagedRelativePath!, c.ComponentRelativePath),
                        $"_trash/assets/{assetId:D}/{c.ComponentRelativePath}",
                        c.ByteLength,
                        c.Sha256,
                        c.ComponentRole)).ToList();
                }
            }

            plan = new AssetTrashPlan(
                Guid.NewGuid(),
                assetId,
                asset.OwnerProfileId!.Value,
                asset.CurrentManagedRelativePath!,
                asset.CurrentManagedFileName!,
                asset.StorageToken!,
                asset.ByteLength!.Value,
                asset.Sha256!,
                asset.MediaType,
                BuildRecoveryRelativePath(assetId, extension),
                appearanceReferences,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                asset.RowVersion,
                packageComponents);
        }

        await _catalog.TrashWrites.PersistEntryAsync(
                new TrashEntryPersistence(
                    plan.TrashEntryId,
                    TrashEntityType.Asset,
                    plan.AssetId,
                    TrashEntryState.Pending,
                    plan.RecoveryRelativePath,
                    plan.ToJson(),
                    plan.PreparedAtUtc,
                    plan.PreparedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<AssetTrashPlan>.Success(plan, plan.OperationId);
    }

    public async Task<OperationResult<AssetTrashOutcome>> ExecuteAssetTrashAsync(
        AssetTrashPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await ReadTrashEntryAsync(connection, plan.TrashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return OperationResult<AssetTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var persistedPlan = AssetTrashPlan.FromJson(entry.PlanJson);
            if (entry.EntityType != TrashEntityType.Asset
                || persistedPlan is null
                || persistedPlan.TrashEntryId != entry.TrashEntryId
                || persistedPlan.AssetId != entry.EntityId)
            {
                return OperationResult<AssetTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The durable Trash plan is missing or does not match this media item.");
            }

            plan = persistedPlan;

            if (entry.State == TrashEntryState.Restored)
            {
                return OperationResult<AssetTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "That Trash record was already reversed. Prepare the move to Trash again.");
            }

            var asset = await ReadAssetTrashStateAsync(connection, transaction: null, plan.AssetId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return OperationResult<AssetTrashOutcome>.NotFound(
                    OperationErrorCode.AssetNotFound,
                    "That media item no longer exists.");
            }

            if (asset.State == AssetState.Trashed && entry.State == TrashEntryState.InTrash)
            {
                var priorOutcome = OperationResult<AssetTrashOutcome>.Success(
                    new AssetTrashOutcome(plan.TrashEntryId, plan.AssetId, plan.RecoveryRelativePath, asset.RowVersion),
                    plan.OperationId);
                return await RefreshAffectedManifestsAfterAssetTrashAsync(
                        plan, priorOutcome, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (asset.State != AssetState.Active || asset.RowVersion != plan.ExpectedAssetRowVersion)
            {
                return OperationResult<AssetTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This media item changed since the move to Trash was prepared. Reload it and try again.");
            }

            if (entry.State == TrashEntryState.Pending)
            {
                await MarkEntryStateAsync(
                        plan.TrashEntryId,
                        TrashEntryState.Executing,
                        plan.RecoveryRelativePath,
                        completedAtUtc: null,
                        entry.RowVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (plan.PackageComponents is { Count: > 0 } packageComponents)
        {
            foreach (var comp in packageComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compMove = await _moveExecutor.ExecuteTrashMoveAsync(
                    new ManagedTrashMoveRequest(
                        plan.AssetId,
                        RelativeDirectoryOf(comp.SourceRelativePath),
                        FileNameOf(comp.SourceRelativePath),
                        comp.RecoveryRelativePath,
                        comp.ByteLength,
                        comp.Sha256),
                    cancellationToken).ConfigureAwait(false);
                if (!compMove.IsSuccess)
                {
                    return MapTrashMoveFailure<AssetTrashOutcome>(compMove);
                }
            }

            try
            {
                var pkgSourceDir = _catalog.Paths.ResolveVaultRelativePath(plan.CurrentManagedRelativePath);
                if (Directory.Exists(pkgSourceDir) && !Directory.EnumerateFileSystemEntries(pkgSourceDir).Any())
                {
                    Directory.Delete(pkgSourceDir, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        else
        {
            var move = await _moveExecutor.ExecuteTrashMoveAsync(
                    new ManagedTrashMoveRequest(
                        plan.AssetId,
                        plan.CurrentManagedRelativePath,
                        plan.CurrentManagedFileName,
                        plan.RecoveryRelativePath,
                        plan.ByteLength,
                        plan.Sha256),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!move.IsSuccess)
            {
                return MapTrashMoveFailure<AssetTrashOutcome>(move);
            }
        }

        var commit = await CommitAssetTrashTransitionAsync(plan, cancellationToken).ConfigureAwait(false);
        var outcome = commit.Outcome;

        if (outcome.IsSuccess)
        {
            _runtimeResources?.InvalidateAsset(plan.AssetId);
            return await RefreshAffectedManifestsAfterAssetTrashAsync(
                    commit.Plan, outcome, cancellationToken)
                .ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<OperationResult<AssetTrashOutcome>> RefreshAffectedManifestsAfterAssetTrashAsync(
        AssetTrashPlan plan,
        OperationResult<AssetTrashOutcome> committed,
        CancellationToken cancellationToken)
    {
        await new RelatedEvidenceProjector(_catalog, _timeProvider)
            .RefreshRelatedProjectionsForAssetAsync(plan.AssetId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var profileId in plan.AffectedAppearanceReferences
                     .Select(reference => reference.ProfileId)
                     .Distinct()
                     .Order())
        {
            var profile = await _catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null || profile.TrashedAtUtc.HasValue)
            {
                continue;
            }

            var refresh = await _manifestWriter.RegenerateManifestAsync(
                    _catalog, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (!refresh.IsSuccess)
            {
                return OperationResult<AssetTrashOutcome>.NeedsAttention(
                    OperationErrorCode.ProfileManifestWriteFailed,
                    "The media item is safely in Trash, but an affected profile.json could not be refreshed. Retry the Trash operation to repair it.",
                    plan.OperationId);
            }
        }

        return committed;
    }

    public async Task<OperationResult<ProfileTrashPlan>> PrepareProfileTrashAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        ProfileTrashPlan plan;

        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var profile = await ReadProfileTrashStateAsync(connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return OperationResult<ProfileTrashPlan>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (profile.IsTrashed)
            {
                return OperationResult<ProfileTrashPlan>.Conflict(
                    OperationErrorCode.ProfileAlreadyTrashed,
                    "That Profile is already in Trash.");
            }

            var existing = await ReadActiveTrashEntryAsync(
                    connection, TrashEntityType.Profile, profileId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var existingPlan = ProfileTrashPlan.FromJson(existing.PlanJson);
                return existingPlan is null
                    ? OperationResult<ProfileTrashPlan>.NeedsAttention(
                        OperationErrorCode.TrashPlanUnreadable,
                        "An earlier Trash record for this Profile cannot be read and needs attention.")
                    : OperationResult<ProfileTrashPlan>.Success(existingPlan);
            }

            var owned = await ReadOwnedActiveAssetsAsync(connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);

            plan = new ProfileTrashPlan(
                Guid.NewGuid(),
                profileId,
                profile.RowVersion,
                owned,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow())
            {
                ExpectedCoverAssetId = profile.CoverAssetId,
                ExpectedBannerAssetId = profile.BannerAssetId,
            };
        }

        await _catalog.TrashWrites.PersistEntryAsync(
                new TrashEntryPersistence(
                    plan.TrashEntryId,
                    TrashEntityType.Profile,
                    plan.ProfileId,
                    TrashEntryState.Pending,
                    RecoveryRelativePath: null,
                    plan.ToJson(),
                    plan.PreparedAtUtc,
                    plan.PreparedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<ProfileTrashPlan>.Success(plan, plan.OperationId);
    }

    public async Task<OperationResult<ProfileTrashOutcome>> CommitProfileTrashAsync(
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedAssetDisposition> dispositions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispositions);

        ProfileTrashValidation validation;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = await ReadTrashEntryAsync(connection, plan.TrashEntryId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists.");
            }

            var persistedPlan = ProfileTrashPlan.FromJson(entry.PlanJson);
            if (entry.EntityType != TrashEntityType.Profile
                || persistedPlan is null
                || persistedPlan.TrashEntryId != entry.TrashEntryId
                || persistedPlan.ProfileId != entry.EntityId)
            {
                return OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashPlanUnreadable,
                    "The durable Trash plan is missing or does not match this Profile.");
            }

            plan = persistedPlan;

            var profile = await ReadProfileTrashStateAsync(connection, transaction: null, plan.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists.");
            }

            if (profile.IsTrashed && entry.State == TrashEntryState.InTrash)
            {
                return OperationResult<ProfileTrashOutcome>.Success(
                    new ProfileTrashOutcome(plan.TrashEntryId, plan.ProfileId, profile.RowVersion, 0, 0),
                    plan.OperationId);
            }

            var owned = await ReadOwnedActiveAssetsAsync(connection, transaction: null, plan.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var plannedAssetIds = plan.OwnedActiveAssets.Select(asset => asset.AssetId).ToHashSet();
            if (owned.Any(asset => !plannedAssetIds.Contains(asset.AssetId)))
            {
                return OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile acquired media after the move to Trash was prepared. Reload it and try again.");
            }

            var dispositionGate = await ValidateDispositionsAsync(
                    connection, plan, dispositions, cancellationToken).ConfigureAwait(false);
            if (dispositionGate.Failure is not null)
            {
                return dispositionGate.Failure;
            }

            if (!await ProfileAuthorityMatchesPlanAsync(
                    connection, profile, plan, dispositions, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This Profile changed since the move to Trash was prepared. Reload it and try again.");
            }

            validation = dispositionGate;
        }

        var trashedAssets = dispositions.Count(
            disposition => disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset);
        var reassignedAssets = dispositions.Count - trashedAssets;

        foreach (var disposition in validation.Ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset)
            {
                var prepared = await PrepareAssetTrashAsync(disposition.AssetId, cancellationToken)
                    .ConfigureAwait(false);
                if (!prepared.IsSuccess || prepared.Value is null)
                {
                    return Propagate<AssetTrashPlan, ProfileTrashOutcome>(prepared);
                }

                var executed = await ExecuteAssetTrashAsync(prepared.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (!executed.IsSuccess)
                {
                    return Propagate<AssetTrashOutcome, ProfileTrashOutcome>(executed);
                }

                continue;
            }

            var expectedRowVersion = plan.OwnedActiveAssets
                .First(asset => asset.AssetId == disposition.AssetId)
                .RowVersion;
            var transfer = await _mediaOperations.ChangePrimaryProfileAsync(
                    new ChangePrimaryProfileRequest(
                        disposition.AssetId,
                        disposition.NewOwnerProfileId!.Value,
                        expectedRowVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!transfer.IsSuccess)
            {
                return Propagate<ChangePrimaryProfileOutcome, ProfileTrashOutcome>(transfer);
            }

        }

        var profileRecovery = await MoveProfileManifestToRecoveryAsync(plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!profileRecovery.IsSuccess)
        {
            return profileRecovery.Failure!;
        }

        return await CommitProfileTrashMarkerAsync(
                plan,
                profileRecovery.RecoveryRelativePath!,
                trashedAssets,
                reassignedAssets,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AssetTrashCommitResult> CommitAssetTrashTransitionAsync(
        AssetTrashPlan plan,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var asset = await ReadAssetTrashStateAsync(connection, transaction, plan.AssetId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || asset is null)
        {
            return new AssetTrashCommitResult(
                OperationResult<AssetTrashOutcome>.NotFound(
                    OperationErrorCode.TrashEntryNotFound,
                    "That Trash record no longer exists."),
                plan);
        }

        if (asset.State == AssetState.Trashed && entry.State == TrashEntryState.InTrash)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AssetTrashCommitResult(
                OperationResult<AssetTrashOutcome>.Success(
                    new AssetTrashOutcome(plan.TrashEntryId, plan.AssetId, plan.RecoveryRelativePath, asset.RowVersion),
                    plan.OperationId),
                plan);
        }

        if (asset.State != AssetState.Active || asset.RowVersion != plan.ExpectedAssetRowVersion)
        {
            return new AssetTrashCommitResult(
                OperationResult<AssetTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "This media item changed while it was being moved to Trash."),
                plan);
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());

        await ExecuteAsync(
            transaction,
            "DELETE FROM profile_assets WHERE asset_id = $assetId AND relation_type = 'OWNER';",
            cancellationToken,
            ("$assetId", DbGuid.Format(plan.AssetId))).ConfigureAwait(false);

        var affected = await ReadAffectedAppearanceReferencesAsync(
            connection, transaction, plan.AssetId, cancellationToken).ConfigureAwait(false);
        var committedPlan = plan with { AffectedAppearanceReferences = affected };
        foreach (var reference in affected)
        {
            await ExecuteAsync(
                transaction,
                """
                UPDATE profiles
                SET cover_asset_id = CASE WHEN cover_asset_id = $assetId THEN NULL ELSE cover_asset_id END,
                    banner_asset_id = CASE WHEN banner_asset_id = $assetId THEN NULL ELSE banner_asset_id END,
                    updated_at_ms = $now,
                    row_version = row_version + 1
                WHERE profile_id = $profileId;
                """,
                cancellationToken,
                ("$assetId", DbGuid.Format(plan.AssetId)),
                ("$now", now),
                ("$profileId", DbGuid.Format(reference.ProfileId))).ConfigureAwait(false);
        }

        var newAssetRowVersion = asset.RowVersion + 1;
        var updatedAssets = await ExecuteAsync(
            transaction,
            """
            UPDATE assets
            SET state = 'TRASHED',
                trashed_at_ms = $now,
                current_managed_relative_path = NULL,
                current_managed_file_name = NULL,
                target_managed_relative_path = NULL,
                target_managed_file_name = NULL,
                path_state = 'NONE',
                reconciliation_operation_id = NULL,
                row_version = $newRowVersion
            WHERE asset_id = $assetId AND row_version = $expectedRowVersion;
            """,
            cancellationToken,
            ("$now", now),
            ("$newRowVersion", newAssetRowVersion),
            ("$assetId", DbGuid.Format(plan.AssetId)),
            ("$expectedRowVersion", plan.ExpectedAssetRowVersion)).ConfigureAwait(false);
        if (updatedAssets != 1)
        {
            throw new CatalogInvariantException(
                $"Asset {plan.AssetId:D} changed while its move to Trash was being committed.");
        }

        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.InTrash,
            plan.RecoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken,
            committedPlan.ToJson()).ConfigureAwait(false);

        await AppendActivityAsync(
            transaction,
            ActivityEventType.AssetMovedToTrash,
            plan.OwnerProfileId,
            plan.AssetId,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.AssetId],
            CatalogInvalidationDomain.Media,
            plan.ExpectedAssetRowVersion + 1));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.OwnerProfileId],
            CatalogInvalidationDomain.Profile,
            0));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.AssetId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        // Queue APPEARANCE for profiles that lost cover/banner.
        var affectedAppearanceProfileIds = affected
            .Select(reference => reference.ProfileId)
            .Distinct()
            .ToArray();
        if (affectedAppearanceProfileIds.Length > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                affectedAppearanceProfileIds,
                CatalogInvalidationDomain.Appearance,
                0));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AssetTrashCommitResult(
            OperationResult<AssetTrashOutcome>.Success(
                new AssetTrashOutcome(plan.TrashEntryId, plan.AssetId, plan.RecoveryRelativePath, newAssetRowVersion),
                plan.OperationId),
            committedPlan);
    }

    private async Task<OperationResult<ProfileTrashOutcome>> CommitProfileTrashMarkerAsync(
        ProfileTrashPlan plan,
        string recoveryRelativePath,
        int trashedAssets,
        int reassignedAssets,
        CancellationToken cancellationToken)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var entry = await ReadTrashEntryAsync(connection, transaction, plan.TrashEntryId, cancellationToken)
            .ConfigureAwait(false);
        var profile = await ReadProfileTrashStateAsync(connection, transaction, plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || profile is null)
        {
            return OperationResult<ProfileTrashOutcome>.NotFound(
                OperationErrorCode.TrashEntryNotFound,
                "That Trash record no longer exists.");
        }

        var remaining = await ReadOwnedActiveAssetsAsync(connection, transaction, plan.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            return OperationResult<ProfileTrashOutcome>.Conflict(
                OperationErrorCode.TrashDispositionRequired,
                $"{remaining.Count} media item(s) this Profile owns still need a destination before it can move to Trash.");
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newProfileRowVersion = profile.RowVersion + 1;
        var updated = await ExecuteAsync(
            transaction,
            """
            UPDATE profiles
            SET trashed_at_ms = $now,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion AND trashed_at_ms IS NULL;
            """,
            cancellationToken,
            ("$now", now),
            ("$newRowVersion", newProfileRowVersion),
            ("$profileId", DbGuid.Format(plan.ProfileId)),
            ("$expectedRowVersion", profile.RowVersion)).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new CatalogInvariantException(
                $"Profile {plan.ProfileId:D} changed while its move to Trash was being committed.");
        }

        await CheckpointTrashEntryAsync(
            transaction,
            plan.TrashEntryId,
            TrashEntryState.InTrash,
            recoveryRelativePath,
            completedAtMs: null,
            entry.RowVersion,
            now,
            cancellationToken).ConfigureAwait(false);

        await AppendActivityAsync(
            transaction,
            ActivityEventType.ProfileTrashed,
            plan.ProfileId,
            assetId: null,
            plan.OperationId,
            now,
            cancellationToken).ConfigureAwait(false);

        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.ProfileId],
            CatalogInvalidationDomain.Profile,
            newProfileRowVersion));
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [plan.ProfileId],
            CatalogInvalidationDomain.Trash,
            0));
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileTrashOutcome>.Success(
            new ProfileTrashOutcome(
                plan.TrashEntryId,
                plan.ProfileId,
                newProfileRowVersion,
                trashedAssets,
                reassignedAssets),
            plan.OperationId);
    }

    private async Task<ProfileManifestRecoveryResult> MoveProfileManifestToRecoveryAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        ProfileTrashState? profile;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            profile = await ReadProfileTrashStateAsync(
                    connection, transaction: null, profileId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (profile is null)
        {
            return ProfileManifestRecoveryResult.Rejected(
                OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "That Profile no longer exists."));
        }

        if (string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath))
        {
            return ProfileManifestRecoveryResult.Rejected(
                OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.ProfilePathReconciliationBlocked,
                    "That Profile has no current managed folder, so its recovery material cannot be moved safely."));
        }

        var recoveryRelativePath = $"_trash/profiles/{profileId:D}";
        var request = new ManagedProfileManifestTrashRequest(
            profileId,
            profile.CurrentManagedRelativePath,
            recoveryRelativePath);
        var recovery = await _moveExecutor.InspectProfileManifestTrashDestinationAsync(
                profileId, recoveryRelativePath, cancellationToken)
            .ConfigureAwait(false);

        if (recovery.Status == StorageOperationStatus.SourceMissing)
        {

            var regeneration = await _manifestWriter.RegenerateManifestAsync(
                    _catalog, profileId, cancellationToken)
                .ConfigureAwait(false);
            if (!regeneration.IsSuccess)
            {
                return ProfileManifestRecoveryResult.Rejected(
                    OperationResult<ProfileTrashOutcome>.NeedsAttention(
                        OperationErrorCode.ProfileManifestWriteFailed,
                        "The Profile manifest could not be prepared for recovery. The Profile remains active."));
            }

        }
        else if (!recovery.IsSuccess)
        {
            return ProfileManifestRecoveryResult.Rejected(MapProfileManifestMoveFailure(recovery));
        }

        var move = await _moveExecutor.ExecuteProfileManifestTrashMoveAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!move.IsSuccess)
        {
            return ProfileManifestRecoveryResult.Rejected(MapProfileManifestMoveFailure(move));
        }

        return ProfileManifestRecoveryResult.Accepted(recoveryRelativePath);
    }

    private static OperationResult<ProfileTrashOutcome> MapProfileManifestMoveFailure(
        StorageOperationResult result) => result.Status switch
        {
            StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision
                or StorageOperationStatus.VerificationFailed => OperationResult<ProfileTrashOutcome>.NeedsAttention(
                    OperationErrorCode.TrashDestinationConflict,
                    "The Profile recovery manifest conflicts with material already in Trash, so nothing was overwritten."),
            StorageOperationStatus.PathOutsideVault => OperationResult<ProfileTrashOutcome>.NeedsAttention(
                OperationErrorCode.CurrentPathAmbiguous,
                "The Profile manifest location falls outside its exact managed recovery boundary."),
            StorageOperationStatus.Cancelled => OperationResult<ProfileTrashOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest move was cancelled before it completed."),
            _ => OperationResult<ProfileTrashOutcome>.Failed(
                OperationErrorCode.TrashPhysicalMoveFailed,
                "The Profile manifest could not be moved into recovery. The operation can be retried."),
        };

    private static OperationResult<AssetTrashPlan>? ValidateAssetIsTrashable(AssetTrashState asset)
    {
        if (asset.State != AssetState.Active)
        {
            return OperationResult<AssetTrashPlan>.Conflict(
                OperationErrorCode.AssetNotActive,
                "Only active media can be moved to Trash.");
        }

        if (asset.OwnerProfileId is null)
        {
            return OperationResult<AssetTrashPlan>.NeedsAttention(
                OperationErrorCode.AssetOwnerConflict,
                "This media item has no recorded owner and needs attention before it can be moved to Trash.");
        }

        if (asset.Sha256 is null || asset.ByteLength is null || asset.StorageToken is null)
        {
            return OperationResult<AssetTrashPlan>.NeedsAttention(
                OperationErrorCode.AssetContentMismatch,
                "This media item's content record is incomplete, so a reversible move to Trash cannot be verified.");
        }

        if (asset.CurrentManagedRelativePath is null || asset.CurrentManagedFileName is null)
        {
            return OperationResult<AssetTrashPlan>.NeedsAttention(
                OperationErrorCode.CurrentPathMissing,
                "This media item has no recorded managed file, so there is nothing to move to Trash.");
        }

        if (asset.PathState != ManagedPathState.None)
        {

            return OperationResult<AssetTrashPlan>.Conflict(
                OperationErrorCode.ProfilePathReconciliationBlocked,
                "This media item is already being moved. Wait for that to finish, then try again.");
        }

        return null;
    }

    private async Task<ProfileTrashValidation> ValidateDispositionsAsync(
        SqliteConnection connection,
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedAssetDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var byAsset = new Dictionary<Guid, ProfileOwnedAssetDisposition>();
        foreach (var disposition in dispositions)
        {
            if (!byAsset.TryAdd(disposition.AssetId, disposition))
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionInvalid,
                    "One media item was given more than one destination."));
            }
        }

        var planned = plan.OwnedActiveAssets.Select(asset => asset.AssetId).ToHashSet();
        var missing = planned.Where(assetId => !byAsset.ContainsKey(assetId)).ToArray();
        if (missing.Length > 0)
        {

            return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.TrashDispositionRequired,
                $"{missing.Length} media item(s) this Profile owns still need a destination."));
        }

        var extra = byAsset.Keys.Where(assetId => !planned.Contains(assetId)).ToArray();
        if (extra.Length > 0)
        {
            return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                OperationErrorCode.TrashDispositionInvalid,
                "A destination was given for media this Profile does not own."));
        }

        var pending = new List<ProfileOwnedAssetDisposition>(byAsset.Count);
        foreach (var disposition in byAsset.Values)
        {
            var expectedRowVersion = plan.OwnedActiveAssets
                .First(asset => asset.AssetId == disposition.AssetId)
                .RowVersion;
            var asset = await ReadAssetTrashStateAsync(
                    connection, transaction: null, disposition.AssetId, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.AssetNotFound,
                    "Media in this Profile Trash plan no longer exists."));
            }

            if (disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset)
            {
                if (disposition.NewOwnerProfileId is not null)
                {
                    return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                        OperationErrorCode.TrashDispositionInvalid,
                        "A media item moved to Trash cannot also be given a new owner."));
                }

                if (asset.State == AssetState.Trashed)
                {
                    continue;
                }

                if (asset.State != AssetState.Active
                    || asset.OwnerProfileId != plan.ProfileId
                    || asset.RowVersion != expectedRowVersion)
                {
                    return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                        OperationErrorCode.TrashPlanStale,
                        "Media in this Profile Trash plan changed before its Trash disposition completed."));
                }

                pending.Add(disposition);
                continue;
            }

            if (disposition.NewOwnerProfileId is not { } newOwnerId || newOwnerId == Guid.Empty)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionRequired,
                    "Changing the owner requires an explicitly chosen Profile."));
            }

            if (newOwnerId == plan.ProfileId)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Validation(
                    OperationErrorCode.TrashDispositionInvalid,
                    "Media cannot be given to the Profile that is moving to Trash."));
            }

            var destination = await ReadProfileTrashStateAsync(
                connection, transaction: null, newOwnerId, cancellationToken).ConfigureAwait(false);
            if (destination is null)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.NotFound(
                    OperationErrorCode.ProfileNotFound,
                    "A chosen destination Profile no longer exists."));
            }

            if (destination.IsTrashed)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.ProfileConflict,
                    "A chosen destination Profile is itself in Trash and cannot take ownership of media."));
            }

            if (asset.State == AssetState.Active && asset.OwnerProfileId == newOwnerId)
            {
                continue;
            }

            if (asset.State != AssetState.Active
                || asset.OwnerProfileId != plan.ProfileId
                || asset.RowVersion != expectedRowVersion)
            {
                return ProfileTrashValidation.Rejected(OperationResult<ProfileTrashOutcome>.Conflict(
                    OperationErrorCode.TrashPlanStale,
                    "Media in this Profile Trash plan changed before its OWNER disposition completed."));
            }

            pending.Add(disposition);
        }

        var ordered = pending
            .OrderBy(disposition => disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset ? 0 : 1)
            .ThenBy(disposition => disposition.AssetId)
            .ToArray();

        return ProfileTrashValidation.Accepted(ordered);
    }

    private static async Task<bool> ProfileAuthorityMatchesPlanAsync(
        SqliteConnection connection,
        ProfileTrashState profile,
        ProfileTrashPlan plan,
        IReadOnlyList<ProfileOwnedAssetDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        var expectedCover = plan.ExpectedCoverAssetId;
        var expectedBanner = plan.ExpectedBannerAssetId;
        var expectedRowVersion = plan.ExpectedProfileRowVersion;

        foreach (var disposition in dispositions.OrderBy(item => item.AssetId))
        {
            if (expectedCover != disposition.AssetId && expectedBanner != disposition.AssetId)
            {
                continue;
            }

            var asset = await ReadAssetTrashStateAsync(
                    connection, transaction: null, disposition.AssetId, cancellationToken)
                .ConfigureAwait(false);
            var completed = disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset
                ? asset?.State == AssetState.Trashed
                : asset?.State == AssetState.Active
                  && asset.OwnerProfileId == disposition.NewOwnerProfileId;
            if (!completed)
            {
                continue;
            }

            var clearsAppearance = disposition.Kind == ProfileOwnedAssetDispositionKind.TrashAsset
                || !await ProfileAssetRelationExistsAsync(
                        connection, plan.ProfileId, disposition.AssetId, cancellationToken)
                    .ConfigureAwait(false);
            if (!clearsAppearance)
            {
                continue;
            }

            if (expectedCover == disposition.AssetId)
            {
                expectedCover = null;
            }

            if (expectedBanner == disposition.AssetId)
            {
                expectedBanner = null;
            }

            expectedRowVersion++;
        }

        return profile.RowVersion == expectedRowVersion
            && profile.CoverAssetId == expectedCover
            && profile.BannerAssetId == expectedBanner;
    }

    private static async Task<bool> ProfileAssetRelationExistsAsync(
        SqliteConnection connection,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM profile_assets WHERE profile_id = $profileId AND asset_id = $assetId);";
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    internal static string BuildRecoveryRelativePath(Guid assetId, string normalizedExtension) =>
        $"_trash/assets/{assetId:D}/payload.{normalizedExtension}";

    internal static async Task<AssetTrashState?> ReadAssetTrashStateAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.state, a.media_type, a.asset_storage_token, a.sha256, a.byte_length,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   a.path_state, a.row_version,
                   (SELECT profile_id FROM profile_assets
                    WHERE asset_id = a.asset_id AND relation_type = 'OWNER') AS owner_profile_id
            FROM assets a
            WHERE a.asset_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AssetTrashState(
            assetId,
            DbEnum.ParseAssetState(reader.GetString(0)),
            DbEnum.ParseMediaType(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DbEnum.ParseManagedPathState(reader.GetString(7)),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : DbGuid.Parse(reader.GetString(9)));
    }

    internal static async Task<ProfileTrashState?> ReadProfileTrashStateAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT kind, display_name, unknown_sequence, profile_storage_token,
                   current_managed_relative_path, trashed_at_ms, row_version,
                   cover_asset_id, banner_asset_id
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
        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1);
        var unknownSequence = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);

        return new ProfileTrashState(
            profileId,
            kind,
            displayName ?? UnknownProfileRules.FormatDerivedLabel(unknownSequence ?? 0),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            !reader.IsDBNull(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : DbGuid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DbGuid.Parse(reader.GetString(8)));
    }

    internal static async Task<IReadOnlyList<ProfileOwnedAssetSnapshot>> ReadOwnedActiveAssetsAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.asset_id, a.row_version
            FROM profile_assets pa
            JOIN assets a ON a.asset_id = pa.asset_id
            WHERE pa.profile_id = $profileId
              AND pa.relation_type = 'OWNER'
              AND a.state = 'ACTIVE'
            ORDER BY a.asset_id;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var owned = new List<ProfileOwnedAssetSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            owned.Add(new ProfileOwnedAssetSnapshot(DbGuid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        }

        return owned;
    }

    internal static async Task<IReadOnlyList<AffectedAppearanceReference>> ReadAffectedAppearanceReferencesAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT profile_id, cover_asset_id, banner_asset_id
            FROM profiles
            WHERE cover_asset_id = $assetId OR banner_asset_id = $assetId
            ORDER BY profile_id;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var formatted = DbGuid.Format(assetId);
        var references = new List<AffectedAppearanceReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isCover = !reader.IsDBNull(1) && reader.GetString(1) == formatted;
            var isBanner = !reader.IsDBNull(2) && reader.GetString(2) == formatted;
            references.Add(new AffectedAppearanceReference(
                DbGuid.Parse(reader.GetString(0)),
                isCover,
                isBanner));
        }

        return references;
    }

    internal static async Task<TrashEntryRow?> ReadTrashEntryAsync(
        SqliteConnection connection,
        Guid trashEntryId,
        CancellationToken cancellationToken) =>
        await ReadTrashEntryAsync(connection, transaction: null, trashEntryId, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<TrashEntryRow?> ReadTrashEntryAsync(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        Guid trashEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT entity_type, entity_id, state, recovery_relative_path, plan_json, row_version
            FROM trash_entries
            WHERE trash_entry_id = $trashEntryId;
            """);
        command.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(trashEntryId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrashEntryRow(
            trashEntryId,
            reader.GetString(0),
            DbGuid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5));
    }

    internal static async Task<TrashEntryRow?> ReadActiveTrashEntryAsync(
        SqliteConnection connection,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trash_entry_id, entity_type, entity_id, state, recovery_relative_path, plan_json, row_version
            FROM trash_entries
            WHERE entity_type = $entityType
              AND entity_id = $entityId
              AND state IN ('PENDING', 'EXECUTING', 'IN_TRASH')
            ORDER BY created_at_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", DbGuid.Format(entityId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrashEntryRow(
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbGuid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
    }

    private Task MarkEntryStateAsync(
        Guid trashEntryId,
        string state,
        string? recoveryRelativePath,
        DateTimeOffset? completedAtUtc,
        long expectedRowVersion,
        CancellationToken cancellationToken) =>
        _catalog.TrashWrites.UpdateEntryStateAsync(
            trashEntryId,
            state,
            recoveryRelativePath,
            completedAtUtc,
            expectedRowVersion,
            cancellationToken);

    internal static async Task CheckpointTrashEntryAsync(
        CatalogTransaction transaction,
        Guid trashEntryId,
        string state,
        string? recoveryRelativePath,
        long? completedAtMs,
        long expectedRowVersion,
        long nowMs,
        CancellationToken cancellationToken,
        string? planJson = null)
    {
        await using var command = transaction.CreateCommand(
            """
            UPDATE trash_entries
            SET state = $state,
                recovery_relative_path = $recoveryPath,
                plan_json = CASE WHEN $planJson IS NULL THEN plan_json ELSE $planJson END,
                completed_at_ms = $completedAt,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE trash_entry_id = $trashEntryId AND row_version = $expectedRowVersion;
            """);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$recoveryPath", (object?)recoveryRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$planJson", (object?)planJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", (object?)completedAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$trashEntryId", DbGuid.Format(trashEntryId));
        command.Parameters.AddWithValue("$expectedRowVersion", expectedRowVersion);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CatalogConcurrencyConflictException(
                $"TrashEntry {trashEntryId:D} changed while its checkpoint was being written.");
        }
    }

    internal static async Task AppendActivityAsync(
        CatalogTransaction transaction,
        string eventType,
        Guid? profileId,
        Guid? assetId,
        Guid? operationId,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, asset_id, operation_id,
                payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $assetId, $operationId, $payload, $now);
            """);
        command.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$profileId", profileId is null ? DBNull.Value : DbGuid.Format(profileId.Value));
        command.Parameters.AddWithValue("$assetId", assetId is null ? DBNull.Value : DbGuid.Format(assetId.Value));
        command.Parameters.AddWithValue("$operationId", operationId is null ? DBNull.Value : DbGuid.Format(operationId.Value));
        command.Parameters.AddWithValue("$payload", "{}");
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> ExecuteAsync(
        CatalogTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = transaction.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SqliteCommand CreateCommand(
        SqliteConnection connection,
        CatalogTransaction? transaction,
        string sql)
    {
        if (transaction is not null)
        {
            return transaction.CreateCommand(sql);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal static OperationResult<T> MapTrashMoveFailure<T>(StorageOperationResult result) => result.Status switch
    {
        StorageOperationStatus.SourceMissing => OperationResult<T>.NeedsAttention(
            OperationErrorCode.CurrentPathMissing,
            "The managed file for this media item was not found where it was recorded."),
        StorageOperationStatus.SourceChanged or StorageOperationStatus.VerificationFailed =>
            OperationResult<T>.NeedsAttention(
                OperationErrorCode.AssetContentMismatch,
                "The managed file does not match its recorded content, so it was left untouched."),
        StorageOperationStatus.UnexpectedTarget or StorageOperationStatus.TargetCollision =>
            OperationResult<T>.NeedsAttention(
                OperationErrorCode.TrashDestinationConflict,
                "Something already exists where this media item would be placed, so nothing was overwritten."),
        StorageOperationStatus.Cancelled => OperationResult<T>.Failed(
            OperationErrorCode.TrashPhysicalMoveFailed,
            "The move was cancelled before it completed."),
        StorageOperationStatus.PathOutsideVault => OperationResult<T>.NeedsAttention(
            OperationErrorCode.CurrentPathAmbiguous,
            "A recorded location falls outside the vault and was refused."),
        _ => OperationResult<T>.Failed(
            OperationErrorCode.TrashPhysicalMoveFailed,
            "The media file could not be moved. Nothing was deleted; the operation can be retried."),
    };

    private static OperationResult<TTarget> Propagate<TSource, TTarget>(OperationResult<TSource> source) =>
        new(source.Status, default, source.ErrorCode, source.UserMessage, source.OperationId);

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A stable identifier cannot be empty.", parameterName);
        }
    }

    private static string CombineRelative(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? fileName : $"{trimmed}/{fileName.TrimStart('/', '\\')}";
    }

    private static string RelativeDirectoryOf(string relativePath)
    {
        var index = relativePath.LastIndexOfAny(['/', '\\']);
        return index <= 0 ? string.Empty : relativePath[..index];
    }

    private static string FileNameOf(string relativePath)
    {
        var index = relativePath.LastIndexOfAny(['/', '\\']);
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    private sealed record ProfileTrashValidation(
        OperationResult<ProfileTrashOutcome>? Failure,
        IReadOnlyList<ProfileOwnedAssetDisposition> Ordered)
    {
        public static ProfileTrashValidation Rejected(OperationResult<ProfileTrashOutcome> failure) =>
            new(failure, []);

        public static ProfileTrashValidation Accepted(IReadOnlyList<ProfileOwnedAssetDisposition> ordered) =>
            new(null, ordered);
    }

    private sealed record AssetTrashCommitResult(
        OperationResult<AssetTrashOutcome> Outcome,
        AssetTrashPlan Plan);

    private sealed record ProfileManifestRecoveryResult(
        string? RecoveryRelativePath,
        OperationResult<ProfileTrashOutcome>? Failure)
    {
        public bool IsSuccess => Failure is null;

        public static ProfileManifestRecoveryResult Accepted(string recoveryRelativePath) =>
            new(recoveryRelativePath, null);

        public static ProfileManifestRecoveryResult Rejected(OperationResult<ProfileTrashOutcome> failure) =>
            new(null, failure);
    }
}

public sealed record AssetTrashOutcome(
    Guid TrashEntryId,
    Guid AssetId,
    string RecoveryRelativePath,
    long AssetRowVersion);

public sealed record ProfileTrashOutcome(
    Guid TrashEntryId,
    Guid ProfileId,
    long ProfileRowVersion,
    int TrashedAssetCount,
    int ReassignedAssetCount);

public sealed record TrashEntryRow(
    Guid TrashEntryId,
    string EntityType,
    Guid EntityId,
    string State,
    string? RecoveryRelativePath,
    string PlanJson,
    long RowVersion);

public sealed record AssetTrashState(
    Guid AssetId,
    AssetState State,
    MediaType MediaType,
    string? StorageToken,
    string? Sha256,
    long? ByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName,
    ManagedPathState PathState,
    long RowVersion,
    Guid? OwnerProfileId);

public sealed record ProfileTrashState(
    Guid ProfileId,
    ProfileKind Kind,
    string Label,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    bool IsTrashed,
    long RowVersion,
    Guid? CoverAssetId,
    Guid? BannerAssetId);
