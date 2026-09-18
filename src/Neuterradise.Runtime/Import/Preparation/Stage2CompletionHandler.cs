using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Handles Stage 2 job completion and durable terminal reconciliation: maps job outcomes to
/// capability state, closes failed/cancelled dependency graphs, then re-evaluates unit readiness.
/// </summary>
public sealed class Stage2CompletionHandler
{
    private readonly CatalogDb _catalog;
    private readonly CapabilityWrites _capabilityWrites;
    private readonly Stage2PreparationCoordinator _coordinator;
    private readonly SchedulerReads _schedulerReads;
    private readonly JobWrites _jobWrites;

    public Stage2CompletionHandler(
        CatalogDb catalog,
        Stage2PreparationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        _catalog = catalog;
        _capabilityWrites = new CapabilityWrites(catalog);
        _coordinator = coordinator;
        _schedulerReads = new SchedulerReads(catalog);
        _jobWrites = new JobWrites(catalog);
    }

    /// <summary>
    /// Called by the scheduler after normal terminal completion. Updates every capability covered by
    /// the job. Terminal non-success also closes dependent jobs immediately; the reconciliation loop
    /// remains the catch-all for idle cancellation, retry exhaustion, and restart recovery.
    /// </summary>
    public async Task HandleCompletionAsync(Guid jobId, JobExecutionResult result)
    {
        var jobInfo = await FindJobInfoAsync(jobId).ConfigureAwait(false);
        if (jobInfo is null)
        {
            return;
        }

        var (assetId, jobKind, mediaType) = jobInfo.Value;
        var capabilities = await ResolveCapabilitiesAsync(jobId, jobKind, mediaType).ConfigureAwait(false);
        if (capabilities.Count == 0)
        {
            return;
        }

        var targetState = result.IsSucceeded
            ? AssetCapabilityState.Ready
            : AssetCapabilityState.Failed;

        foreach (var capability in capabilities)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                assetId, capability, targetState, jobId)
                .ConfigureAwait(false);
        }

        if (string.Equals(jobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
        {
            if (result.IsSucceeded)
            {
                await ReconcileSuccessfulFaceAnalysisAsync(assetId, jobId).ConfigureAwait(false);
            }
            else
            {
                await ReconcileUnsuccessfulFaceAnalysisAsync(assetId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var affectedAssetIds = new HashSet<Guid> { assetId };
        if (!result.IsSucceeded)
        {
            await CascadeDependentJobsAsync(
                    jobId,
                    new HashSet<Guid> { jobId },
                    affectedAssetIds,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        await TryReadinessForAssetsAsync(affectedAssetIds, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AssetCapability>> ResolveCapabilitiesAsync(
        Guid jobId,
        string jobKind,
        MediaType mediaType)
    {
        var capabilities = Stage2PreparationCoordinator.MapJobKindToCapabilities(jobKind, mediaType);
        if (capabilities.Count > 0)
        {
            return capabilities;
        }

        var legacyCap = await FindCapabilityForJobAsync(jobId).ConfigureAwait(false);
        return legacyCap.HasValue ? [legacyCap.Value] : [];
    }

    private async Task ReconcileSuccessfulFaceAnalysisAsync(Guid assetId, Guid jobId)
    {
        var (faceCount, embeddedCount) = await CountFaceEmbeddingsAsync(assetId).ConfigureAwait(false);
        if (faceCount == 0)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, AssetCapability.FaceEmbedding, AssetCapabilityState.NotApplicable)
                .ConfigureAwait(false);
        }
        else if (embeddedCount >= faceCount)
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, AssetCapability.FaceEmbedding, AssetCapabilityState.Ready, jobId)
                .ConfigureAwait(false);
        }
        else
        {
            await _capabilityWrites.UpsertCapabilityAsync(
                    assetId, AssetCapability.FaceEmbedding, AssetCapabilityState.Failed, jobId)
                .ConfigureAwait(false);
        }

        var hasRelatedEvidence = await HasRelatedEvidenceAsync(assetId).ConfigureAwait(false);
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                AssetCapability.SimilarityRelated,
                hasRelatedEvidence ? AssetCapabilityState.Ready : AssetCapabilityState.NotApplicable)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A failed or cancelled FaceAnalysis cannot produce new embeddings. Conditional capabilities
    /// must still become terminal so cancellation cannot strand SimilarityRelated in QUEUED.
    /// Existing durable related evidence remains authoritative if it already exists.
    /// </summary>
    private async Task ReconcileUnsuccessfulFaceAnalysisAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                AssetCapability.FaceEmbedding,
                AssetCapabilityState.NotApplicable,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var hasRelatedEvidence = await HasRelatedEvidenceAsync(assetId, cancellationToken).ConfigureAwait(false);
        await _capabilityWrites.UpsertCapabilityAsync(
                assetId,
                AssetCapability.SimilarityRelated,
                hasRelatedEvidence ? AssetCapabilityState.Ready : AssetCapabilityState.NotApplicable,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(Guid AssetId, string JobKind, MediaType MediaType)?> FindJobInfoAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT cr.asset_id, j.kind, COALESCE(a.media_type, 'Image')
            FROM asset_capability_readiness cr
            JOIN jobs j ON j.job_id = cr.job_id
            LEFT JOIN assets a ON a.asset_id = cr.asset_id
            WHERE cr.job_id = $jobId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            DbGuid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DbEnum.ParseMediaType(reader.GetString(2)));
    }

    private async Task<AssetCapability?> FindCapabilityForJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT capability
            FROM asset_capability_readiness
            WHERE job_id = $jobId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", DbGuid.Format(jobId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return DbEnum.ParseAssetCapability(reader.GetString(0));
    }

    private async Task<IReadOnlyList<Guid>> FindUnitsForAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT import_unit_id
            FROM import_items
            WHERE disposition IN ('INCLUDED', 'REUSED')
              AND (candidate_asset_id = $assetId OR reused_asset_id = $assetId);
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private async Task<(int FaceCount, int EmbeddedCount)> CountFaceEmbeddingsAsync(Guid assetId)
    {
        await using var connection = await _catalog.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) AS face_count, COUNT(embedding) AS embedded_count
            FROM face_detections
            WHERE asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return (0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<bool> HasRelatedEvidenceAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM related_profile_evidence WHERE asset_id = $assetId);";
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Reconciles durable terminal non-success jobs into capability truth. The query includes jobs
    /// whose own capability is already FAILED when they still have non-terminal dependents, which
    /// is required for dependency closure after a normal terminal failure.
    /// </summary>
    public async Task ReconcileTerminalCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT cr.asset_id, j.kind, j.job_id, COALESCE(a.media_type, 'Image')
            FROM asset_capability_readiness cr
            JOIN jobs j ON j.job_id = cr.job_id
            LEFT JOIN assets a ON a.asset_id = cr.asset_id
            WHERE j.state IN ('FAILED_TERMINAL', 'CANCELLED')
              AND (
                    cr.state NOT IN ('READY', 'NOT_APPLICABLE', 'FAILED')
                    OR EXISTS (
                        SELECT 1
                        FROM job_dependencies d
                        JOIN jobs dependent ON dependent.job_id = d.job_id
                        WHERE d.depends_on_job_id = j.job_id
                          AND dependent.state NOT IN ('SUCCEEDED', 'FAILED_TERMINAL', 'CANCELLED')
                    )
                    OR (
                        j.kind = 'FaceAnalysis'
                        AND EXISTS (
                            SELECT 1
                            FROM asset_capability_readiness conditional
                            WHERE conditional.asset_id = cr.asset_id
                              AND conditional.capability IN ('FaceEmbedding', 'SimilarityRelated')
                              AND conditional.state NOT IN ('READY', 'NOT_APPLICABLE', 'FAILED')
                        )
                    )
              );
            """;

        var terminalJobs = new List<(Guid AssetId, string JobKind, Guid JobId, MediaType MediaType)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                terminalJobs.Add((
                    DbGuid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    DbGuid.Parse(reader.GetString(2)),
                    DbEnum.ParseMediaType(reader.GetString(3))));
            }
        }

        var affectedAssetIds = new HashSet<Guid>();
        foreach (var (assetId, jobKind, jobId, mediaType) in terminalJobs)
        {
            affectedAssetIds.Add(assetId);

            var capabilities = await ResolveCapabilitiesAsync(jobId, jobKind, mediaType).ConfigureAwait(false);
            foreach (var capability in capabilities)
            {
                await _capabilityWrites.UpsertCapabilityAsync(
                        assetId,
                        capability,
                        AssetCapabilityState.Failed,
                        jobId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(jobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
            {
                await ReconcileUnsuccessfulFaceAnalysisAsync(assetId, cancellationToken).ConfigureAwait(false);
            }

            await CascadeDependentJobsAsync(
                    jobId,
                    new HashSet<Guid> { jobId },
                    affectedAssetIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await TryReadinessForAssetsAsync(affectedAssetIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A terminal failed/cancelled predecessor makes non-success-dependent work impossible. Close
    /// each idle dependent durably as CANCELLED before projecting its capability to FAILED, then
    /// recurse so no durable job remains PENDING/RUNNABLE/PAUSED/FAILED_RETRYABLE forever.
    /// </summary>
    private async Task CascadeDependentJobsAsync(
        Guid terminalPredecessorJobId,
        HashSet<Guid> visited,
        HashSet<Guid> affectedAssetIds,
        CancellationToken cancellationToken)
    {
        var dependents = await _schedulerReads
            .GetDirectDependentsAsync(terminalPredecessorJobId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var dependentId in dependents)
        {
            if (!visited.Add(dependentId))
            {
                continue;
            }

            var dependent = await _schedulerReads.GetJobAsync(dependentId, cancellationToken)
                .ConfigureAwait(false);
            if (dependent is null || dependent.State == JobState.Succeeded)
            {
                continue;
            }

            if (dependent.State is not (JobState.FailedTerminal or JobState.Cancelled))
            {
                await _jobWrites.CancelIdleAsync(
                        dependentId,
                        null,
                        null,
                        TimeProvider.System.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);

                dependent = await _schedulerReads.GetJobAsync(dependentId, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A RUNNING dependent should be unreachable because dispatch requires all predecessors
            // SUCCEEDED. If a race ever produces one, do not falsify terminal truth; a later pass
            // will reconcile it after the scheduler settles the durable job state.
            if (dependent is null || dependent.State is not (JobState.FailedTerminal or JobState.Cancelled))
            {
                continue;
            }

            var depInfo = await FindJobInfoAsync(dependentId, cancellationToken).ConfigureAwait(false);
            if (depInfo is not null)
            {
                var (depAssetId, depJobKind, depMediaType) = depInfo.Value;
                affectedAssetIds.Add(depAssetId);
                var depCaps = await ResolveCapabilitiesAsync(dependentId, depJobKind, depMediaType)
                    .ConfigureAwait(false);
                foreach (var cap in depCaps)
                {
                    await _capabilityWrites.UpsertCapabilityAsync(
                            depAssetId,
                            cap,
                            AssetCapabilityState.Failed,
                            dependentId,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (string.Equals(depJobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
                {
                    await ReconcileUnsuccessfulFaceAnalysisAsync(depAssetId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await CascadeDependentJobsAsync(dependentId, visited, affectedAssetIds, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TryReadinessForAssetsAsync(
        IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        var affectedUnitIds = new HashSet<Guid>();
        foreach (var assetId in assetIds)
        {
            var unitIds = await FindUnitsForAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
            foreach (var unitId in unitIds)
            {
                affectedUnitIds.Add(unitId);
            }
        }

        foreach (var unitId in affectedUnitIds)
        {
            try
            {
                await _coordinator.TryTransitionToReadyAsync(unitId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Readiness is recoverable/idempotent and will be retried by the bounded scheduler
                // reconciliation pass or a later job completion.
            }
        }
    }
}
