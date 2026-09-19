using System.IO;
using System.Security.Cryptography;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Preparation;

public sealed record CandidatePreparationResult(
    Guid ImportItemId,
    Guid CandidateAssetId,
    string Sha256,
    long ByteLength,
    bool IsExactDuplicate,
    ExactDuplicateMatch? DuplicateMatch);

public sealed record UnitPreparationResult(
    Guid UnitId,
    int TotalItemCount,
    int PreparedCount,
    int ExactDuplicateCount,
    PreparationReadiness Readiness);

/// <summary>
/// Canonical pre-Stage-1 preparation authority. C001C constrains this coordinator to source
/// fingerprint admission only. Metadata, previews, face analysis and final ReadyForVerification
/// belong to Stage 2 after canonical materialization.
/// </summary>
public sealed class ImportPreparationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly ImportWrites _importWrites;
    private readonly ImportReads _reads;
    private readonly ExactDuplicateDetector _duplicateDetector;
    private readonly PreparationReadinessEvaluator _readinessEvaluator;
    private readonly JobWrites _jobWrites;

    public ImportPreparationCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importWrites = new ImportWrites(catalog, timeProvider);
        _reads = catalog.ImportReads;
        _duplicateDetector = new ExactDuplicateDetector(catalog);
        _readinessEvaluator = new PreparationReadinessEvaluator(catalog);
        _jobWrites = new JobWrites(catalog, timeProvider);
    }

    private static long _reusedFingerprints;
    private static long _computedFingerprints;

    public static (long Reused, long Computed) FingerprintCounters =>
        (Interlocked.Read(ref _reusedFingerprints), Interlocked.Read(ref _computedFingerprints));

    private async Task<(string? Sha256, long? ByteLength)> ReadStoredFingerprintAsync(
        Guid candidateAssetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, byte_length FROM assets WHERE asset_id = $id;";
        command.Parameters.AddWithValue("$id", DbGuid.Format(candidateAssetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    public async Task<Guid?> ReadCandidateAssetIdAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        return item?.CandidateAssetId;
    }

    /// <summary>
    /// Executes the HashAsset operation for one persisted Candidate. This is the only work that may
    /// still read original intake bytes before Stage 1. Model package identity is part of hashing the
    /// original Candidate and remains necessary for exact duplicate/materialization semantics.
    /// </summary>
    public async Task<CandidatePreparationResult> PrepareCandidateAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} was not found.");
        }

        if (!item.CandidateAssetId.HasValue)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} has no allocated CandidateAssetId.");
        }

        var fileInfo = new FileInfo(item.SourcePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"Source file not found at '{item.SourcePath}'.", item.SourcePath);
        }

        var byteLength = fileInfo.Length;
        var lastWriteMs = fileInfo.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
        var stored = await ReadStoredFingerprintAsync(item.CandidateAssetId.Value, cancellationToken)
            .ConfigureAwait(false);
        var sourceUnchanged = stored.Sha256 is not null
            && stored.ByteLength == byteLength
            && item.SourceByteLength == byteLength
            && item.SourceLastWriteUtc?.ToUnixTimeMilliseconds() == lastWriteMs;

        string sha256;
        if (sourceUnchanged)
        {
            sha256 = stored.Sha256!;
            Interlocked.Increment(ref _reusedFingerprints);
        }
        else
        {
            using (await ResourceGovernor.Shared.AcquireAsync(ResourceClass.DiskHeavy, cancellationToken).ConfigureAwait(false))
            await using (var stream = new FileStream(
                item.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                useAsync: true))
            {
                var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexStringLower(hashBytes);
            }

            Interlocked.Increment(ref _computedFingerprints);
            await _importWrites.UpdateCandidateFingerprintAsync(
                    item.CandidateAssetId.Value,
                    sha256,
                    byteLength,
                    cancellationToken)
                .ConfigureAwait(false);
            await _importWrites.UpdateItemSourceDetailsAsync(
                    importItemId,
                    byteLength,
                    lastWriteMs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string? bundleSha256 = null;
        if (item.MediaType == MediaType.Model)
        {
            var discovery = await Task.Run(
                    () => ModelPackageDiscovery.Discover(item.SourcePath, sha256, byteLength),
                    cancellationToken)
                .ConfigureAwait(false);
            bundleSha256 = discovery.BundleSha256;

            await _catalog.AssetWrites.UpdateAssetPackageIdentityAsync(
                    item.CandidateAssetId.Value,
                    discovery.DependencyStatus,
                    discovery.DiscoveryState,
                    discovery.BundleSha256,
                    cancellationToken)
                .ConfigureAwait(false);

            if (discovery.Components.Count > 0)
            {
                var compRecords = discovery.Components.Select(c => new AssetComponentRecord(
                    item.CandidateAssetId.Value,
                    c.RelativePath,
                    c.NormalizedRelativePath,
                    c.Role,
                    c.Sha256,
                    c.ByteLength,
                    c.OriginalSourcePath,
                    SourceIdentityJson: SourceIdentityHelper.CaptureIdentity(
                        c.OriginalSourcePath
                        ?? Path.Combine(
                            Path.GetDirectoryName(item.SourcePath) ?? string.Empty,
                            c.RelativePath.Replace("/", "\\"))),
                    SourceCleanupState: SourceCleanupState.SourcePresent)).ToList();

                await _catalog.AssetWrites.SaveAssetComponentsAsync(
                        item.CandidateAssetId.Value,
                        compRecords,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var dupResult = await _duplicateDetector.DetectDuplicateAsync(
                sha256,
                byteLength,
                item.CandidateAssetId.Value,
                bundleSha256,
                cancellationToken)
            .ConfigureAwait(false);

        return new CandidatePreparationResult(
            importItemId,
            item.CandidateAssetId.Value,
            sha256,
            byteLength,
            dupResult.IsExactDuplicate,
            dupResult.Match);
    }

    /// <summary>
    /// Compatibility entry used by normal intake and Retry. It is deliberately equivalent to the
    /// canonical Hash admission path and cannot schedule metadata/preview/face work or decide final
    /// verification readiness.
    /// </summary>
    public async Task<UnitPreparationResult> PrepareUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var unit = await _reads.GetUnitSummaryAsync(unitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            throw new InvalidOperationException($"ImportUnit {unitId:D} was not found.");
        }

        await ScheduleAdmissionHashJobsAsync(unitId, cancellationToken).ConfigureAwait(false);
        var readiness = await ResolveUnitStateAsync(unitId, cancellationToken).ConfigureAwait(false);
        var counts = await ReadAdmissionCountsAsync(unitId, cancellationToken).ConfigureAwait(false);

        return new UnitPreparationResult(
            unitId,
            counts.TotalCount,
            counts.PreparedCount,
            ExactDuplicateCount: 0,
            Readiness: readiness);
    }

    /// <summary>
    /// Single pre-Stage-1 work authority. It owns the durable INTAKE -> PREPARING handoff and creates
    /// only deterministic HashAsset jobs. The item read is intentionally unbounded by presentation
    /// paging so normal intake, retry, recovery and split-child admission see the same complete unit.
    /// </summary>
    public async Task<int> ScheduleAdmissionHashJobsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var unit = await _reads.GetUnitSummaryAsync(unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null
            || unit.State is ImportUnitState.Cancelled or ImportUnitState.FailedTerminal
            || unit.State.IsUnitCommitted())
        {
            return 0;
        }

        if (unit.State == ImportUnitState.Intake)
        {
            await _importWrites
                .UpdateUnitStateAsync(unitId, ImportUnitState.Preparing, cancellationToken)
                .ConfigureAwait(false);
        }

        var items = await ReadAdmissionCandidatesAsync(unitId, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            var plan = CandidatePreparationPlan.Create(
                item.ImportItemId,
                item.CandidateAssetId,
                item.SourcePath);

            await _jobWrites.CreateJobAsync(plan.HashJob, cancellationToken).ConfigureAwait(false);
        }

        return items.Count;
    }

    private async Task<IReadOnlyList<AdmissionCandidate>> ReadAdmissionCandidatesAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var items = new List<AdmissionCandidate>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.import_item_id, i.candidate_asset_id, i.source_path
            FROM import_items i
            JOIN assets a ON a.asset_id = i.candidate_asset_id
            WHERE i.import_unit_id = $unitId
              AND i.candidate_asset_id IS NOT NULL
              AND i.disposition NOT IN ('SKIPPED','INVALID','REUSED')
              AND a.state <> 'RETIRED'
            ORDER BY i.created_at_ms, i.import_item_id;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AdmissionCandidate(
                DbGuid.Parse(reader.GetString(0)),
                DbGuid.Parse(reader.GetString(1)),
                reader.GetString(2)));
        }

        return items;
    }

    /// <summary>
    /// Compatibility name retained for current callers, but the semantics are no longer legacy
    /// preparation readiness. This is a read-only, complete-dataset Hash admission probe and never
    /// mutates ImportUnit lifecycle state.
    /// </summary>
    public Task<PreparationReadiness> ResolveUnitStateAsync(
        Guid unitId,
        CancellationToken cancellationToken = default) =>
        _readinessEvaluator.EvaluateUnitReadinessAsync(
            unitId,
            verifySourceFilesOnDisk: false,
            cancellationToken);

    /// <summary>
    /// Compatibility duplicate-decision entry. Persisting the decision is allowed; deciding
    /// ReadyForVerification is not. Finalizer/Stage2 remain the lifecycle authorities.
    /// </summary>
    public async Task<PreparationReadiness> ApplyDuplicateDecisionAsync(
        Guid importItemId,
        DuplicateDecision decision,
        Guid? reusedAssetId = null,
        CancellationToken cancellationToken = default)
    {
        var item = await _reads.GetItemSummaryAsync(importItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            throw new InvalidOperationException($"ImportItem {importItemId:D} was not found.");
        }

        await _importWrites.ApplyDuplicateDecisionAsync(
                importItemId,
                DbEnum.DispositionFor(decision),
                decision,
                reusedAssetId,
                cancellationToken)
            .ConfigureAwait(false);

        return await _readinessEvaluator
            .EvaluateUnitReadinessAsync(item.UnitId, verifySourceFilesOnDisk: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AdmissionCounts> ReadAdmissionCountsAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE
                       WHEN i.disposition = 'REUSED' THEN 1
                       WHEN i.disposition = 'INCLUDED' AND a.sha256 IS NOT NULL THEN 1
                       ELSE 0
                   END), 0)
            FROM import_items i
            LEFT JOIN assets a ON a.asset_id = i.candidate_asset_id
            WHERE i.import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AdmissionCounts(0, 0);
        }

        return new AdmissionCounts(reader.GetInt32(0), reader.GetInt32(1));
    }

    private sealed record AdmissionCandidate(
        Guid ImportItemId,
        Guid CandidateAssetId,
        string SourcePath);

    private sealed record AdmissionCounts(int TotalCount, int PreparedCount);
}
