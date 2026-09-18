using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class CacheMaintenanceOperations
{
    private readonly CachePaths _paths;
    private readonly PersistentCacheBudget _budget;

    public CacheMaintenanceOperations(CachePaths paths, PersistentCacheBudget budget)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(budget);

        _paths = paths;
        _budget = budget;
    }

    public CachePaths Paths => _paths;

    public PersistentCacheBudget Budget => _budget;

    public Task<int> CleanOrphanCacheAsync(ISet<string> activeKeysOrPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activeKeysOrPaths);

        return Task.Run(() =>
        {
            if (!Directory.Exists(_paths.CacheRoot))
            {
                return 0;
            }

            var deleted = 0;
            var families = new[]
            {
                _paths.ThumbnailsPath,
                _paths.VideoPreviewsPath,
                _paths.BannerPreviewsPath,
                _paths.FaceCropsPath,
                _paths.ModelPreviewsPath,
            };

            foreach (var familyDir in families)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(familyDir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(familyDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_paths.IsContainedCachePath(file))
                    {
                        continue;
                    }

                    if (_budget.IsPinned(file))
                    {
                        continue;
                    }

                    var relativeToCache = Path.GetRelativePath(_paths.CacheRoot, file).Replace('\\', '/');
                    var relativeToFamily = Path.GetRelativePath(familyDir, file).Replace('\\', '/');

                    if (!activeKeysOrPaths.Contains(file) &&
                        !activeKeysOrPaths.Contains(relativeToCache) &&
                        !activeKeysOrPaths.Contains(relativeToFamily))
                    {
                        if (TryDeleteFile(file))
                        {
                            deleted++;
                        }
                    }
                }
            }

            return deleted;
        }, ct);
    }

    public Task<int> ResetThumbnailCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.Thumbnails, ct);

    public Task<int> ResetVideoPreviewCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.VideoPreviews, ct);

    public Task<int> ResetBannerPreviewCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.BannerPreviews, ct);

    public Task<int> ResetFaceCropCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.FaceCrops, ct);

    public Task<int> ResetModelPreviewCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.ModelPreviews, ct);

    public Task<int> CleanTempCacheAsync(CancellationToken ct = default) =>
        ResetFamilyAsync(CacheFamily.Temp, ct);

    public async Task<int> ClearAllDerivedCacheAsync(CancellationToken ct = default)
    {
        var total = 0;
        total += await ResetThumbnailCacheAsync(ct);
        total += await ResetVideoPreviewCacheAsync(ct);
        total += await ResetBannerPreviewCacheAsync(ct);
        total += await ResetFaceCropCacheAsync(ct);
        total += await ResetModelPreviewCacheAsync(ct);
        total += await CleanTempCacheAsync(ct);
        return total;
    }

    private Task<int> ResetFamilyAsync(CacheFamily family, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var familyDir = _paths.GetFamilyPath(family);
            if (!Directory.Exists(familyDir))
            {
                return 0;
            }

            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(familyDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!_paths.IsContainedCachePath(file))
                {
                    continue;
                }

                if (TryDeleteFile(file))
                {
                    deleted++;
                }
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(familyDir, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return deleted;
        }, ct);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }
}
