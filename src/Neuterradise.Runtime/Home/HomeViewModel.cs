using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Activity;
using Neuterradise.App.Gallery;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Home;

public sealed class HomeViewModel : ScreenStateViewModel, IDisposable
{
    public const int MaxSpotlightCandidates = 12;
    public const int RecentlyActiveCount = 12;
    public const int MaxAttentionItems = 3;
    public const int MaxDiscoveryItems = 6;
    public const int MaxActivityItems = 10;
    public static readonly TimeSpan MinimumSpotlightRotationInterval = TimeSpan.FromSeconds(10);

    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly ImportReads? _importReads;
    private readonly ProfileOperations? _profileOperations;
    private readonly ImportActivityService? _activity;
    private readonly StillExtractionCoordinator? _derivedStills;
    private readonly VideoPreviewRegenerationCoordinator? _videoPreviewRegeneration;
    private readonly StaleResultGuard _loadGuard = new();
    private readonly ObservableCollection<ImportActivityItem> _activeImports = [];
    private readonly ObservableCollection<HomeSpotlightViewModel> _spotlightCandidates = [];
    private readonly ObservableCollection<HomeProfileTileViewModel> _recentlyActive = [];
    private readonly ObservableCollection<HomeAttentionItem> _needsAttention = [];
    private readonly ObservableCollection<HomeDiscoveryItem> _discovery = [];
    private readonly ObservableCollection<HomeActivityItem> _recentActivity = [];

    private int _spotlightIndex;
    private HomeVaultPulse _vaultPulse = HomeVaultPulse.Unknown;
    private bool _hasMoreAttentionItems;
    private bool _disposed;
    private RefreshCoalescer? _galleryCoalescer;
    private RefreshCoalescer? _attentionCoalescer;
    private RefreshCoalescer? _relatedCoalescer;
    private RefreshCoalescer? _chronicleCoalescer;

    public HomeViewModel() : this(null, null) { }

    public HomeViewModel(
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        ImportReads? importReads = null,
        ProfileOperations? profileOperations = null,
        ImportActivityService? activityService = null,
        StillExtractionCoordinator? derivedStills = null,
        VideoPreviewRegenerationCoordinator? videoPreviewRegeneration = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _activity = activityService;
        _derivedStills = derivedStills;
        _videoPreviewRegeneration = videoPreviewRegeneration;
        _importReads = importReads ?? _catalog?.ImportReads;
        _profileOperations = profileOperations ?? (_catalog is not null ? new ProfileOperations(_catalog) : null);

        NextSpotlightCommand = new RelayCommand(_ => MoveSpotlight(1), _ => HasMultipleSpotlightCandidates);
        PreviousSpotlightCommand = new RelayCommand(_ => MoveSpotlight(-1), _ => HasMultipleSpotlightCandidates);

        if (_activity is not null)
        {
            _activity.Changed += OnImportActivityChanged;
            ApplyImportActivity(_activity.Current);
        }

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
        else
        {
            ShowEmpty();
        }
    }

    public bool HasMoreAttentionItems { get => _hasMoreAttentionItems; private set => SetProperty(ref _hasMoreAttentionItems, value); }
    public ObservableCollection<HomeSpotlightViewModel> SpotlightCandidates => _spotlightCandidates;
    public ObservableCollection<HomeProfileTileViewModel> RecentlyActive => _recentlyActive;
    public ObservableCollection<HomeAttentionItem> NeedsAttention => _needsAttention;
    public ObservableCollection<HomeDiscoveryItem> Discovery => _discovery;
    public HomeDiscoveryItem? FeaturedConnection => _discovery.Count > 0 ? _discovery[0] : null;
    public ObservableCollection<HomeActivityItem> RecentActivity => _recentActivity;
    public ObservableCollection<ImportActivityItem> ActiveImports => _activeImports;
    public bool ShowActiveImports => _activeImports.Count > 0;
    public HomeVaultPulse VaultPulse { get => _vaultPulse; private set => SetProperty(ref _vaultPulse, value); }
    public HomeSpotlightViewModel? CurrentSpotlight => _spotlightIndex >= 0 && _spotlightIndex < _spotlightCandidates.Count ? _spotlightCandidates[_spotlightIndex] : null;

    public int SpotlightIndex
    {
        get => _spotlightIndex;
        private set
        {
            if (SetProperty(ref _spotlightIndex, value))
            {
                RaisePropertyChanged(nameof(CurrentSpotlight));
                RaisePropertyChanged(nameof(SpotlightPositionText));
                RaisePropertyChanged(nameof(HeroAmbientIntensity));
            }
        }
    }

    public bool HasMultipleSpotlightCandidates => _spotlightCandidates.Count > 1;
    public string SpotlightPositionText => _spotlightCandidates.Count == 0
        ? string.Empty
        : SurfaceText.Format("Home.Spotlight.Position", "{0} of {1}", _spotlightIndex + 1, _spotlightCandidates.Count);
    public bool ShowSpotlight => true;
    public bool ReduceMotion => ReducedMotionAuthority.IsReduced;
    public double HeroAmbientIntensity => CurrentSpotlight?.HasHeroImage == true ? 0.45 : 1.0;
    public bool ShowRecentlyActive => _recentlyActive.Count > 0;
    public bool ShowNeedsAttention => _needsAttention.Count > 0;
    public bool ShowDiscovery => _discovery.Count > 0;
    public bool ShowFeaturedConnection => _discovery.Count > 0;
    public bool ShowRecentActivity => _recentActivity.Count > 0;
    public bool ShowVaultChronicle => _recentActivity.Count > 0;
    public bool ShowVaultPulse => true;
    public bool IsLibraryEmpty => _spotlightCandidates.Count == 0 && _recentlyActive.Count == 0;
    public ICommand NextSpotlightCommand { get; }
    public ICommand PreviousSpotlightCommand { get; }

