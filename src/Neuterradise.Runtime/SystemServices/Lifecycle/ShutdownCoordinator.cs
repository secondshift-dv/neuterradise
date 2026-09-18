using System.Diagnostics;
using System.Text.Json;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class ShutdownCoordinator
{
    public static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(10);
    private readonly BootstrapContext _context;
    private readonly Action? _stopAcceptingCommands;
    private readonly Func<TimeSpan, CancellationToken, Task<SchedulerShutdownReport>>? _shutdownSchedulerAsync;
    private readonly IReadOnlyList<IAsyncDisposable> _services;
    private readonly List<StartupState> _stateHistory = [StartupState.Ready];
    private readonly AppStatePaths? _appState;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly object _shutdownGate = new();
    private Task<ShutdownReport>? _shutdown;
    private int _markerWritten;

    public ShutdownCoordinator(BootstrapContext context, Action? stopAcceptingCommands = null,
        Func<TimeSpan, CancellationToken, Task<SchedulerShutdownReport>>? shutdownSchedulerAsync = null,
        IEnumerable<IAsyncDisposable>? services = null, AppStatePaths? appState = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stopAcceptingCommands = stopAcceptingCommands;
        _shutdownSchedulerAsync = shutdownSchedulerAsync;
        _services = (services ?? []).ToArray();
        _appState = appState;
    }

    public StartupState State { get; private set; } = StartupState.Ready;
    public Guid SessionId => _sessionId;
    public IReadOnlyList<StartupState> StateHistory => _stateHistory.AsReadOnly();

    public Task<ShutdownReport> ShutdownAsync(TimeSpan? budget = null, CancellationToken cancellationToken = default)
    {
        var effectiveBudget = budget ?? DefaultShutdownBudget;
        if (effectiveBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        lock (_shutdownGate)
        {
            // One coordinator owns exactly one ordered shutdown pipeline. Later callers observe the
            // same task instead of racing a second scheduler/service/context retirement sequence.
            return _shutdown ??= ShutdownCoreAsync(effectiveBudget, cancellationToken);
        }
    }

    private async Task<ShutdownReport> ShutdownCoreAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        TransitionTo(StartupState.ShuttingDown);
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        SchedulerShutdownReport? schedulerReport = null;
        var timedOut = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(budget);
        try
        {
            try
            {
                _stopAcceptingCommands?.Invoke();
            }
            catch (Exception e)
            {
                failures.Add("COMMAND_QUIESCE_FAILED:" + e.GetType().Name);
            }

            if (_shutdownSchedulerAsync is not null)
            {
                try
                {
                    schedulerReport = await _shutdownSchedulerAsync(Remaining(budget, stopwatch.Elapsed), bounded.Token)
                        .WaitAsync(bounded.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (bounded.IsCancellationRequested)
                {
                    timedOut = true;
                    failures.Add("SCHEDULER_SHUTDOWN_TIMEOUT");
                }
                catch (Exception e)
                {
                    failures.Add("SCHEDULER_SHUTDOWN_FAILED:" + e.GetType().Name);
                }
            }

            foreach (var service in _services)
            {
                try
                {
                    await service.DisposeAsync().AsTask().WaitAsync(bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (bounded.IsCancellationRequested)
                {
                    timedOut = true;
                    failures.Add("SERVICE_DISPOSAL_TIMEOUT");
                }
                catch (Exception e)
                {
                    failures.Add("SERVICE_DISPOSAL_FAILED:" + e.GetType().Name);
                }
            }

            if (!timedOut && failures.Count == 0)
            {
                await WriteCleanMarkerAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                failures.Add("CLEAN_MARKER_NOT_WRITTEN");
            }
        }
        finally
        {
            await _context.DisposeAsync().ConfigureAwait(false);
            stopwatch.Stop();
            TransitionTo(StartupState.Stopped);
        }

        return new ShutdownReport(
            timedOut,
            schedulerReport?.NonterminalJobsLeftForRestart ?? 0,
            schedulerReport?.CancelledDuringShutdown ?? 0,
            schedulerReport?.ProfilingWorkerReleased ?? false,
            stopwatch.Elapsed,
            failures.AsReadOnly(),
            _sessionId,
            _markerWritten != 0);
    }

    private async Task WriteCleanMarkerAsync(CancellationToken cancellationToken)
    {
        if (_appState is null)
        {
            return;
        }

        _appState.EnsureStructuralDirectories();
        var store = new SessionMarkerStore(_appState);
        await store.WriteCleanAsync(_sessionId, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _markerWritten, 1);
    }

    private static TimeSpan Remaining(TimeSpan budget, TimeSpan elapsed) =>
        budget - elapsed > TimeSpan.Zero ? budget - elapsed : TimeSpan.FromTicks(1);

    private void TransitionTo(StartupState state)
    {
        State = state;
        _stateHistory.Add(state);
    }
}

public sealed record ShutdownReport(
    bool TimedOut,
    int NonterminalJobsLeftForRestart,
    long CancelledDuringShutdown,
    bool ProfilingWorkerReleased,
    TimeSpan Elapsed,
    IReadOnlyList<string> Failures,
    Guid SessionId,
    bool CleanMarkerWritten)
{
    public bool PreservedUnfinishedWork => CancelledDuringShutdown == 0;
}
