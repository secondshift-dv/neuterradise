using System.IO;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Maintenance;

public sealed class StorageMetricsProvider
{
    private readonly CatalogDb _catalog;
    private readonly CachePaths _cachePaths;
    private readonly PersistentCacheBudget _cacheBudget;
    private readonly TimeProvider _timeProvider;

    public StorageMetricsProvider(
        CatalogDb catalog,
        PersistentCacheBudget? cacheBudget = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _cachePaths = new CachePaths(catalog.Paths);
        _cacheBudget = cacheBudget ?? new PersistentCacheBudget(_cachePaths);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CachePaths CachePaths => _cachePaths;

    public PersistentCacheBudget CacheBudget => _cacheBudget;

    public async Task<StorageMetricsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var healthSummary = await _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        var authorityCounts = await _catalog.HealthReads.GetStorageMetricsCountsAsync(cancellationToken)
            .ConfigureAwait(false);
        var integrityFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
            .ConfigureAwait(false);

        var filesystem = await Task.Run(
                () => ComputeFilesystemMetrics(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        return new StorageMetricsSnapshot(
            ManagedMediaBytes: healthSummary.ActiveManagedByteLength,
            CacheBytesByFamily: filesystem.CacheBytesByFamily,
            CacheBytesTotal: filesystem.CacheBytesTotal,
            CacheQuotaBytes: _cacheBudget.QuotaBytes,
            TrashBytes: authorityCounts.TrashedAssetByteLength,
            StagingBytes: filesystem.StagingBytes,
            DatabaseBytes: filesystem.DatabaseBytes,
            ProfileCount: healthSummary.TotalProfiles,
            ActiveAssetCount: healthSummary.ActiveAssets,
            TrashItemCount: authorityCounts.TrashEntryCount,
            PendingCleanupCount: authorityCounts.PendingSourceCleanupCount,
            HealthFindingCount: integrityFindings.Count,
            EvaluatedAtUtc: _timeProvider.GetUtcNow());
    }

    private FilesystemMetrics ComputeFilesystemMetrics(CancellationToken cancellationToken)
    {
        var cacheBytesByFamily = new Dictionary<CacheFamily, long>();
        long cacheBytesTotal = 0;
        foreach (var family in Enum.GetValues<CacheFamily>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var familyBytes = ComputeDirectoryBytes(_cachePaths.GetFamilyPath(family), cancellationToken);
            cacheBytesByFamily[family] = familyBytes;
            cacheBytesTotal += familyBytes;
        }

        var stagingBytes = ComputeDirectoryBytes(_catalog.Paths.StagingPath, cancellationToken);
        var databaseBytes = ComputeDatabaseBytes();

        return new FilesystemMetrics(cacheBytesByFamily, cacheBytesTotal, stagingBytes, databaseBytes);
    }

    private static long ComputeDirectoryBytes(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += file.Length;
        }

        return total;
    }

    private long ComputeDatabaseBytes()
    {
        long total = 0;
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var path = _catalog.Paths.CatalogDbPath + suffix;
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static readonly string[] DatabaseSidecarSuffixes = ["", "-wal", "-shm"];

    private sealed record FilesystemMetrics(
        IReadOnlyDictionary<CacheFamily, long> CacheBytesByFamily,
        long CacheBytesTotal,
        long StagingBytes,
        long DatabaseBytes);
}