    public async Task RefreshAsync()
    {
        if (_catalog is null) return;
        var token = _loadGuard.Begin();
        if (_spotlightCandidates.Count == 0 && _recentlyActive.Count == 0) ShowLoading(); else BeginBackgroundUpdate();

        try
        {
            var cancellation = token.CancellationToken;
            var spotlightTask = _catalog.GalleryReads.GetSpotlightCandidatesAsync(MaxSpotlightCandidates, cancellation);
            var recentTask = _catalog.GalleryReads.GetGalleryPageAsync(
                new GalleryQuery(SortOrder: GallerySortOrder.UpdatedAtDesc, PageSize: RecentlyActiveCount, KindFilter: GalleryProfileKindFilter.NormalOnly), cancellation);
            var healthTask = _catalog.HealthReads.GetHealthSummaryAsync(cancellation);
            var unitsTask = _importReads is null ? Task.FromResult<IReadOnlyList<ImportUnitSummary>>([]) : _importReads.ListRecentUnitsAsync(50, cancellation);
            var featuredTask = _catalog.RelatedReads.GetFeaturedConnectionAsync(cancellation);
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            var startOfMonthLocal = new DateTimeOffset(localNow.Year, localNow.Month, 1, 0, 0, 0, localNow.Offset);
            var endOfMonthLocal = startOfMonthLocal.AddMonths(1);
            var chronicleTask = _catalog.ActivityReads.GetMonthlyChronicleSummaryAsync(startOfMonthLocal.ToUniversalTime(), endOfMonthLocal.ToUniversalTime(), cancellation);

            await Task.WhenAll(spotlightTask, recentTask, healthTask, unitsTask, featuredTask, chronicleTask).ConfigureAwait(false);
            if (!_loadGuard.IsCurrent(token)) return;

            var spotlightCandidates = spotlightTask.Result;
            var recentPage = recentTask.Result;
            var paths = _catalog.Paths;
            var mediaReads = _catalog.MediaReads;

            var (spotlightResolved, recentCovers) = await Task.Run(() =>
            {
                var spotResults = new List<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)>();
                foreach (var item in spotlightCandidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var bannerPath = item.Summary.BannerStillTimestampMilliseconds is null
                        ? ResolveDerivedPresentationPath(mediaReads, paths, item.Summary.BannerAssetId, preferBanner: true)
                        : null;
                    var coverPath = item.Summary.CoverStillTimestampMilliseconds is null
                        ? ResolveDerivedPresentationPath(mediaReads, paths, item.Summary.CoverAssetId, preferBanner: false)
                        : null;
                    var bannerVideoPath = item.Summary.EffectiveBannerSourceKind == BannerVisualSourceKind.VideoClip
                        ? ResolveBannerVideoPath(mediaReads, paths, item.Summary.BannerAssetId)
                        : null;
                    spotResults.Add((item, bannerPath, coverPath, bannerVideoPath));
                }

                var recentCoverPaths = new Dictionary<Guid, string?>();
                foreach (var summary in recentPage.Items)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (summary.CoverAssetId.HasValue && !recentCoverPaths.ContainsKey(summary.CoverAssetId.Value))
                    {
                        recentCoverPaths[summary.CoverAssetId.Value] = ResolveDerivedPresentationPath(mediaReads, paths, summary.CoverAssetId, preferBanner: false);
                    }
                }
                return (spotResults, recentCoverPaths);
            }, cancellation).ConfigureAwait(false);

            if (_derivedStills is not null)
            {
                for (var i = 0; i < spotlightResolved.Count; i++)
                {
                    var item = spotlightResolved[i];
                    var bannerPath = item.BannerPath;
                    var coverPath = item.CoverPath;
                    if (item.Candidate.Summary.BannerStillTimestampMilliseconds is { } bannerTs
                        && item.Candidate.Summary.BannerAssetId is { } bannerAssetId)
                    {
                        bannerPath = await ResolveHomeStillAsync(
                                bannerAssetId,
                                bannerTs,
                                item.Candidate.Summary.BannerContentFingerprint,
                                cancellation)
                            .ConfigureAwait(false);
                    }
                    if (item.Candidate.Summary.CoverStillTimestampMilliseconds is { } coverTs
                        && item.Candidate.Summary.CoverAssetId is { } coverAssetId)
                    {
                        coverPath = await ResolveHomeStillAsync(
                                coverAssetId,
                                coverTs,
                                item.Candidate.Summary.CoverContentFingerprint,
                                cancellation)
                            .ConfigureAwait(false);
                    }
                    spotlightResolved[i] = (item.Candidate, bannerPath, coverPath, item.BannerVideoPath);
                }

                foreach (var summary in recentPage.Items)
                {
                    if (summary.CoverStillTimestampMilliseconds is { } coverTs
                        && summary.CoverAssetId is { } coverAssetId)
                    {
                        recentCovers[coverAssetId] = await ResolveHomeStillAsync(
                                coverAssetId,
                                coverTs,
                                summary.CoverContentFingerprint,
                                cancellation)
                            .ConfigureAwait(false);
                    }
                }
            }

