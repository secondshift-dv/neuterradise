using System.Collections.Concurrent;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Cache;

public sealed record RuntimeResourceKey
{
    public RuntimeResourceKey(Guid assetId, string contentFingerprint, int derivationVersion, string variantKey)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivationVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKey);

        AssetId = assetId;
        ContentFingerprint = contentFingerprint.Trim();
        DerivationVersion = derivationVersion;
        VariantKey = variantKey.Trim();
    }

    public Guid AssetId { get; }

    public string ContentFingerprint { get; }

    public int DerivationVersion { get; }

    public string VariantKey { get; }
}

public sealed class RuntimeMediaResourceCache : IDisposable
{
    private readonly RuntimeResourceBudget _budget;
    private readonly IClock _clock;
    private readonly object _syncLock = new();

    private readonly ConcurrentDictionary<RuntimeResourceKey, RuntimeCacheEntry> _entries = new();
    private readonly Timer _samplerTimer;
    private bool _disposed;

    public RuntimeMediaResourceCache(RuntimeResourceBudget? budget = null, IClock? clock = null)
    {
        _budget = budget ?? new RuntimeResourceBudget();
        _clock = clock ?? new SystemClock();

        _samplerTimer = new Timer(
            _ => PerformMaintenancePass(GetUtcNow()),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    public RuntimeResourceBudget Budget => _budget;

    public int Count => _entries.Count;

    public bool TryGet(RuntimeResourceKey key, out RuntimeResourceLease? lease)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.TryPin())
            {
                entry.LastAccessedAtUtc = GetUtcNow();
                lease = new RuntimeResourceLease(entry.Resource, entry.Unpin);
                return true;
            }
        }

