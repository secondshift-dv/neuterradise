using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;
using System.Diagnostics;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Faces;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Jobs;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Jobs.Transport;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public enum ProfilingInitializationState { Available, Disabled, Unavailable }

public sealed record PresentationPreferences(
    string ThemeId,
    DensityMode Density,
    bool UserReduceMotion)
{

    public static PresentationPreferences CreateDefault() =>
        new(ThemeLoader.FallbackThemeId, DensityMode.Comfortable, false);
}

public sealed class ProductionRuntimeRegistry : IAsyncDisposable
{

    private static readonly ConcurrentDictionary<string, byte> _activeVaultRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly BootstrapContext _context;
    private readonly string _vaultRootKey;
    private readonly SchedulerReads _schedulerReads;
    private readonly SemaphoreSlim _workerGate = new(1, 1);

    private ProfilingWorkerConnection? _worker;
    private CancellationTokenSource? _statusLoop;
    private Task? _statusTask;
    private int _started;
    private int _disposed;

    private ProductionRuntimeRegistry(
        BootstrapContext context,
        string vaultRootKey,
        JobScheduler scheduler,
        JobCancellationOperations cancellation,
        JobHandlerRegistry registry,
        PresentationPreferences preferences,
        ThumbnailCache thumbnails,
        BannerPreviewCache bannerPreviews,
        VideoPreviewCache videoPreviews,
        ModelPreviewCache modelPreviews,
        FaceCropCache faceCrops,
        RuntimeMediaResourceCache runtimeMedia,
        ModelPreviewAdapterRegistry modelAdapters,
        RuntimeCapabilitySnapshot capabilities)
    {
        _context = context;
        _vaultRootKey = vaultRootKey;
        _schedulerReads = new SchedulerReads(context.Catalog);
        Scheduler = scheduler;
        Cancellation = cancellation;
        Registry = registry;
        Preferences = preferences;
        Thumbnails = thumbnails;
        BannerPreviews = bannerPreviews;
        VideoPreviews = videoPreviews;
        VideoPreviewRegeneration = new VideoPreviewRegenerationCoordinator(context.Catalog);
        ModelPreviews = modelPreviews;
        FaceCrops = faceCrops;
        RuntimeMedia = runtimeMedia;
        ModelAdapters = modelAdapters;
        Capabilities = capabilities;
    }

    public JobScheduler Scheduler { get; }

    /// <summary>
    /// Shared cancellation operations used by both the scheduler and ImportUnitControlAuthority.
    /// Sharing ensures that control intents (Pause/Cancel/Shutdown) set by the authority are
    /// visible to the scheduler's completion path.
    /// </summary>
    public JobCancellationOperations Cancellation { get; }

    public JobHandlerRegistry Registry { get; }

    public PresentationPreferences Preferences { get; }

    public ThumbnailCache Thumbnails { get; }

    public BannerPreviewCache BannerPreviews { get; }

    public VideoPreviewCache VideoPreviews { get; }

    public VideoPreviewRegenerationCoordinator VideoPreviewRegeneration { get; }

    public ModelPreviewCache ModelPreviews { get; }

    public FaceCropCache FaceCrops { get; }

    public StillExtractionCoordinator DerivedStills { get; private set; } = null!;

    public RuntimeMediaResourceCache RuntimeMedia { get; }

    public ModelPreviewAdapterRegistry ModelAdapters { get; }

    /// <summary>
    /// Truthful, session-scoped capability facts for tools, inference models and preview adapters.
    /// Missing or integrity-failed artifacts disable only the features that need them.
    /// </summary>
    public RuntimeCapabilitySnapshot Capabilities { get; }

    public ProfilingWorkerConnection? Worker => Volatile.Read(ref _worker);

    public ProfilingInitializationState ProfilingState { get; private set; } = ProfilingInitializationState.Disabled;
    public string? ProfilingStateReason { get; private set; }

    public static async Task<ProductionRuntimeRegistry> CreateAsync(
        BootstrapContext context,
        bool systemReduceMotion = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.State != StartupState.Ready && context.State != StartupState.Prewarming)
        {
            throw new InvalidOperationException(
                "The production runtime may only be composed during startup prewarming or ready.");
        }

        var vaultRootKey = context.Paths.Root;
        if (!_activeVaultRoots.TryAdd(vaultRootKey, 0))
        {
            throw new InvalidOperationException(
                "A production runtime is already composed for this vault; one writer has one scheduler.");
        }