            if (_videoPreviewRegeneration is not null)
            {
                foreach (var item in spotlightResolved)
                {
                    if (item.BannerVideoPath is null
                        && item.Candidate.Summary.EffectiveBannerSourceKind == BannerVisualSourceKind.VideoClip
                        && item.Candidate.Summary.BannerAssetId is { } bannerAssetId)
                    {
                        await _videoPreviewRegeneration
                            .RequeueMissingCurrentAsync(bannerAssetId, cancellation)
                            .ConfigureAwait(false);
                    }
                }
            }

            if (!_loadGuard.IsCurrent(token)) return;
            UiDispatch.Run(() =>
            {
                ApplySpotlight(spotlightResolved);
                ApplyRecentlyActive(recentPage, recentCovers);
                ApplyNeedsAttention(unitsTask.Result, healthTask.Result);
                ApplyFeaturedConnection(featuredTask.Result);
                ApplyChronicle(chronicleTask.Result);
                ApplyVaultPulse(healthTask.Result);
                ShowReady();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_loadGuard.IsCurrent(token))
            {
                Trace.TraceWarning("Home load failed: {0}", exception.GetType().Name);
                UiDispatch.Run(() => ShowRecoverableError(SurfaceText.Get("Home.Error.Load", "Home could not be loaded.")));
            }
        }
        finally
        {
            if (_loadGuard.IsCurrent(token)) UiDispatch.Run(() => EndBackgroundUpdate());
        }
    }

    public static IReadOnlyList<GalleryProfileSummary> RankSpotlightCandidates(IEnumerable<GalleryProfileSummary> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.Kind == ProfileKind.Normal)
            .OrderByDescending(candidate => candidate.CoverAssetId.HasValue || candidate.BannerAssetId.HasValue)
            .ThenByDescending(candidate => candidate.IsFavorite)
            .ThenByDescending(candidate => candidate.Rating.HasValue)
            .ThenByDescending(candidate => candidate.Rating ?? -1)
            .ThenByDescending(candidate => candidate.UpdatedAtUtc)
            .ThenBy(candidate => candidate.ProfileId)
            .Take(MaxSpotlightCandidates)
            .ToList();
    }

    public void ApplySpotlight(IReadOnlyList<HomeSpotlightCandidateReadModel> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var resolved = candidates.Select(item =>
        {
            var bannerPath = ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, item.Summary.BannerAssetId, preferBanner: true);
            var coverPath = ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, item.Summary.CoverAssetId, preferBanner: false);
            var bannerVideoPath = item.Summary.EffectiveBannerSourceKind == BannerVisualSourceKind.VideoClip
                ? ResolveBannerVideoPath(_catalog?.MediaReads, _catalog?.Paths, item.Summary.BannerAssetId)
                : null;
            return (Candidate: item, BannerPath: bannerPath, CoverPath: coverPath, BannerVideoPath: bannerVideoPath);
        }).ToList();
        ApplySpotlight(resolved);
    }

    public void ApplySpotlight(IReadOnlyList<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var previousId = CurrentSpotlight?.ProfileId;
        _spotlightCandidates.Clear();
        foreach (var (item, bannerPath, coverPath, bannerVideoPath) in resolved)
        {
            _spotlightCandidates.Add(new HomeSpotlightViewModel(item.Summary)
            {
                BannerImagePath = bannerPath,
                CoverImagePath = coverPath,
                BannerVideoPath = bannerVideoPath,
                OverviewExcerpt = item.OverviewExcerpt,
            });
        }

        if (_spotlightCandidates.Count == 0) SpotlightIndex = -1;
        else if (previousId.HasValue)
        {
            var matchedIndex = -1;
            for (var i = 0; i < _spotlightCandidates.Count; i++)
            {
                if (_spotlightCandidates[i].ProfileId == previousId.Value) { matchedIndex = i; break; }
            }
            SpotlightIndex = matchedIndex >= 0 ? matchedIndex : 0;
        }
        else SpotlightIndex = 0;

        RaisePropertyChanged(nameof(HasMultipleSpotlightCandidates));
        (NextSpotlightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousSpotlightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(CurrentSpotlight));
        RaisePropertyChanged(nameof(SpotlightPositionText));
        RaisePropertyChanged(nameof(HeroAmbientIntensity));
        RaisePropertyChanged(nameof(ReduceMotion));
        RaisePropertyChanged(nameof(IsLibraryEmpty));
    }

    public void ApplySpotlight(GalleryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ApplySpotlight(RankSpotlightCandidates(page.Items).Select(s => new HomeSpotlightCandidateReadModel(s, null)).ToList());
    }

    public void ApplyRecentlyActive(GalleryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Dictionary<Guid, string?>? coverPaths = null;
        if (_catalog is not null)
        {
            coverPaths = new Dictionary<Guid, string?>();
            foreach (var summary in page.Items)
            {
                if (summary.CoverAssetId.HasValue && !coverPaths.ContainsKey(summary.CoverAssetId.Value))
                {
                    coverPaths[summary.CoverAssetId.Value] = ResolveDerivedPresentationPath(_catalog.MediaReads, _catalog.Paths, summary.CoverAssetId, preferBanner: false);
                }
            }
        }
        ApplyRecentlyActive(page, coverPaths);
    }

    public void ApplyRecentlyActive(GalleryPage page, Dictionary<Guid, string?>? preResolvedCovers)
    {
        ArgumentNullException.ThrowIfNull(page);
        _recentlyActive.Clear();
        foreach (var summary in page.Items.Where(item => item.Kind == ProfileKind.Normal).OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.ProfileId).Take(RecentlyActiveCount))
        {
            string? coverPath = null;
            if (summary.CoverAssetId.HasValue && preResolvedCovers is not null) preResolvedCovers.TryGetValue(summary.CoverAssetId.Value, out coverPath);
            _recentlyActive.Add(new HomeProfileTileViewModel(summary) { CoverImagePath = coverPath });
        }
        RaisePropertyChanged(nameof(ShowRecentlyActive));
        RaisePropertyChanged(nameof(IsLibraryEmpty));
    }

    private async Task<string?> ResolveHomeStillAsync(
        Guid assetId,
        long timestampMilliseconds,
        string? fingerprint,
        CancellationToken cancellationToken)
    {
        if (_derivedStills is null)
        {
            return null;
        }

        var request = new DerivedStillRequest(assetId, timestampMilliseconds);
        var published = _derivedStills.TryResolvePublishedPath(request, fingerprint);
        return published ?? await _derivedStills.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public static string? ResolveBannerVideoPath(MediaReads? mediaReads, VaultPaths? paths, Guid? assetId)
    {
        var source = ResolveAssetSource(mediaReads, assetId);
        if (paths is null || source is null || source.MediaType != MediaType.Video || string.IsNullOrWhiteSpace(source.Sha256)) return null;

        try
        {
            var key = new VideoPreviewCacheKey(source.AssetId, source.Sha256, CacheVersionSet.Default.VideoPreviewVersion, ProductionMediaToolPlanSource.VideoPreviewVariantKey);
            var cachePaths = new CachePaths(paths);
            var physical = cachePaths.ResolveContainedPath(CacheFamily.VideoPreviews, key.GetRelativePath(".mp4"));
            return File.Exists(physical) ? physical : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ResolveBannerVideoPath(VaultPaths? paths, Guid? assetId) => null;

    public static string? ResolveDerivedPresentationPath(
        MediaReads? mediaReads,
        VaultPaths? paths,
        Guid? assetId,
        bool preferBanner = false,
        string? managedFallbackPath = null)
    {
        if (paths is null || !assetId.HasValue || assetId.Value == Guid.Empty) return null;
        var source = ResolveAssetSource(mediaReads, assetId);

        if (source is not null && source.MediaType == MediaType.Image && !string.IsNullOrWhiteSpace(source.Sha256))
        {
            try
            {
                var key = new ThumbnailCacheKey(source.AssetId, source.Sha256, CacheVersionSet.Default.ThumbnailVersion, ImageThumbnailJobOperation.SizeKey);
                var cachePaths = new CachePaths(paths);
                var physical = cachePaths.ResolveContainedPath(CacheFamily.Thumbnails, key.GetRelativePath(".jpg"));
                if (File.Exists(physical)) return physical;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        if (source is not null && source.MediaType != MediaType.Image)
        {
            return null;
        }

        var canonical = managedFallbackPath ?? source?.ResolveManagedPath(paths);
        return !string.IsNullOrWhiteSpace(canonical) && File.Exists(canonical) ? canonical : null;
    }

    public static string? ResolveDerivedPresentationPath(VaultPaths? paths, Guid? assetId, bool preferBanner = false, string? managedFallbackPath = null) =>
        !string.IsNullOrWhiteSpace(managedFallbackPath) && File.Exists(managedFallbackPath) ? managedFallbackPath : null;

    public static string? ResolveManagedFallback(MediaReads? mediaReads, VaultPaths? paths, Guid? assetId)
    {
        if (paths is null) return null;
        return ResolveAssetSource(mediaReads, assetId)?.ResolveManagedPath(paths);
    }

    private static AssetSource? ResolveAssetSource(MediaReads? mediaReads, Guid? assetId)
    {
        if (mediaReads is null || !assetId.HasValue || assetId.Value == Guid.Empty) return null;
        try
        {
            return mediaReads.GetAssetSourceAsync(assetId.Value, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    public void ApplyNeedsAttention(IReadOnlyList<ImportUnitSummary> units, LibraryHealthSummary health)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(health);
        _needsAttention.Clear();
        var allCandidates = new List<HomeAttentionItem>();
        foreach (var unit in units.Where(unit => unit.State == ImportUnitState.ReadyForVerification))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.VerificationReady,
                SurfaceText.Get("Home.Attention.Verification.Title", "Waiting for your choices"),
                SurfaceText.Format("Home.Attention.Verification.Detail", "{0} — choose who these {1} media are for.", unit.SourceDisplayName, unit.TotalItemCount), new ImportRoute()));
        }
        foreach (var unit in units.Where(unit => unit.State is ImportUnitState.FailedRetryable or ImportUnitState.FailedTerminal))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.ImportFailure,
                SurfaceText.Get("Home.Attention.Import.Title", "Import needs attention"),
                SurfaceText.Format("Home.Attention.Import.Detail", "{0} stopped before finishing. You can try it again from Import.", unit.SourceDisplayName), new ImportRoute()));
        }
        foreach (var unit in units.Where(unit => unit.CleanupFailedCount > 0))
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.SourceCleanupFailure,
                SurfaceText.Get("Home.Attention.Cleanup.Title", "Original files need attention"),
                SurfaceText.Format("Home.Attention.Cleanup.Detail", "{0} is in your library, but {1} original file(s) could not be removed from the source folder.", unit.SourceDisplayName, unit.CleanupFailedCount), new ImportRoute()));
        }
        if (health.NeedsAttentionReconciliationCount > 0)
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.PathReconciliationFailure,
                SurfaceText.Get("Home.Attention.Reconciliation.Title", "Some files need attention"),
                SurfaceText.Format("Home.Attention.Reconciliation.Detail", "{0} item(s) could not be filed into their final place in your library.", health.NeedsAttentionReconciliationCount), new LibraryHealthRoute()));
        }
        if (health.UnresolvedFaceDetections > 0)
        {
            allCandidates.Add(new HomeAttentionItem(HomeAttentionKind.FaceReviewPending,
                SurfaceText.Get("Home.Attention.Faces.Title", "People to confirm"),
                SurfaceText.Format("Home.Attention.Faces.Detail", "{0} face(s) are waiting for you to say who they are.", health.UnresolvedFaceDetections), new FaceReviewRoute()));
        }
        foreach (var item in allCandidates.Take(MaxAttentionItems)) _needsAttention.Add(item);
        HasMoreAttentionItems = allCandidates.Count > MaxAttentionItems;
        RaisePropertyChanged(nameof(ShowNeedsAttention));
    }

    public void ApplyFeaturedConnection(FeaturedConnectionReadModel? connection)
    {
        _discovery.Clear();
        if (connection is not null)
        {
            var evidence = DescribeFeaturedEvidence(connection);
            if (!string.IsNullOrWhiteSpace(evidence)) _discovery.Add(new HomeDiscoveryItem(connection.ProfileIdLow, connection.LowDisplayName, connection.ProfileIdHigh, connection.HighDisplayName, evidence));
        }
        RaisePropertyChanged(nameof(ShowDiscovery));
        RaisePropertyChanged(nameof(ShowFeaturedConnection));
        RaisePropertyChanged(nameof(FeaturedConnection));
    }

    public static string? DescribeFeaturedEvidence(FeaturedConnectionReadModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var parts = new List<string>();
        if (connection.SharedAssetCount > 0) parts.Add(connection.SharedAssetCount == 1 ? SurfaceText.Get("Home.Evidence.Shared.One", "1 shared media item") : SurfaceText.Format("Home.Evidence.Shared.Many", "{0} shared media items", connection.SharedAssetCount));
        if (connection.ConfirmedFaceCount > 0) parts.Add(connection.ConfirmedFaceCount == 1 ? SurfaceText.Get("Home.Evidence.Face.One", "1 confirmed face association") : SurfaceText.Format("Home.Evidence.Face.Many", "{0} confirmed face associations", connection.ConfirmedFaceCount));
        if (connection.ManualRelation) parts.Add(SurfaceText.Get("Home.Evidence.Manual", "a manual relation"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public void ApplyDiscovery(IReadOnlyList<HomeDiscoveryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _discovery.Clear();
        foreach (var item in items.Take(MaxDiscoveryItems)) _discovery.Add(item);
        RaisePropertyChanged(nameof(ShowDiscovery));
        RaisePropertyChanged(nameof(ShowFeaturedConnection));
        RaisePropertyChanged(nameof(FeaturedConnection));
    }

    public void ApplyChronicle(HomeChronicleReadModel chronicle)
    {
        ArgumentNullException.ThrowIfNull(chronicle);
        _recentActivity.Clear();
        if (chronicle.MediaCommittedCount > 0) _recentActivity.Add(new HomeActivityItem(Guid.NewGuid(), chronicle.MediaCommittedCount == 1 ? SurfaceText.Get("Home.Chronicle.Media.One", "1 media added this month") : SurfaceText.Format("Home.Chronicle.Media.Many", "{0} media added this month", chronicle.MediaCommittedCount), chronicle.StartOfMonthUtc, null));
        if (chronicle.DurableRelatedPairsCount > 0) _recentActivity.Add(new HomeActivityItem(Guid.NewGuid(), chronicle.DurableRelatedPairsCount == 1 ? SurfaceText.Get("Home.Chronicle.Connection.One", "1 evidence-backed profile connection formed or updated") : SurfaceText.Format("Home.Chronicle.Connection.Many", "{0} evidence-backed profile connections formed or updated", chronicle.DurableRelatedPairsCount), chronicle.StartOfMonthUtc, null));
        if (chronicle.FacesConfirmedCount > 0) _recentActivity.Add(new HomeActivityItem(Guid.NewGuid(), chronicle.FacesConfirmedCount == 1 ? SurfaceText.Get("Home.Chronicle.Face.One", "1 face confirmed this month") : SurfaceText.Format("Home.Chronicle.Face.Many", "{0} faces confirmed this month", chronicle.FacesConfirmedCount), chronicle.StartOfMonthUtc, null));
        if (chronicle.ProfilesCreatedCount > 0) _recentActivity.Add(new HomeActivityItem(Guid.NewGuid(), chronicle.ProfilesCreatedCount == 1 ? SurfaceText.Get("Home.Chronicle.Profile.One", "1 profile created this month") : SurfaceText.Format("Home.Chronicle.Profile.Many", "{0} profiles created this month", chronicle.ProfilesCreatedCount), chronicle.StartOfMonthUtc, null));
        RaisePropertyChanged(nameof(ShowRecentActivity));
        RaisePropertyChanged(nameof(ShowVaultChronicle));
    }

    public void ApplyRecentActivity(IReadOnlyList<ActivityItemReadModel> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _recentActivity.Clear();
        foreach (var entry in entries.Take(MaxActivityItems)) _recentActivity.Add(new HomeActivityItem(entry.ActivityId, DescribeActivity(entry.EventType), entry.OccurredAtUtc, entry.ProfileId));
        RaisePropertyChanged(nameof(ShowRecentActivity));
        RaisePropertyChanged(nameof(ShowVaultChronicle));
    }

    public void ApplyVaultPulse(LibraryHealthSummary health)
    {
        ArgumentNullException.ThrowIfNull(health);
        VaultPulse = new HomeVaultPulse(health.TotalProfiles, health.ActiveAssets, health.TrashedAssets, FormatBytes(health.ActiveManagedByteLength), DescribeHealth(health));
    }

    public void MoveSpotlight(int delta)
    {
        if (_spotlightCandidates.Count == 0) { SpotlightIndex = -1; return; }
        var count = _spotlightCandidates.Count;
        SpotlightIndex = ((_spotlightIndex + delta) % count + count) % count;
    }

    public static string? DescribeEvidence(RelatedProfiles.RelatedProfileSummaryReadModel summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var parts = new List<string>();
        if (summary.SharedAssetCount > 0) parts.Add(summary.SharedAssetCount == 1 ? SurfaceText.Get("Home.Evidence.Shared.One", "1 shared media item") : SurfaceText.Format("Home.Evidence.Shared.Many", "{0} shared media items", summary.SharedAssetCount));
        if (summary.ConfirmedFaceCount > 0) parts.Add(summary.ConfirmedFaceCount == 1 ? SurfaceText.Get("Home.Evidence.Face.One", "1 confirmed face association") : SurfaceText.Format("Home.Evidence.Face.Many", "{0} confirmed face associations", summary.ConfirmedFaceCount));
        if (summary.ManualRelation) parts.Add(SurfaceText.Get("Home.Evidence.Manual", "a manual relation"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static string DescribeHealth(LibraryHealthSummary health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.NeedsAttentionReconciliationCount > 0) return SurfaceText.Get("Library.Condition.NeedsAttention", "Needs attention");
        if (health.PendingReconciliationCount > 0 || health.ActiveJobsCount > 0) return SurfaceText.Get("Library.Condition.Working", "Working");
        return SurfaceText.Get("Library.Condition.AllGood", "All good");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B") : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static string DescribeActivity(string eventType) => eventType switch
    {
        "PROFILE_CREATED" => SurfaceText.Get("Activity.ProfileCreated", "Profile created"),
        "PROFILE_RENAMED" => SurfaceText.Get("Activity.ProfileRenamed", "Profile renamed"),
        "PROFILE_TRASHED" => SurfaceText.Get("Activity.ProfileTrashed", "Profile moved to Trash"),
        "PROFILE_RESTORED" => SurfaceText.Get("Activity.ProfileRestored", "Profile restored"),
        "ASSET_ACTIVATED" => SurfaceText.Get("Activity.AssetActivated", "Media added to library"),
        "ASSET_TRASHED" => SurfaceText.Get("Activity.AssetTrashed", "Media moved to Trash"),
        "ASSET_RESTORED" => SurfaceText.Get("Activity.AssetRestored", "Media restored"),
        "ASSET_PURGED" => SurfaceText.Get("Activity.AssetPurged", "Media deleted permanently"),
        "OWNER_CHANGED" => SurfaceText.Get("Activity.OwnerChanged", "Primary profile changed"),
        "IMPORT_COMMITTED" => SurfaceText.Get("Activity.ImportCommitted", "Import finished"),
        "IMPORT_CANCELLED" => SurfaceText.Get("Activity.ImportCancelled", "Import cancelled"),
        "FACE_CONFIRMED" => SurfaceText.Get("Activity.FaceConfirmed", "Face association confirmed"),
        "FACE_REJECTED" => SurfaceText.Get("Activity.FaceRejected", "Face association rejected"),
        _ => eventType,
    };

    private void Navigate(AppRoute route) => _navigation?.Navigate(route);
    private void OnImportActivityChanged(object? sender, ImportActivitySnapshot snapshot) => ApplyImportActivity(snapshot);

    private void ApplyImportActivity(ImportActivitySnapshot snapshot)
    {
        if (_disposed) return;
        _activeImports.Clear();
        foreach (var item in snapshot.Items.Where(item => item.IsActive || item.CanContinue).Take(3)) _activeImports.Add(item);
        RaisePropertyChanged(nameof(ShowActiveImports));
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed) return;
        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Profile:
            case CatalogInvalidationDomain.Category:
            case CatalogInvalidationDomain.Tag:
            case CatalogInvalidationDomain.Appearance:
                SignalCoalescer(ref _galleryCoalescer, RefreshGalleryAsync); break;
            case CatalogInvalidationDomain.Health:
            case CatalogInvalidationDomain.Import:
                SignalCoalescer(ref _attentionCoalescer, RefreshAttentionAsync); break;
            case CatalogInvalidationDomain.Related:
                SignalCoalescer(ref _relatedCoalescer, RefreshRelatedAsync); break;
            case CatalogInvalidationDomain.Activity:
                SignalCoalescer(ref _chronicleCoalescer, RefreshChronicleAsync); break;
        }
    }

    private void SignalCoalescer(ref RefreshCoalescer? field, Func<CancellationToken, Task> action)
    {
        if (_disposed) return;
        field ??= new RefreshCoalescer(ct => _disposed ? Task.CompletedTask : action(ct), TimeSpan.FromMilliseconds(600));
        UiDispatch.Run(() => field.Signal());
    }

    private async Task RefreshGalleryAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;
        var spotlightTask = _catalog.GalleryReads.GetSpotlightCandidatesAsync(MaxSpotlightCandidates, cancellationToken);
        var recentTask = _catalog.GalleryReads.GetGalleryPageAsync(new GalleryQuery(SortOrder: GallerySortOrder.UpdatedAtDesc, PageSize: RecentlyActiveCount, KindFilter: GalleryProfileKindFilter.NormalOnly), cancellationToken);
        await Task.WhenAll(spotlightTask, recentTask).ConfigureAwait(false);
        if (_disposed) return;

        var spotlightCandidates = spotlightTask.Result;
        var recentPage = recentTask.Result;
        var paths = _catalog.Paths;
        var mediaReads = _catalog.MediaReads;
        var (spotlightResolved, recentCovers) = await Task.Run(() =>
        {
            var spotResults = new List<(HomeSpotlightCandidateReadModel Candidate, string? BannerPath, string? CoverPath, string? BannerVideoPath)>();
            foreach (var item in spotlightCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bannerPath = item.Summary.BannerStillTimestampMilliseconds is null
                    ? ResolveDerivedPresentationPath(mediaReads, paths, item.Summary.BannerAssetId, preferBanner: true)
                    : null;
                var coverPath = item.Summary.CoverStillTimestampMilliseconds is null
                    ? ResolveDerivedPresentationPath(mediaReads, paths, item.Summary.CoverAssetId, preferBanner: false)
                    : null;
                var bannerVideoPath = item.Summary.EffectiveBannerSourceKind == BannerVisualSourceKind.VideoClip ? ResolveBannerVideoPath(mediaReads, paths, item.Summary.BannerAssetId) : null;
                spotResults.Add((item, bannerPath, coverPath, bannerVideoPath));
            }
            var recentCoverPaths = new Dictionary<Guid, string?>();
            foreach (var summary in recentPage.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (summary.CoverAssetId.HasValue && !recentCoverPaths.ContainsKey(summary.CoverAssetId.Value))
                    recentCoverPaths[summary.CoverAssetId.Value] = ResolveDerivedPresentationPath(mediaReads, paths, summary.CoverAssetId, preferBanner: false);
            }
            return (spotResults, recentCoverPaths);
        }, cancellationToken).ConfigureAwait(false);

        if (_derivedStills is not null)
        {
            for (var i = 0; i < spotlightResolved.Count; i++)
            {
                var item = spotlightResolved[i];
                var bannerPath = item.BannerPath;
                var coverPath = item.CoverPath;
                if (item.Candidate.Summary.BannerStillTimestampMilliseconds is { } bannerTs
                    && item.Candidate.Summary.BannerAssetId is { } bannerAssetId)
                {
                    bannerPath = await ResolveHomeStillAsync(
                            bannerAssetId,
                            bannerTs,
                            item.Candidate.Summary.BannerContentFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (item.Candidate.Summary.CoverStillTimestampMilliseconds is { } coverTs
                    && item.Candidate.Summary.CoverAssetId is { } coverAssetId)
                {
                    coverPath = await ResolveHomeStillAsync(
                            coverAssetId,
                            coverTs,
                            item.Candidate.Summary.CoverContentFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                spotlightResolved[i] = (item.Candidate, bannerPath, coverPath, item.BannerVideoPath);
            }

            foreach (var summary in recentPage.Items)
            {
                if (summary.CoverStillTimestampMilliseconds is { } coverTs
                    && summary.CoverAssetId is { } coverAssetId)
                {
                    recentCovers[coverAssetId] = await ResolveHomeStillAsync(
                            coverAssetId,
                            coverTs,
                            summary.CoverContentFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (_videoPreviewRegeneration is not null)
        {
            foreach (var item in spotlightResolved)
            {
                if (item.BannerVideoPath is null
                    && item.Candidate.Summary.EffectiveBannerSourceKind == BannerVisualSourceKind.VideoClip
                    && item.Candidate.Summary.BannerAssetId is { } bannerAssetId)
                {
                    await _videoPreviewRegeneration
                        .RequeueMissingCurrentAsync(bannerAssetId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (_disposed) return;
        UiDispatch.Run(() => { ApplySpotlight(spotlightResolved); ApplyRecentlyActive(recentPage, recentCovers); });
    }

    private async Task RefreshAttentionAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;
        var healthTask = _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken);
        var unitsTask = _importReads is null ? Task.FromResult<IReadOnlyList<ImportUnitSummary>>([]) : _importReads.ListRecentUnitsAsync(50, cancellationToken);
        await Task.WhenAll(healthTask, unitsTask).ConfigureAwait(false);
        if (_disposed) return;
        UiDispatch.Run(() => { ApplyNeedsAttention(unitsTask.Result, healthTask.Result); ApplyVaultPulse(healthTask.Result); });
    }

    private async Task RefreshRelatedAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;
        var featuredConnection = await _catalog.RelatedReads.GetFeaturedConnectionAsync(cancellationToken);
        if (!_disposed) UiDispatch.Run(() => ApplyFeaturedConnection(featuredConnection));
    }

    private async Task RefreshChronicleAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null) return;
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var start = new DateTimeOffset(localNow.Year, localNow.Month, 1, 0, 0, 0, localNow.Offset);
        var chronicle = await _catalog.ActivityReads.GetMonthlyChronicleSummaryAsync(start.ToUniversalTime(), start.AddMonths(1).ToUniversalTime(), cancellationToken);
        if (!_disposed) UiDispatch.Run(() => ApplyChronicle(chronicle));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _galleryCoalescer?.Dispose();
        _attentionCoalescer?.Dispose();
        _relatedCoalescer?.Dispose();
        _chronicleCoalescer?.Dispose();
        if (_activity is not null) _activity.Changed -= OnImportActivityChanged;
        if (_catalog is not null) _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        _loadGuard.Dispose();
    }
}

public sealed class HomeSpotlightViewModel
{
    public HomeSpotlightViewModel(GalleryProfileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ProfileId = summary.ProfileId; DisplayName = summary.DisplayName; CategoryName = summary.CategoryName; Tags = summary.Tags; Rating = summary.Rating; IsFavorite = summary.IsFavorite; CoverAssetId = summary.CoverAssetId; BannerAssetId = summary.BannerAssetId; MediaCount = summary.ActiveOwnedAssetCount;
    }
    public Guid ProfileId { get; }
    public string DisplayName { get; }
    public string? CategoryName { get; }
    public IReadOnlyList<string> Tags { get; }
    public int? Rating { get; }
    public bool IsFavorite { get; }
    public Guid? CoverAssetId { get; }
    public Guid? BannerAssetId { get; }
    public long MediaCount { get; }
    public string? OverviewExcerpt { get; init; }
    public string? BannerImagePath { get; init; }
    public string? BannerVideoPath { get; init; }
    public bool HasBannerVideo => !string.IsNullOrWhiteSpace(BannerVideoPath);
    public string? CoverImagePath { get; init; }
    public string? HeroImagePath => !string.IsNullOrWhiteSpace(BannerImagePath) ? BannerImagePath : !string.IsNullOrWhiteSpace(CoverImagePath) ? CoverImagePath : null;
    public bool HasHeroImage => !string.IsNullOrWhiteSpace(HeroImagePath);
    public bool HasCoverImage => !string.IsNullOrWhiteSpace(CoverImagePath);
    public bool HasAppearance => CoverAssetId.HasValue || BannerAssetId.HasValue;
    public string MediaCountText => MediaCount == 1 ? SurfaceText.Get("Media.Count.One", "1 item") : SurfaceText.Format("Media.Count.Many", "{0} items", MediaCount);
    public string TagSummary => Tags.Count == 0 ? string.Empty : string.Join(" · ", Tags.Take(4));
    public string RatingText => Rating is int rating ? $"{rating}/5" : string.Empty;
    public string IdentityNote => !string.IsNullOrWhiteSpace(OverviewExcerpt) ? OverviewExcerpt! : TagSummary.Length > 0 ? TagSummary : string.Empty;
}

public sealed class HomeProfileTileViewModel
{
    public HomeProfileTileViewModel(GalleryProfileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ProfileId = summary.ProfileId; DisplayName = summary.DisplayName; CoverAssetId = summary.CoverAssetId; MediaCount = summary.ActiveOwnedAssetCount; UpdatedAtUtc = summary.UpdatedAtUtc; IsFavorite = summary.IsFavorite;
    }
    public Guid ProfileId { get; }
    public string DisplayName { get; }
    public Guid? CoverAssetId { get; }
    public string? CoverImagePath { get; init; }
    public bool HasCoverImage => !string.IsNullOrWhiteSpace(CoverImagePath);
    public long MediaCount { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public bool IsFavorite { get; }
    public string MediaCountText => MediaCount == 1 ? SurfaceText.Get("Media.Count.One", "1 item") : SurfaceText.Format("Media.Count.Many", "{0} items", MediaCount);
}

public enum HomeAttentionKind { VerificationReady, ImportFailure, SourceCleanupFailure, PathReconciliationFailure, FaceReviewPending, LibraryHealthCritical }
public sealed record HomeAttentionItem(HomeAttentionKind Kind, string Title, string Detail, AppRoute? Route);
public sealed record HomeDiscoveryItem(Guid ProfileId, string ProfileDisplayName, Guid RelatedProfileId, string RelatedDisplayName, string EvidenceSummary);

public sealed record HomeActivityItem(Guid ActivityId, string Description, DateTimeOffset OccurredAtUtc, Guid? ProfileId)
{
    public string RelativeTimeText => DescribeRelative(OccurredAtUtc, DateTimeOffset.UtcNow);
    public static string DescribeRelative(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment; if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return SurfaceText.Get("Time.JustNow", "Just now");
        if (elapsed < TimeSpan.FromHours(1)) { var minutes = (int)elapsed.TotalMinutes; return minutes == 1 ? SurfaceText.Get("Time.OneMinute", "1 minute ago") : SurfaceText.Format("Time.Minutes", "{0} minutes ago", minutes); }
        if (elapsed < TimeSpan.FromDays(1)) { var hours = (int)elapsed.TotalHours; return hours == 1 ? SurfaceText.Get("Time.OneHour", "1 hour ago") : SurfaceText.Format("Time.Hours", "{0} hours ago", hours); }
        if (elapsed < TimeSpan.FromDays(30)) { var days = (int)elapsed.TotalDays; return days == 1 ? SurfaceText.Get("Time.Yesterday", "Yesterday") : SurfaceText.Format("Time.Days", "{0} days ago", days); }
        return TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}

public sealed record HomeVaultPulse(long ActiveProfileCount, long ActiveAssetCount, long TrashCount, string StorageSummary, string HealthState)
{
    public static HomeVaultPulse Unknown { get; } = new(0, 0, 0, "-", "-");
}
