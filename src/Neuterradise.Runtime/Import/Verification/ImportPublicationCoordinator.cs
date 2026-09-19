using Neuterradise.App.Faces;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Import.Verification;

/// <summary>
/// Stage 3 authority. Stage 1 owns canonical bytes and Stage 2 owns preparation; this class is the
/// only boundary that makes an ImportUnit visible as published product state.
/// </summary>
public sealed class ImportPublicationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly VerificationOperations _verification;
    private readonly VerificationValidator _validator;
    private readonly TrashCoordinator _trash;
    private readonly TimeProvider _timeProvider;

    public ImportPublicationCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _verification = new VerificationOperations(catalog, _timeProvider);
        _validator = new VerificationValidator(catalog, _timeProvider);
        _trash = CreateTrashCoordinator(catalog);
    }

    /// <summary>
    /// Persists the final Verify draft without changing the materialized destination, then converges
    /// the durable publication operation. Save/Continue never copies source bytes again.
    /// </summary>
    public async Task<ImportPublicationResult> PublishAsync(
        Guid unitId,
        VerificationDraftV1 finalDraft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalDraft);
        if (unitId == Guid.Empty)
        {
            return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
        }

        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _verification.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
            }

            var existing = VerificationDraftV1.FromJson(
                model.VerificationDraftJson,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var durableDraft = finalDraft with
            {
                CurrentStep = 5,
                ImportRequested = true,
                AttentionItemIds = existing.AttentionItemIds ?? finalDraft.AttentionItemIds,
            };

            try
            {
                await _catalog.ImportWrites.UpdateVerificationDraftAsync(
                    unitId,
                    durableDraft.CurrentStep,
                    durableDraft.ToJson(),
                    durableDraft.SchemaVersion,
                    model.RowVersion,
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }

        return await PublishDurableAsync(unitId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restart-safe publication. COMMITTING means Save was accepted and publication must converge;
    /// re-entry repeats only idempotent/durably-proven steps.
    /// </summary>
    public async Task<ImportPublicationResult> PublishDurableAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var model = await _verification.LoadVerificationReadModelAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ImportPublicationResult.Blocked("UNIT_NOT_FOUND", "That import no longer exists.");
        }

        if (model.State.IsUnitCommitted())
        {
            return ImportPublicationResult.AlreadyPublished();
        }

        if (model.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal)
        {
            return ImportPublicationResult.Blocked("IMPORT_NOT_PUBLISHABLE", "This import can no longer be published.");
        }

        if (model.State is not (ImportUnitState.ReadyForVerification or ImportUnitState.Committing))
        {
            return ImportPublicationResult.Blocked(
                "IMPORT_NOT_READY_FOR_PUBLICATION",
                "This import is still preparing. Review becomes publishable when preparation is complete.");
        }

        var draft = VerificationDraftV1.FromJson(
            model.VerificationDraftJson,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var destination = await ReadDurableDestinationAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (destination is null || destination.ProfileId == Guid.Empty)
        {
            return ImportPublicationResult.Pending("PUBLICATION_DESTINATION_UNAVAILABLE", "The prepared Profile destination could not be resolved yet.");
        }

        var destinationBlocker = ValidateDestinationAuthority(draft, destination);
        if (destinationBlocker is not null)
        {
            return destinationBlocker;
        }

        if (model.State == ImportUnitState.ReadyForVerification)
        {
            var readiness = await _validator.ValidateCommitReadinessAsync(unitId, cancellationToken).ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                return ImportPublicationResult.Blocked(
                    readiness.Blockers.FirstOrDefault()?.Code ?? "VERIFY_BLOCKED",
                    "Verify still has unresolved decisions.");
            }

            await _catalog.ImportWrites.UpdateUnitStateAsync(
                unitId,
                ImportUnitState.Committing,
                expectedState: ImportUnitState.ReadyForVerification,
                expectedRowVersion: model.RowVersion,
                cancellationToken).ConfigureAwait(false);
        }

        // Ownership choices can change the Stage-1 delta, but the relation remains unpublished until
        // the core publication transaction clears its Unit marker.
        var collisionResult = await ApplyCollisionDecisionsAsync(
            unitId,
            destination.ProfileId,
            draft,
            cancellationToken).ConfigureAwait(false);
        if (!collisionResult.IsSuccess)
        {
            return collisionResult;
        }

        try
        {
            await ApplyCorePublicationAsync(unitId, destination, draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Import publication core remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "PUBLICATION_CORE_RETRY_REQUIRED",
                "Your Save is durable, but publication still needs recovery.");
        }

        // Face choices are deliberately after the core boundary: APPEARS/evidence/identity samples
        // must never leak into a published Profile before Save. They are idempotent on re-entry.
        var faceResult = await ApplyFaceDecisionsAsync(destination.ProfileId, draft, cancellationToken).ConfigureAwait(false);
        if (!faceResult.IsSuccess)
        {
            return faceResult;
        }

        // Related evidence is a published projection. Build it only after the core transaction has
        // exposed this unit's relations/Profile and after staged face decisions have become durable.
        // Re-entry is safe because evidence keys are deterministic and RelatedWrites is idempotent.
        try
        {
            var related = new RelatedEvidenceProjector(_catalog, _timeProvider);
            foreach (var assetId in await ReadUnitPublishedAssetIdsAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                await related.ProjectSharedAssetEvidenceForAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
                await related.ProjectConfirmedFaceEvidenceForAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Published related projection remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "RELATED_PUBLICATION_PENDING",
                "The Profile is published; related-media projection still needs recovery.");
        }

        ImportCommitResult cleanup;
        try
        {
            cleanup = await ImportFinalizer.CreateDefaultCommitCoordinator(_catalog)
                .CommitAsync(unitId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Published import cleanup remains pending for {0:D}: {1}",
                unitId,
                exception.GetType().Name);
            return ImportPublicationResult.Pending(
                "PUBLICATION_CLEANUP_RETRY_REQUIRED",
                "The Profile is published; source cleanup will be recovered.");
        }

        var finalState = cleanup.SourceCleanupAttentionItemIds.Count > 0
            ? ImportUnitState.CommittedWithCleanupAttention
            : cleanup.Checkpoint >= ImportCommitCheckpoint.SourceCleanupComplete
                ? ImportUnitState.Completed
                : ImportUnitState.Committed;
        await _catalog.ImportWrites.UpdateUnitStateAsync(unitId, finalState, cancellationToken).ConfigureAwait(false);

        return finalState == ImportUnitState.CommittedWithCleanupAttention
            ? ImportPublicationResult.PublishedWithAttention(destination.ProfileId)
            : ImportPublicationResult.Published(destination.ProfileId);
    }

    private async Task<ImportPublicationResult> ApplyCollisionDecisionsAsync(
        Guid unitId,
        Guid destinationProfileId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        foreach (var decision in draft.ProfileCollisionDecisions ?? [])
        {
            if (decision.Action == ProfileCollisionAction.KeepDestination)
            {
                continue;
            }

            var item = await ReadCollisionItemAsync(unitId, decision.ImportItemId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return ImportPublicationResult.Blocked("COLLISION_ITEM_NOT_FOUND", "One Verify ownership decision no longer matches this import.");
            }

            if (decision.Action == ProfileCollisionAction.Skip)
            {
                if (item.ReusedAssetId is { } reusedId)
                {
                    await DeleteImportRelationAsync(unitId, destinationProfileId, reusedId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (item.CandidateAssetId is not { } candidateId)
                {
                    continue;
                }

                var state = await ReadAssetStateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (state is null || state.Value.State is AssetState.Trashed or AssetState.Retired)
                {
                    continue;
                }
                if (state.Value.State != AssetState.Active)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_ASSET_NOT_SETTLED", "A skipped media item has not reached a safe library state yet.");
                }

                var plan = await _trash.PrepareAssetTrashAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (!plan.IsSuccess || plan.Value is null)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_TRASH_PENDING", "A skipped media item still needs recoverable Trash disposition.");
                }
                var executed = await _trash.ExecuteAssetTrashAsync(plan.Value, cancellationToken).ConfigureAwait(false);
                if (!executed.IsSuccess)
                {
                    return ImportPublicationResult.Pending("COLLISION_SKIP_TRASH_PENDING", "A skipped media item still needs recoverable Trash disposition.");
                }
                continue;
            }

            if (decision.Action == ProfileCollisionAction.MoveToProfile)
            {
                if (decision.TargetProfileId is not { } targetId || targetId == Guid.Empty)
                {
                    return ImportPublicationResult.Blocked("COLLISION_MOVE_TARGET_REQUIRED", "A moved media item needs a destination Profile.");
                }
                if (item.ReusedAssetId is not null || item.CandidateAssetId is not { } candidateId)
                {
                    return ImportPublicationResult.Blocked("COLLISION_REUSED_MOVE_UNSUPPORTED", "Existing shared media cannot have its primary owner changed by this import.");
                }

                var state = await ReadAssetStateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                if (state is null || state.Value.State != AssetState.Active)
                {
                    return ImportPublicationResult.Pending("COLLISION_MOVE_ASSET_NOT_ACTIVE", "A moved media item is not ready for ownership publication yet.");
                }
                if (state.Value.OwnerProfileId == targetId)
                {
                    continue;
                }

                var moved = await new MediaOperations(_catalog).ChangePrimaryProfileAsync(
                    new ChangePrimaryProfileRequest(candidateId, targetId, state.Value.RowVersion),
                    cancellationToken).ConfigureAwait(false);
                if (!moved.IsSuccess)
                {
                    return ImportPublicationResult.Pending("COLLISION_MOVE_PENDING", "A media ownership move still needs recovery.");
                }
            }
        }

        return ImportPublicationResult.StepSucceeded();
    }

    private async Task ApplyCorePublicationAsync(
        Guid unitId,
        DurableDestination destination,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var profileId = destination.ProfileId;

        if (destination.Kind == DestinationKind.NewNormal)
        {
            var profile = draft.Destination.NewProfile
                ?? throw new CatalogInvariantException("A new Profile publication lost its final draft metadata.");
            await using (var update = transaction.CreateCommand(
                """
                UPDATE profiles
                SET display_name = $name,
                    category_id = $categoryId,
                    rating = $rating,
                    is_favorite = $favorite,
                    overview = $overview,
                    updated_at_ms = $now,
                    row_version = row_version + 1
                WHERE profile_id = $profileId
                  AND kind = 'NORMAL'
                  AND trashed_at_ms IS NULL;
                """))
            {
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                update.Parameters.AddWithValue("$name", profile.DisplayName.Trim());
                update.Parameters.AddWithValue("$categoryId", (object?)profile.CategoryId ?? DBNull.Value);
                update.Parameters.AddWithValue("$rating", (object?)profile.Rating ?? DBNull.Value);
                update.Parameters.AddWithValue("$favorite", profile.Favorite == true ? 1 : 0);
                update.Parameters.AddWithValue("$overview", (object?)profile.Overview ?? DBNull.Value);
                update.Parameters.AddWithValue("$now", now);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CatalogInvariantException("The draft Profile is no longer publishable.");
                }
            }

            await using (var clearTags = transaction.CreateCommand("DELETE FROM profile_tags WHERE profile_id = $profileId;"))
            {
                clearTags.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await clearTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var tagId in (profile.TagIds ?? []).Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                await using var insert = transaction.CreateCommand(
                    "INSERT INTO profile_tags(profile_id, tag_id, created_at_ms) VALUES ($profileId, $tagId, $now);");
                insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                insert.Parameters.AddWithValue("$tagId", tagId.Trim());
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await ApplyAppearanceInTransactionAsync(transaction, profileId, draft.Appearance, now, cancellationToken).ConfigureAwait(false);

        // This UPDATE is the media-delta publication boundary for existing Profiles. Every public
        // profile/media read excludes rows carrying this marker.
        await using (var publishRelations = transaction.CreateCommand(
            """
            UPDATE profile_assets
            SET publication_import_unit_id = NULL
            WHERE publication_import_unit_id = $unitId;
            """))
        {
            publishRelations.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await publishRelations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (destination.Kind == DestinationKind.NewNormal)
        {
            await using var publishProfile = transaction.CreateCommand(
                """
                UPDATE profiles
                SET visibility = 'PUBLISHED', updated_at_ms = $now, row_version = row_version + 1
                WHERE profile_id = $profileId AND visibility = 'DRAFT';
                """);
            publishProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            publishProfile.Parameters.AddWithValue("$now", now);
            await publishProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var mark = transaction.CreateCommand(
            """
            UPDATE import_units
            SET verification_step = 5,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE import_unit_id = $unitId AND state = 'COMMITTING';
            """))
        {
            mark.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            mark.Parameters.AddWithValue("$now", now);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Read published asset IDs for this import unit before committing.
        var publishedAssetIds = new List<Guid>();
        await using (var readAssets = transaction.CreateCommand(
            """
            SELECT DISTINCT COALESCE(reused_asset_id, candidate_asset_id)
            FROM import_items
            WHERE import_unit_id = $unitId
              AND disposition IN ('INCLUDED','REUSED')
              AND COALESCE(reused_asset_id, candidate_asset_id) IS NOT NULL;
            """))
        {
            readAssets.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await readAssets.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    publishedAssetIds.Add(DbGuid.Parse(reader.GetString(0)));
                }
            }
        }

        // Queue public domain invalidations at the Stage3 publication boundary.
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [profileId],
            CatalogInvalidationDomain.Profile,
            0));
        if (publishedAssetIds.Count > 0)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                publishedAssetIds,
                CatalogInvalidationDomain.Media,
                0));
        }
        // R01.10: Invalidate Appearance on any intent change (Set or Clear).
        if (draft.Appearance.CoverIntent != VerificationAppearanceIntent.Unchanged
            || draft.Appearance.BannerIntent != VerificationAppearanceIntent.Unchanged)
        {
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [profileId],
                CatalogInvalidationDomain.Appearance,
                0));
        }
        transaction.QueueInvalidation(CatalogInvalidationDomain.Health);
        transaction.QueueInvalidation(CatalogInvalidationDomain.Activity);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAppearanceInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        VerificationAppearanceDraft appearance,
        long now,
        CancellationToken cancellationToken)
    {
        // R01.8: Intent-driven semantics.
        var applyCover = appearance.CoverIntent != VerificationAppearanceIntent.Unchanged;
        var applyBanner = appearance.BannerIntent != VerificationAppearanceIntent.Unchanged;
        if (!applyCover && !applyBanner)
        {
            return;
        }

        // R01.8: Validate SET intent has a valid asset ID.
        if (appearance.CoverIntent == VerificationAppearanceIntent.Set
            && (appearance.CoverAssetId is null || appearance.CoverAssetId.Value == Guid.Empty))
        {
            throw new CatalogInvariantException("Cover intent is SET but no valid Cover asset ID is present.");
        }
        if (appearance.BannerIntent == VerificationAppearanceIntent.Set
            && (appearance.BannerAssetId is null || appearance.BannerAssetId.Value == Guid.Empty))
        {
            throw new CatalogInvariantException("Banner intent is SET but no valid Banner asset ID is present.");
        }

        string? currentJson;
        await using (var read = transaction.CreateCommand(
            "SELECT overrides_json FROM profile_appearance WHERE profile_id = $profileId;"))
        {
            read.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            currentJson = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        var overrides = ProfileAppearanceOverrides.Parse(currentJson);
        if (appearance.CoverIntent == VerificationAppearanceIntent.Set)
        {
            overrides = overrides with
            {
                CoverSourceKind = appearance.CoverSourceKind ?? overrides.CoverSourceKind,
                CoverVideoTimestampMilliseconds = appearance.CoverVideoTimestampMilliseconds,
            };
        }
        else if (appearance.CoverIntent == VerificationAppearanceIntent.Clear)
        {
            // R01.9: Clear stale source-specific Cover metadata.
            overrides = overrides with
            {
                CoverSourceKind = null,
                CoverVideoTimestampMilliseconds = null,
            };
        }

        if (appearance.BannerIntent == VerificationAppearanceIntent.Set)
        {
            var isClip = string.Equals(appearance.BannerSourceKind, BannerVisualSourceKind.VideoClip.ToString(), StringComparison.OrdinalIgnoreCase);
            overrides = overrides with
            {
                BannerSourceKind = appearance.BannerSourceKind ?? overrides.BannerSourceKind,
                BannerVideoFrameTimestampMilliseconds = appearance.BannerVideoFrameTimestampMilliseconds,
                BannerStartPointSeconds = isClip
                    ? appearance.BannerStartPointSeconds ?? overrides.BannerStartPointSeconds
                    : ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                BannerDurationSeconds = isClip
                    ? appearance.BannerDurationSeconds ?? overrides.BannerDurationSeconds
                    : ProfileAppearanceOverrides.Default.BannerDurationSeconds,
            };
        }
        else if (appearance.BannerIntent == VerificationAppearanceIntent.Clear)
        {
            // R01.9: Clear stale source-specific Banner metadata.
            overrides = overrides with
            {
                BannerSourceKind = null,
                BannerVideoFrameTimestampMilliseconds = null,
                BannerStartPointSeconds = null,
                BannerDurationSeconds = null,
            };
        }

        // R01.8: Intent-driven SQL update.
        // Set: write new asset ID. Clear: write NULL. Unchanged: leave as-is.
        var coverIdValue = appearance.CoverIntent == VerificationAppearanceIntent.Set
            ? DbGuid.Format(appearance.CoverAssetId!.Value)
            : DBNull.Value;
        var bannerIdValue = appearance.BannerIntent == VerificationAppearanceIntent.Set
            ? DbGuid.Format(appearance.BannerAssetId!.Value)
            : DBNull.Value;

        await using (var sources = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_asset_id = CASE WHEN $coverIntent = 1 THEN $coverId WHEN $coverIntent = 2 THEN NULL ELSE cover_asset_id END,
                banner_asset_id = CASE WHEN $bannerIntent = 1 THEN $bannerId WHEN $bannerIntent = 2 THEN NULL ELSE banner_asset_id END,
                updated_at_ms = $now,
                row_version = row_version + 1
            WHERE profile_id = $profileId AND trashed_at_ms IS NULL;
            """))
        {
            sources.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            // 0 = Unchanged, 1 = Set, 2 = Clear
            sources.Parameters.AddWithValue("$coverIntent", (int)appearance.CoverIntent);
            sources.Parameters.AddWithValue("$bannerIntent", (int)appearance.BannerIntent);
            sources.Parameters.AddWithValue("$coverId", coverIdValue);
            sources.Parameters.AddWithValue("$bannerId", bannerIdValue);
            sources.Parameters.AddWithValue("$now", now);
            await sources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, NULL, $overrides, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """);
        upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
        upsert.Parameters.AddWithValue("$overrides", overrides.ToJson());
        upsert.Parameters.AddWithValue("$now", now);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportPublicationResult> ApplyFaceDecisionsAsync(
        Guid destinationProfileId,
        VerificationDraftV1 draft,
        CancellationToken cancellationToken)
    {
        var operations = new FaceDecisionOperations(_catalog, _timeProvider);
        foreach (var decision in draft.FaceDecisions)
        {
            if (decision.Decision == VerificationFaceDecision.Reject)
            {
                var rejected = await operations.RejectFaceAsync(
                    new RejectFaceCommand(decision.FaceId, decision.ExpectedFaceRowVersion),
                    cancellationToken).ConfigureAwait(false);
                if (!rejected.IsSuccess)
                {
                    return ImportPublicationResult.Pending("FACE_PUBLICATION_PENDING", "A reviewed face decision still needs recovery.");
                }
                continue;
            }

            Guid targetProfileId;
            if (decision.TargetKind == VerificationFaceTargetKind.Destination)
            {
                targetProfileId = destinationProfileId;
            }
            else if (decision.TargetKind == VerificationFaceTargetKind.ExistingProfile
                && decision.TargetProfileId is { } existingTarget
                && existingTarget != Guid.Empty)
            {
                targetProfileId = existingTarget;
            }
            else
            {
                return ImportPublicationResult.Blocked("FACE_TARGET_REQUIRED", "A confirmed face needs a valid Profile target.");
            }

            var confirmed = await operations.ConfirmFaceAsync(
                new ConfirmFaceCommand(decision.FaceId, targetProfileId, decision.ExpectedFaceRowVersion),
                cancellationToken).ConfigureAwait(false);
            if (!confirmed.IsSuccess)
            {
                return ImportPublicationResult.Pending("FACE_PUBLICATION_PENDING", "A reviewed face decision still needs recovery.");
            }
        }

        return ImportPublicationResult.StepSucceeded();
    }

    private static ImportPublicationResult? ValidateDestinationAuthority(
        VerificationDraftV1 draft,
        DurableDestination destination)
    {
        if (draft.Destination.Kind != destination.Kind)
        {
            return ImportPublicationResult.Blocked(
                "DESTINATION_CHANGED_AFTER_STAGE1",
                "The destination Profile cannot be changed after canonical media preparation.");
        }

        if (destination.Kind == DestinationKind.ExistingNormal
            && draft.Destination.ProfileId != destination.ProfileId)
        {
            return ImportPublicationResult.Blocked(
                "DESTINATION_CHANGED_AFTER_STAGE1",
                "The destination Profile cannot be changed after canonical media preparation.");
        }

        return null;
    }

    private async Task<DurableDestination?> ReadDurableDestinationAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT destination_kind, destination_profile_id FROM import_units WHERE import_unit_id = $unitId;";
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            return null;
        }

        var kindText = reader.GetString(0);
        var kind = Enum.TryParse<DestinationKind>(kindText, ignoreCase: true, out var parsed)
            ? parsed
            : kindText.ToUpperInvariant() switch
            {
                "NEW_NORMAL" => DestinationKind.NewNormal,
                "EXISTING_NORMAL" => DestinationKind.ExistingNormal,
                "SYSTEM_UNKNOWN" => DestinationKind.SystemUnknown,
                _ => throw new CatalogInvariantException($"Unknown import destination kind '{kindText}'."),
            };
        return new DurableDestination(kind, DbGuid.Parse(reader.GetString(1)));
    }

    private async Task<IReadOnlyList<Guid>> ReadUnitPublishedAssetIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT COALESCE(reused_asset_id, candidate_asset_id)
            FROM import_items
            WHERE import_unit_id = $unitId
              AND disposition IN ('INCLUDED','REUSED')
              AND COALESCE(reused_asset_id, candidate_asset_id) IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(DbGuid.Parse(reader.GetString(0)));
        }
        return result;
    }

    private async Task<CollisionItem?> ReadCollisionItemAsync(
        Guid unitId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_asset_id, reused_asset_id
            FROM import_items
            WHERE import_unit_id = $unitId AND import_item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        command.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new CollisionItem(
            reader.IsDBNull(0) ? null : DbGuid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)));
    }

    private async Task<(AssetState State, Guid? OwnerProfileId, long RowVersion)?> ReadAssetStateAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.state,
                   (SELECT pa.profile_id FROM profile_assets pa
                    WHERE pa.asset_id = a.asset_id AND pa.relation_type = 'OWNER' LIMIT 1),
                   a.row_version
            FROM assets a
            WHERE a.asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return (
            DbEnum.ParseAssetState(reader.GetString(0)),
            reader.IsDBNull(1) ? null : DbGuid.Parse(reader.GetString(1)),
            reader.GetInt64(2));
    }

    private async Task DeleteImportRelationAsync(
        Guid unitId,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM profile_assets
            WHERE profile_id = $profileId
              AND asset_id = $assetId
              AND publication_import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrashCoordinator CreateTrashCoordinator(CatalogDb catalog)
    {
        var volume = new WindowsVolumeIdentityProvider();
        var verifier = new ManagedFileVerifier();
        var moveExecutor = new ManagedMoveExecutor(catalog.Paths, volume, verifier, new AssetWrites(catalog));
        return new TrashCoordinator(catalog, moveExecutor, new MediaOperations(catalog));
    }

    private sealed record DurableDestination(DestinationKind Kind, Guid ProfileId);
    private sealed record CollisionItem(Guid? CandidateAssetId, Guid? ReusedAssetId);
}

