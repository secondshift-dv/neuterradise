using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Lifecycle;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.App.Ui;

namespace Neuterradise.App;

/// <summary>
/// Uno Skia Desktop entry. Startup order is unchanged from the WPF product: roots → configuration and
/// language → crash diagnostics → approved Vault root → bootstrapper (lock, migrations, recovery) →
/// runtime prewarm (ResourceGovernor, caches, Presentation registry + compiled plans) → shell.
/// Shutdown stays controlled: placement and scale flush, the import finalizer stops at a durable
/// checkpoint, then the runtime and Vault lock are released in order.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private InstallPaths? _install;
    private AppStatePaths? _appState;
    private AppConfigurationStore? _configuration;
    private HttpClient? _updateHttpClient;
    private UpdateCoordinator? _updateCoordinator;
    private CrashDiagnostics? _crash;
    private AppBootstrapper? _bootstrapper;
    private BootstrapContext? _context;
    private ProductionRuntimeRegistry? _runtime;
    private PresentationRuntime? _presentation;
    private ShutdownCoordinator? _shutdown;
    private ImportActivityService? _importActivity;
    private ImportFinalizer? _finalizer;
    private UnoWindowPlacement? _placement;
    private UiScaleService? _uiScale;
    private DerivedImageLoader? _derivedImages;
    private ResourceGovernor? _resourceGovernor;
    private int _bootstrapAttemptActive;
    private int _shutdownStarted;
    private bool _allowClose;
    private readonly WindowStateProjection _windowState = new();
    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = ProductIdentity.DisplayName };
        var queue = DispatcherQueue.GetForCurrentThread();
        UiDispatch.Install(new DispatcherQueueSyncContext(queue));
        SynchronizationContext.SetSynchronizationContext(UiDispatch.Context);
        ThemeRuntime.Install(new ThemeRuntime());
        _window.Content = StartupScreens.Splash(UI.T("Startup.Opening", "Opening your library…"));
        _window.AppWindow.Closing += OnClosing;
        _uiSettings = new Windows.UI.ViewManagement.UISettings();
        _window.Activated += (_, e) =>
        {
            ApplyWindowState(_windowState.SetActive(e.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated));
            RefreshSystemReducedMotion();
        };
        _window.Activate();
        ObserveStartup(StartAsync(), "App.StartAsync");
    }

    private async Task StartAsync()
    {
        _install = InstallPaths.CreateProduction();
        _appState = AppStatePaths.CreateProduction();
        _configuration = new AppConfigurationStore(_appState, ThemeLoader.FallbackThemeId, DensityMode.Comfortable.ToString());
        try
        {
            var initial = await _configuration.LoadAsync().ConfigureAwait(true);
            SurfaceText.ApplyLanguage(initial.Configuration.UiLanguage);
        }
        catch (Exception)
        {
            SurfaceText.ApplyLanguage(AppConfiguration.EnglishLanguage);
        }

        _crash = CrashDiagnostics.CreateProduction(_appState, CreateCrashContext, RequestControlledShutdown);
        _crash.Install();
        UnhandledException += (_, e) => e.Handled = _crash?.HandleUiThreadException(e.Exception) ?? false;

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    private async Task BootstrapLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _bootstrapAttemptActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await CleanupPartialStartupAsync().ConfigureAwait(true);

            var resolution = await ResolveVaultRootAsync().ConfigureAwait(true);
            if (resolution.VaultPaths is null)
            {
                if (resolution.ErrorCode is not null)
                {
                    ShowFailure(resolution.ErrorCode, null, resolution.PreservedCopyPath);
                }

                return;
            }

            _window!.Content = StartupScreens.Splash(UI.T("Startup.Opening", "Opening your library…"));
            _bootstrapper = AppBootstrapper.CreateProduction(resolution.VaultPaths, _appState!);
            var result = await _bootstrapper.BootstrapAsync(
                onReady: ShowShellAsync,
                prewarm: PrewarmAsync,
                rollbackActivatedRuntime: RollbackActivatedStartupAsync,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            if (result.IsReady)
            {
                _context = result.Context;
                return;
            }

            if (result.PreservesWritableAuthority)
            {
                _context = result.Context;
                ShowFailure(result.ErrorCode, result.Exception);
                return;
            }

            await CleanupPartialStartupAsync().ConfigureAwait(true);
            ShowFailure(result.ErrorCode, result.Exception);
        }
        catch (Exception exception)
        {
            await CleanupPartialStartupAsync().ConfigureAwait(true);
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
            ShowFailure(StartupRecoveryViewModel.GenericErrorCode, exception);
        }
        finally
        {
            Volatile.Write(ref _bootstrapAttemptActive, 0);
        }
    }

    private async Task RollbackActivatedStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HoverVideoCoordinator.Shared.StopAll();

        _shutdown = null;

        _updateCoordinator?.Dispose();
        _updateCoordinator = null;
        _updateHttpClient?.Dispose();
        _updateHttpClient = null;

        _uiScale = null;
        _placement?.Dispose();
        _placement = null;

        if (_finalizer is not null)
        {
            await _finalizer.DisposeAsync().ConfigureAwait(true);
            _finalizer = null;
        }

        if (_importActivity is not null)
        {
            await _importActivity.DisposeAsync().ConfigureAwait(true);
            _importActivity = null;
        }

        _presentation = null;

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(true);
            _runtime = null;
        }

        if (_resourceGovernor is not null)
        {
            _resourceGovernor.TrimRequested -= OnTrimRequested;
        }

        _derivedImages?.Dispose();
        _derivedImages = null;
        _resourceGovernor?.Dispose();
        _resourceGovernor = null;
    }

    private async Task CleanupPartialStartupAsync()
    {
        HoverVideoCoordinator.Shared.StopAll();

        if (_finalizer is not null)
        {
            try
            {
                await _finalizer.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup import finalizer disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _finalizer = null;
            }
        }

        if (_importActivity is not null)
        {
            try
            {
                await _importActivity.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup import activity disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _importActivity = null;
            }
        }

        _placement?.Dispose();
        _placement = null;
        _uiScale = null;
        _shutdown = null;

        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Partial-startup runtime disposal failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _runtime = null;
            }
        }

        if (_resourceGovernor is not null)
        {
            _resourceGovernor.TrimRequested -= OnTrimRequested;
        }

        _derivedImages?.Dispose();
        _derivedImages = null;
        _resourceGovernor?.Dispose();
        _resourceGovernor = null;
        _presentation = null;
        _context = null;
    }

    private void ObserveStartup(Task task, string context) =>
        TaskObserver.Observe(task, context, exception =>
        {
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
            ShowFailure(StartupRecoveryViewModel.GenericErrorCode, exception);
        });

    private async Task<VaultRootResolution> ResolveVaultRootAsync()
    {
        var load = await _configuration!.LoadAsync().ConfigureAwait(true);
        if (load.Status == AppConfigurationStatus.AccessDenied)
        {
            return new(null, "CONFIGURATION_ACCESS_DENIED", null);
        }

        if (load.Status == AppConfigurationStatus.Unreadable)
        {
            Trace.TraceError("Configuration at {0} is unreadable; the original was preserved at {1}.", load.ConfigurationFilePath, load.PreservedCopyPath ?? "(preservation failed)");
            return new(null, "CONFIGURATION_UNREADABLE");
        }

        if (load.Configuration.HasVaultRoot)
        {
            try
            {
                var vault = new VaultPaths(load.Configuration.VaultRoot!);
                vault.EnsureDisjointFrom(_install!, _appState!);
                return new(vault, null);
            }
            catch (ArgumentException exception)
            {
                Trace.TraceError("Refused the configured vault root: {0}", exception.Message);
                return new(null, "VAULT_ROOT_INVALID");
            }
        }

        // First run: the user chooses the Vault. Nothing is created until they accept.
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var suggestion = Path.Combine(string.IsNullOrWhiteSpace(pictures) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : pictures, "Neu Terradise Vault");
        var chosen = new TaskCompletionSource<string?>();
        _window!.Content = StartupScreens.Onboarding(
            suggestion,
            _ => Pickers.PickFolderAsync(_window),
            path => chosen.TrySetResult(path),
            () => chosen.TrySetResult(null));
        var selected = await chosen.Task.ConfigureAwait(true);
        if (selected is null)
        {
            Exit();
            return new(null, null, null);
        }

        return await AcceptVaultRootAsync(selected).ConfigureAwait(true);
    }

    /// <summary>
    /// One reusable Vault-selection acceptance path used by both first-run onboarding and invalid-Vault
    /// recovery. Validates the same safety contract (disjoint roots, reparse-point rejection, unsafe
    /// placement) and atomically persists the chosen root through <see cref="AppConfigurationStore"/>.
    /// </summary>
    private async Task<VaultRootResolution> AcceptVaultRootAsync(string selected)
    {
        try
        {
            var vault = new VaultPaths(selected);
            vault.EnsureDisjointFrom(_install!, _appState!);
            RootPathRules.RejectExistingReparsePoints(vault.Root, vault.Root);
            var save = await _configuration!.UpdateAsync(configuration => configuration with { VaultRoot = vault.Root }).ConfigureAwait(true);
            return save.IsSaved ? new(vault, null, null) : new(null, "CONFIGURATION_ACCESS_DENIED", null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Refused the selected vault root: {0}", exception.Message);
            return new(null, "VAULT_ROOT_INVALID", null);
        }
    }

    /// <summary>
    /// Recovery action for an invalid persisted Vault: opens the folder picker, validates and persists
    /// the chosen root through the shared acceptance path, then resumes bootstrap from the corrected
    /// authority without requiring app-config editing.
    /// </summary>
    private void RecoverVaultRootAsync() =>
        ObserveStartup(ChooseVaultAndResumeAsync(), "App.RecoverVaultRoot");

    private async Task ChooseVaultAndResumeAsync()
    {
        var selected = await Pickers.PickFolderAsync(_window!).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        var resolution = await AcceptVaultRootAsync(selected).ConfigureAwait(true);
        if (resolution.VaultPaths is null)
        {
            ShowFailure(resolution.ErrorCode, null, resolution.PreservedCopyPath);
            return;
        }

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Recovery action for an unreadable configuration: the original bytes are preserved by
    /// <see cref="AppConfigurationStore.LoadAsync"/>; this replaces only the application configuration
    /// with a valid default, then resumes bootstrap into normal Vault selection. The Vault is never
    /// deleted or mutated by this recovery.
    /// </summary>
    private void ResetConfigurationSafely() =>
        ObserveStartup(ResetConfigurationAndResumeAsync(), "App.ResetConfiguration");

    private async Task ResetConfigurationAndResumeAsync()
    {
        var reset = await _configuration!.UpdateAsync(_ => _configuration.CreateDefaultConfiguration()).ConfigureAwait(true);
        if (!reset.IsSaved)
        {
            ShowFailure("CONFIGURATION_ACCESS_DENIED", null, null);
            return;
        }

        await BootstrapLoopAsync().ConfigureAwait(true);
    }

    private async Task PrewarmAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        // One process-wide authority for heavy work, installed before any job or decode can start.
        _resourceGovernor = new ResourceGovernor();
        ResourceGovernor.InstallShared(_resourceGovernor);
        ApplyWindowState(_windowState.Current);
        _derivedImages = new DerivedImageLoader(maxEntries: 640, maxBytes: 128L * 1024 * 1024, maxConcurrentDecodes: 2);
        DerivedImageLoader.InstallShared(_derivedImages);
        _resourceGovernor.TrimRequested += OnTrimRequested;

        var systemReduceMotion = !(_uiSettings?.AnimationsEnabled ?? true);
        _runtime = await ProductionRuntimeRegistry.CreateAsync(context, systemReduceMotion, cancellationToken).ConfigureAwait(true);
        ReducedMotionAuthority.Apply(_runtime.Preferences.UserReduceMotion, systemReduceMotion);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotionAuthority.IsReduced);
        var mediaPreferences = await new SettingsOperations(context.Catalog)
            .GetMediaPreferencesAsync(cancellationToken)
            .ConfigureAwait(true);
        HoverVideoCoordinator.Shared.ApplyPreferences(mediaPreferences);

        new CachePaths(context.Paths).EnsureDirectories();
        context.Paths.EnsureStructuralDirectories();

        // Presentation registry: built-in + Vault packs validated and compiled once, during warm startup.
        var builtInAssets = _install!.ResolveContainedPath(
            InstallPathArea.Assets,
            Path.Combine("Presentation", "BuiltIn"));
        _presentation = new PresentationRuntime(context.Catalog, new PresentationPackStore(context.Paths.PresentationPacksPath, builtInAssets));
        await _presentation.InitializeAsync(cancellationToken).ConfigureAwait(true);
        ThemeRuntime.Current.AttachPresentation(_presentation);
        ThemeRuntime.Current.ApplyDensity(_runtime.Preferences.Density);
        PresentationBinder.ApplyGlobal(_presentation);

        foreach (var limitation in _runtime.Capabilities.Limitations)
        {
            Trace.TraceWarning("Prewarm capability {0}: {1} ({2})", limitation.CapabilityId, limitation.State, limitation.Reason);
        }

        try
        {
            await Task.WhenAll(
                context.Catalog.GalleryReads.GetSpotlightCandidatesAsync(HomeViewModel.MaxSpotlightCandidates, cancellationToken),
                context.Catalog.GalleryReads.GetGalleryPageAsync(new GalleryQuery(PageSize: GalleryPageSizes.Default), cancellationToken),
                context.Catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken),
                context.Catalog.SettingsReads.GetAllTagsAsync(cancellationToken),
                context.Catalog.ImportReads.ListRecentUnitsAsync(50, cancellationToken)).ConfigureAwait(true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("Prewarm read failed (non-fatal): {0}", exception.Message);
        }
    }

    private void OnTrimRequested(object? sender, EventArgs e) => _derivedImages?.Trim(0.5);

    private static void ApplyWindowState(WindowResourceState state) =>
        ResourceGovernor.Shared.SetWindowState(state.IsActive, state.IsMinimized);

    private void RefreshSystemReducedMotion()
    {
        if (_uiSettings is null)
        {
            return;
        }

        ReducedMotionAuthority.ApplySystem(!_uiSettings.AnimationsEnabled);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotionAuthority.IsReduced);
    }

    private async Task ShowShellAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("The runtime must be composed before the shell.");
        var presentation = _presentation ?? throw new InvalidOperationException("Presentation must be composed before the shell.");

        var navigation = new NavigationCoordinator();
        var status = new ShellStatusViewModel();
        status.ApplyCapabilities(runtime.Capabilities);

        _importActivity = new ImportActivityService(context.Catalog, UiDispatch.Context);
        _importActivity.Changed += (_, snapshot) => status.ApplyImports(snapshot);
        status.ApplyImports(_importActivity.Current);
        _importActivity.Start();

        _finalizer = new ImportFinalizer(context.Catalog);
        _finalizer.Changed += (_, _) => _importActivity.RequestRefresh();
        _finalizer.Start();

        _placement = new UnoWindowPlacement(_configuration, _windowState);
        await _placement.AttachAsync(_window!, cancellationToken).ConfigureAwait(true);
        _uiScale = new UiScaleService(_configuration);
        await _uiScale.LoadAsync(cancellationToken).ConfigureAwait(true);

        _updateCoordinator?.Dispose();
        _updateHttpClient?.Dispose();
        var updateHttpClient = new HttpClient();
        _updateHttpClient = updateHttpClient;

        var updateTrust = new UpdateTrustPolicy();
        var updateValidator = new UpdatePackageValidator(_install!);
        var updateStager = new UpdatePackageStager(_appState!, updateValidator);
        var updateHandoff = new UpdateHandoffService(_appState!, _install!);
        _updateCoordinator = new UpdateCoordinator(
            context.Catalog.MutationAdmission,
            _configuration!,
            _appState!,
            _install!,
            context.Paths.Root,
            new UpdateCheckService(updateHttpClient, updateTrust),
            new UpdateDownloadService(updateHttpClient, _appState!),
            new LocalUpdatePackageReader(),
            updateTrust,
            updateStager,
            updateHandoff,
            RequestUpdateShutdown);

        var overlay = new OverlayHostViewModel();
        var factory = new RouteViewModelFactory(
            context.Catalog,
            navigation,
            ThemeRuntime.Current,
            overlay,
            runtime.DerivedStills,
            runtime.ModelAdapters,
            _configuration,
            _importActivity,
            _placement,
            _finalizer,
            _uiScale,
            runtime.VideoPreviews,
            runtime.Cancellation,
            runtime.VideoPreviewRegeneration,
            updateCoordinator: _updateCoordinator);

        var services = new AppServices
        {
            Catalog = context.Catalog,
            Paths = context.Paths,
            Runtime = runtime,
            Presentation = presentation,
            Navigation = navigation,
            Factory = factory,
            Overlay = overlay,
            ImportActivity = _importActivity,
            Finalizer = _finalizer,
            Status = status,
            UiScale = _uiScale,
            Configuration = _configuration!,
            Placement = _placement,
            Window = _window!,
        };

        _shutdown = new ShutdownCoordinator(context, shutdownSchedulerAsync: runtime.ShutdownSchedulerAsync, services: [runtime], appState: _appState);

        await runtime.StartAsync(snapshot => UiDispatch.Run(() => status.Apply(snapshot)), cancellationToken).ConfigureAwait(true);
        status.ApplyProfiling(runtime.ProfilingState, runtime.ProfilingStateReason);
        _context = context;
        _window!.Content = new ProductRoot(services);
    }

    private void ShowFailure(string? errorCode, Exception? exception, string? preservedCopyPath = null)
    {
        if (exception is not null)
        {
            _crash?.Capture(exception, CrashOrigin.StartupFailure);
        }

        var code = errorCode ?? StartupRecoveryViewModel.GenericErrorCode;
        Action? primary = null;
        string? primaryLabel = null;
        var (title, message, next, canRetry) = code switch
        {
            "VAULT_LOCKED" or "VAULT_LOCK_UNAVAILABLE" => (UI.T("Startup.Locked.Title", "Neu Terradise is already open"), UI.T("Startup.Locked.Message", "Another window is using this library."), UI.T("Startup.Locked.Next", "Close the other window, then try again."), true),
            "VAULT_ROOT_INVALID" => (UI.T("Startup.Vault.Title", "The library folder can't be used"), UI.T("Startup.Vault.Message", "The configured library folder is not a safe, writable location."), UI.T("Startup.Vault.Next", "Choose a different library folder, then try again."), true),
            "CONFIGURATION_UNREADABLE" => (UI.T("Startup.Config.Title", "Settings could not be read"), UI.T("Startup.Config.UnreadableMessage", "Neu Terradise could not read its configuration. The original file was preserved for diagnostics. Reset settings to a safe default, then choose your library folder again."), UI.T("Startup.Config.UnreadableNext", "Reset settings safely to continue, or open diagnostics to inspect the preserved file."), true),
            "CONFIGURATION_ACCESS_DENIED" => (UI.T("Startup.Config.Title", "Settings could not be read"), UI.T("Startup.Config.Message", "Neu Terradise could not read its configuration. The original file was kept."), UI.T("Startup.Config.Next", "Check folder permissions, then try again."), true),
            "STARTUP_ROLLBACK_UNSAFE" => (UI.T("Startup.Rollback.Title", "Neu Terradise must close"), UI.T("Startup.Rollback.Message", "Startup stopped after runtime activation, and runtime shutdown could not be proven complete."), UI.T("Startup.Rollback.Next", "Close Neu Terradise before trying again so the library lock remains exclusive."), false),
            _ => (UI.T("Startup.Failed.Title", "Neu Terradise could not start"), UI.T("Startup.Failed.Message", "Something prevented your library from opening. Nothing was changed."), UI.T("Startup.Failed.Next", "Try again. If it keeps happening, open diagnostics."), true),
        };

        if (code == "VAULT_ROOT_INVALID")
        {
            primary = RecoverVaultRootAsync;
            primaryLabel = UI.T("Startup.Vault.Choose", "Choose library folder…");
        }
        else if (code == "CONFIGURATION_UNREADABLE")
        {
            primary = ResetConfigurationSafely;
            primaryLabel = UI.T("Startup.Config.Reset", "Reset settings safely…");
            if (!string.IsNullOrWhiteSpace(preservedCopyPath))
            {
                next = UI.T("Startup.Config.PreservedAt", "The original was preserved for diagnostics.") + " " + preservedCopyPath;
            }
        }

        _window!.Content = StartupScreens.Failure(
            title,
            message,
            next,
            code,
            canRetry ? () => ObserveStartup(BootstrapLoopAsync(), "App.BootstrapRetry") : null,
            () => _crash?.OpenLocation(),
            Exit,
            primary,
            primaryLabel);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        await ControlledShutdownAsync(0).ConfigureAwait(true);
    }

    private void RequestControlledShutdown() =>
        UiDispatch.Post(() => TaskObserver.Observe(
            ControlledShutdownAsync(-1),
            "App.ControlledShutdown",
            exception => _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure)));

    private void RequestUpdateShutdown(DateTimeOffset shutdownDeadlineUtc) =>
        UiDispatch.Post(() => TaskObserver.Observe(
            ControlledShutdownAsync(0, shutdownDeadlineUtc),
            "App.UpdateShutdown",
            exception => _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure)));

    private async Task ControlledShutdownAsync(
        int exitCode,
        DateTimeOffset? shutdownDeadlineUtc = null)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        var absoluteDeadlineUtc = shutdownDeadlineUtc
            ?? DateTimeOffset.UtcNow.Add(ShutdownCoordinator.DefaultShutdownBudget);

        // X54/X55: close user mutation admission synchronously at top-level shutdown entry,
        // before persistence/finalizer/activity awaits can consume the shared deadline.
        _context?.Catalog.MutationAdmission.Close();

        try
        {
            if (_context is not null)
            {
                var remainingForCommandDrain = absoluteDeadlineUtc - DateTimeOffset.UtcNow;
                if (remainingForCommandDrain > TimeSpan.Zero)
                {
                    using var commandDrain = new CancellationTokenSource(remainingForCommandDrain);
                    try
                    {
                        await _context.Catalog.MutationAdmission
                            .WaitForIdleAsync(commandDrain.Token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (commandDrain.IsCancellationRequested)
                    {
                        // Safety outranks the advertised deadline. The updater shares the same
                        // absolute deadline and will deterministically defer before InstallRoot
                        // mutation if the process cannot quiesce in time.
                        await _context.Catalog.MutationAdmission
                            .WaitForIdleAsync(CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                }
                else
                {
                    await _context.Catalog.MutationAdmission
                        .WaitForIdleAsync(CancellationToken.None)
                        .ConfigureAwait(true);
                }
            }

            HoverVideoCoordinator.Shared.StopAll();
            if (_placement is not null)
            {
                await _placement.PersistAsync().ConfigureAwait(true);
            }

            if (_uiScale is not null)
            {
                await _uiScale.FlushAsync().ConfigureAwait(true);
            }

            // Stop finishing imports first; an in-flight commit stops at a durable checkpoint and resumes next start.
            if (_finalizer is not null)
            {
                await _finalizer.DisposeAsync().ConfigureAwait(true);
                _finalizer = null;
            }

            if (_importActivity is not null)
            {
                await _importActivity.DisposeAsync().ConfigureAwait(true);
                _importActivity = null;
            }

            if (_shutdown is not null)
            {
                var remaining = absoluteDeadlineUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    remaining = TimeSpan.FromMilliseconds(1);
                }

                var report = await _shutdown.ShutdownAsync(remaining).ConfigureAwait(true);
                Trace.TraceInformation("Shutdown: timedOut={0}, remainingJobs={1}, cancelled={2}, workerReleased={3}",
                    report.TimedOut, report.NonterminalJobsLeftForRestart, report.CancelledDuringShutdown, report.ProfilingWorkerReleased);
            }
            else if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(true);
                _context?.Dispose();
            }
        }
        catch (Exception exception)
        {
            _crash?.Capture(exception, CrashOrigin.ControlledShutdownFailure);
        }
        finally
        {
            _placement?.Dispose();
            _placement = null;
            _uiScale = null;
            if (_resourceGovernor is not null)
            {
                _resourceGovernor.TrimRequested -= OnTrimRequested;
            }
            _derivedImages?.Dispose();
            _derivedImages = null;
            _resourceGovernor?.Dispose();
            _resourceGovernor = null;
            _updateCoordinator?.Dispose();
            _updateCoordinator = null;
            _updateHttpClient?.Dispose();
            _updateHttpClient = null;
            _crash?.Dispose();
            _allowClose = true;
            Environment.ExitCode = exitCode;
            Exit();
        }
    }

    private CrashDiagnosticContext CreateCrashContext() => new(
        ProductIdentity.Version,
        _context?.SchemaVersion.ToString("D4"),
        _context?.Paths.Root,
        _shutdown?.State ?? _bootstrapper?.State ?? StartupState.ProcessStart);

    private sealed record VaultRootResolution(VaultPaths? VaultPaths, string? ErrorCode, string? PreservedCopyPath = null);
}
