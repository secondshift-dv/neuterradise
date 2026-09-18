using System.Collections.Concurrent;
using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class ThumbnailCache
{
    private readonly CachePaths _paths;
    private readonly CacheFilePublisher _publisher;
    private readonly PersistentCacheBudget _budget;
    private readonly ConcurrentDictionary<ThumbnailCacheKey, TaskCompletionSource<bool>> _activeGenerations = new();

    public ThumbnailCache(CachePaths paths, CacheFilePublisher publisher, PersistentCacheBudget budget)
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
        ThumbnailCacheKey key,
        bool acquireLease = false,
        Func<string, bool>? validator = null,
        string extension = ".jpg")
    {
        ArgumentNullException.ThrowIfNull(key);

        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.Thumbnails, relativePath);

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

    public bool TryBeginGeneration(ThumbnailCacheKey key, out IDisposable? generationScope)
    {
        ArgumentNullException.ThrowIfNull(key);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_activeGenerations.TryAdd(key, completion))
        {
            generationScope = new ActionDisposable(() =>
            {
                if (_activeGenerations.TryRemove(key, out var active))
                {
                    active.TrySetResult(true);
                }
            });
            return true;
        }

        generationScope = null;
        return false;
    }

    /// <summary>
    /// Joins the currently active generation for this cache key. If no generation is active by the time
    /// the caller arrives, the task completes immediately and the caller can re-check the cache.
    /// </summary>
    public async Task WaitForGenerationAsync(ThumbnailCacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_activeGenerations.TryGetValue(key, out var completion))
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> StoreAsync(
        ThumbnailCacheKey key,
        ReadOnlyMemory<byte> bytes,
        Func<string, bool>? validator = null,
        string extension = ".jpg",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.Thumbnails, relativePath);
        return _publisher.PublishAsync(physicalPath, bytes, validator, ct);
    }

    public Task<bool> StoreStreamAsync(
        ThumbnailCacheKey key,
        Func<Stream, Task> writeAction,
        Func<string, bool>? validator = null,
        string extension = ".jpg",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.Thumbnails, relativePath);
        return _publisher.PublishStreamAsync(physicalPath, writeAction, validator, ct);
    }

    public bool Invalidate(ThumbnailCacheKey key, string extension = ".jpg")
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.Thumbnails, relativePath);

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
            return true;
        }

        return false;
    }

    public int InvalidateAsset(Guid assetId)
    {
        var assetDir = Path.Combine(_paths.ThumbnailsPath, assetId.ToString("N"));
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
        if (!Directory.Exists(_paths.ThumbnailsPath))
        {
            return 0;
        }

        var count = 0;
        var pattern = $"*_v{obsoleteVersion}_*";
        foreach (var file in Directory.EnumerateFiles(_paths.ThumbnailsPath, pattern, SearchOption.AllDirectories))
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
