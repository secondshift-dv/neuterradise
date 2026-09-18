using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.Profiles;

public sealed class ProfileAppearanceOperations
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly BannerPreviewRefreshEnqueue? _bannerPreviewRefresh;
    private readonly TimeProvider _timeProvider;

    public ProfileAppearanceOperations(
        CatalogDb catalog,
        TimeProvider? timeProvider = null,
        BannerPreviewRefreshEnqueue? bannerPreviewRefresh = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bannerPreviewRefresh = bannerPreviewRefresh;
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetCoverAssetAsync(
        SetCoverAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var gate = await ValidateProfileAsync(
            transaction, request.ProfileId, request.ExpectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var coverSourceKind = CoverVisualSourceKind.Image;
        long? coverTimestamp = null;

        if (request.CoverAssetId is { } coverAssetId)
        {
            var asset = await ReadAppearanceAssetAsync(transaction, coverAssetId, cancellationToken)
                .ConfigureAwait(false);
            var rejection = await ValidateAppearanceAssetAsync(
                transaction,
                request.ProfileId,
                coverAssetId,
                ProfileAppearanceRules.IsCoverMediaTypeEligible,
                "A Cover must be an image or a video frame that is already linked to this Profile.",
                cancellationToken).ConfigureAwait(false);
            if (rejection is not null)
            {
                return rejection;
            }

            coverSourceKind = ProfileAppearanceRules.ResolveCoverSourceKind(
                asset?.MediaType ?? MediaType.Image);
            coverTimestamp = coverSourceKind == CoverVisualSourceKind.VideoFrame
                ? request.CoverVideoTimestampMilliseconds
                : null;

            if (!ProfileAppearanceRules.IsCoverVisualSourceValid(
                    asset?.MediaType ?? MediaType.Image,
                    coverSourceKind,
                    coverTimestamp))
            {
                return OperationResult<ProfileAppearanceOutcome>.Validation(
                    OperationErrorCode.ProfileAppearanceInvalid,
                    coverSourceKind == CoverVisualSourceKind.VideoFrame
                        ? "A video Cover must name the exact frame it uses, in milliseconds."
                        : "An image Cover carries no frame timestamp.");
            }
        }

        var newRowVersion = request.ExpectedProfileRowVersion + 1;
        await using (var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_asset_id = $coverAssetId,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """))
        {
            update.Parameters.AddWithValue(
                "$coverAssetId",
                request.CoverAssetId is { } id ? DbGuid.Format(id) : DBNull.Value);
            update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            update.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedProfileRowVersion);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var storedOverrides = await ReadOverridesInTransactionAsync(
            transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        await WriteOverridesAsync(
            transaction,
            request.ProfileId,
            storedOverrides with
            {
                CoverSourceKind = request.CoverAssetId is null ? null : coverSourceKind.ToString(),
                CoverVideoTimestampMilliseconds = coverTimestamp,
            },
            cancellationToken).ConfigureAwait(false);

        await AppendAppearanceActivityAsync(transaction, request.ProfileId, "cover", DbTime.Format(_timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Appearance,
            newRowVersion));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileAppearanceOutcome>.Success(
            new ProfileAppearanceOutcome(request.ProfileId, newRowVersion));
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetBannerAssetAsync(
        SetBannerAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        var refreshVideoPreview = false;
        long newRowVersion;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var gate = await ValidateProfileAsync(
                transaction, request.ProfileId, request.ExpectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
            if (gate is not null)
            {
                return gate;
            }

            if (request.BannerAssetId is { } bannerAssetId)
            {
                var asset = await ReadAppearanceAssetAsync(transaction, bannerAssetId, cancellationToken)
                    .ConfigureAwait(false);
                var rejection = await ValidateAppearanceAssetAsync(
                    transaction,
                    request.ProfileId,
                    bannerAssetId,
                    ProfileAppearanceRules.IsBannerMediaTypeEligible,
                    "A banner must be an image, video frame, or video clip that already belongs to this profile.",
                    cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    return rejection;
                }

                var mediaType = asset?.MediaType ?? MediaType.Image;
                var sourceKind = request.SourceKind ?? ProfileAppearanceRules.ResolveBannerSourceKind(mediaType);
                var startPointSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                    ? request.StartPointSeconds ?? ProfileAppearanceOverrides.Default.BannerStartPointSeconds
                    : request.StartPointSeconds;
                var durationSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                    ? request.DurationSeconds ?? ProfileAppearanceOverrides.Default.BannerDurationSeconds
                    : request.DurationSeconds;
                if (!ProfileAppearanceRules.IsBannerVisualSourceValid(
                        mediaType,
                        sourceKind,
                        request.VideoFrameTimestampMilliseconds,
                        startPointSeconds,
                        durationSeconds))
                {
                    return OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        "The banner source kind and timing information do not match its media.");
                }

                var stored = await ReadOverridesInTransactionAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
                await WriteOverridesAsync(
                    transaction,
                    request.ProfileId,
                    stored with
                    {
                        BannerSourceKind = sourceKind.ToString(),
                        BannerVideoFrameTimestampMilliseconds = sourceKind == BannerVisualSourceKind.VideoFrame
                            ? request.VideoFrameTimestampMilliseconds
                            : null,
                        BannerStartPointSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                            ? startPointSeconds!.Value
                            : ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                        BannerDurationSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                            ? durationSeconds!.Value
                            : ProfileAppearanceOverrides.Default.BannerDurationSeconds,
                    },
                    cancellationToken).ConfigureAwait(false);
                refreshVideoPreview = sourceKind == BannerVisualSourceKind.VideoClip;
            }
            else
            {
                var stored = await ReadOverridesInTransactionAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
                await WriteOverridesAsync(
                    transaction,
                    request.ProfileId,
                    stored with
                    {
                        BannerSourceKind = null,
                        BannerVideoFrameTimestampMilliseconds = null,
                        BannerStartPointSeconds = ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                        BannerDurationSeconds = ProfileAppearanceOverrides.Default.BannerDurationSeconds,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            newRowVersion = request.ExpectedProfileRowVersion + 1;
            await using (var update = transaction.CreateCommand(
                """
                UPDATE profiles
                SET banner_asset_id = $bannerAssetId,
                    updated_at_ms = $now,
                    row_version = $newRowVersion
                WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
                """))
            {
                update.Parameters.AddWithValue(
                    "$bannerAssetId",
                    request.BannerAssetId is { } id ? DbGuid.Format(id) : DBNull.Value);
                update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
                update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
                update.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedProfileRowVersion);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendAppearanceActivityAsync(transaction, request.ProfileId, "banner", DbTime.Format(_timeProvider.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [request.ProfileId],
                CatalogInvalidationDomain.Appearance,
                newRowVersion));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (refreshVideoPreview)
        {
            await RequestBannerPreviewRefreshAsync(
                request.ProfileId, request.BannerAssetId, "banner-asset", cancellationToken).ConfigureAwait(false);
        }

        return OperationResult<ProfileAppearanceOutcome>.Success(
            new ProfileAppearanceOutcome(request.ProfileId, newRowVersion));
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> ClearAppearanceReferencesAsync(
        Guid profileId,
        long expectedProfileRowVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);

        var gate = await ValidateProfileAsync(
            transaction, profileId, expectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var newRowVersion = expectedProfileRowVersion + 1;
        await using (var update = transaction.CreateCommand(
            """
            UPDATE profiles
            SET cover_asset_id = NULL,
                banner_asset_id = NULL,
                updated_at_ms = $now,
                row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """))
        {
            update.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
            update.Parameters.AddWithValue("$newRowVersion", newRowVersion);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            update.Parameters.AddWithValue("$expectedRowVersion", expectedProfileRowVersion);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var storedOverrides = await ReadOverridesInTransactionAsync(
            transaction, profileId, cancellationToken).ConfigureAwait(false);
        await WriteOverridesAsync(
            transaction,
            profileId,
            storedOverrides with
            {
                CoverSourceKind = null,
                CoverVideoTimestampMilliseconds = null,
                BannerSourceKind = null,
                BannerVideoFrameTimestampMilliseconds = null,
                BannerStartPointSeconds = ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                BannerDurationSeconds = ProfileAppearanceOverrides.Default.BannerDurationSeconds,
            },
            cancellationToken).ConfigureAwait(false);

        await AppendAppearanceActivityAsync(transaction, profileId, "cleared", DbTime.Format(_timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [profileId],
            CatalogInvalidationDomain.Appearance,
            newRowVersion));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return OperationResult<ProfileAppearanceOutcome>.Success(
            new ProfileAppearanceOutcome(profileId, newRowVersion));
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetProfileLayoutOverrideAsync(
        SetProfileLayoutOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        var presetId = string.IsNullOrWhiteSpace(request.LayoutPresetId) ? null : request.LayoutPresetId.Trim();
        if (presetId is not null && !ProfileLayoutResolver.IsBuiltInPresetId(presetId))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileLayoutInvalid,
                "That Profile layout is not one of the available layouts.");
        }

        return await UpdateAppearanceRecordAsync(
            request.ProfileId,
            request.ExpectedProfileRowVersion,
            presetId,
            overrides: null,
            requestBannerRefresh: false,
            bannerAssetId: null,
            "layout",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetProfileGalleryCardOverrideAsync(
        SetProfileGalleryCardOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        var variantId = string.IsNullOrWhiteSpace(request.CardVariantId) ? null : request.CardVariantId.Trim();
        if (variantId is not null && !GalleryCardCatalog.TryGetVariant(variantId, out _))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                $"The gallery card variant '{request.CardVariantId}' is not recognized.");
        }

        var stored = await ReadOverridesAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        var overrides = stored with { GalleryCardVariantId = variantId };

        return await UpdateAppearanceRecordAsync(
            request.ProfileId,
            request.ExpectedProfileRowVersion,
            layoutPresetId: null,
            overrides,
            requestBannerRefresh: false,
            bannerAssetId: null,
            "galleryCardVariant",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetProfileCoverAppearanceAsync(
        SetProfileCoverAppearanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        var resolution = CoverFrameCatalog.Resolve(
            new CoverAppearanceRequest(
                request.CoverShape,
                request.CoverFrameId,
                request.CoverFrameScale,
                request.CoverFrameTint,
                request.CoverFrameIntensity,
                request.CoverFrameAnimation,
                request.CoverShadow),
            reduceMotion: false);

        if (resolution.Diagnostics.Count > 0)
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileAppearanceInvalid,
                resolution.Diagnostics[0].Detail);
        }

        if (!ProfileAppearanceRules.IsCoverCropValid(request.CropX)
            || !ProfileAppearanceRules.IsCoverCropValid(request.CropY))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileAppearanceInvalid,
                "A Cover crop position must sit between 0 and 1.");
        }

        if (!ProfileAppearanceRules.IsCoverZoomValid(request.Zoom))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileAppearanceInvalid,
                $"A Cover zoom must be between {ProfileAppearanceRules.MinimumCoverZoom} and "
                + $"{ProfileAppearanceRules.MaximumCoverZoom}.");
        }

        var stored = await ReadOverridesAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        var overrides = stored with
        {
            CoverShape = resolution.Appearance.Shape.ToString(),
            CoverFrameId = resolution.Appearance.Frame.Id,
            CoverFrameScale = resolution.Appearance.FrameScale,
            CoverFrameTint = resolution.Appearance.FrameTint,
            CoverFrameIntensity = resolution.Appearance.FrameIntensity,
            CoverFrameAnimation = resolution.Appearance.FrameAnimation.ToString(),
            CoverShadow = resolution.Appearance.CoverShadow,
            CropX = request.CropX,
            CropY = request.CropY,
            Zoom = request.Zoom,
        };

        return await UpdateAppearanceRecordAsync(
            request.ProfileId,
            request.ExpectedProfileRowVersion,
            layoutPresetId: null,
            overrides,
            requestBannerRefresh: false,
            bannerAssetId: null,
            "coverAppearance",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ProfileAppearanceOutcome>> SetProfileBannerPresentationAsync(
        SetProfileBannerPresentationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNonEmpty(request.ProfileId, nameof(request));

        var resolution = BannerPresentationPolicy.Resolve(
            new BannerPresentationRequest(
                request.StartPointSeconds,
                request.DurationSeconds,
                request.FocusX,
                request.FocusY,
                request.Zoom,
                request.Loop));

        if (resolution.Diagnostics.Count > 0)
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileBannerPresentationInvalid,
                resolution.Diagnostics[0].Detail);
        }

        var stored = await ReadOverridesAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        var overrides = stored with
        {
            BannerStartPointSeconds = resolution.Presentation.StartPoint.TotalSeconds,
            BannerDurationSeconds = resolution.Presentation.Duration.TotalSeconds,
            BannerFocusX = resolution.Presentation.FocusX,
            BannerFocusY = resolution.Presentation.FocusY,
            BannerZoom = resolution.Presentation.Zoom,
            BannerLoop = resolution.Presentation.Loop,
        };

        return await UpdateAppearanceRecordAsync(
            request.ProfileId,
            request.ExpectedProfileRowVersion,
            layoutPresetId: null,
            overrides,
            requestBannerRefresh: stored.ResolvedBannerSourceKind == BannerVisualSourceKind.VideoClip,
            bannerAssetId: null,
            "bannerPresentation",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a Profile's committed presentation in one optimistic transaction: layout preset (when
    /// <see cref="ApplyProfilePresentationRequest.ApplyLayout"/>) and the complete canonical appearance
    /// payload (frame, Cover transform, Banner transform and playback, card override). This is the
    /// Customization Center's Profile-scope commit; nothing is written per pointer frame.
    /// </summary>
    public async Task<OperationResult<ProfileAppearanceOutcome>> ApplyPresentationAsync(
        ApplyProfilePresentationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _writeCoordinator);
        var write = await ApplyPresentationInTransactionAsync(request, transaction, cancellationToken).ConfigureAwait(false);
        if (!write.Result.IsSuccess)
        {
            return write.Result;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await CompletePresentationApplyAsync(write, request.ProfileId, cancellationToken).ConfigureAwait(false);
        return write.Result;
    }

    internal async Task<ProfilePresentationWriteResult> ApplyPresentationInTransactionAsync(
        ApplyProfilePresentationRequest request,
        CatalogTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureNonEmpty(request.ProfileId, nameof(request));
        var overrides = request.Overrides ?? ProfileAppearanceOverrides.Default;
        var presetId = string.IsNullOrWhiteSpace(request.LayoutPresetId) ? null : request.LayoutPresetId.Trim();
        var invalid = ValidatePresentation(request, presetId, overrides);
        if (invalid is not null)
        {
            return new(invalid, false, null);
        }

        var gate = await ValidateProfileAsync(
            transaction, request.ProfileId, request.ExpectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return new(gate, false, null);
        }

        var stored = await ReadOverridesInTransactionAsync(transaction, request.ProfileId, cancellationToken).ConfigureAwait(false);
        if (request.Sources is not null)
        {
            var applied = await ApplySourcesInTransactionAsync(
                transaction, request.ProfileId, request.Sources, overrides, cancellationToken).ConfigureAwait(false);
            if (applied.Rejection is not null)
            {
                return new(applied.Rejection, false, null);
            }

            overrides = applied.Overrides;
        }

        var now = DbTime.Format(_timeProvider.GetUtcNow());
        var newRowVersion = CatalogTransaction.NextRowVersion(request.ExpectedProfileRowVersion);
        await using (var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(
                profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, $layoutPresetId, $overridesJson, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                layout_preset_id = CASE WHEN $applyLayout = 1
                    THEN $layoutPresetId ELSE profile_appearance.layout_preset_id END,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """))
        {
            upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
            upsert.Parameters.AddWithValue("$layoutPresetId", (object?)presetId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$overridesJson", overrides.ToJson());
            upsert.Parameters.AddWithValue("$now", now);
            upsert.Parameters.AddWithValue("$applyLayout", request.ApplyLayout ? 1 : 0);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var bump = transaction.CreateCommand(
            """
            UPDATE profiles
            SET updated_at_ms = $now, row_version = $newRowVersion
            WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
            """))
        {
            bump.Parameters.AddWithValue("$now", now);
            bump.Parameters.AddWithValue("$newRowVersion", newRowVersion);
            bump.Parameters.AddWithValue("$profileId", DbGuid.Format(request.ProfileId));
            bump.Parameters.AddWithValue("$expectedRowVersion", request.ExpectedProfileRowVersion);
            if (await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new CatalogConcurrencyConflictException("The Profile changed while its presentation was being applied.");
            }
        }

        await AppendAppearanceActivityAsync(transaction, request.ProfileId, "presentation", now, cancellationToken)
            .ConfigureAwait(false);
        transaction.QueueInvalidation(new CatalogInvalidation(
            Guid.Empty,
            [request.ProfileId],
            CatalogInvalidationDomain.Appearance,
            newRowVersion));
        var refreshBanner = stored.BannerStartPointSeconds != overrides.BannerStartPointSeconds
            || stored.BannerDurationSeconds != overrides.BannerDurationSeconds
            || request.Sources?.BannerChanged == true;
        return new(
            OperationResult<ProfileAppearanceOutcome>.Success(
                new ProfileAppearanceOutcome(request.ProfileId, newRowVersion)),
            refreshBanner,
            request.Sources?.BannerAssetId);
    }

    internal Task CompletePresentationApplyAsync(
        ProfilePresentationWriteResult write,
        Guid profileId,
        CancellationToken cancellationToken) =>
        write.RefreshBanner
            ? RequestBannerPreviewRefreshAsync(
                profileId,
                write.BannerAssetId,
                "presentation",
                cancellationToken)
            : Task.CompletedTask;

    private static OperationResult<ProfileAppearanceOutcome>? ValidatePresentation(
        ApplyProfilePresentationRequest request,
        string? presetId,
        ProfileAppearanceOverrides overrides)
    {
        if (request.ApplyLayout && presetId is not null && !ProfileLayoutResolver.IsBuiltInPresetId(presetId))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileLayoutInvalid, "That Profile layout is not one of the available layouts.");
        if (overrides.GalleryCardVariantId is { } variant && !GalleryCardCatalog.TryGetVariant(variant, out _))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.GalleryPresentationInvalid, $"The gallery card variant '{variant}' is not recognized.");
        var cover = CoverFrameCatalog.Resolve(overrides.ToCoverAppearanceRequest(), reduceMotion: false);
        if (overrides.CoverFrameId is not null && cover.Diagnostics.Count > 0)
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileAppearanceInvalid, cover.Diagnostics[0].Detail);
        var banner = BannerPresentationPolicy.Resolve(overrides.ToBannerPresentationRequest());
        if (banner.Diagnostics.Count > 0)
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileBannerPresentationInvalid, banner.Diagnostics[0].Detail);
        if (!ProfileAppearanceRules.IsCoverCropValid(overrides.CropX)
            || !ProfileAppearanceRules.IsCoverCropValid(overrides.CropY)
            || !ProfileAppearanceRules.IsCoverZoomValid(overrides.Zoom)
            || !InRange(overrides.CoverOffsetX, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.CoverOffsetY, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.BannerOffsetX, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.BannerOffsetY, ProfileAppearanceOverrides.MaximumOffset)
            || !InRange(overrides.CoverRotation, ProfileAppearanceOverrides.MaximumRotation)
            || !InRange(overrides.BannerRotation, ProfileAppearanceOverrides.MaximumRotation)
            || !double.IsFinite(overrides.BannerPlaybackRate)
            || overrides.BannerPlaybackRate < ProfileAppearanceOverrides.MinimumPlaybackRate
            || overrides.BannerPlaybackRate > ProfileAppearanceOverrides.MaximumPlaybackRate
            || (overrides.CoverFit is { } coverFit && !ProfileAppearanceOverrides.FitModes.Contains(coverFit))
            || (overrides.BannerFit is { } bannerFit && !ProfileAppearanceOverrides.FitModes.Contains(bannerFit))
            || (overrides.BannerLoopMode is { } loop && !ProfileAppearanceOverrides.LoopModes.Contains(loop))
            || (overrides.BannerReducedMotion is { } reduced && !ProfileAppearanceOverrides.ReducedMotionPolicies.Contains(reduced)))
            return OperationResult<ProfileAppearanceOutcome>.Validation(OperationErrorCode.ProfileAppearanceInvalid, "The Cover or Banner presentation is outside the supported range.");
        return null;

        static bool InRange(double value, double limit) => double.IsFinite(value) && Math.Abs(value) <= limit;
    }

    public async Task<ProfileAppearanceOverrides> ReadOverridesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(profileId, nameof(profileId));

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT overrides_json FROM profile_appearance WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return stored is string json
            ? ProfileAppearanceOverrides.Parse(json)
            : ProfileAppearanceOverrides.Default;
    }

    private async Task<OperationResult<ProfileAppearanceOutcome>> UpdateAppearanceRecordAsync(
        Guid profileId,
        long expectedProfileRowVersion,
        string? layoutPresetId,
        ProfileAppearanceOverrides? overrides,
        bool requestBannerRefresh,
        Guid? bannerAssetId,
        string changeKind,
        CancellationToken cancellationToken,
        bool? applyLayout = null,
        ProfileMediaSourceChange? sources = null)
    {
        long newRowVersion;
        var applyLayoutFlag = applyLayout ?? overrides is null;

        await using (var lease = await _writeCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = CatalogTransaction.Begin(connection, _writeCoordinator))
        {
            var gate = await ValidateProfileAsync(
                transaction, profileId, expectedProfileRowVersion, cancellationToken).ConfigureAwait(false);
            if (gate is not null)
            {
                return gate;
            }

            // Source changes ride in this same transaction, so a Customization Apply commits the Cover/Banner
            // source together with its framing and playback, or not at all.
            if (sources is not null && overrides is not null)
            {
                var applied = await ApplySourcesInTransactionAsync(transaction, profileId, sources, overrides, cancellationToken).ConfigureAwait(false);
                if (applied.Rejection is not null)
                {
                    return applied.Rejection;
                }

                overrides = applied.Overrides;
                if (applied.RefreshBanner)
                {
                    requestBannerRefresh = true;
                    bannerAssetId = sources.BannerAssetId;
                }
            }

            var now = DbTime.Format(_timeProvider.GetUtcNow());
            newRowVersion = expectedProfileRowVersion + 1;

            await using (var upsert = transaction.CreateCommand(
                """
                INSERT INTO profile_appearance(
                    profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
                VALUES ($profileId, $schemaVersion, $layoutPresetId, $overridesJson, $now, 1)
                ON CONFLICT(profile_id) DO UPDATE SET
                    layout_preset_id = CASE WHEN $applyLayout = 1
                        THEN $layoutPresetId ELSE profile_appearance.layout_preset_id END,
                    overrides_json = CASE WHEN $applyOverrides = 1
                        THEN $overridesJson ELSE profile_appearance.overrides_json END,
                    updated_at_ms = excluded.updated_at_ms,
                    row_version = profile_appearance.row_version + 1;
                """))
            {
                upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
                upsert.Parameters.AddWithValue("$layoutPresetId", (object?)layoutPresetId ?? DBNull.Value);
                upsert.Parameters.AddWithValue(
                    "$overridesJson",
                    (overrides ?? ProfileAppearanceOverrides.Default).ToJson());
                upsert.Parameters.AddWithValue("$now", now);
                upsert.Parameters.AddWithValue("$applyLayout", applyLayoutFlag ? 1 : 0);
                upsert.Parameters.AddWithValue("$applyOverrides", overrides is null ? 0 : 1);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var bumpProfile = transaction.CreateCommand(
                """
                UPDATE profiles
                SET updated_at_ms = $now,
                    row_version = $newRowVersion
                WHERE profile_id = $profileId AND row_version = $expectedRowVersion;
                """))
            {
                bumpProfile.Parameters.AddWithValue("$now", now);
                bumpProfile.Parameters.AddWithValue("$newRowVersion", newRowVersion);
                bumpProfile.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                bumpProfile.Parameters.AddWithValue("$expectedRowVersion", expectedProfileRowVersion);
                await bumpProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendAppearanceActivityAsync(transaction, profileId, changeKind, DbTime.Format(_timeProvider.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
            transaction.QueueInvalidation(new CatalogInvalidation(
                Guid.Empty,
                [profileId],
                CatalogInvalidationDomain.Appearance,
                newRowVersion));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (requestBannerRefresh)
        {
            await RequestBannerPreviewRefreshAsync(profileId, bannerAssetId, changeKind, cancellationToken)
                .ConfigureAwait(false);
        }

        return OperationResult<ProfileAppearanceOutcome>.Success(
            new ProfileAppearanceOutcome(profileId, newRowVersion));
    }

    private async Task<(OperationResult<ProfileAppearanceOutcome>? Rejection, ProfileAppearanceOverrides Overrides, bool RefreshBanner)> ApplySourcesInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        ProfileMediaSourceChange sources,
        ProfileAppearanceOverrides overrides,
        CancellationToken cancellationToken)
    {
        var result = overrides;
        var refreshBanner = false;
        if (sources.CoverChanged)
        {
            var kind = CoverVisualSourceKind.Image;
            long? timestamp = null;
            if (sources.CoverAssetId is { } coverAssetId)
            {
                var asset = await ReadAppearanceAssetAsync(transaction, coverAssetId, cancellationToken).ConfigureAwait(false);
                var rejection = await ValidateAppearanceAssetAsync(
                    transaction,
                    profileId,
                    coverAssetId,
                    ProfileAppearanceRules.IsCoverMediaTypeEligible,
                    "A Cover must be an image or a video frame that is already linked to this Profile.",
                    cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    return (rejection, overrides, false);
                }

                var mediaType = asset?.MediaType ?? MediaType.Image;
                kind = ProfileAppearanceRules.ResolveCoverSourceKind(mediaType);
                timestamp = kind == CoverVisualSourceKind.VideoFrame ? overrides.CoverVideoTimestampMilliseconds : null;
                if (!ProfileAppearanceRules.IsCoverVisualSourceValid(mediaType, kind, timestamp))
                {
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        kind == CoverVisualSourceKind.VideoFrame
                            ? "A video Cover must name the exact frame it uses, in milliseconds."
                            : "An image Cover carries no frame timestamp."), overrides, false);
                }
            }

            await using (var update = transaction.CreateCommand("UPDATE profiles SET cover_asset_id = $assetId WHERE profile_id = $profileId;"))
            {
                update.Parameters.AddWithValue("$assetId", sources.CoverAssetId is { } id ? DbGuid.Format(id) : DBNull.Value);
                update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            result = result with
            {
                CoverSourceKind = sources.CoverAssetId is null ? null : kind.ToString(),
                CoverVideoTimestampMilliseconds = timestamp,
            };
        }

        if (sources.BannerChanged)
        {
            BannerVisualSourceKind? sourceKind = null;
            long? frameTimestamp = null;
            if (sources.BannerAssetId is { } bannerAssetId)
            {
                var asset = await ReadAppearanceAssetAsync(transaction, bannerAssetId, cancellationToken).ConfigureAwait(false);
                var rejection = await ValidateAppearanceAssetAsync(
                    transaction,
                    profileId,
                    bannerAssetId,
                    ProfileAppearanceRules.IsBannerMediaTypeEligible,
                    "A banner must be an image, video frame, or video clip that already belongs to this profile.",
                    cancellationToken).ConfigureAwait(false);
                if (rejection is not null)
                {
                    return (rejection, overrides, false);
                }

                var mediaType = asset?.MediaType ?? MediaType.Image;
                sourceKind = sources.BannerSourceKind ?? ProfileAppearanceRules.ResolveBannerSourceKind(mediaType);
                frameTimestamp = sourceKind == BannerVisualSourceKind.VideoFrame
                    ? sources.BannerVideoFrameTimestampMilliseconds
                    : null;
                var start = sourceKind == BannerVisualSourceKind.VideoClip ? (double?)overrides.BannerStartPointSeconds : null;
                var duration = sourceKind == BannerVisualSourceKind.VideoClip ? (double?)overrides.BannerDurationSeconds : null;
                if (!ProfileAppearanceRules.IsBannerVisualSourceValid(mediaType, sourceKind.Value, frameTimestamp, start, duration))
                {
                    return (OperationResult<ProfileAppearanceOutcome>.Validation(
                        OperationErrorCode.ProfileAppearanceInvalid,
                        "The banner source kind and timing information do not match its media."), overrides, false);
                }
                refreshBanner = sourceKind == BannerVisualSourceKind.VideoClip;
            }

            await using var update = transaction.CreateCommand("UPDATE profiles SET banner_asset_id = $assetId WHERE profile_id = $profileId;");
            update.Parameters.AddWithValue("$assetId", sources.BannerAssetId is { } id ? DbGuid.Format(id) : DBNull.Value);
            update.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            result = result with
            {
                BannerSourceKind = sourceKind?.ToString(),
                BannerVideoFrameTimestampMilliseconds = frameTimestamp,
                BannerStartPointSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                    ? overrides.BannerStartPointSeconds
                    : ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                BannerDurationSeconds = sourceKind == BannerVisualSourceKind.VideoClip
                    ? overrides.BannerDurationSeconds
                    : ProfileAppearanceOverrides.Default.BannerDurationSeconds,
            };
        }

        return (null, result, refreshBanner);
    }

    private static async Task<ProfileAppearanceOverrides> ReadOverridesInTransactionAsync(
        CatalogTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            "SELECT overrides_json FROM profile_appearance WHERE profile_id = $profileId;");
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return stored is string json
            ? ProfileAppearanceOverrides.Parse(json)
            : ProfileAppearanceOverrides.Default;
    }

    private async Task WriteOverridesAsync(
        CatalogTransaction transaction,
        Guid profileId,
        ProfileAppearanceOverrides overrides,
        CancellationToken cancellationToken)
    {
        await using var upsert = transaction.CreateCommand(
            """
            INSERT INTO profile_appearance(
                profile_id, schema_version, layout_preset_id, overrides_json, updated_at_ms, row_version)
            VALUES ($profileId, $schemaVersion, NULL, $overridesJson, $now, 1)
            ON CONFLICT(profile_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                overrides_json = excluded.overrides_json,
                updated_at_ms = excluded.updated_at_ms,
                row_version = profile_appearance.row_version + 1;
            """);
        upsert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        upsert.Parameters.AddWithValue("$schemaVersion", ProfileAppearanceOverrides.SchemaVersion);
        upsert.Parameters.AddWithValue("$overridesJson", overrides.ToJson());
        upsert.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RequestBannerPreviewRefreshAsync(
        Guid profileId,
        Guid? bannerAssetId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_bannerPreviewRefresh is null)
        {
            return;
        }

        try
        {
            await _bannerPreviewRefresh(
                new BannerPreviewRefreshRequest(profileId, bannerAssetId, reason),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

        }
    }

    private static async Task<OperationResult<ProfileAppearanceOutcome>?> ValidateProfileAsync(
        CatalogTransaction transaction,
        Guid profileId,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT kind, trashed_at_ms, row_version
            FROM profiles
            WHERE profile_id = $profileId;
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ProfileAppearanceOutcome>.NotFound(
                OperationErrorCode.ProfileNotFound,
                "That Profile no longer exists.");
        }

        var kind = DbEnum.ParseProfileKind(reader.GetString(0));
        var isTrashed = !reader.IsDBNull(1);
        var currentRowVersion = reader.GetInt64(2);

        if (kind != ProfileKind.Normal)
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.ProfileNotNormal,
                "An Unknown Profile has no Cover, Banner, or appearance of its own.");
        }

        if (isTrashed)
        {
            return OperationResult<ProfileAppearanceOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile was moved to Trash and cannot be restyled.");
        }

        if (currentRowVersion != expectedRowVersion)
        {
            return OperationResult<ProfileAppearanceOutcome>.Conflict(
                OperationErrorCode.ProfileConflict,
                "That Profile changed somewhere else. Reload it and apply your change again.");
        }

        return null;
    }

    private static async Task<OperationResult<ProfileAppearanceOutcome>?> ValidateAppearanceAssetAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        Func<MediaType, bool> isMediaTypeEligible,
        string ineligibleMessage,
        CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset identifier cannot be empty.", nameof(assetId));
        }

        var asset = await ReadAppearanceAssetAsync(transaction, assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return OperationResult<ProfileAppearanceOutcome>.NotFound(
                OperationErrorCode.AssetNotFound,
                "That media item no longer exists.");
        }

        var hasRelation = await HasAnyRelationAsync(transaction, profileId, assetId, cancellationToken)
            .ConfigureAwait(false);

        if (!ProfileAppearanceRules.ValidateEligibility(
                ProfileKind.Normal,
                isProfileActive: true,
                asset.State,
                isAssetActive: !asset.IsTrashed,
                hasRelation))
        {
            return OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.AppearanceAssetInvalid,
                "That media item is not an active item linked to this Profile.");
        }

        return isMediaTypeEligible(asset.MediaType)
            ? null
            : OperationResult<ProfileAppearanceOutcome>.Validation(
                OperationErrorCode.AppearanceAssetInvalid,
                ineligibleMessage);
    }

    private static async Task<AppearanceAsset?> ReadAppearanceAssetAsync(
        CatalogTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT state, media_type, trashed_at_ms
            FROM assets
            WHERE asset_id = $assetId;
            """);
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AppearanceAsset(
            DbEnum.ParseAssetState(reader.GetString(0)),
            DbEnum.ParseMediaType(reader.GetString(1)),
            !reader.IsDBNull(2));
    }

    private static async Task<bool> HasAnyRelationAsync(
        CatalogTransaction transaction,
        Guid profileId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM profile_assets
                WHERE profile_id = $profileId AND asset_id = $assetId
            );
            """);
        command.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var found = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(found, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task AppendAppearanceActivityAsync(
        CatalogTransaction transaction,
        Guid profileId,
        string changeKind,
        long occurredAtMs,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(
            """
            INSERT INTO activity_log(
                activity_id, event_type, profile_id, payload_json, occurred_at_ms)
            VALUES ($activityId, $eventType, $profileId, $payloadJson, $occurredAtMs);
            """);
        insert.Parameters.AddWithValue("$activityId", DbGuid.Format(Guid.NewGuid()));
        insert.Parameters.AddWithValue("$eventType", ActivityEventType.ProfileAppearanceChanged);
        insert.Parameters.AddWithValue("$profileId", DbGuid.Format(profileId));
        insert.Parameters.AddWithValue("$payloadJson", $"{{\"change\":\"{changeKind}\"}}");
        insert.Parameters.AddWithValue("$occurredAtMs", occurredAtMs);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", parameterName);
        }
    }

    private sealed record AppearanceAsset(AssetState State, MediaType MediaType, bool IsTrashed);
}

