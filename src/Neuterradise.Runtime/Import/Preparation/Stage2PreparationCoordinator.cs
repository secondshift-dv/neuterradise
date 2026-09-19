using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Orchestrates Stage 2 preparation: per-asset capability fork/join after Stage 1 is authoritative.
///
/// Effective order:
///   Stage 1 DomainAuthorityCommitted
///   → seed capabilities per asset (deterministic applicability)
///   → schedule independent Stage 2 jobs reading canonical Vault media
///   → each job updates its capability to READY on success
///   → readiness join: all applicable capabilities terminal → ReadyForVerification
///
/// One slow asset does NOT serialize unrelated assets.
/// One slow capability does NOT serialize unrelated capabilities.
/// </summary>
public sealed class Stage2PreparationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly CapabilityReads _capabilityReads;
    private readonly CapabilityWrites _capabilityWrites;
    private readonly ImportWrites _importWrites;
    private readonly ImportReads _importReads;
    private readonly JobWrites _jobWrites;
    private readonly VaultPaths _vaultPaths;
    private readonly SettingsOperations _settings;
    private readonly TimeProvider _timeProvider;

    public Stage2PreparationCoordinator(
        CatalogDb catalog,
        VaultPaths vaultPaths,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(vaultPaths);
        _catalog = catalog;
        _vaultPaths = vaultPaths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capabilityReads = new CapabilityReads(catalog);
        _capabilityWrites = new CapabilityWrites(catalog, timeProvider);
        _importWrites = new ImportWrites(catalog, timeProvider);
        _importReads = catalog.ImportReads;
        _jobWrites = new JobWrites(catalog, timeProvider);
        _settings = new SettingsOperations(catalog, timeProvider);
    }

    /// <summary>
    /// Entry point: seed capabilities and schedule Stage 2 jobs for all eligible assets.
    /// Called after Stage 1 DomainAuthorityCommitted. Idempotent: existing terminal capabilities
    /// are not disturbed, and jobs are created idempotently by deterministic JobId.
    /// </summary>
    public async Task<Stage2ScheduleResult> ScheduleStage2Async(
        Guid unitId,
        bool includeFaceAnalysis = true,
        CancellationToken cancellationToken = default)
    {
        // AutoAnalyzeAfterImport preference is authoritative: OFF means no face-analysis
        // scheduling regardless of the default parameter value.
        var importPrefs = await _settings.GetImportPreferencesAsync(cancellationToken).ConfigureAwait(false);
        includeFaceAnalysis = importPrefs.AutoAnalyzeAfterImport;

        var unit = await _importReads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return new Stage2ScheduleResult(unitId, 0, 0, false);
        }

        // Mark Stage 2 started.
        await MarkStage2StartedAsync(unitId, cancellationToken).ConfigureAwait(false);

        var items = await _importReads.GetUnitItemsUnboundedAsync(unitId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        int assetsScheduled = 0;
        int jobsScheduled = 0;
        var processedAssetIds = new HashSet<Guid>();

        foreach (var item in items)
        {
            if (item.Disposition is ItemDisposition.Skipped or ItemDisposition.Invalid)
            {
                continue;
            }

            // Stage 1 duplicate reuse retires the transient candidate and records the active
            // canonical asset in reused_asset_id. Stage 2 must operate on that effective asset,
            // not the retired candidate identity.
            var effectiveAssetId = item.Disposition == ItemDisposition.Reused
                ? item.ReusedAssetId
                : item.CandidateAssetId;
            if (effectiveAssetId is not { } assetId || !processedAssetIds.Add(assetId))
            {
                continue;
            }

            await _jobWrites.RegisterImportAssetInterestAsync(unitId, assetId, cancellationToken)
                .ConfigureAwait(false);

            // Stage 2 begins only after Stage 1 domain authority. Resolve media type from the
            // effective ACTIVE asset so reused items do not inherit retired candidate authority.
            var mediaType = await ReadActiveAssetMediaTypeAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (mediaType is null)
            {
                continue;
            }

            // Seed capabilities: deterministic per media type. All applicable capabilities get a row;
            // required ones gate readiness, optional ones are informational.
            var allCapabilities = CapabilityApplicability.GetAll(mediaType.Value);
            foreach (var (cap, _) in allCapabilities)
            {
                var initialState = cap == AssetCapability.CanonicalMedia
                    ? AssetCapabilityState.Ready
                    : AssetCapabilityState.Queued;
                await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, cap, initialState, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // FaceEmbedding is seeded as NOT_APPLICABLE initially; FaceDetection will promote it
            // to QUEUED if it finds face candidates.
            if (CapabilityApplicability.IsFaceEmbeddingApplicable(mediaType.Value))
            {
                await _capabilityWrites.MarkNotApplicableAsync(
                    assetId, AssetCapability.FaceEmbedding, cancellationToken).ConfigureAwait(false);
            }

            // When face analysis is excluded by preference, mark face-related capabilities
            // as NOT_APPLICABLE so they are terminal and cannot block readiness.
            if (!includeFaceAnalysis && mediaType.Value is MediaType.Image or MediaType.Video)
            {
                await _capabilityWrites.MarkNotApplicableAsync(
                    assetId, AssetCapability.FaceDetection, cancellationToken).ConfigureAwait(false);
                await _capabilityWrites.MarkNotApplicableAsync(
                    assetId, AssetCapability.SimilarityRelated, cancellationToken).ConfigureAwait(false);
            }

            // Read canonical vault path for this effective asset.
            var vaultPath = await ReadCanonicalVaultPathAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (vaultPath is null)
            {
                // Managed-path scheduling failure: the asset has no canonical vault path, so no
                // Stage 2 jobs can be created. Mark all non-terminal capabilities as FAILED so the
                // asset surfaces as needing attention rather than blocking readiness silently.
                // The C012A readiness denominator still includes this asset (capability rows exist).
                foreach (var (cap, _) in allCapabilities)
                {
                    await _capabilityWrites.UpsertCapabilityAsync(
                        assetId, cap, AssetCapabilityState.Failed,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            var fingerprint = await ReadAssetFingerprintAsync(assetId, cancellationToken).ConfigureAwait(false);

            // Schedule independent capability jobs. All read from canonical vault media.
            var assetJobs = await ScheduleAssetCapabilitiesAsync(
                assetId, item.ItemId, unitId, mediaType.Value, vaultPath, fingerprint,
                includeFaceAnalysis, cancellationToken).ConfigureAwait(false);

            jobsScheduled += assetJobs;
            assetsScheduled++;
        }

        // Evaluate readiness after scheduling.
        var readiness = await EvaluateReadinessAsync(unitId, cancellationToken).ConfigureAwait(false);

        return new Stage2ScheduleResult(unitId, assetsScheduled, jobsScheduled, readiness.AllRequiredTerminal);
    }

    /// <summary>
    /// Re-evaluate Stage 2 readiness. Called by progress observers and after job completion.
    /// Returns whether all assets have all applicable capabilities in terminal state.
    /// </summary>
    public async Task<Stage2Readiness> EvaluateReadinessAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var summary = await _capabilityReads.GetUnitCapabilitySummaryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        var failedAssetIds = await _capabilityReads.GetUnitFailedAssetIdsAsync(unitId, cancellationToken)
            .ConfigureAwait(false);

        return new Stage2Readiness(
            summary.AllRequiredTerminal,
            summary.AssetsAllCapabilitiesTerminal,
            summary.TotalAssets,
            failedAssetIds,
            HasFailures: failedAssetIds.Count > 0);
    }

    /// <summary>
    /// Applies the Stage 2 readiness join to the Import Unit. Required capability failure is a
    /// durable terminal failure, not an endless Preparing state; otherwise the unit advances to
    /// ReadyForVerification only when the complete required capability set is ready/terminal.
    /// </summary>
    public async Task<bool> TryTransitionToReadyAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var mutationLease = await _catalog.ImportUnitMutations
            .EnterAsync(unitId, cancellationToken).ConfigureAwait(false);

        var readiness = await EvaluateReadinessAsync(unitId, cancellationToken).ConfigureAwait(false);
        var unit = await _importReads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null || unit.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal
            || unit.State.IsUnitCommitted())
        {
            return false;
        }

        if (readiness.HasFailures)
        {
            await _importWrites
                .UpdateUnitStateAsync(
                    unitId,
                    ImportUnitState.FailedTerminal,
                    expectedState: unit.State,
                    expectedRowVersion: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (!readiness.AllRequiredTerminal)
        {
            return false;
        }

        if (unit.State != ImportUnitState.ReadyForVerification)
        {
            await _importWrites
                .UpdateUnitStateAsync(
                    unitId,
                    ImportUnitState.ReadyForVerification,
                    expectedState: unit.State,
                    expectedRowVersion: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private async Task<int> ScheduleAssetCapabilitiesAsync(
        Guid assetId,
        Guid importItemId,
        Guid unitId,
        MediaType mediaType,
        string vaultPath,
        string? fingerprint,
        bool includeFaceAnalysis,
        CancellationToken cancellationToken)
    {
        var jobsCreated = 0;

        // All Stage 2 jobs have the same prerequisite: the canonical pre-Stage-1 HashAsset job.
        // HashAsset deliberately keeps its historical deterministic identity because Stage 2 does
        // not create another Hash job; it references the already-authoritative prerequisite.
        var hashJobId = DeriveJobId(assetId, "HashAsset");

        // Metadata
        var metadataJobId = DeriveJobId(assetId, "ExtractMetadata");
        var metadataLane = mediaType == MediaType.Image ? JobLane.Cpu : JobLane.Media;
        var metadataCheckpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceKind = "vault",
            vaultPath,
            importItemId = importItemId.ToString("D"),
            mediaType = mediaType.ToString(),
        });
        var metadataJob = new JobDefinition(
            metadataJobId, "ExtractMetadata", metadataLane, JobState.Pending,
            JobPriorityPolicy.PriorityBackground,
            "Asset", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: metadataCheckpoint);

        // Preview / Thumbnail
        var previewLane = mediaType == MediaType.Image ? JobLane.Cpu : JobLane.Media;
        var previewKind = mediaType switch
        {
            MediaType.Image => "GenerateThumbnail",
            MediaType.Video => "GenerateVideoPreview",
            MediaType.Model => "GenerateModelPreview",
            _ => "GenerateThumbnail",
        };
        var previewJobId = DeriveJobId(assetId, previewKind);
        var previewCheckpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceKind = "vault",
            vaultPath,
            importItemId = importItemId.ToString("D"),
            mediaType = mediaType.ToString(),
        });
        var previewJob = new JobDefinition(
            previewJobId, previewKind, previewLane, JobState.Pending,
            JobPriorityPolicy.PriorityVisible,
            "Asset", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: previewCheckpoint);

        // PresentationStill (uses same preview generator but tracks a separate capability)
        // For images the thumbnail IS the presentation still; for video a representative frame is needed.
        // We reuse the preview job for the presentation still capability on image; for video it is
        // the same GenerateVideoPreview job but we track it under a separate capability.
        // For simplicity, the preview job covers both Thumbnail and PresentationStill capabilities.
        // The job handler marks both capabilities READY when it succeeds.

        // Face detection
        JobDefinition? faceJob = null;
        if (includeFaceAnalysis && mediaType is MediaType.Image or MediaType.Video)
        {
            var faceJobId = DeriveJobId(assetId, "FaceAnalysis");
            var faceCheckpoint = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sourceKind = "vault",
                vaultPath,
                importItemId = importItemId.ToString("D"),
                mediaType = mediaType.ToString(),
            });
            faceJob = new JobDefinition(
                faceJobId, "FaceAnalysis", JobLane.Face, JobState.Pending,
                JobPriorityPolicy.PriorityBackground,
                "Asset", assetId, JobRetryPolicy.DefaultMaxAttempts, CheckpointJson: faceCheckpoint);
        }

        // Create jobs and dependencies.
        var allJobs = new List<JobDefinition> { metadataJob, previewJob };
        if (faceJob is not null)
        {
            allJobs.Add(faceJob);
        }

        foreach (var job in allJobs)
        {
            await _jobWrites.CreateJobAsync(job, cancellationToken).ConfigureAwait(false);
            jobsCreated++;

            // All Stage 2 jobs depend on HashAsset.
            await _jobWrites.AddDependencyAsync(job.JobId, hashJobId, cancellationToken)
                .ConfigureAwait(false);

            // Update capability rows with the job ID. One job may satisfy multiple capabilities.
            var caps = MapJobKindToCapabilities(job.Kind, mediaType);
            foreach (var cap in caps)
            {
                await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, cap, AssetCapabilityState.Queued,
                    job.JobId, fingerprint, cancellationToken).ConfigureAwait(false);
            }
        }

        // FaceEmbedding: conditional applicability. Initially NOT_APPLICABLE; after FaceDetection
        // completes, the completion handler checks persisted face_detections. If faces found,
        // FaceEmbedding transitions to READY (SFace embeddings are persisted by the same worker).
        // If zero faces, it stays NOT_APPLICABLE. This is genuine conditional applicability.

        // SearchProjection: the asset row + metadata are in the DB after Stage 1 commit.
        // Gallery/search queries read directly from assets/profiles tables. No separate
        // projection step exists or is needed. Mark READY immediately.
        if (mediaType is MediaType.Image or MediaType.Video)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                assetId, AssetCapability.SearchProjection, AssetCapabilityState.Ready,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // SimilarityRelated: depends on face detection results + related evidence projection.
        // Only applicable to Image/Video where face-based related profiles are surfaced.
        // Left QUEUED; the completion handler transitions it after face analysis determines
        // whether similarity evidence applies. Genuine conditional applicability.
        // For Model, GetAll() does not include it so no action needed.

        return jobsCreated;
    }

    /// <summary>
    /// Returns all capabilities that a completed job satisfies. One job may complete multiple capabilities
    /// (e.g. GenerateVideoPreview satisfies Thumbnail + PresentationStill + VideoPreview).
    /// </summary>
    public static IReadOnlyList<AssetCapability> MapJobKindToCapabilities(string jobKind, MediaType mediaType) => jobKind switch
    {
        "ExtractMetadata" => [AssetCapability.Metadata],
        "GenerateThumbnail" => [AssetCapability.Thumbnail, AssetCapability.PresentationStill, AssetCapability.PresentationInput],
        "GenerateVideoPreview" => [AssetCapability.Thumbnail, AssetCapability.PresentationStill, AssetCapability.VideoPreview, AssetCapability.PresentationInput],
        "GenerateModelPreview" => [AssetCapability.Thumbnail, AssetCapability.PresentationInput],
        "FaceAnalysis" => [AssetCapability.FaceDetection],
        _ => [],
    };

    private async Task<MediaType?> ReadActiveAssetMediaTypeAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_type
            FROM assets
            WHERE asset_id = $assetId
              AND state = 'ACTIVE';
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : DbEnum.ParseMediaType(value);
    }

    private async Task<string?> ReadCanonicalVaultPathAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT current_managed_relative_path, current_managed_file_name
            FROM assets
            WHERE asset_id = $assetId
              AND current_managed_relative_path IS NOT NULL
              AND current_managed_relative_path <> ''
              AND current_managed_file_name IS NOT NULL
              AND current_managed_file_name <> '';
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var relativePath = reader.GetString(0);
        var fileName = reader.GetString(1);

        var location = _vaultPaths.ResolveManagedMediaLocation(
            assetId, null, "unknown", relativePath, fileName, null, ManagedPathState.None);
        return location.AbsoluteManagedFilePath;
    }

    private async Task<string?> ReadAssetFingerprintAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM assets WHERE asset_id = $assetId;";
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task MarkStage2StartedAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE import_units
            SET stage_2_started_at_ms = $now
            WHERE import_unit_id = $unitId AND stage_2_started_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// HashAsset keeps the canonical pre-Stage-1 deterministic identity because Stage 2 only
    /// references it as a prerequisite. Every job Stage 2 itself creates is namespaced so a persisted
    /// legacy original-source job can never satisfy idempotent creation for the canonical Vault job.
    /// </summary>
    private static Guid DeriveJobId(Guid ownerId, string jobKind)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var identity = string.Equals(jobKind, "HashAsset", StringComparison.Ordinal)
            ? $"{ownerId:D}:{jobKind}"
            : $"{ownerId:D}:Stage2:v1:{jobKind}";
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }
}

public sealed record Stage2ScheduleResult(
    Guid UnitId,
    int AssetsScheduled,
    int JobsScheduled,
    bool AllRequiredTerminal);

public sealed record Stage2Readiness(
    bool AllRequiredTerminal,
    int AssetsAllCapabilitiesTerminal,
    int TotalAssets,
    IReadOnlyList<Guid> FailedAssetIds,
    bool HasFailures);