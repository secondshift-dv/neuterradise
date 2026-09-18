using System.Collections.Concurrent;
using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class BannerPreviewCache
{
    private readonly CachePaths _paths;
    private readonly CacheFilePublisher _publisher;
    private readonly PersistentCacheBudget _budget;
    private readonly ConcurrentDictionary<BannerPreviewCacheKey, byte> _activeGenerations = new();

    public BannerPreviewCache(CachePaths paths, CacheFilePublisher publisher, PersistentCacheBudget budget)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(budget);

        _paths = paths;
        _publisher = publisher;
        _budget = budget;
    }

    public CachePaths Paths => _paths;

    public CacheLookupResult Lookup(
        BannerPreviewCacheKey key,
        bool acquireLease = false,
        Func<string, bool>? validator = null,
        string extension = ".mp4")
    {
        ArgumentNullException.ThrowIfNull(key);

        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.BannerPreviews, relativePath);

        if (File.Exists(physicalPath))
        {
            var fileInfo = new FileInfo(physicalPath);
            if (fileInfo.Length == 0 || (validator != null && !validator(physicalPath)))
            {
                return CacheLookupResult.InvalidOrCorrupt();
            }

            var lease = acquireLease ? _budget.AcquireReadLease(physicalPath) : null;
            _budget.RecordAccess(physicalPath);
            return CacheLookupResult.Hit(physicalPath, lease);
        }

        if (_activeGenerations.ContainsKey(key))
        {
            return CacheLookupResult.GenerationPending();
        }

        return CacheLookupResult.Miss();
    }

    public bool TryBeginGeneration(BannerPreviewCacheKey key, out IDisposable? generationScope)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_activeGenerations.TryAdd(key, 0))
        {
            generationScope = new ActionDisposable(() => _activeGenerations.TryRemove(key, out _));
            return true;
        }

        generationScope = null;
        return false;
    }

    public Task<bool> StoreAsync(
        BannerPreviewCacheKey key,
        ReadOnlyMemory<byte> bytes,
        Func<string, bool>? validator = null,
        string extension = ".mp4",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.BannerPreviews, relativePath);
        return _publisher.PublishAsync(physicalPath, bytes, validator, ct);
    }

    public Task<bool> StoreStreamAsync(
        BannerPreviewCacheKey key,
        Func<Stream, Task> writeAction,
        Func<string, bool>? validator = null,
        string extension = ".mp4",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.BannerPreviews, relativePath);
        return _publisher.PublishStreamAsync(physicalPath, writeAction, validator, ct);
    }

    public bool Invalidate(BannerPreviewCacheKey key, string extension = ".mp4")
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.BannerPreviews, relativePath);

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
            return true;
        }

        return false;
    }

    public int InvalidateAsset(Guid assetId)
    {
        var assetDir = Path.Combine(_paths.BannerPreviewsPath, assetId.ToString("N"));
        if (!Directory.Exists(assetDir))
        {
            return 0;
        }

        var count = Directory.EnumerateFiles(assetDir, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(assetDir, recursive: true);
        return count;
    }

    public int InvalidateVersion(int obsoleteVersion)
    {
        if (!Directory.Exists(_paths.BannerPreviewsPath))
        {
            return 0;
        }

        var count = 0;
        var pattern = $"*_v{obsoleteVersion}_*";
        foreach (var file in Directory.EnumerateFiles(_paths.BannerPreviewsPath, pattern, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                count++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return count;
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }
}
