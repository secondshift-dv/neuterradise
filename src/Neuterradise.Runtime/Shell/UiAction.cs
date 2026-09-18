using System.Diagnostics;

namespace Neuterradise.App.Shell;

/// <summary>
/// Owns latest-value asynchronous persistence. One write runs at a time; changes arriving while it
/// runs replace the pending value, so the durable state always converges to the latest UI intent.
/// </summary>
public sealed class LatestValueAction<T> : IDisposable
{
    private readonly Func<T, CancellationToken, Task> _write;
    private readonly Action<Exception>? _onFailure;
    private readonly Action<T>? _onSuccessValue;
    private readonly Action<T, Exception>? _onFailureValue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sync = new();
    private T? _pending;
    private bool _hasPending;
    private bool _running;
    private bool _disposed;

    public LatestValueAction(Func<T, CancellationToken, Task> write, Action<Exception>? onFailure = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _onFailure = onFailure;
    }

    public LatestValueAction(
        Func<T, CancellationToken, Task> write,
        Action<T> onSuccess,
        Action<T, Exception> onFailure)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _onSuccessValue = onSuccess ?? throw new ArgumentNullException(nameof(onSuccess));
        _onFailureValue = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
    }

    public void Submit(T value)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = value;
            _hasPending = true;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        TaskObserver.Observe(DrainAsync(), $"{nameof(LatestValueAction<T>)}<{typeof(T).Name}>");
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            T value;
            lock (_sync)
            {
                if (_disposed || !_hasPending)
                {
                    _running = false;
                    return;
                }

                value = _pending!;
                _hasPending = false;
            }

            try
            {
                await _write(value, _lifetime.Token).ConfigureAwait(false);
                _onSuccessValue?.Invoke(value);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Latest-value action failed: {0}", exception);
                bool hasNewerValue;
                lock (_sync)
                {
                    hasNewerValue = _hasPending;
                }

                if (!hasNewerValue && (_onFailure is not null || _onFailureValue is not null))
                {
                    try
                    {
                        _onFailure?.Invoke(exception);
                        _onFailureValue?.Invoke(value, exception);
                    }
                    catch (Exception reportFailure)
                    {
                        Trace.TraceError("Latest-value failure reporting failed: {0}", reportFailure);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hasPending = false;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