        RuntimeMediaResourceCache? runtimeMedia = null;
        ProductionRuntimeRegistry? services = null;
        try
        {
            var preferences = await ReadPresentationPreferencesAsync(
                    context.Catalog,
                    systemReduceMotion,
                    cancellationToken)
                .ConfigureAwait(false);

            var cachePaths = new CachePaths(context.Paths);
            var budget = new PersistentCacheBudget(cachePaths);
            var publisher = new CacheFilePublisher(cachePaths, budget);

            var thumbnails = new ThumbnailCache(cachePaths, publisher, budget);
            var bannerPreviews = new BannerPreviewCache(cachePaths, publisher, budget);
            var videoPreviews = new VideoPreviewCache(cachePaths, publisher, budget);
            var modelPreviews = new ModelPreviewCache(cachePaths, publisher, budget);
            var faceCrops = new FaceCropCache(cachePaths, publisher, budget);
            runtimeMedia = new RuntimeMediaResourceCache();

            var launcher = new BoundedProcessLauncher();

            var install = InstallPaths.CreateProduction();
            var toolResolver = ExternalToolResolver.ForInstall(install);
            var modelAdapters = ModelPreviewAdapterRegistry.CreateDefaultRegistry(launcher);

            // Capability probing happens once per session. A missing tool or model is a feature-scoped
            // limitation, never a startup failure and never a silent success.
            var capabilities = RuntimeCapabilitySnapshot.Create(
                install,
                toolResolver,
                modelAdapters.DescribeAdapters());

            foreach (var limitation in capabilities.Limitations)
            {
                Trace.TraceWarning(
                    "Runtime capability {0} is {1}: {2}",
                    limitation.CapabilityId,
                    limitation.State,
                    limitation.Reason);
            }

            var mediaReads = new MediaReads(context.Catalog);
            var assetWrites = new AssetWrites(context.Catalog);
            var faceWrites = new FaceWrites(context.Catalog);

            var registry = new JobHandlerRegistry();
            var cancellation = new JobCancellationOperations(context.Catalog);
            services = new ProductionRuntimeRegistry(
                context,
                vaultRootKey,
                CreateScheduler(context, registry, cancellation),
                cancellation,
                registry,
                preferences,
                thumbnails,
                bannerPreviews,
                videoPreviews,
                modelPreviews,
                faceCrops,
                runtimeMedia,
                modelAdapters,
                capabilities);

            var planSource = new ProductionMediaToolPlanSource(
                mediaReads,
                assetWrites,
                context.Paths,
                toolResolver,
                modelAdapters,
                videoPreviews);
            var externalTool = new ExternalMediaToolJobOperation(launcher, planSource);

            services.DerivedStills = new StillExtractionCoordinator(
                mediaReads,
                context.Paths,
                thumbnails,
                faceCrops,
                services.SendToWorkerAsync);

            registry.Register(
                "HashAsset",
                new HashAssetJobHandler(new CandidateHashJobOperation(
                    new ImportPreparationCoordinator(context.Catalog))));
            var pathReconciler = new PathReconciler(
                context.Paths, context.Catalog.ProfileWrites, new AssetWrites(context.Catalog),
                new ManagedFileVerifier(), new WindowsVolumeIdentityProvider());
            registry.Register("ProfileRenameReconciliation", new ProfileRenameReconciliationJobHandler(pathReconciler));
            registry.Register("OwnerRelocation", new OwnerRelocationJobHandler(pathReconciler));
            registry.Register("ExtractMetadata", new ExtractMetadataJobHandler(externalTool));
            registry.Register(
                "GenerateThumbnail",
                new GenerateThumbnailJobHandler(new ImageThumbnailJobOperation(
                    mediaReads,
                    thumbnails,
                    context.Paths)));
            registry.Register("GenerateVideoPreview", new GenerateVideoPreviewJobHandler(externalTool));
            registry.Register("GenerateModelPreview", new GenerateModelPreviewJobHandler(externalTool));
            registry.Register(
                "FaceAnalysis",
                new FaceAnalysisJobHandler(new ProfilingFaceAnalysisJobOperation(
                    mediaReads,
                    faceWrites,
                    context.Paths,
                    services.SendToWorkerAsync,
                    analysisTimeout: null,

                    identityBank: new IdentityBankProvider(context.Catalog))));

            // Wire Stage 2 capability tracking: when a job completes, update capability state
            // and attempt readiness join. After reconciliation, project terminal job state into
            // capability state for jobs that were interrupted and moved to terminal states.
            var stage2Coordinator = new Stage2PreparationCoordinator(context.Catalog, context.Paths);
            var stage2Handler = new Stage2CompletionHandler(context.Catalog, stage2Coordinator);
            services.Scheduler.OnJobCompleted = stage2Handler.HandleCompletionAsync;
            services.Scheduler.OnReconciled = stage2Handler.ReconcileTerminalCapabilitiesAsync;

            return services;
        }
        catch
        {
            if (services is not null)
            {
                try
                {
                    await services.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Partial runtime disposal failed: {0}", exception.GetType().Name);
                }
            }

            runtimeMedia?.Dispose();
            _activeVaultRoots.TryRemove(vaultRootKey, out _);
            throw;
        }
    }

    public async Task StartAsync(
        Action<ShellWorkSnapshot>? publishStatus = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The production runtime can start only once.");
        }

        await VideoPreviewRegeneration.RequeueMissingCurrentAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var worker = await ConnectWorkerAsync(cancellationToken).ConfigureAwait(false);
            var hello = worker.Host.ProfilingHello;
            if (hello is null)
            {
                throw new ProfilingProtocolException("Worker handshake did not expose Hello outcome.");
            }
            if (hello.YuNetModelAvailable && hello.SFaceModelAvailable)
            {
                ProfilingState = ProfilingInitializationState.Available;
                ProfilingStateReason = null;
            }
            else
            {
                ProfilingState = ProfilingInitializationState.Unavailable;
                ProfilingStateReason = "Profiling worker connected, but required YuNet/SFace model outcome is unavailable.";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ProfilingProtocolException or FileNotFoundException)
        {
            ProfilingState = ProfilingInitializationState.Unavailable;
            ProfilingStateReason = $"Profiling initialization unavailable: {ex.GetType().Name}.";
        }

        if (publishStatus is not null)
        {
            _statusLoop = new CancellationTokenSource();
            _statusTask = Task.Run(
                () => PublishStatusLoopAsync(publishStatus, _statusLoop.Token),
                CancellationToken.None);
        }
    }

    public async Task<SchedulerShutdownReport> ShutdownSchedulerAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        await StopStatusLoopAsync().ConfigureAwait(false);

        var releaseWorker = Volatile.Read(ref _worker) is null
            ? (Func<CancellationToken, Task>?)null
            : ReleaseWorkerAsync;

        return await Scheduler
            .ShutdownAsync(gracePeriod, releaseWorker, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProfilingEnvelope> SendToWorkerAsync(
        ProfilingEnvelope request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await ConnectWorkerAsync(cancellationToken).ConfigureAwait(false);
        return await connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfilingWorkerConnection> ConnectWorkerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var existing = Volatile.Read(ref _worker);
        if (existing is not null)
        {
            await existing.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = Volatile.Read(ref _worker);
            if (existing is not null)
            {
                await existing.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var created = new ProfilingWorkerConnection();
            try
            {
                await created.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {

                await created.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _worker, created);
            return created;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopStatusLoopAsync().ConfigureAwait(false);

        try
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Scheduler disposal failed: {0}", exception.GetType().Name);
        }

        await ReleaseWorkerAsync(CancellationToken.None).ConfigureAwait(false);

        RuntimeMedia.Dispose();
        _workerGate.Dispose();
        _activeVaultRoots.TryRemove(_vaultRootKey, out _);
    }

    private static JobScheduler CreateScheduler(
        BootstrapContext context,
        JobHandlerRegistry registry,
        JobCancellationOperations cancellation) =>
        new(context.Catalog, registry, cancellation: cancellation);

    internal static async Task<PresentationPreferences> ReadPresentationPreferencesAsync(
        CatalogDb catalog,
        bool systemReduceMotion,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = new SettingsOperations(catalog);
            var theme = await settings.GetThemeAsync(cancellationToken).ConfigureAwait(false);
            var density = await settings.GetDensityAsync(cancellationToken).ConfigureAwait(false);
            var reduceMotion = await settings.GetReduceMotionAsync(cancellationToken).ConfigureAwait(false);

            return new PresentationPreferences(theme, density, reduceMotion);
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
        {
            Trace.TraceWarning(
                "Persisted presentation settings could not be read at startup: {0}",
                exception.GetType().Name);
            return PresentationPreferences.CreateDefault();
        }
    }

    private async Task PublishStatusLoopAsync(
        Action<ShellWorkSnapshot> publishStatus,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                publishStatus(await ReadShellSnapshotAsync(cancellationToken).ConfigureAwait(false));
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException)
            {

                Trace.TraceWarning("Shell status read failed: {0}", exception.GetType().Name);
            }
        }
    }

    public async Task<ShellWorkSnapshot> ReadShellSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var byLane = await _schedulerReads.GetStateCountsByLaneAsync(cancellationToken).ConfigureAwait(false);

        var running = 0L;
        var pending = 0L;
        var attention = 0L;
        var completed = 0L;

        foreach (var lane in byLane.Values)
        {
            foreach (var (state, count) in lane)
            {
                switch (state)
                {
                    case JobState.Running:
                        running += count;
                        break;
                    case JobState.Pending:
                    case JobState.Runnable:
                    case JobState.FailedRetryable:
                        pending += count;
                        break;
                    case JobState.FailedTerminal:
                        attention += count;
                        break;
                    case JobState.Succeeded:
                        completed += count;
                        break;
                    default:
                        break;
                }
            }
        }

        var outstanding = running + pending;
        double? progress = outstanding + completed > 0 && running > 0
            ? Math.Clamp((double)completed / (outstanding + completed), 0, 1)
            : null;

        return new ShellWorkSnapshot(
            (int)Math.Min(int.MaxValue, running),
            (int)Math.Min(int.MaxValue, pending),
            (int)Math.Min(int.MaxValue, attention),
            progress);
    }

    private async Task StopStatusLoopAsync()
    {
        var loop = Interlocked.Exchange(ref _statusLoop, null);
        var task = Interlocked.Exchange(ref _statusTask, null);
        if (loop is null)
        {
            return;
        }

        await loop.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {

            }
        }

        loop.Dispose();
    }

    private async Task ReleaseWorkerAsync(CancellationToken cancellationToken)
    {
        var worker = Interlocked.Exchange(ref _worker, null);
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.ShutdownAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Profiling Worker shutdown failed: {0}", exception.GetType().Name);
        }
        finally
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
