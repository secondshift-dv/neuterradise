using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs.Handlers;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class VideoPreviewRegenerationCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly CachePaths _cachePaths;
    private readonly JobWrites _jobWrites;

    public VideoPreviewRegenerationCoordinator(CatalogDb catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cachePaths = new CachePaths(catalog.Paths);
        _jobWrites = new JobWrites(catalog);
    }

    public async Task<int> RequeueMissingCurrentAsync(
        Guid? assetId = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(Guid JobId, Guid AssetId, string Fingerprint)>();
        await using (var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT j.job_id, a.asset_id, a.sha256
                FROM jobs j
                JOIN assets a ON j.owner_type = 'Asset' AND j.owner_id = a.asset_id
                WHERE j.kind = 'GenerateVideoPreview'
                  AND j.state = 'SUCCEEDED'
                  AND a.state = 'ACTIVE'
                  AND a.media_type = 'Video'
                  AND a.sha256 IS NOT NULL
                  AND ($assetId IS NULL OR a.asset_id = $assetId)
                ORDER BY j.created_at_ms DESC, j.job_id DESC;
                """;
            command.Parameters.AddWithValue("$assetId", assetId.HasValue ? DbGuid.Format(assetId.Value) : DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var seenAssets = new HashSet<Guid>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ownerId = DbGuid.Parse(reader.GetString(1));
                if (!seenAssets.Add(ownerId)) continue;
                candidates.Add((DbGuid.Parse(reader.GetString(0)), ownerId, reader.GetString(2)));
            }
        }

        var requeued = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string physicalPath;
            try
            {
                var key = new VideoPreviewCacheKey(
                    candidate.AssetId,
                    candidate.Fingerprint,
                    CacheVersionSet.Default.VideoPreviewVersion,
                    ProductionMediaToolPlanSource.VideoPreviewVariantKey);
                physicalPath = _cachePaths.ResolveContainedPath(CacheFamily.VideoPreviews, key.GetRelativePath(".mp4"));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(physicalPath) && new FileInfo(physicalPath).Length > 0) continue;

            if (await _jobWrites.TryRequeueSucceededDerivedJobAsync(
                    candidate.JobId, "GenerateVideoPreview", candidate.AssetId, cancellationToken)
                .ConfigureAwait(false))
            {
                requeued++;
            }
        }

        return requeued;
    }
}
