using System.Collections.Concurrent;
using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class FaceCropCache
{
    private readonly CachePaths _paths;
    private readonly CacheFilePublisher _publisher;
    private readonly PersistentCacheBudget _budget;
    private readonly ConcurrentDictionary<FaceCropCacheKey, TaskCompletionSource<bool>> _activeGenerations = new();

    public FaceCropCache(CachePaths paths, CacheFilePublisher publisher, PersistentCacheBudget budget)
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
        FaceCropCacheKey key,
        bool acquireLease = false,
        Func<string, bool>? validator = null,
        string extension = ".jpg")
    {
        ArgumentNullException.ThrowIfNull(key);

        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.FaceCrops, relativePath);

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

    public bool TryBeginGeneration(FaceCropCacheKey key, out IDisposable? generationScope)
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
    /// Joins the currently active generation for this face-crop key. Completion only means the owner
    /// retired its generation scope; callers re-check the cache and may take over if generation failed.
    /// </summary>
    public async Task WaitForGenerationAsync(FaceCropCacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_activeGenerations.TryGetValue(key, out var completion))
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> StoreAsync(
        FaceCropCacheKey key,
        ReadOnlyMemory<byte> bytes,
        Func<string, bool>? validator = null,
        string extension = ".jpg",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.FaceCrops, relativePath);
        return _publisher.PublishAsync(physicalPath, bytes, validator, ct);
    }

    public Task<bool> StoreStreamAsync(
        FaceCropCacheKey key,
        Func<Stream, Task> writeAction,
        Func<string, bool>? validator = null,
        string extension = ".jpg",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.FaceCrops, relativePath);
        return _publisher.PublishStreamAsync(physicalPath, writeAction, validator, ct);
    }

    public bool Invalidate(FaceCropCacheKey key, string extension = ".jpg")
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.FaceCrops, relativePath);

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
            return true;
        }

        return false;
    }

    public int InvalidateFace(Guid faceId)
    {
        var faceDir = Path.Combine(_paths.FaceCropsPath, faceId.ToString("N"));
        if (!Directory.Exists(faceDir))
        {
            return 0;
        }

        var count = Directory.EnumerateFiles(faceDir, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(faceDir, recursive: true);
        return count;
    }

    public int InvalidateVersion(int obsoleteVersion)
    {
        if (!Directory.Exists(_paths.FaceCropsPath))
        {
            return 0;
        }

        var count = 0;
        var pattern = $"*_v{obsoleteVersion}_*";
        foreach (var file in Directory.EnumerateFiles(_paths.FaceCropsPath, pattern, SearchOption.AllDirectories))
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
