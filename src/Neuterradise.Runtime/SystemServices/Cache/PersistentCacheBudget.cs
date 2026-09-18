using System.Collections.Concurrent;
using System.IO;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class PersistentCacheBudget
{
    public const long MinQuotaBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxQuotaBytes = 100L * 1024 * 1024 * 1024;
    public const long DefaultQuotaBytes = 10L * 1024 * 1024 * 1024;
    public const long RequiredDerivedBudgetBytes = 2L * 1024 * 1024 * 1024;

    private readonly CachePaths _paths;
    private readonly IClock _clock;
    private readonly object _syncLock = new();

    private readonly ConcurrentDictionary<string, int> _pinnedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _accessTimes = new(StringComparer.OrdinalIgnoreCase);

    private long _quotaBytes;

    public PersistentCacheBudget(CachePaths paths, long quotaBytes = DefaultQuotaBytes, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateQuota(quotaBytes);

        _paths = paths;
        _quotaBytes = quotaBytes;
        _clock = clock ?? new SystemClock();
    }

    public long QuotaBytes
    {
        get => Interlocked.Read(ref _quotaBytes);
        set
        {
            ValidateQuota(value);
            Interlocked.Exchange(ref _quotaBytes, value);
        }
    }

    public CachePaths Paths => _paths;

    public static void ValidateQuota(long bytes)
    {
        if (bytes < MinQuotaBytes || bytes > MaxQuotaBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                bytes,
                $"Persistent cache quota must be between {MinQuotaBytes} (1 GiB) and {MaxQuotaBytes} (100 GiB) bytes.");
        }
    }

    public IDisposable AcquireReadLease(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizePath(path);

        _pinnedPaths.AddOrUpdate(normalized, 1, (_, count) => count + 1);
        RecordAccess(normalized);

        return new CacheReadLease(this, normalized);
    }

    public bool IsPinned(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizePath(path);
        return _pinnedPaths.TryGetValue(normalized, out var count) && count > 0;
    }

    public void RecordAccess(string path, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = NormalizePath(path);
        var timestamp = now ?? _clock.UtcNow;
        _accessTimes[normalized] = timestamp;

        try
        {
            if (File.Exists(normalized))
            {
                File.SetLastAccessTimeUtc(normalized, timestamp.UtcDateTime);
            }
        }
        catch (IOException)
        {

        }
        catch (UnauthorizedAccessException)
        {

        }
    }

    public long GetTotalCurrentCacheBytes()
    {
        if (!Directory.Exists(_paths.CacheRoot))
        {
            return 0;
        }

        long total = 0;
        var dirInfo = new DirectoryInfo(_paths.CacheRoot);
        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {

            if (file.FullName.StartsWith(_paths.TempPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total += file.Length;
        }

        return total;
    }

    public bool EnsureCapacity(long incomingBytes, out long freedBytes)
    {
        freedBytes = 0;
        var currentQuota = QuotaBytes;

        if (incomingBytes > currentQuota)
        {
            return false;
        }

        lock (_syncLock)
        {
            var currentBytes = GetTotalCurrentCacheBytes();
            if (currentBytes + incomingBytes <= currentQuota)
            {
                return true;
            }

            if (!Directory.Exists(_paths.CacheRoot))
            {
                return true;
            }

            var candidates = new List<(string Path, long Length, DateTimeOffset LastAccess)>();
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
                if (!Directory.Exists(familyDir))
                {
                    continue;
                }

                var dir = new DirectoryInfo(familyDir);
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var fullPath = NormalizePath(file.FullName);
                    if (IsPinned(fullPath))
                    {
                        continue;
                    }

                    var lastAccess = _accessTimes.TryGetValue(fullPath, out var recordedTime)
                        ? recordedTime
                        : new DateTimeOffset(file.LastAccessTimeUtc, TimeSpan.Zero);

                    candidates.Add((fullPath, file.Length, lastAccess));
                }
            }

            candidates.Sort((a, b) => a.LastAccess.CompareTo(b.LastAccess));

            foreach (var (path, length, _) in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        freedBytes += length;
                        _accessTimes.TryRemove(path, out _);
                    }
                }
                catch (IOException)
                {

                }
                catch (UnauthorizedAccessException)
                {

                }

                if (currentBytes - freedBytes + incomingBytes <= currentQuota)
                {
                    return true;
                }
            }

            return currentBytes - freedBytes + incomingBytes <= currentQuota;
        }
    }

    private void ReleaseReadLease(string normalizedPath)
    {
        _pinnedPaths.AddOrUpdate(normalizedPath, 0, (_, count) => Math.Max(0, count - 1));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private sealed class CacheReadLease : IDisposable
    {
        private readonly PersistentCacheBudget _budget;
        private readonly string _normalizedPath;
        private int _disposed;

        public CacheReadLease(PersistentCacheBudget budget, string normalizedPath)
        {
            _budget = budget;
            _normalizedPath = normalizedPath;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _budget.ReleaseReadLease(_normalizedPath);
            }
        }
    }
}
