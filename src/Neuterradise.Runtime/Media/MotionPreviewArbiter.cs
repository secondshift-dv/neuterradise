using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Neuterradise.App.Media;

public sealed record MotionLeaseKey(
    Guid? VaultId,
    long RouteGeneration,
    Guid EntityId,
    string Role,
    long Generation)
{
    public static MotionLeaseKey Create(Guid entityId, string role, Guid? vaultId = null, long routeGeneration = 0)
    {
        return new MotionLeaseKey(vaultId, routeGeneration, entityId, role, Environment.TickCount64);
    }
}

public interface IMotionLease : IDisposable
{
    MotionLeaseKey Key { get; }
    bool IsActive { get; }
    void Release();
}

public sealed class MotionPreviewArbiter
{
    public const int MaxActiveHoverMotion = 1;
    public const int DefaultDwellMs = 350;
    public const int ScrollCooldownMs = 150;
    public const int DefaultHoverClipDurationSeconds = 5;

    private static readonly Lazy<MotionPreviewArbiter> _instance = new(() => new MotionPreviewArbiter());
    public static MotionPreviewArbiter Instance => _instance.Value;

    private readonly object _lock = new();
    private ActiveLease? _currentLease;
    private long _lastScrollTimestampMs;
    private long _generation;

    public event EventHandler? ActiveLeaseChanged;

    public MotionPreviewArbiter()
    {
        _lastScrollTimestampMs = 0;
    }

    public bool HasActiveLease
    {
        get
        {
            lock (_lock)
            {
                return _currentLease is not null && _currentLease.IsActive;
            }
        }
    }

    public MotionLeaseKey? ActiveKey
    {
        get
        {
            lock (_lock)
            {
                return _currentLease?.Key;
            }
        }
    }

    public bool CanAcquireAfterScroll()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - Interlocked.Read(ref _lastScrollTimestampMs)) * 1000 / Stopwatch.Frequency;
        return elapsedMs >= ScrollCooldownMs;
    }

    public void NotifyScroll()
    {
        Interlocked.Exchange(ref _lastScrollTimestampMs, Stopwatch.GetTimestamp());
        StopAll();
    }

    public IMotionLease? TryAcquire(Guid entityId, string role, Action? onEvicted = null)
    {
        if (!CanAcquireAfterScroll())
        {
            return null;
        }

        ActiveLease? oldLease = null;
        ActiveLease newLease;

        lock (_lock)
        {
            var gen = Interlocked.Increment(ref _generation);
            var key = new MotionLeaseKey(null, 0, entityId, role, gen);

            if (_currentLease is not null)
            {
                oldLease = _currentLease;
                _currentLease = null;
            }

            newLease = new ActiveLease(this, key, onEvicted);
            _currentLease = newLease;
        }

        if (oldLease is not null)
        {
            oldLease.Evict();
        }

        ActiveLeaseChanged?.Invoke(this, EventArgs.Empty);
        return newLease;
    }

    public void StopAll()
    {
        ActiveLease? leaseToStop = null;
        lock (_lock)
        {
            if (_currentLease is not null)
            {
                leaseToStop = _currentLease;
                _currentLease = null;
            }
        }

        if (leaseToStop is not null)
        {
            leaseToStop.Evict();
            ActiveLeaseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseInternal(ActiveLease lease)
    {
        bool changed = false;
        lock (_lock)
        {
            if (ReferenceEquals(_currentLease, lease))
            {
                _currentLease = null;
                changed = true;
            }
        }

        if (changed)
        {
            ActiveLeaseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ActiveLease : IMotionLease
    {
        private readonly MotionPreviewArbiter _arbiter;
        private readonly Action? _onEvicted;
        private int _state; // 0 = active, 1 = released/evicted

        public ActiveLease(MotionPreviewArbiter arbiter, MotionLeaseKey key, Action? onEvicted)
        {
            _arbiter = arbiter;
            Key = key;
            _onEvicted = onEvicted;
        }

        public MotionLeaseKey Key { get; }

        public bool IsActive => Volatile.Read(ref _state) == 0;

        public void Release()
        {
            if (Interlocked.Exchange(ref _state, 1) == 0)
            {
                _arbiter.ReleaseInternal(this);
            }
        }

        public void Evict()
        {
            if (Interlocked.Exchange(ref _state, 1) == 0)
            {
                try
                {
                    _onEvicted?.Invoke();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("MotionPreviewArbiter eviction handler error: {0}", ex);
                }
            }
        }

        public void Dispose()
        {
            Release();
        }
    }
}
