using System.Collections.Concurrent;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Process-local serialization authority for all mutation actors that can advance, cancel,
/// publish, recover, or roll back one ImportUnit. The lease is re-entrant across the current
/// async flow so layered authorities can share the same unit boundary without deadlocking.
/// </summary>
public sealed class ImportUnitMutationCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly AsyncLocal<HashSet<Guid>?> _owned = new();
    private int _disposed;

    public async ValueTask<Lease> EnterAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("An ImportUnit mutation lease requires a stable identifier.", nameof(unitId));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var inherited = _owned.Value;
        if (inherited?.Contains(unitId) == true)
        {
            return Lease.Nested;
        }

        var gate = _gates.GetOrAdd(unitId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (Volatile.Read(ref _disposed) != 0)
        {
            gate.Release();
            throw new ObjectDisposedException(nameof(ImportUnitMutationCoordinator));
        }

        var next = inherited is null ? new HashSet<Guid>() : new HashSet<Guid>(inherited);
        next.Add(unitId);
        _owned.Value = next;
        return new Lease(this, unitId, gate, inherited);
    }

    private void Exit(Guid unitId, SemaphoreSlim gate, HashSet<Guid>? inherited)
    {
        var current = _owned.Value;
        if (current is null || !current.Contains(unitId))
        {
            throw new InvalidOperationException("ImportUnit mutation lease ownership was lost before release.");
        }

        _owned.Value = inherited;
        gate.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
        _owned.Value = null;
    }

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        internal static Lease Nested { get; } = new();

        private ImportUnitMutationCoordinator? _owner;
        private readonly Guid _unitId;
        private readonly SemaphoreSlim? _gate;
        private readonly HashSet<Guid>? _inherited;

        private Lease()
        {
        }

        internal Lease(ImportUnitMutationCoordinator owner, Guid unitId, SemaphoreSlim gate, HashSet<Guid>? inherited)
        {
            _owner = owner;
            _unitId = unitId;
            _gate = gate;
            _inherited = inherited;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner.Exit(_unitId, _gate!, _inherited);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