public sealed record SetCoverAssetRequest(
    Guid ProfileId,
    Guid? CoverAssetId,
    long ExpectedProfileRowVersion,
    long? CoverVideoTimestampMilliseconds = null);

public sealed record SetBannerAssetRequest(
    Guid ProfileId,
    Guid? BannerAssetId,
    long ExpectedProfileRowVersion,
    BannerVisualSourceKind? SourceKind = null,
    long? VideoFrameTimestampMilliseconds = null,
    double? StartPointSeconds = null,
    double? DurationSeconds = null);

public sealed record SetProfileLayoutOverrideRequest(
    Guid ProfileId,
    string? LayoutPresetId,
    long ExpectedProfileRowVersion);

public sealed record SetProfileGalleryCardOverrideRequest(
    Guid ProfileId,
    string? CardVariantId,
    long ExpectedProfileRowVersion);

public sealed record SetProfileCoverAppearanceRequest(
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    string? CoverShape = null,
    string? CoverFrameId = null,
    double? CoverFrameScale = null,
    string? CoverFrameTint = null,
    double? CoverFrameIntensity = null,
    string? CoverFrameAnimation = null,
    bool CoverShadow = true,
    double CropX = ProfileAppearanceRules.DefaultCoverCrop,
    double CropY = ProfileAppearanceRules.DefaultCoverCrop,
    double Zoom = ProfileAppearanceRules.MinimumCoverZoom);