public enum ImportPublicationStatus
{
    StepSucceeded,
    Published,
    PublishedWithAttention,
    AlreadyPublished,
    Blocked,
    PendingRecovery,
}

public sealed record ImportPublicationResult(
    ImportPublicationStatus Status,
    Guid? ProfileId,
    string? Code,
    string? UserMessage)
{
    public bool IsSuccess => Status is ImportPublicationStatus.StepSucceeded
        or ImportPublicationStatus.Published
        or ImportPublicationStatus.PublishedWithAttention
        or ImportPublicationStatus.AlreadyPublished;

    public bool IsPublished => Status is ImportPublicationStatus.Published
        or ImportPublicationStatus.PublishedWithAttention
        or ImportPublicationStatus.AlreadyPublished;

    public static ImportPublicationResult StepSucceeded() =>
        new(ImportPublicationStatus.StepSucceeded, null, null, null);

    public static ImportPublicationResult Published(Guid profileId) =>
        new(ImportPublicationStatus.Published, profileId, null, null);

    public static ImportPublicationResult PublishedWithAttention(Guid profileId) =>
        new(ImportPublicationStatus.PublishedWithAttention, profileId, "SOURCE_CLEANUP_ATTENTION", "Published; source cleanup still needs attention.");

    public static ImportPublicationResult AlreadyPublished() =>
        new(ImportPublicationStatus.AlreadyPublished, null, null, null);

    public static ImportPublicationResult Blocked(string code, string message) =>
        new(ImportPublicationStatus.Blocked, null, code, message);

    public static ImportPublicationResult Pending(string code, string message) =>
        new(ImportPublicationStatus.PendingRecovery, null, code, message);
}
