using System.Diagnostics;
using System.IO;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Recovery;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class AppBootstrapper
{
    public const string DiagnosticCapability = "lifecycle.startup";

    private readonly VaultPaths _paths;
    private readonly StructuredDiagnostics? _diagnostics;
    private readonly AppStatePaths? _appState;
    private readonly Guid _startupOperationId = Guid.NewGuid();
    private readonly List<StartupState> _stateHistory = [StartupState.ProcessStart];
    private int _started;

    public AppBootstrapper(VaultPaths paths, StructuredDiagnostics? diagnostics = null, AppStatePaths? appState = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _diagnostics = diagnostics;
        _appState = appState;
    }

    public StartupState State { get; private set; } = StartupState.ProcessStart;

    public IReadOnlyList<StartupState> StateHistory => _stateHistory.AsReadOnly();

    public Guid StartupOperationId => _startupOperationId;

    /// <summary>Raised after each bounded startup stage is durably/observably completed.</summary>
    public event EventHandler<StartupStageChangedEventArgs>? StageChanged;

    /// <summary>
    /// Builds the production bootstrapper for an explicitly approved Vault root. The Vault is supplied
    /// by configuration or onboarding; it is never derived from the application directory, the working
    /// directory or a repository layout. Diagnostics are written under AppStateRoot.
    /// </summary>
    public static AppBootstrapper CreateProduction(VaultPaths paths, AppStatePaths appState)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(appState);

        return new AppBootstrapper(paths, StructuredDiagnostics.CreateProduction(appState), appState);
    }

    public async Task<StartupResult> BootstrapAsync(
        Func<BootstrapContext, CancellationToken, Task>? onReady = null,
        Func<BootstrapContext, CancellationToken, Task>? prewarm = null,
        Func<CancellationToken, Task>? rollbackActivatedRuntime = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("An AppBootstrapper instance can run only once.");
        }

        VaultLock? vaultLock = null;
        BootstrapContext? context = null;
        var sessionId = Guid.NewGuid();
        var sessionGeneration = Guid.NewGuid();
        UpdateStateStore? updateStore = null;
        var updateRecoveryResult = UpdateStartupRecoveryResult.NoAction();
        var runtimeActivationBegan = false;
        var timings = new List<StartupStageTiming>();
        var stageStartedAt = Stopwatch.GetTimestamp();

        void CompleteStage(StartupStage stage)
        {
            var completedAt = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(stageStartedAt, completedAt);
            timings.Add(new StartupStageTiming(stage, elapsed));
            stageStartedAt = completedAt;
            StageChanged?.Invoke(this, new StartupStageChangedEventArgs(stage, State, elapsed));
        }

        try
        {
            TransitionTo(StartupState.Bootstrapping);
            cancellationToken.ThrowIfCancellationRequested();

            CompleteStage(StartupStage.ProcessStartToRootResolved);
            _paths.EnsureStructuralDirectories();

            TransitionTo(StartupState.OpeningStorage);
            vaultLock = await VaultLock.AcquireAsync(_paths, cancellationToken).ConfigureAwait(true);

            // X23: acquiring writable Vault authority makes this session UNCLEAN before any catalog
            // or recovery mutation can begin. Clean is written only by ordered shutdown using the
            // same SessionId/Generation after every mutation authority has stopped.
            if (_appState is not null)
            {
                _appState.EnsureStructuralDirectories();
                await new SessionMarkerStore(_appState)
                    .WriteUncleanAsync(
                        sessionId,
                        sessionGeneration,
                        Array.Empty<Guid>(),
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            // Update replacement recovery is reconciled before any catalog mutation-capable operation.
            // HandoffPending is not promoted merely because the application started: filesystem and
            // recovery-journal evidence must prove that the replacement is installed first.
            if (_appState is not null)
            {
                var install = InstallPaths.CreateProduction();
                updateStore = new UpdateStateStore(_appState);
                var updateRecovery = new UpdateStartupRecovery(
                    _appState,
                    install,
                    _paths.Root,
                    updateStore);
                updateRecoveryResult = await updateRecovery.ReconcileAsync(cancellationToken).ConfigureAwait(true);
                if (!updateRecoveryResult.CanContinue)
                {
                    throw new InvalidOperationException(
                        updateRecoveryResult.SafeError ?? "Application startup is blocked because update recovery is required.");
                }
            }

            TransitionTo(StartupState.MigratingDatabase);
            var catalog = new CatalogDb(_paths);

            // This is the first boundary after which the new binary may mutate catalog state. It is
            // published only after replacement evidence has been validated, never from HandoffPending alone.
            if (updateStore is not null
                && updateRecoveryResult.ReplacementInstalled
                && updateRecoveryResult.OperationId is Guid updateOperationId)
            {
                await updateStore.PublishCatalogWriteStartedAsync(
                    updateOperationId,
                    updateRecoveryResult.BackupPath,
                    cancellationToken).ConfigureAwait(true);
            }

            var migration = await catalog.InitializeAsync(
                    () => CompleteStage(StartupStage.RootResolvedToDbOpened),
                    cancellationToken)
                .ConfigureAwait(true);
            CompleteStage(StartupStage.DbOpenedToMigrationsComplete);

            TransitionTo(StartupState.Recovering);
            var recovery = await new RecoveryCoordinator(catalog, _diagnostics).RecoverAsync(cancellationToken)
                .ConfigureAwait(true);
            if (recovery.HasFatal || recovery.BlocksWritableStartup)
            {
                throw new RecoveryFailedException(recovery);
            }
            CompleteStage(StartupStage.MigrationsCompleteToRecoveryComplete);

            var gate = new CriticalIntegrityGate(catalog, _diagnostics);
            var gateResult = await gate.EvaluateAsync(cancellationToken).ConfigureAwait(true);
            if (!gateResult.Passed)
            {
                throw new CriticalIntegrityException(gateResult);
            }
            CompleteStage(StartupStage.RecoveryCompleteToCriticalGateComplete);

            TransitionTo(StartupState.Prewarming);
            context = new BootstrapContext(
                _paths,
                vaultLock,
                catalog,
                recovery,
                migration.CurrentVersion,
                sessionId,
                sessionGeneration,
                StartupState.Prewarming);
            vaultLock = null;

            if (prewarm is not null)
            {
                runtimeActivationBegan = true;
                await prewarm(context, cancellationToken).ConfigureAwait(true);
            }
            CompleteStage(StartupStage.CriticalGateCompleteToPrewarmComplete);

            // The Ready state is published only after the composed ready callback returns. A callback
            // failure/cancellation therefore cannot leave an apparently-ready context behind.
            if (onReady is not null)
            {
                runtimeActivationBegan = true;
                await onReady(context, cancellationToken).ConfigureAwait(true);
            }
            CompleteStage(StartupStage.PrewarmCompleteToMainWindowShown);

            context.State = StartupState.Ready;
            TransitionTo(StartupState.Ready);

            // Completed is evidence-based: only a replacement that crossed the catalog-write boundary
            // and then reached Ready can become terminally successful.
            if (updateStore is not null && updateRecoveryResult.OperationId is Guid completedOperationId)
            {
                var currentUpdateState = await updateStore.LoadAsync(cancellationToken).ConfigureAwait(true);
                if (currentUpdateState is not null
                    && currentUpdateState.OperationId == completedOperationId
                    && currentUpdateState.Cutoff == UpdateStartupCutoff.CatalogWriteStarted)
                {
                    await updateStore.PublishCompletedAsync(completedOperationId, cancellationToken).ConfigureAwait(true);
                }
            }

            return StartupResult.Ready(context, new StartupTimingReport(timings.AsReadOnly()));
        }
        catch (Exception ex)
        {
            var failure = ex;
            var preserveWritableAuthority = false;

            if (context is not null && runtimeActivationBegan)
            {
                if (rollbackActivatedRuntime is null)
                {
                    preserveWritableAuthority = true;
                    failure = new AggregateException(
                        ex,
                        new InvalidOperationException(
                            "Startup activated mutation-capable runtime without a registered rollback authority."));
                }
                else
                {
                    try
                    {
                        await rollbackActivatedRuntime(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        preserveWritableAuthority = true;
                        failure = new AggregateException(ex, rollbackException);
                    }
                }
            }

            if (!preserveWritableAuthority)
            {
                if (context is not null)
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                else if (vaultLock is not null)
                {
                    await vaultLock.DisposeAsync().ConfigureAwait(false);
                }
            }

            TransitionTo(StartupState.StartupFailed);

            var errorCode = preserveWritableAuthority
                ? "STARTUP_ROLLBACK_UNSAFE"
                : ex switch
            {
                VaultLockUnavailableException => "VAULT_LOCK_UNAVAILABLE",
                OperationCanceledException => "STARTUP_CANCELLED",
                MigrationIntegrityException => "DATABASE_MIGRATION_FAILED",
                MigrationApplyException => "DATABASE_MIGRATION_FAILED",
                RecoveryFailedException => "RECOVERY_FAILED",
                CriticalIntegrityException => "CRITICAL_INTEGRITY_VIOLATION",
                _ when CatalogDb.IsDatabaseFailure(ex) => "DATABASE_MIGRATION_FAILED",
                UnauthorizedAccessException => "VAULT_ROOT_ACCESS_DENIED",
                IOException => "VAULT_ROOT_IO_FAILURE",
                _ => "STARTUP_FAILURE",
            };

            _diagnostics?.Write(new DiagnosticEvent(
                DateTimeOffset.UtcNow,
                ex is OperationCanceledException
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error,
                DiagnosticCapability,
                errorCode,
                SafeErrorDetail: $"{failure.GetType().Name}: {failure.Message}",
                OperationId: _startupOperationId,
                StateTransition: $"{StartupState.StartupFailed}"));

            var timingReport = new StartupTimingReport(timings.AsReadOnly());
            return preserveWritableAuthority && context is not null
                ? StartupResult.FailedPreservingAuthority(errorCode, failure, context, timingReport)
                : StartupResult.Failed(errorCode, failure, timingReport);
        }
    }

    private void TransitionTo(StartupState state)
    {
        var previous = State;
        State = state;
        _stateHistory.Add(state);

        _diagnostics?.Write(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            state == StartupState.StartupFailed ? DiagnosticSeverity.Error : DiagnosticSeverity.Information,
            DiagnosticCapability,
            state == StartupState.StartupFailed ? "STARTUP_FAILED" : "STARTUP_STATE_TRANSITION",
            OperationId: _startupOperationId,
            StateTransition: $"{previous}->{state}"));
    }
}