        lease = null;
        return false;
    }

    public bool TryGetImage(RuntimeResourceKey key, out RuntimeResourceLease? lease)
    {
        if (TryGet(key, out lease) && lease is { Resource: DecodedImage })
        {
            return true;
        }

        lease?.Dispose();
        lease = null;
        return false;
    }

    public bool TryGetModelData(RuntimeResourceKey key, out RuntimeResourceLease? lease)
    {
        if (TryGet(key, out lease) && lease is { Resource: ReadOnlyMemory<byte> })
        {
            return true;
        }

        lease?.Dispose();
        lease = null;
        return false;
    }

    public bool PutImage(RuntimeResourceKey key, DecodedImage image, bool isPinned = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(image);
        return PutInternal(key, image, Math.Max(1, image.Bytes), isPinned);
    }

    public bool PutModelData(RuntimeResourceKey key, ReadOnlyMemory<byte> modelData, long estimatedBytes, bool isPinned = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        RuntimeResourceBudget.EstimateModelBytes(estimatedBytes);

        return PutInternal(key, modelData, estimatedBytes, isPinned);
    }

    public IDisposable Pin(RuntimeResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_entries.TryGetValue(key, out var entry))
        {
            return entry.TryPin() ? new ResourcePin(entry) : new NoOpDisposable();
        }

        return new NoOpDisposable();
    }

    public bool Invalidate(RuntimeResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                _budget.RemoveTrackedBytes(entry.EstimatedBytes);
                entry.Retire();
                return true;
            }
        }

        return false;
    }

    public int InvalidateAsset(Guid assetId)
    {
        lock (_syncLock)
        {
            var count = 0;
            foreach (var (key, _) in _entries)
            {
                if (key.AssetId == assetId && _entries.TryRemove(key, out var removed))
                {
                    _budget.RemoveTrackedBytes(removed.EstimatedBytes);
                    removed.Retire();
                    count++;
                }
            }

            return count;
        }
    }

    public void Clear()
    {
        lock (_syncLock)
        {
            ClearCore();
        }
    }

    public void PerformMaintenancePass(DateTimeOffset now)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            var idleThreshold = now - RuntimeResourceBudget.IdleEvictionThreshold;
            var unpinned = new List<RuntimeCacheEntry>();

            foreach (var (_, entry) in _entries)
            {
                if (!entry.IsPinned)
                {
                    if (entry.LastAccessedAtUtc <= idleThreshold)
                    {
                        if (_entries.TryRemove(entry.Key, out var removed))
                        {
                            _budget.RemoveTrackedBytes(removed.EstimatedBytes);
                            removed.Retire();
                        }
                    }
                    else
                    {
                        unpinned.Add(entry);
                    }
                }
            }

            if (_budget.IsAboveSoftBudget)
            {
                unpinned.Sort((a, b) => a.LastAccessedAtUtc.CompareTo(b.LastAccessedAtUtc));

                foreach (var entry in unpinned)
                {
                    if (!_budget.IsAboveSoftBudget)
                    {
                        break;
                    }

                    if (_entries.TryRemove(entry.Key, out var removed))
                    {
                        _budget.RemoveTrackedBytes(removed.EstimatedBytes);
                        removed.Retire();
                    }
                }
            }
        }
    }

    private bool PutInternal(RuntimeResourceKey key, object resource, long estimatedBytes, bool isPinned)
    {
        ValidateForbiddenTypes(resource);

        lock (_syncLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryRemove(key, out var oldEntry))
            {
                _budget.RemoveTrackedBytes(oldEntry.EstimatedBytes);
                oldEntry.Retire();
            }

            if (_budget.TotalCommittedBytes + estimatedBytes >= _budget.SoftBudgetBytes)
            {
                EvictLruForCapacity(estimatedBytes);
            }

            if (!isPinned && (_budget.TotalCommittedBytes + estimatedBytes > _budget.HardBudgetBytes))
            {
                return false;
            }

            var entry = new RuntimeCacheEntry(key, resource, estimatedBytes, GetUtcNow())
            {
                IsProtected = isPinned,
            };

            _entries[key] = entry;
            _budget.AddTrackedBytes(estimatedBytes);
            return true;
        }
    }

    private void EvictLruForCapacity(long neededBytes)
    {
        var unpinned = new List<RuntimeCacheEntry>();
        foreach (var (_, entry) in _entries)
        {
            if (!entry.IsPinned)
            {
                unpinned.Add(entry);
            }
        }

        unpinned.Sort((a, b) => a.LastAccessedAtUtc.CompareTo(b.LastAccessedAtUtc));

        foreach (var entry in unpinned)
        {
            if (_budget.TotalCommittedBytes + neededBytes <= _budget.SoftBudgetBytes)
            {
                break;
            }

            if (_entries.TryRemove(entry.Key, out var removed))
            {
                _budget.RemoveTrackedBytes(removed.EstimatedBytes);
                removed.Retire();
            }
        }
    }

    private static void ValidateForbiddenTypes(object resource)
    {
        // No UI element or composition visual may ever live in shared cache; image resources are opaque
        // renderer handles wrapped in DecodedImage and are released by eviction.
        var typeName = resource.GetType().FullName ?? string.Empty;
        if (typeName.StartsWith("Microsoft.UI.Xaml.Controls.", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.UI.Composition.", StringComparison.Ordinal)
            || typeName.StartsWith("System.Windows.", StringComparison.Ordinal)
            || resource is SkiaSharp.SKSurface)
        {
            throw new ArgumentException(
                $"Forbidden runtime cache object: instances of '{resource.GetType().FullName}' (UIElement/Visual) must never be retained in shared cache.",
                nameof(resource));
        }
    }

    private static void DisposeResource(object resource)
    {
        if (resource is DecodedImage decoded && decoded.Image is IDisposable ownedImage)
        {
            ownedImage.Dispose();
            return;
        }

        if (resource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private DateTimeOffset GetUtcNow() => _clock.UtcNow;

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearCore();
        }

        _samplerTimer.Dispose();
    }

    private void ClearCore()
    {
        foreach (var (_, entry) in _entries)
        {
            _budget.RemoveTrackedBytes(entry.EstimatedBytes);
            entry.Retire();
        }
        _entries.Clear();
    }

    private sealed class RuntimeCacheEntry
    {
        private readonly Lock _lifetime = new();
        private bool _retired;
        public RuntimeCacheEntry(RuntimeResourceKey key, object resource, long estimatedBytes, DateTimeOffset accessedAt)
        {
            Key = key;
            Resource = resource;
            EstimatedBytes = estimatedBytes;
            LastAccessedAtUtc = accessedAt;
        }

        public RuntimeResourceKey Key { get; }

        public object Resource { get; }

        public long EstimatedBytes { get; }

        public DateTimeOffset LastAccessedAtUtc { get; set; }

        public int PinCount;

        public bool IsProtected { get; init; }

        public bool IsPinned => IsProtected || Volatile.Read(ref PinCount) > 0;

        public bool TryPin()
        {
            lock (_lifetime)
            {
                if (_retired)
                {
                    return false;
                }

                PinCount++;
                return true;
            }
        }

        public void Unpin()
        {
            lock (_lifetime)
            {
                if (PinCount > 0 && --PinCount == 0 && _retired)
                {
                    DisposeResource(Resource);
                }
            }
        }

        public void Retire()
        {
            lock (_lifetime)
            {
                if (_retired)
                {
                    return;
                }

                _retired = true;
                if (PinCount == 0)
                {
                    DisposeResource(Resource);
                }
            }
        }
    }

    private sealed class ResourcePin : IDisposable
    {
        private readonly RuntimeCacheEntry _entry;
        private int _disposed;

        public ResourcePin(RuntimeCacheEntry entry)
        {
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entry.Unpin();
            }
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public sealed class RuntimeResourceLease(object resource, Action release) : IDisposable
{
    private Action? _release = release;
    public object Resource { get; } = resource;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
