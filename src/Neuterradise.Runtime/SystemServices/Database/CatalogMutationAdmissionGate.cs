namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Process-wide admission authority for user-initiated mutations. Shutdown closes admission
/// synchronously, then waits for every already-admitted command lease to retire before writers,
/// runtime services, and finally the VaultLock are released.
/// </summary>
public sealed class CatalogMutationAdmissionGate
{
    private readonly object _gate = new();
    private bool _accepting = true;
    private int _active;
    private TaskCompletionSource _idle = CompletedSource();

    public bool IsAccepting
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    public int ActiveLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public Lease Enter(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        lock (_gate)
        {
            if (!_accepting)
            {
                throw new MutationAdmissionClosedException(operation.Trim());
            }

            if (_active == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _active++;
            return new Lease(this);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _accepting = false;
            if (_active == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }

        return cancellationToken.CanBeCanceled
            ? idle.WaitAsync(cancellationToken)
            : idle;
    }

    private void Exit()
    {
        lock (_gate)
        {
            if (_active <= 0)
            {
                throw new InvalidOperationException("Mutation admission lease count underflow.");
            }

            _active--;
            if (_active == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    public sealed class Lease : IDisposable
    {
        private CatalogMutationAdmissionGate? _owner;

        internal Lease(CatalogMutationAdmissionGate owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}

public sealed class MutationAdmissionClosedException : InvalidOperationException
{
    public MutationAdmissionClosedException(string operation)
        : base($"Mutation command '{operation}' was rejected because application shutdown has begun.")
    {
        Operation = operation;
    }

    public string Operation { get; }
}
