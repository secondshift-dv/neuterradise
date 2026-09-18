using System.Collections.Concurrent;
using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class VideoPreviewCache
{
    private readonly CachePaths _paths;
    private readonly CacheFilePublisher _publisher;
    private readonly PersistentCacheBudget _budget;
    private readonly ConcurrentDictionary<VideoPreviewCacheKey, TaskCompletionSource<bool>> _activeGenerations = new();

    public VideoPreviewCache(CachePaths paths, CacheFilePublisher publisher, PersistentCacheBudget budget)
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
        VideoPreviewCacheKey key,
        bool acquireLease = false,
        Func<string, bool>? validator = null,
        string extension = ".mp4")
    {
        ArgumentNullException.ThrowIfNull(key);

        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.VideoPreviews, relativePath);

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

    public bool TryBeginGeneration(VideoPreviewCacheKey key, out IDisposable? generationScope)
    {
        ArgumentNullException.ThrowIfNull(key);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_activeGenerations.TryAdd(key, completion))
        {
            generationScope = new ActionDisposable(() =>
            {
                _activeGenerations.TryRemove(key, out _);
                completion.TrySetResult(true);
            });
            return true;
        }

        generationScope = null;
        return false;
    }

    /// <summary>
    /// Joins the currently active generation for this video-preview key. Completion only means the
    /// owner retired its generation scope; callers re-check the cache and may take over if
    /// generation failed to produce a cached file.
    /// </summary>
    public async Task WaitForGenerationAsync(VideoPreviewCacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_activeGenerations.TryGetValue(key, out var completion))
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> StoreAsync(
        VideoPreviewCacheKey key,
        ReadOnlyMemory<byte> bytes,
        Func<string, bool>? validator = null,
        string extension = ".mp4",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.VideoPreviews, relativePath);
        return _publisher.PublishAsync(physicalPath, bytes, validator, ct);
    }

    /// <summary>
    /// Publishes an already-rendered temp file (e.g. ffmpeg's own output) directly into the cache
    /// via the same atomic move + validation path as <see cref="StoreAsync"/>, without requiring the
    /// caller to buffer the derived clip into memory first.
    /// </summary>
    public bool PublishFromTempFile(
        VideoPreviewCacheKey key,
        string sourceTempFile,
        Func<string, bool>? validator = null,
        string extension = ".mp4")
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.VideoPreviews, relativePath);
        return _publisher.PublishTempFile(sourceTempFile, physicalPath, validator);
    }

    public Task<bool> StoreStreamAsync(
        VideoPreviewCacheKey key,
        Func<Stream, Task> writeAction,
        Func<string, bool>? validator = null,
        string extension = ".mp4",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.VideoPreviews, relativePath);
        return _publisher.PublishStreamAsync(physicalPath, writeAction, validator, ct);
    }

    public bool Invalidate(VideoPreviewCacheKey key, string extension = ".mp4")
    {
        ArgumentNullException.ThrowIfNull(key);
        var relativePath = key.GetRelativePath(extension);
        var physicalPath = _paths.ResolveContainedPath(CacheFamily.VideoPreviews, relativePath);

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
            return true;
        }

        return false;
    }

    public int InvalidateAsset(Guid assetId)
    {
        var assetDir = Path.Combine(_paths.VideoPreviewsPath, assetId.ToString("N"));
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
        if (!Directory.Exists(_paths.VideoPreviewsPath))
        {
            return 0;
        }

        var count = 0;
        var pattern = $"*_v{obsoleteVersion}_*";
        foreach (var file in Directory.EnumerateFiles(_paths.VideoPreviewsPath, pattern, SearchOption.AllDirectories))
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