public sealed record SetProfileBannerPresentationRequest(
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    double? StartPointSeconds = null,
    double? DurationSeconds = null,
    double? FocusX = null,
    double? FocusY = null,
    double? Zoom = null,
    bool Loop = true);

public sealed record ProfileAppearanceOutcome(Guid ProfileId, long RowVersion);

internal sealed record ProfilePresentationWriteResult(
    OperationResult<ProfileAppearanceOutcome> Result,
    bool RefreshBanner,
    Guid? BannerAssetId);

/// <summary>A Profile's committed presentation, applied as one transaction.</summary>
public sealed record ApplyProfilePresentationRequest(
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    bool ApplyLayout,
    string? LayoutPresetId,
    ProfileAppearanceOverrides Overrides,
    ProfileMediaSourceChange? Sources = null);

/// <summary>Cover/Banner source edits committed in the same transaction as the Profile presentation.</summary>
public sealed record ProfileMediaSourceChange(
    bool CoverChanged,
    Guid? CoverAssetId,
    bool BannerChanged,
    Guid? BannerAssetId,
    BannerVisualSourceKind? BannerSourceKind = null,
    long? BannerVideoFrameTimestampMilliseconds = null);

public sealed record ProfileAppearanceOverrides(
    string? CoverShape,
    string? CoverFrameId,
    double? CoverFrameScale,
    string? CoverFrameTint,
    double? CoverFrameIntensity,
    string? CoverFrameAnimation,
    bool CoverShadow,
    double CropX,
    double CropY,
    double Zoom,
    double BannerStartPointSeconds,
    double BannerDurationSeconds,
    double BannerFocusX,
    double BannerFocusY,
    double BannerZoom,
    bool BannerLoop,
    string? GalleryCardVariantId = null,
    string? CoverSourceKind = null,
    long? CoverVideoTimestampMilliseconds = null,
    string? CoverFit = null,
    double CoverOffsetX = 0,
    double CoverOffsetY = 0,
    double CoverRotation = 0,
    string? BannerFit = null,
    double BannerOffsetX = 0,
    double BannerOffsetY = 0,
    double BannerRotation = 0,
    bool BannerMute = true,
    double BannerPlaybackRate = 1,
    string? BannerLoopMode = null,
    string? BannerReducedMotion = null,
    string? BannerSourceKind = null,
    long? BannerVideoFrameTimestampMilliseconds = null)
{

    /// <summary>
    /// v5 records whether a Banner uses an image, a video frame, or a video clip, including the exact
    /// timestamp for a video frame. Older payloads retain the legacy media-based behavior.
    /// </summary>
    public const int SchemaVersion = 5;

    public const double MaximumOffset = 1;

    public const double MaximumRotation = 180;

    public const double MinimumPlaybackRate = 0.25;

    public const double MaximumPlaybackRate = 2;

    public static IReadOnlyList<string> FitModes { get; } = ["fill", "fit"];

    public static IReadOnlyList<string> LoopModes { get; } = ["loop", "once"];

    public static IReadOnlyList<string> ReducedMotionPolicies { get; } = ["poster", "still-frame", "play-once"];

    public static ProfileAppearanceOverrides Default { get; } = new(
        CoverShape: null,
        CoverFrameId: null,
        CoverFrameScale: null,
        CoverFrameTint: null,
        CoverFrameIntensity: null,
        CoverFrameAnimation: null,
        CoverShadow: true,
        CropX: ProfileAppearanceRules.DefaultCoverCrop,
        CropY: ProfileAppearanceRules.DefaultCoverCrop,
        Zoom: ProfileAppearanceRules.MinimumCoverZoom,
        BannerStartPointSeconds: 0,
        BannerDurationSeconds: BannerPresentationPolicy.DefaultDuration.TotalSeconds,
        BannerFocusX: BannerPresentationPolicy.DefaultFocus,
        BannerFocusY: BannerPresentationPolicy.DefaultFocus,
        BannerZoom: BannerPresentationPolicy.MinimumZoom,
        BannerLoop: true,
        GalleryCardVariantId: null,
        CoverSourceKind: null,
        CoverVideoTimestampMilliseconds: null);

    public CoverVisualSourceKind ResolvedCoverSourceKind =>
        Enum.TryParse<CoverVisualSourceKind>(CoverSourceKind, ignoreCase: true, out var parsed)
            ? parsed
            : CoverVisualSourceKind.Image;

    public bool IsVideoFrameCover =>
        ResolvedCoverSourceKind == CoverVisualSourceKind.VideoFrame
        && CoverVideoTimestampMilliseconds is >= 0;

    public long? CoverStillTimestampMilliseconds =>
        IsVideoFrameCover ? CoverVideoTimestampMilliseconds : null;

    public BannerVisualSourceKind ResolvedBannerSourceKind =>
        ProfileAppearanceRules.ResolveBannerSourceKind(this, bannerMediaType: null);

    public long? BannerStillTimestampMilliseconds =>
        ResolvedBannerSourceKind == BannerVisualSourceKind.VideoFrame
            ? BannerVideoFrameTimestampMilliseconds
            : null;

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            WriteOptionalString(writer, "coverShape", CoverShape);
            WriteOptionalString(writer, "coverFrameId", CoverFrameId);
            WriteOptionalNumber(writer, "coverFrameScale", CoverFrameScale);
            WriteOptionalString(writer, "coverFrameTint", CoverFrameTint);
            WriteOptionalNumber(writer, "coverFrameIntensity", CoverFrameIntensity);
            WriteOptionalString(writer, "coverFrameAnimation", CoverFrameAnimation);
            writer.WriteBoolean("coverShadow", CoverShadow);
            writer.WriteNumber("cropX", CropX);
            writer.WriteNumber("cropY", CropY);
            writer.WriteNumber("zoom", Zoom);
            writer.WriteNumber("bannerStartPointSeconds", BannerStartPointSeconds);
            writer.WriteNumber("bannerDurationSeconds", BannerDurationSeconds);
            writer.WriteNumber("bannerFocusX", BannerFocusX);
            writer.WriteNumber("bannerFocusY", BannerFocusY);
            writer.WriteNumber("bannerZoom", BannerZoom);
            writer.WriteBoolean("bannerLoop", BannerLoop);
            WriteOptionalString(writer, "galleryCardVariantId", GalleryCardVariantId);
            WriteOptionalString(writer, "coverSourceKind", CoverSourceKind);
            if (CoverVideoTimestampMilliseconds is { } coverTimestamp)
            {
                writer.WriteNumber("coverVideoTimestampMs", coverTimestamp);
            }
            else
            {
                writer.WriteNull("coverVideoTimestampMs");
            }

            WriteOptionalString(writer, "coverFit", CoverFit);
            writer.WriteNumber("coverOffsetX", CoverOffsetX);
            writer.WriteNumber("coverOffsetY", CoverOffsetY);
            writer.WriteNumber("coverRotation", CoverRotation);
            WriteOptionalString(writer, "bannerFit", BannerFit);
            writer.WriteNumber("bannerOffsetX", BannerOffsetX);
            writer.WriteNumber("bannerOffsetY", BannerOffsetY);
            writer.WriteNumber("bannerRotation", BannerRotation);
            writer.WriteBoolean("bannerMute", BannerMute);
            writer.WriteNumber("bannerPlaybackRate", BannerPlaybackRate);
            WriteOptionalString(writer, "bannerLoopMode", BannerLoopMode);
            WriteOptionalString(writer, "bannerReducedMotion", BannerReducedMotion);
            WriteOptionalString(writer, "bannerSourceKind", BannerSourceKind);
            if (BannerVideoFrameTimestampMilliseconds is { } bannerTimestamp)
            {
                writer.WriteNumber("bannerVideoFrameTimestampMs", bannerTimestamp);
            }
            else
            {
                writer.WriteNull("bannerVideoFrameTimestampMs");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static ProfileAppearanceOverrides Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            var root = document.RootElement;
            var version = (int)ReadNumber(root, "schemaVersion", SchemaVersion);
            if (version is < 1 or > SchemaVersion)
            {
                return Default;
            }

            return new ProfileAppearanceOverrides(
                ReadString(root, "coverShape"),
                ReadString(root, "coverFrameId"),
                ReadOptionalNumber(root, "coverFrameScale"),
                ReadString(root, "coverFrameTint"),
                ReadOptionalNumber(root, "coverFrameIntensity"),
                ReadString(root, "coverFrameAnimation"),
                ReadBoolean(root, "coverShadow", Default.CoverShadow),
                ReadNumber(root, "cropX", Default.CropX),
                ReadNumber(root, "cropY", Default.CropY),
                ReadNumber(root, "zoom", Default.Zoom),
                ReadNumber(root, "bannerStartPointSeconds", Default.BannerStartPointSeconds),
                ReadNumber(root, "bannerDurationSeconds", Default.BannerDurationSeconds),
                ReadNumber(root, "bannerFocusX", Default.BannerFocusX),
                ReadNumber(root, "bannerFocusY", Default.BannerFocusY),
                ReadNumber(root, "bannerZoom", Default.BannerZoom),
                ReadBoolean(root, "bannerLoop", Default.BannerLoop),
                version >= 2 ? ReadString(root, "galleryCardVariantId") : null,
                version >= 3 ? ReadString(root, "coverSourceKind") : null,
                version >= 3 ? ReadOptionalLong(root, "coverVideoTimestampMs") : null,
                version >= 4 ? ReadString(root, "coverFit") : null,
                version >= 4 ? ReadNumber(root, "coverOffsetX", 0) : 0,
                version >= 4 ? ReadNumber(root, "coverOffsetY", 0) : 0,
                version >= 4 ? ReadNumber(root, "coverRotation", 0) : 0,
                version >= 4 ? ReadString(root, "bannerFit") : null,
                version >= 4 ? ReadNumber(root, "bannerOffsetX", 0) : 0,
                version >= 4 ? ReadNumber(root, "bannerOffsetY", 0) : 0,
                version >= 4 ? ReadNumber(root, "bannerRotation", 0) : 0,
                version < 4 || ReadBoolean(root, "bannerMute", true),
                version >= 4 ? ReadNumber(root, "bannerPlaybackRate", 1) : 1,
                version >= 4 ? ReadString(root, "bannerLoopMode") : null,
                version >= 4 ? ReadString(root, "bannerReducedMotion") : null,
                version >= 5 ? ReadString(root, "bannerSourceKind") : null,
                version >= 5 ? ReadOptionalLong(root, "bannerVideoFrameTimestampMs") : null);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public CoverAppearanceRequest ToCoverAppearanceRequest() => new(
        CoverShape,
        CoverFrameId,
        CoverFrameScale,
        CoverFrameTint,
        CoverFrameIntensity,
        CoverFrameAnimation,
        CoverShadow);

    public BannerPresentationRequest ToBannerPresentationRequest() => new(
        BannerStartPointSeconds,
        BannerDurationSeconds,
        BannerFocusX,
        BannerFocusY,
        BannerZoom,
        BannerLoop);

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadOptionalLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;

    private static double? ReadOptionalNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : null;

    private static double ReadNumber(JsonElement root, string name, double fallback) =>
        ReadOptionalNumber(root, name) ?? fallback;

    private static bool ReadBoolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
}

public sealed record BannerPreviewRefreshRequest(Guid ProfileId, Guid? BannerAssetId, string Reason);

public delegate Task BannerPreviewRefreshEnqueue(
    BannerPreviewRefreshRequest request,
    CancellationToken cancellationToken);
