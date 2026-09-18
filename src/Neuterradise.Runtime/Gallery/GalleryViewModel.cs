using Neuterradise.App.SystemServices.Cache;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Input;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Home;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Gallery;

public sealed class GalleryViewModel : ScreenStateViewModel, IDisposable
{
    public static readonly IReadOnlyList<int> PageSizeOptions = GalleryPageSizes.Allowed;

    public static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(150);

    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly SettingsOperations? _settingsOperations;
    private readonly TimeSpan _searchDebounce;
    private readonly StaleResultGuard _queryGuard = new();
    private readonly StaleResultGuard _suggestionGuard = new();

    private readonly ObservableCollection<GalleryCardViewModel> _cards = [];
    private readonly ObservableCollection<GallerySearchSuggestion> _suggestions = [];
    private readonly ObservableCollection<GalleryFilterOption> _categories = [];
    private readonly ObservableCollection<GalleryFilterOption> _tags = [];
    private readonly ObservableCollection<GalleryFilterChip> _activeFilters = [];

    private string _searchText = string.Empty;
    private string? _selectedCategoryId;
    private string? _selectedTagId;
    private bool _favoritesOnly;
    private int? _minRating;
    private int? _maxRating;
    private bool _hasImages;
    private bool _hasVideos;
    private bool _hasModels;
    private Guid? _relatedToProfileId;
    private string? _relatedToProfileName;
    private bool _hasSharedMedia;
    private bool _hasConfirmedFaceRelation;
    private bool _hasManualRelation;
    private bool _showUnresolvedUnknown;
    private GallerySortOrder _sortOrder = GallerySortOrder.UpdatedAtDesc;

    private GalleryPresentationPreference _presentation = GalleryCardCatalog.Default;
    private bool _reduceMotion;

    private long _totalCount;
    private int _pageIndex = 1;
    private int _pageSize = GalleryPageSizes.Default;
    private int _totalPages = 1;
    private long _rangeStart;
    private long _rangeEnd;
    private bool _isSuggestionFlyoutOpen;
    private int _selectedSuggestionIndex = -1;
    private string? _errorNotice;
    private Guid? _selectedProfileId;
    private long _presentationMutationVersion;
    private readonly OverlayHostViewModel? _overlay;
    private readonly StillExtractionCoordinator? _derivedStills;
    private bool _disposed;

    private long _artworkResolveGeneration;
    private CancellationTokenSource? _artworkResolveCts;
    private readonly VideoPreviewCache? _videoPreviews;
    private readonly VideoPreviewRegenerationCoordinator? _videoPreviewRegeneration;

    public GalleryViewModel()
        : this(null, null)
    {
    }

    public GalleryViewModel(
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        TimeSpan? searchDebounce = null,
        SettingsOperations? settingsOperations = null,
        OverlayHostViewModel? overlay = null,
        StillExtractionCoordinator? derivedStills = null,
        GalleryOriginState? origin = null,
        VideoPreviewCache? videoPreviews = null,
        VideoPreviewRegenerationCoordinator? videoPreviewRegeneration = null)
    {
        _catalog = catalog;
        _navigation = navigation;
        _searchDebounce = searchDebounce ?? DefaultSearchDebounce;
        _settingsOperations = settingsOperations ?? (_catalog is not null ? new SettingsOperations(_catalog) : null);
        _overlay = overlay;
        _derivedStills = derivedStills;
        _videoPreviews = videoPreviews;
        _videoPreviewRegeneration = videoPreviewRegeneration;

        if (origin is not null)
        {
            ApplyOrigin(origin);
        }

        PickRelatedToProfileCommand = new AsyncRelayCommand(async () =>
        {
            if (_overlay is null)
            {
                return;
            }

            IReadOnlyList<ProfilePickerItem> candidates = [];
            if (_catalog is not null)
            {
                candidates = await new ProfilePickerReads(_catalog).GetAllCandidatesAsync(excludeProfileId: null);
            }

            var request = new ProfilePickerOverlayRequest(
                candidates,
                picked =>
                {
                    SetRelatedToProfile(picked.ProfileId, picked.DisplayName);
                },
                title: SurfaceText.Get("Gallery.RelatedPicker.Title", "Filter by Related Profile"),
                prompt: SurfaceText.Get("Gallery.RelatedPicker.Prompt", "Select a Profile to filter related Profiles:"));

            _overlay.Push(request);
        });

        SetPageSizeCommand = new RelayCommand(parameter =>
        {
            if (parameter is int size)
            {
                SetPageSize(size);
            }
            else if (parameter is string text && int.TryParse(text, out var parsed))
            {
                SetPageSize(parsed);
            }
        });

        RemoveFilterCommand = new RelayCommand(parameter =>
        {
            if (parameter is GalleryFilterChip chip)
            {
                RemoveFilter(chip.Kind);
            }
            else if (parameter is GalleryFilterKind kind)
            {
                RemoveFilter(kind);
            }
        });

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
        else
        {
            ShowEmpty();
        }
    }

    public ObservableCollection<GalleryCardViewModel> Cards => _cards;

    public ObservableCollection<GallerySearchSuggestion> Suggestions => _suggestions;

    public ObservableCollection<GalleryFilterOption> Categories => _categories;

    public ObservableCollection<GalleryFilterOption> Tags => _tags;

    public ObservableCollection<GalleryFilterChip> ActiveFilters => _activeFilters;

    public IReadOnlyList<GallerySortOption> SortOptions { get; } =
    [
        new(GallerySortOrder.UpdatedAtDesc, SurfaceText.Get("Gallery.Sort.Recent", "Recently Updated")),
        new(GallerySortOrder.DisplayNameAsc, SurfaceText.Get("Gallery.Sort.NameAsc", "Name A-Z")),
        new(GallerySortOrder.DisplayNameDesc, SurfaceText.Get("Gallery.Sort.NameDesc", "Name Z-A")),
        new(GallerySortOrder.RatingDesc, SurfaceText.Get("Gallery.Sort.RatingDesc", "Rating High-Low")),
        new(GallerySortOrder.RatingAsc, SurfaceText.Get("Gallery.Sort.RatingAsc", "Rating Low-High")),
        new(GallerySortOrder.MediaCountDesc, SurfaceText.Get("Gallery.Sort.MediaCount", "Media Count")),
    ];

    public IReadOnlyList<GalleryCardDefinition> CardVariants => GalleryCardCatalog.Variants;

    public IReadOnlyList<GalleryCardSize> CardSizes { get; } =
        [GalleryCardSize.Small, GalleryCardSize.Medium, GalleryCardSize.Large];

    public IReadOnlyList<GalleryRatingOption> RatingOptions { get; } =
    [
        new(null, SurfaceText.Get("Common.Any", "Any")),
        new(1, "1"),
        new(2, "2"),
        new(3, "3"),
        new(4, "4"),
        new(5, "5"),
    ];

    public string SearchText
    {
        get => _searchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _searchText, next))
            {
                RaisePropertyChanged(nameof(HasSearchText));
                TaskObserver.Observe(OnSearchTextChangedAsync(next), "GalleryViewModel.OnSearchTextChangedAsync");
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

    public string? SelectedCategoryId
    {
        get => _selectedCategoryId;
        set
        {
            if (SetProperty(ref _selectedCategoryId, value))
            {
                OnFilterChanged();
            }
        }
    }

    public string? SelectedTagId
    {
        get => _selectedTagId;
        set
        {
            if (SetProperty(ref _selectedTagId, value))
            {
                OnFilterChanged();
            }
        }
    }

    public string? ActiveCategoryId =>
        string.IsNullOrWhiteSpace(_selectedCategoryId) ? null : _selectedCategoryId;

    public string? ActiveTagId =>
        string.IsNullOrWhiteSpace(_selectedTagId) ? null : _selectedTagId;

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                OnFilterChanged();
            }
        }
    }

    public int? MinRating
    {
        get => _minRating;
        set
        {
            if (SetProperty(ref _minRating, value))
            {
                OnFilterChanged();
            }
        }
    }

    public int? MaxRating
    {
        get => _maxRating;
        set
        {
            if (SetProperty(ref _maxRating, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasImages
    {
        get => _hasImages;
        set
        {
            if (SetProperty(ref _hasImages, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasVideos
    {
        get => _hasVideos;
        set
        {
            if (SetProperty(ref _hasVideos, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasModels
    {
        get => _hasModels;
        set
        {
            if (SetProperty(ref _hasModels, value))
            {
                OnFilterChanged();
            }
        }
    }

    public Guid? RelatedToProfileId => _relatedToProfileId;

    public string? RelatedToProfileName => _relatedToProfileName;

    public bool HasRelatedToProfileFilter => _relatedToProfileId.HasValue && _relatedToProfileId.Value != Guid.Empty;

    public string RelatedToProfileButtonLabel => HasRelatedToProfileFilter
        ? SurfaceText.Format(
            "Gallery.Related.Label",
            "Related: {0}",
            _relatedToProfileName ?? SurfaceText.Get("Common.Profile", "Profile"))
        : SurfaceText.Get("Gallery.Related.Select", "Select Profile...");

    public bool HasSharedMedia
    {
        get => _hasSharedMedia;
        set
        {
            if (SetProperty(ref _hasSharedMedia, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasConfirmedFaceRelation
    {
        get => _hasConfirmedFaceRelation;
        set
        {
            if (SetProperty(ref _hasConfirmedFaceRelation, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasManualRelation
    {
        get => _hasManualRelation;
        set
        {
            if (SetProperty(ref _hasManualRelation, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool ShowUnresolvedUnknown
    {
        get => _showUnresolvedUnknown;
        set
        {
            if (SetProperty(ref _showUnresolvedUnknown, value))
            {
                OnFilterChanged();
            }
        }
    }

    public GallerySortOrder SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value))
            {
                RaisePropertyChanged(nameof(SortDisplayName));
                TaskObserver.Observe(ReloadAsync(), "GalleryViewModel.ReloadAsync");
            }
        }
    }

    public string SortDisplayName =>
        SortOptions.FirstOrDefault(option => option.Order == _sortOrder)?.DisplayName ?? _sortOrder.ToString();

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion != value)
            {
                _reduceMotion = value;
                RaisePropertyChanged();
            }
        }
    }

    public GalleryPresentationPreference Presentation
    {
        get => _presentation;
        private set
        {
            if (SetProperty(ref _presentation, value))
            {
                RaisePropertyChanged(nameof(CardVariant));
                RaisePropertyChanged(nameof(CardSize));
                RaisePropertyChanged(nameof(InformationDensity));
            }
        }
    }

    public GalleryCardDefinition CardVariant => GalleryCardCatalog.ResolveVariant(_presentation);

    public GalleryCardSize CardSize => _presentation.CardSize;

    public GalleryInformationDensity InformationDensity => _presentation.InformationDensity;

    public long TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                RaisePropertyChanged(nameof(ResultCountText));
                RaisePropertyChanged(nameof(RangeText));
            }
        }
    }

    public string ResultCountText => _totalCount == 1
        ? SurfaceText.Get("Gallery.Count.One", "1 Profile")
        : SurfaceText.Format("Gallery.Count.Many", "{0} Profiles", _totalCount);

    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                RaisePropertyChanged(nameof(CanGoPrevious));
                RaisePropertyChanged(nameof(CanGoNext));
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        private set
        {
            if (SetProperty(ref _pageSize, value))
            {
                RaisePropertyChanged(nameof(RangeText));
            }
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (SetProperty(ref _totalPages, value))
            {
                RaisePropertyChanged(nameof(CanGoPrevious));
                RaisePropertyChanged(nameof(CanGoNext));
            }
        }
    }

    public bool CanGoPrevious => _pageIndex > 1;

    public bool CanGoNext => _pageIndex < _totalPages;

    public Guid? SelectedProfileId => _selectedProfileId;

    public string RangeText => SurfaceText.Format(
        "Gallery.Range",
        "{0}-{1} of {2}",
        _totalCount == 0 ? 0 : _rangeStart,
        _totalCount == 0 ? 0 : _rangeEnd,
        _totalCount);

    public bool HasActiveFilters => _activeFilters.Count > 0;

    public bool IsNoResults => IsEmpty && (HasActiveFilters || HasSearchText);

    public bool IsEmptyLibrary => IsEmpty && !HasActiveFilters && !HasSearchText;

    public bool IsSuggestionFlyoutOpen
    {
        get => _isSuggestionFlyoutOpen;
        private set => SetProperty(ref _isSuggestionFlyoutOpen, value);
    }

    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        private set
        {
            if (SetProperty(ref _selectedSuggestionIndex, value))
            {
                RaisePropertyChanged(nameof(SelectedSuggestion));
            }
        }
    }

    public GallerySearchSuggestion? SelectedSuggestion =>
        _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _suggestions.Count
            ? _suggestions[_selectedSuggestionIndex]
            : null;

    public string? ErrorNotice
    {
        get => _errorNotice;
        private set => SetProperty(ref _errorNotice, value);
    }

    public ICommand SetPageSizeCommand { get; }
    public ICommand RemoveFilterCommand { get; }
    public ICommand PickRelatedToProfileCommand { get; }

    public GalleryQuery BuildQuery() => new(
        SearchText: string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
        CategoryId: ActiveCategoryId,
        TagId: ActiveTagId,
        IsFavorite: _favoritesOnly ? true : null,
        MinRating: _minRating,
        HasMedia: null,
        HasRelatedEvidence: null,
        SortOrder: _sortOrder,
        PageSize: _pageSize,
        PageIndex: _pageIndex,
        HasImages: _hasImages ? true : null,
        HasVideos: _hasVideos ? true : null,
        HasModels: _hasModels ? true : null,
        MaxRating: _maxRating,
        RelatedToProfileId: _relatedToProfileId,
        HasSharedMedia: _hasSharedMedia ? true : null,
        HasConfirmedFaceRelation: _hasConfirmedFaceRelation ? true : null,
        HasManualRelation: _hasManualRelation ? true : null,
        KindFilter: _showUnresolvedUnknown
            ? GalleryProfileKindFilter.NormalAndUnresolvedUnknown
            : GalleryProfileKindFilter.NormalOnly);

    public void ApplyFacets(GalleryFacets facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        ReconcileFacets(_categories, facets.Categories);
        ReconcileFacets(_tags, facets.Tags);

        RaisePropertyChanged(nameof(SelectedCategoryId));
        RaisePropertyChanged(nameof(SelectedTagId));
    }

    private static void ReconcileFacets(
        ObservableCollection<GalleryFilterOption> target,
        IReadOnlyList<GalleryFilterOption> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var existingIndex = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (string.Equals(target[candidate].Id, item.Id, StringComparison.Ordinal))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, item);
            }
            else
            {
                if (existingIndex != index)
                {
                    target.Move(existingIndex, index);
                }

                if (target[index] != item)
                {
                    target[index] = item;
                }
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    public async Task InitializeAsync()
    {
        if (_settingsOperations is not null)
        {
            try
            {
                var pref = await _settingsOperations.GetGalleryPresentationPreferencesAsync();
                Presentation = pref;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Gallery presentation preferences could not be loaded: {0}", exception.GetType().Name);
                ErrorNotice = SurfaceText.Get(
                    "Gallery.Error.PreferencesLoad",
                    "Gallery preferences could not be loaded.");
            }
        }

        if (_catalog is null)
        {
            return;
        }

        try
        {
            var facets = await _catalog.GalleryReads.GetGalleryFacetsAsync();
            ApplyFacets(facets);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Gallery filter options could not be loaded: {0}", exception.GetType().Name);
            ErrorNotice = SurfaceText.Get(
                "Gallery.Error.FiltersLoad",
                "Filter options could not be loaded.");
        }

        await LoadCurrentPageAsync();
    }

    /// <summary>
    /// Reloads the Gallery from page 1 under the current search/filter/sort/page-size state.
    /// Every filter, search, sort or page-size mutation routes through here so the contract
    /// "query/filter/sort/page-size change resets to page 1" holds uniformly. Explicit page
    /// navigation (First/Previous/Next/Last) goes through <see cref="GoToPageAsync"/> instead;
    /// restoring an exact page from Gallery Back navigation goes through
    /// <see cref="LoadCurrentPageAsync"/> directly so the restored page is preserved.
    /// </summary>
    public async Task ReloadAsync()
    {
        PageIndex = 1;
        await LoadCurrentPageAsync();
    }

    public async Task RefreshAsync()
    {
        if (_catalog is null)
        {
            return;
        }

        try
        {
            var facets = await _catalog.GalleryReads.GetGalleryFacetsAsync().ConfigureAwait(true);
            ApplyFacets(facets);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Gallery filter options could not be refreshed: {0}", exception.GetType().Name);
            ErrorNotice = SurfaceText.Get(
                "Gallery.Error.FiltersRefresh",
                "Filter options could not be refreshed.");
        }

        await LoadCurrentPageAsync().ConfigureAwait(true);
    }

    public async Task GoToPageAsync(int requestedPageIndex)
    {
        if (_catalog is null)
        {
            return;
        }

        var targetIndex = Math.Max(1, requestedPageIndex);
        if (targetIndex == _pageIndex)
        {
            return;
        }

        PageIndex = targetIndex;
        await LoadCurrentPageAsync();
    }

    private async Task LoadCurrentPageAsync()
    {
        RebuildActiveFilters();

        if (_catalog is null)
        {
            return;
        }

        var token = _queryGuard.Begin();

        if (_cards.Count == 0)
        {
            ShowLoading();
        }
        else
        {
            BeginBackgroundUpdate();
        }

        try
        {
            var page = await _catalog.GalleryReads
                .GetGalleryPageAsync(BuildQuery(), token.CancellationToken);

            if (!_queryGuard.IsCurrent(token))
            {
                return;
            }

            ApplyPage(page);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (_queryGuard.IsCurrent(token))
            {
                Trace.TraceWarning("Gallery page load failed: {0}", exception.GetType().Name);
                ShowRecoverableError(SurfaceText.Get("Gallery.Error.Load", "Gallery could not be loaded."));
            }
        }
        finally
        {
            if (_queryGuard.IsCurrent(token))
            {
                EndBackgroundUpdate();
            }
        }
    }

    /// <summary>
    /// Changes the active page size (24/48/96) and reloads from page 1, per the R13 contract
    /// that a page-size change resets pagination.
    /// </summary>
    public void SetPageSize(int size)
    {
        var normalized = GalleryPageSizes.Normalize(size);
        if (normalized == _pageSize)
        {
            return;
        }

        PageSize = normalized;
        PageIndex = 1;
        TaskObserver.Observe(ReloadAsync(), "GalleryViewModel.ReloadAsync");
    }

    /// <summary>Decode widths near what a gallery card actually shows.</summary>
    private const int CardCoverDecodeWidth = 480;
    private const int CardBannerDecodeWidth = 960;

    /// <summary>
    /// Cancels the previous artwork resolution batch and starts a new one for the current page.
    /// One batch for the entire page avoids spawning one Task.Run per card.
    /// </summary>
    private void CancelAndScheduleArtworkBatch(IReadOnlyList<GalleryCardViewModel> cards)
    {
        _artworkResolveCts?.Cancel();
        _artworkResolveCts?.Dispose();
        _artworkResolveCts = null;

        if (_catalog?.Paths is not { } paths || cards.Count == 0)
        {
            return;
        }

        var generation = ++_artworkResolveGeneration;
        var cts = new CancellationTokenSource();
        _artworkResolveCts = cts;

        // Build an immutable snapshot of what the batch needs.
        var snapshot = new List<ArtworkResolveRequest>(cards.Count);
        foreach (var card in cards)
        {
            snapshot.Add(new ArtworkResolveRequest(
                card.ProfileId,
                card.CoverAssetId,
                card.BannerAssetId,
                card.CoverStillTimestampMilliseconds,
                card.BannerStillTimestampMilliseconds,
                card.BannerContentFingerprint,
                card.CoverContentFingerprint,
                card.BannerManagedRelativePath,
                card.BannerManagedFileName,
                card.HasVideoBanner,
                card.RowVersion));
        }

        TaskObserver.Observe(
            ResolveArtworkBatchAsync(generation, snapshot, paths, cts.Token),
            "Gallery artwork batch");
    }

    private async Task ResolveArtworkBatchAsync(
        long generation,
        IReadOnlyList<ArtworkResolveRequest> requests,
        VaultPaths paths,
        CancellationToken cancellationToken)
    {
        var stills = _derivedStills;

        // De-duplicate: resolve each unique asset at most once per direction.
        var coverRequests = new Dictionary<Guid, ArtworkResolveRequest>();
        var bannerRequests = new Dictionary<Guid, ArtworkResolveRequest>();
        foreach (var r in requests)
        {
            if (r.CoverAssetId.HasValue && !coverRequests.ContainsKey(r.CoverAssetId.Value))
            {
                coverRequests[r.CoverAssetId.Value] = r;
            }
            if (r.BannerAssetId.HasValue && !bannerRequests.ContainsKey(r.BannerAssetId.Value))
            {
                bannerRequests[r.BannerAssetId.Value] = r;
            }
        }

        // Resolve all paths off the UI thread.
        var resolved = await Task.Run(() =>
        {
            var coverPaths = new Dictionary<Guid, string?>();
            var bannerPaths = new Dictionary<Guid, string?>();
            var coverStills = new Dictionary<Guid, DerivedStillRequest>();
            var bannerStills = new Dictionary<Guid, DerivedStillRequest>();
            var motionPaths = new Dictionary<Guid, string?>();

            var mediaReads = _catalog?.MediaReads;

            foreach (var (assetId, req) in coverRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (req.CoverStillTimestampMilliseconds is null)
                {
                    var fallback = HomeViewModel.ResolveManagedFallback(mediaReads, paths, assetId);
                    coverPaths[assetId] = HomeViewModel.ResolveDerivedPresentationPath(paths, assetId, preferBanner: false, fallback);
                }
                else if (stills is not null)
                {
                    var stillReq = new DerivedStillRequest(assetId, req.CoverStillTimestampMilliseconds.Value);
                    var published = stills.TryResolvePublishedPath(stillReq, req.CoverContentFingerprint);
                    if (published is not null)
                    {
                        coverPaths[assetId] = published;
                    }
                    else
                    {
                        coverStills[assetId] = stillReq;
                    }
                }
            }

            foreach (var (assetId, req) in bannerRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (req.BannerStillTimestampMilliseconds is { } bannerTimestamp && stills is not null)
                {
                    var stillReq = new DerivedStillRequest(assetId, bannerTimestamp);
                    var published = stills.TryResolvePublishedPath(stillReq, req.BannerContentFingerprint);
                    if (published is not null)
                    {
                        bannerPaths[assetId] = published;
                    }
                    else
                    {
                        bannerStills[assetId] = stillReq;
                    }
                }
                else
                {
                    bannerPaths[assetId] = HomeViewModel.ResolveDerivedPresentationPath(
                        mediaReads,
                        paths,
                        assetId,
                        preferBanner: true);
                }
            }

            // J11.2: Use VideoPreviewCache for video banner hover, not full source video.
            var videoCache = stills is not null ? _videoPreviews : null;
            foreach (var req in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (req.HasVideoBanner
                    && req.BannerAssetId is { } bannerId
                    && !motionPaths.ContainsKey(bannerId))
                {
                    if (videoCache is not null
                        && !string.IsNullOrWhiteSpace(req.BannerContentFingerprint))
                    {
                        try
                        {
                            var key = new VideoPreviewCacheKey(
                                bannerId,
                                req.BannerContentFingerprint,
                                CacheVersionSet.Default.VideoPreviewVersion,
                                SystemServices.Jobs.Handlers.ProductionMediaToolPlanSource.VideoPreviewVariantKey);
                            var result = videoCache.Lookup(key);
                            motionPaths[bannerId] = result.Status == CacheLookupStatus.Hit
                                ? result.PhysicalPath
                                : null;
                        }
                        catch (Exception)
                        {
                            motionPaths[bannerId] = null;
                        }
                    }
                    else
                    {
                        motionPaths[bannerId] = null;
                    }
                }
            }

            return (coverPaths, bannerPaths, coverStills, bannerStills, motionPaths);
        }, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested || generation != _artworkResolveGeneration)
        {
            return;
        }

        if (_videoPreviewRegeneration is not null)
        {
            foreach (var request in requests)
            {
                if (request.HasVideoBanner
                    && request.BannerAssetId is { } bannerAssetId
                    && resolved.motionPaths.GetValueOrDefault(bannerAssetId) is null)
                {
                    await _videoPreviewRegeneration
                        .RequeueMissingCurrentAsync(bannerAssetId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        // Apply resolved paths on UI thread.
        UiDispatch.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested || generation != _artworkResolveGeneration)
            {
                return;
            }

            for (var i = 0; i < _cards.Count && i < requests.Count; i++)
            {
                var card = _cards[i];
                var req = requests[i];
                if (card.ProfileId != req.ProfileId || card.RowVersion != req.RowVersion)
                {
                    continue;
                }

                if (req.CoverAssetId.HasValue
                    && resolved.coverPaths.TryGetValue(req.CoverAssetId.Value, out var coverPath))
                {
                    card.CoverSource = ImageRef.FromPath(coverPath, CardCoverDecodeWidth) ?? card.CoverSource;
                }

                if (req.BannerAssetId.HasValue
                    && resolved.bannerPaths.TryGetValue(req.BannerAssetId.Value, out var bannerPath))
                {
                    card.BannerStillSource = ImageRef.FromPath(bannerPath, CardBannerDecodeWidth);
                }

                if (req.BannerAssetId.HasValue
                    && resolved.motionPaths.TryGetValue(req.BannerAssetId.Value, out var motionPath))
                {
                    card.BannerMotionPath = motionPath;
                }
            }
        });

        // Request missing stills through the coordinator (not duplicating generation).
        if (stills is not null && (resolved.coverStills.Count > 0 || resolved.bannerStills.Count > 0))
        {
            await ResolveMissingStillsAsync(generation, requests, resolved.coverStills, resolved.bannerStills, stills, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task ResolveMissingStillsAsync(
        long generation,
        IReadOnlyList<ArtworkResolveRequest> requests,
        Dictionary<Guid, DerivedStillRequest> coverStills,
        Dictionary<Guid, DerivedStillRequest> bannerStills,
        StillExtractionCoordinator stills,
        CancellationToken cancellationToken)
    {
        foreach (var (assetId, request) in coverStills)
        {
            if (cancellationToken.IsCancellationRequested || generation != _artworkResolveGeneration)
            {
                return;
            }
            try
            {
                var produced = await stills.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(false);
                if (produced is not null && generation == _artworkResolveGeneration)
                {
                    var capturedPath = produced;
                    UiDispatch.Run(() =>
                    {
                        if (generation != _artworkResolveGeneration) return;
                        foreach (var card in _cards)
                        {
                            if (card.CoverAssetId == assetId)
                            {
                                card.CoverSource = ImageRef.FromPath(capturedPath, CardCoverDecodeWidth);
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Trace.TraceWarning("Gallery cover still failed: {0}", ex.GetType().Name);
            }
        }

        foreach (var (assetId, request) in bannerStills)
        {
            if (cancellationToken.IsCancellationRequested || generation != _artworkResolveGeneration)
            {
                return;
            }
            try
            {
                var produced = await stills.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(false);
                if (produced is not null && generation == _artworkResolveGeneration)
                {
                    var capturedPath = produced;
                    UiDispatch.Run(() =>
                    {
                        if (generation != _artworkResolveGeneration) return;
                        foreach (var card in _cards)
                        {
                            if (card.BannerAssetId == assetId)
                            {
                                card.BannerStillSource = ImageRef.FromPath(capturedPath, CardBannerDecodeWidth);
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Trace.TraceWarning("Gallery banner still failed: {0}", ex.GetType().Name);
            }
        }
    }

    private sealed record ArtworkResolveRequest(
        Guid ProfileId,
        Guid? CoverAssetId,
        Guid? BannerAssetId,
        long? CoverStillTimestampMilliseconds,
        long? BannerStillTimestampMilliseconds,
        string? BannerContentFingerprint,
        string? CoverContentFingerprint,
        string? BannerManagedRelativePath,
        string? BannerManagedFileName,
        bool HasVideoBanner,
        long RowVersion);

    private static string? ResolveBannerMotionPath(VaultPaths paths, GalleryCardViewModel card)
    {
        if (!card.HasVideoBanner
            || card.BannerManagedRelativePath is not { } relativePath
            || card.BannerManagedFileName is not { } fileName)
        {
            return null;
        }

        try
        {
            var absolute = paths.ResolveVaultRelativePath(
                $"{relativePath}/{fileName}");
            return File.Exists(absolute) ? absolute : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return null;
        }
    }

    public void ApplyPage(GalleryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var current = _cards.ToDictionary(card => card.ProfileId);
        var desired = new List<GalleryCardViewModel>(page.Items.Count);
        foreach (var summary in page.Items)
        {
            if (current.TryGetValue(summary.ProfileId, out var existing)
                && string.Equals(existing.GalleryCardVariantId, summary.GalleryCardVariantId, StringComparison.Ordinal))
            {
                if (existing.RowVersion != summary.RowVersion)
                {
                    existing.UpdateFrom(summary, ReduceMotion);
                }
                desired.Add(existing);
                continue;
            }

            var card = new GalleryCardViewModel(summary, ReduceMotion);
            card.IsSelected = card.ProfileId == _selectedProfileId;
            desired.Add(card);
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var card = desired[index];
            if (index < _cards.Count && ReferenceEquals(_cards[index], card))
            {
                continue;
            }

            var existingIndex = _cards.IndexOf(card);
            if (existingIndex >= 0)
            {
                _cards.Move(existingIndex, index);
            }
            else
            {
                _cards.Insert(index, card);
            }
        }

        while (_cards.Count > desired.Count)
        {
            _cards.RemoveAt(_cards.Count - 1);
        }

        foreach (var card in _cards)
        {
            card.IsSelected = card.ProfileId == _selectedProfileId;
        }

        // One batch artwork resolution for the entire page, replacing the old per-card Task.Run.
        CancelAndScheduleArtworkBatch(_cards.ToList());

        TotalCount = page.TotalCount;
        PageIndex = page.PageIndex;
        PageSize = page.PageSize;
        TotalPages = page.TotalPages;
        _rangeStart = page.RangeStart;
        _rangeEnd = page.RangeEnd;
        RaisePropertyChanged(nameof(RangeText));

        if (_cards.Count == 0)
        {
            ShowEmpty();
        }
        else
        {
            ShowReady();
        }

        RaisePropertyChanged(nameof(IsNoResults));
        RaisePropertyChanged(nameof(IsEmptyLibrary));
    }

    public void OpenProfile(Guid profileId, GalleryOriginState? origin = null)
    {
        if (profileId == Guid.Empty)
        {
            return;
        }

        _navigation?.UpdateCurrentRoute(new GalleryRoute(origin ?? BuildOrigin()));
        _navigation?.Navigate(new ProfileRoute(profileId));
    }

    public void SelectProfile(Guid? profileId)
    {
        var normalized = profileId == Guid.Empty ? null : profileId;
        if (_selectedProfileId == normalized)
        {
            return;
        }

        _selectedProfileId = normalized;
        foreach (var card in _cards)
        {
            card.IsSelected = card.ProfileId == normalized;
        }
        RaisePropertyChanged(nameof(SelectedProfileId));
    }

    /// <summary>
    /// Captures the current search/filter/sort/page/page-size state so it can be restored
    /// verbatim when the user navigates back to Gallery from a Profile.
    /// </summary>
    public GalleryOriginState BuildOrigin(Guid? anchorProfileId = null, double anchorOffsetDip = 0) => new(
        SearchText: string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
        CategoryId: ActiveCategoryId,
        TagId: ActiveTagId,
        FavoritesOnly: _favoritesOnly,
        MinRating: _minRating,
        MaxRating: _maxRating,
        HasImages: _hasImages,
        HasVideos: _hasVideos,
        HasModels: _hasModels,
        RelatedToProfileId: _relatedToProfileId,
        RelatedToProfileName: _relatedToProfileName,
        HasSharedMedia: _hasSharedMedia,
        HasConfirmedFaceRelation: _hasConfirmedFaceRelation,
        HasManualRelation: _hasManualRelation,
        ShowUnresolvedUnknown: _showUnresolvedUnknown,
        SortOrder: _sortOrder,
        PageIndex: _pageIndex,
        PageSize: _pageSize,
        SelectedProfileId: _selectedProfileId,
        AnchorProfileId: anchorProfileId,
        AnchorOffsetDip: anchorOffsetDip);

    public void RestoreOrigin(GalleryOriginState origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _suggestionGuard.Begin();
        DismissSuggestions();
        ApplyOrigin(origin);
        RaiseOriginProperties();
        RebuildActiveFilters();
    }

    private void ApplyOrigin(GalleryOriginState origin)
    {
        _searchText = origin.SearchText ?? string.Empty;
        _selectedCategoryId = string.IsNullOrWhiteSpace(origin.CategoryId) ? null : origin.CategoryId;
        _selectedTagId = string.IsNullOrWhiteSpace(origin.TagId) ? null : origin.TagId;
        _favoritesOnly = origin.FavoritesOnly;
        _minRating = origin.MinRating;
        _maxRating = origin.MaxRating;
        _hasImages = origin.HasImages;
        _hasVideos = origin.HasVideos;
        _hasModels = origin.HasModels;
        _relatedToProfileId = origin.RelatedToProfileId;
        _relatedToProfileName = origin.RelatedToProfileName;
        _hasSharedMedia = origin.HasSharedMedia;
        _hasConfirmedFaceRelation = origin.HasConfirmedFaceRelation;
        _hasManualRelation = origin.HasManualRelation;
        _showUnresolvedUnknown = origin.ShowUnresolvedUnknown;
        _sortOrder = origin.SortOrder;
        _pageIndex = Math.Max(1, origin.PageIndex);
        _pageSize = GalleryPageSizes.Normalize(origin.PageSize);
        _selectedProfileId = origin.SelectedProfileId;
    }

    private void RaiseOriginProperties()
    {
        RaisePropertyChanged(nameof(SearchText));
        RaisePropertyChanged(nameof(HasSearchText));
        RaisePropertyChanged(nameof(SelectedCategoryId));
        RaisePropertyChanged(nameof(SelectedTagId));
        RaisePropertyChanged(nameof(ActiveCategoryId));
        RaisePropertyChanged(nameof(ActiveTagId));
        RaisePropertyChanged(nameof(FavoritesOnly));
        RaisePropertyChanged(nameof(MinRating));
        RaisePropertyChanged(nameof(MaxRating));
        RaisePropertyChanged(nameof(HasImages));
        RaisePropertyChanged(nameof(HasVideos));
        RaisePropertyChanged(nameof(HasModels));
        RaisePropertyChanged(nameof(RelatedToProfileId));
        RaisePropertyChanged(nameof(RelatedToProfileName));
        RaisePropertyChanged(nameof(HasRelatedToProfileFilter));
        RaisePropertyChanged(nameof(RelatedToProfileButtonLabel));
        RaisePropertyChanged(nameof(HasSharedMedia));
        RaisePropertyChanged(nameof(HasConfirmedFaceRelation));
        RaisePropertyChanged(nameof(HasManualRelation));
        RaisePropertyChanged(nameof(ShowUnresolvedUnknown));
        RaisePropertyChanged(nameof(SortOrder));
        RaisePropertyChanged(nameof(SortDisplayName));
        RaisePropertyChanged(nameof(PageIndex));
        RaisePropertyChanged(nameof(PageSize));
        RaisePropertyChanged(nameof(SelectedProfileId));
    }

    public void SetRelatedToProfile(Guid? profileId, string? displayName)
    {
        _relatedToProfileId = profileId == Guid.Empty ? null : profileId;
        _relatedToProfileName = _relatedToProfileId is null ? null : displayName;

        RaisePropertyChanged(nameof(RelatedToProfileId));
        RaisePropertyChanged(nameof(RelatedToProfileName));
        RaisePropertyChanged(nameof(HasRelatedToProfileFilter));
        RaisePropertyChanged(nameof(RelatedToProfileButtonLabel));
        OnFilterChanged();
    }

    public void ClearFilters()
    {
        _selectedCategoryId = null;
        _selectedTagId = null;
        _favoritesOnly = false;
        _minRating = null;
        _maxRating = null;
        _hasImages = false;
        _hasVideos = false;
        _hasModels = false;
        _relatedToProfileId = null;
        _relatedToProfileName = null;
        _hasSharedMedia = false;
        _hasConfirmedFaceRelation = false;
        _hasManualRelation = false;
        _showUnresolvedUnknown = false;
        _searchText = string.Empty;

        RaisePropertyChanged(nameof(SelectedCategoryId));
        RaisePropertyChanged(nameof(SelectedTagId));
        RaisePropertyChanged(nameof(ActiveCategoryId));
        RaisePropertyChanged(nameof(ActiveTagId));
        RaisePropertyChanged(nameof(FavoritesOnly));
        RaisePropertyChanged(nameof(MinRating));
        RaisePropertyChanged(nameof(MaxRating));
        RaisePropertyChanged(nameof(HasImages));
        RaisePropertyChanged(nameof(HasVideos));
        RaisePropertyChanged(nameof(HasModels));
        RaisePropertyChanged(nameof(RelatedToProfileId));
        RaisePropertyChanged(nameof(RelatedToProfileName));
        RaisePropertyChanged(nameof(HasRelatedToProfileFilter));
        RaisePropertyChanged(nameof(RelatedToProfileButtonLabel));
        RaisePropertyChanged(nameof(HasSharedMedia));
        RaisePropertyChanged(nameof(HasConfirmedFaceRelation));
        RaisePropertyChanged(nameof(HasManualRelation));
        RaisePropertyChanged(nameof(ShowUnresolvedUnknown));
        RaisePropertyChanged(nameof(SearchText));
        RaisePropertyChanged(nameof(HasSearchText));

        DismissSuggestions();
        OnFilterChanged();
    }

    public void RemoveFilter(GalleryFilterKind kind)
    {
        switch (kind)
        {
            case GalleryFilterKind.Search:
                SearchText = string.Empty;
                break;
            case GalleryFilterKind.Category:
                SelectedCategoryId = null;
                break;
            case GalleryFilterKind.Tag:
                SelectedTagId = null;
                break;
            case GalleryFilterKind.Favorites:
                FavoritesOnly = false;
                break;
            case GalleryFilterKind.Rating:
                _minRating = null;
                _maxRating = null;
                RaisePropertyChanged(nameof(MinRating));
                RaisePropertyChanged(nameof(MaxRating));
                OnFilterChanged();
                break;
            case GalleryFilterKind.HasImages:
                HasImages = false;
                break;
            case GalleryFilterKind.HasVideos:
                HasVideos = false;
                break;
            case GalleryFilterKind.HasModels:
                HasModels = false;
                break;
            case GalleryFilterKind.RelatedToProfile:
                SetRelatedToProfile(null, null);
                break;
            case GalleryFilterKind.HasSharedMedia:
                HasSharedMedia = false;
                break;
            case GalleryFilterKind.HasConfirmedFaceRelation:
                HasConfirmedFaceRelation = false;
                break;
            case GalleryFilterKind.HasManualRelation:
                HasManualRelation = false;
                break;
            case GalleryFilterKind.UnresolvedUnknown:
                ShowUnresolvedUnknown = false;
                break;
        }
    }

    public void SetCardVariant(string variantId)
    {
        if (GalleryCardCatalog.TryGetVariant(variantId, out var definition))
        {
            var previous = _presentation;
            Presentation = _presentation with { CardVariantId = definition.Id };
            PersistPresentation(previous);
        }
    }

    public async Task SetCardVariantAsync(string variantId, CancellationToken cancellationToken = default)
    {
        if (GalleryCardCatalog.TryGetVariant(variantId, out var definition))
        {
            var previous = _presentation;
            Presentation = _presentation with { CardVariantId = definition.Id };
            var version = Interlocked.Increment(ref _presentationMutationVersion);
            await PersistPresentationAsync(previous, version, cancellationToken).ConfigureAwait(true);
        }
    }

    public void SetCardSize(GalleryCardSize size)
    {
        var previous = _presentation;
        Presentation = _presentation with { CardSize = size };
        PersistPresentation(previous);
    }

    public void SetInformationDensity(GalleryInformationDensity density)
    {
        var previous = _presentation;
        Presentation = _presentation with { InformationDensity = density };
        PersistPresentation(previous);
    }

    private void PersistPresentation(GalleryPresentationPreference previous)
    {
        var version = Interlocked.Increment(ref _presentationMutationVersion);
        TaskObserver.Observe(PersistPresentationAsync(previous, version, CancellationToken.None), "GalleryViewModel.PersistPresentationAsync");
    }

    private async Task PersistPresentationAsync(
        GalleryPresentationPreference previous,
        long mutationVersion,
        CancellationToken cancellationToken)
    {
        if (_settingsOperations is null)
        {
            return;
        }

        try
        {
            var result = await _settingsOperations
                .SetGalleryPresentationPreferencesAsync(_presentation, cancellationToken)
                .ConfigureAwait(true);
            if (!result.IsSuccess && mutationVersion == Interlocked.Read(ref _presentationMutationVersion))
            {
                Presentation = previous;
                ErrorNotice = SurfaceText.Get(
                    "Gallery.Error.PreferencesSave",
                    "Gallery preferences could not be saved.");
            }
        }
        catch (Exception exception)
        {
            if (mutationVersion == Interlocked.Read(ref _presentationMutationVersion))
            {
                Trace.TraceWarning("Gallery presentation save failed: {0}", exception.GetType().Name);
                Presentation = previous;
                ErrorNotice = SurfaceText.Get(
                    "Gallery.Error.PreferencesSave",
                    "Gallery preferences could not be saved.");
            }
        }
    }

    public void MoveSuggestionSelection(int delta)
    {
        if (_suggestions.Count == 0)
        {
            SelectedSuggestionIndex = -1;
            return;
        }

        var next = _selectedSuggestionIndex + delta;
        if (next < 0)
        {
            next = _suggestions.Count - 1;
        }
        else if (next >= _suggestions.Count)
        {
            next = 0;
        }

        SelectedSuggestionIndex = next;
    }

    public void ApplySuggestion(GallerySearchSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        DismissSuggestions();

        switch (suggestion.Kind)
        {
            case GallerySuggestionKind.Profile when suggestion.ProfileId.HasValue:
                OpenProfile(suggestion.ProfileId.Value);
                break;

            case GallerySuggestionKind.Category:
                SelectedCategoryId = suggestion.Id;
                break;

            case GallerySuggestionKind.Tag:
                SelectedTagId = suggestion.Id;
                break;
        }
    }

    public void DismissSuggestions()
    {
        _suggestions.Clear();
        SelectedSuggestionIndex = -1;
        IsSuggestionFlyoutOpen = false;
    }

    private async Task OnSearchTextChangedAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _suggestionGuard.Begin();
            DismissSuggestions();
            await ReloadAsync();
            return;
        }

        var token = _suggestionGuard.Begin();

        try
        {
            if (_searchDebounce > TimeSpan.Zero)
            {
                await Task.Delay(_searchDebounce, token.CancellationToken);
            }

            if (!_suggestionGuard.IsCurrent(token))
            {
                return;
            }

            if (_catalog is not null)
            {
                IReadOnlyList<GallerySearchSuggestion> suggestions;
                try
                {
                    suggestions = await _catalog.GalleryReads
                        .GetSearchSuggestionsAsync(text, GalleryReads.MaxSearchSuggestions, token.CancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (_suggestionGuard.IsCurrent(token))
                    {
                        Trace.TraceWarning("Gallery suggestions failed: {0}", exception.GetType().Name);
                        ErrorNotice = SurfaceText.Get(
                            "Gallery.Error.Suggestions",
                            "Search suggestions are unavailable right now.");
                        DismissSuggestions();
                    }
                    suggestions = [];
                }

                if (!_suggestionGuard.IsCurrent(token))
                {
                    return;
                }

                _suggestions.Clear();
                foreach (var suggestion in suggestions)
                {
                    _suggestions.Add(suggestion);
                }

                SelectedSuggestionIndex = -1;
                IsSuggestionFlyoutOpen = _suggestions.Count > 0;
            }

            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnFilterChanged()
    {
        RaisePropertyChanged(nameof(HasActiveFilters));
        TaskObserver.Observe(ReloadAsync(), "GalleryViewModel.ReloadAsync");
    }

    private void RebuildActiveFilters()
    {
        _activeFilters.Clear();

        if (HasSearchText)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.Search,
                SurfaceText.Format("Gallery.Filter.Search", "Search: {0}", _searchText)));
        }

        if (ActiveCategoryId is not null)
        {
            var name = _categories.FirstOrDefault(c => c.Id == ActiveCategoryId)?.Name ?? ActiveCategoryId;
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.Category,
                SurfaceText.Format("Gallery.Filter.Category", "Category: {0}", name)));
        }

        if (ActiveTagId is not null)
        {
            var name = _tags.FirstOrDefault(t => t.Id == ActiveTagId)?.Name ?? ActiveTagId;
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.Tag,
                SurfaceText.Format("Gallery.Filter.Tag", "Tag: {0}", name)));
        }

        if (_favoritesOnly)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.Favorites,
                SurfaceText.Get("Gallery.Filter.Favorites", "Favorites")));
        }

        if (_minRating.HasValue || _maxRating.HasValue)
        {
            var any = SurfaceText.Get("Common.Any.Lower", "any");
            var low = _minRating?.ToString() ?? any;
            var high = _maxRating?.ToString() ?? any;
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.Rating,
                SurfaceText.Format("Gallery.Filter.Rating", "Rating {0}-{1}", low, high)));
        }

        if (_hasImages)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasImages,
                SurfaceText.Get("Gallery.Filter.HasImages", "Has Images")));
        }

        if (_hasVideos)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasVideos,
                SurfaceText.Get("Gallery.Filter.HasVideos", "Has Videos")));
        }

        if (_hasModels)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasModels,
                SurfaceText.Get("Gallery.Filter.HasModels", "Has Models")));
        }

        if (_relatedToProfileId.HasValue)
        {
            var label = _relatedToProfileName
                ?? SurfaceText.Get("Gallery.Filter.SelectedProfile", "selected Profile");
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.RelatedToProfile,
                SurfaceText.Format("Gallery.Filter.RelatedTo", "Related to {0}", label)));
        }

        if (_hasSharedMedia)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasSharedMedia,
                SurfaceText.Get("Gallery.Filter.SharedMedia", "Has Shared Media")));
        }

        if (_hasConfirmedFaceRelation)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasConfirmedFaceRelation,
                SurfaceText.Get("Gallery.Filter.ConfirmedFace", "Has Confirmed Face Relation")));
        }

        if (_hasManualRelation)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.HasManualRelation,
                SurfaceText.Get("Gallery.Filter.ManualRelation", "Has Manual Relation")));
        }

        if (_showUnresolvedUnknown)
        {
            _activeFilters.Add(new GalleryFilterChip(
                GalleryFilterKind.UnresolvedUnknown,
                SurfaceText.Get("Gallery.Filter.UnresolvedUnknown", "Unresolved (Unknown)")));
        }

        RaisePropertyChanged(nameof(HasActiveFilters));
        RaisePropertyChanged(nameof(IsNoResults));
        RaisePropertyChanged(nameof(IsEmptyLibrary));
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Profile:
            case CatalogInvalidationDomain.Category:
            case CatalogInvalidationDomain.Tag:
            case CatalogInvalidationDomain.Appearance:
                ScheduleRefresh();
                break;

            case CatalogInvalidationDomain.Related:
                if (_relatedToProfileId.HasValue
                    || _hasSharedMedia
                    || _hasConfirmedFaceRelation
                    || _hasManualRelation)
                {
                    ScheduleRefresh();
                }
                break;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        // One refresh per burst of catalog writes, not one per write.
        UiDispatch.Run(() => Coalescer.Signal());
    }

    private RefreshCoalescer? _refreshCoalescer;

    private RefreshCoalescer Coalescer => _refreshCoalescer ??= new RefreshCoalescer(
        _ => _disposed ? Task.CompletedTask : RefreshAsync(),
        TimeSpan.FromMilliseconds(600));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _artworkResolveCts?.Cancel();
        _artworkResolveCts?.Dispose();
        _artworkResolveCts = null;
        _refreshCoalescer?.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
        _queryGuard.Dispose();
        _suggestionGuard.Dispose();
    }
}

public sealed record GallerySortOption(GallerySortOrder Order, string DisplayName);

public sealed record GalleryRatingOption(int? Value, string DisplayName);

public enum GalleryFilterKind
{
    Search,
    Category,
    Tag,
    Favorites,
    Rating,
    HasImages,
    HasVideos,
    HasModels,
    RelatedToProfile,
    HasSharedMedia,
    HasConfirmedFaceRelation,
    HasManualRelation,
    UnresolvedUnknown
}

public sealed record GalleryFilterChip(GalleryFilterKind Kind, string DisplayText);

public sealed class GalleryCardViewModel : ObservableObject
{
    private GalleryProfileSummary _summary;
    private CoverAppearance _coverAppearance;
    private BannerPresentation _bannerPresentation;
    private ImageRef? _coverSource;
    private ImageRef? _bannerStillSource;
    private string? _bannerMotionPath;
    private bool _isSelected;

    public GalleryCardViewModel(GalleryProfileSummary summary, bool reduceMotion = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        ReduceMotion = reduceMotion;
        _summary = summary;
        _coverAppearance = CoverFrameCatalog
            .Resolve(Appearance.ToCoverAppearanceRequest(), reduceMotion)
            .Appearance;
        _bannerPresentation = BannerPresentationPolicy
            .Resolve(Appearance.ToBannerPresentationRequest())
            .Presentation;
    }

    public Guid ProfileId => _summary.ProfileId;

    public ProfileKind Kind => _summary.Kind;

    public string DisplayName => _summary.DisplayName;

    public string? StorageToken => _summary.StorageToken;

    public string? CategoryName => _summary.CategoryName;

    public IReadOnlyList<string> Tags => _summary.Tags;

    public int? Rating => _summary.Rating;

    public bool IsFavorite => _summary.IsFavorite;

    public Guid? CoverAssetId => _summary.CoverAssetId;

    public Guid? BannerAssetId => _summary.BannerAssetId;

    public long MediaCount => _summary.ActiveOwnedAssetCount;

    public int RelatedProfileCount => _summary.RelatedProfileCount;

    public DateTimeOffset UpdatedAtUtc => _summary.UpdatedAtUtc;

    public long RowVersion => _summary.RowVersion;

    public string? GalleryCardVariantId => _summary.GalleryCardVariantId;

    public ProfileAppearanceOverrides Appearance => _summary.EffectiveAppearance;

    public CoverAppearance CoverAppearance => _coverAppearance;

    public BannerPresentation BannerPresentation => _bannerPresentation;

    public bool ReduceMotion { get; }

    public long? CoverStillTimestampMilliseconds => Appearance.CoverStillTimestampMilliseconds;

    public long? BannerStillTimestampMilliseconds => Appearance.BannerStillTimestampMilliseconds;

    public bool HasVideoBanner => _summary.HasVideoBanner;

    public string? BannerManagedRelativePath => _summary.BannerManagedRelativePath;

    public string? BannerManagedFileName => _summary.BannerManagedFileName;

    public string? CoverContentFingerprint => _summary.CoverContentFingerprint;

    public string? BannerContentFingerprint => _summary.BannerContentFingerprint;

    public string? BannerMotionPath
    {
        get => _bannerMotionPath;
        set
        {
            if (SetProperty(ref _bannerMotionPath, value))
            {
                RaisePropertyChanged(nameof(HasBannerMotion));
            }
        }
    }

    public bool HasBannerMotion => !string.IsNullOrWhiteSpace(BannerMotionPath);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool UpdateFrom(GalleryProfileSummary summary, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.ProfileId != ProfileId)
        {
            throw new ArgumentException("A Gallery card cannot change Profile identity.", nameof(summary));
        }

        var artworkChanged = CoverAssetId != summary.CoverAssetId
            || BannerAssetId != summary.BannerAssetId
            || !string.Equals(CoverContentFingerprint, summary.CoverContentFingerprint, StringComparison.Ordinal)
            || !string.Equals(BannerContentFingerprint, summary.BannerContentFingerprint, StringComparison.Ordinal)
            || !string.Equals(BannerManagedRelativePath, summary.BannerManagedRelativePath, StringComparison.Ordinal)
            || Appearance != summary.EffectiveAppearance;

        _summary = summary;
        _coverAppearance = CoverFrameCatalog.Resolve(Appearance.ToCoverAppearanceRequest(), reduceMotion).Appearance;
        _bannerPresentation = BannerPresentationPolicy.Resolve(Appearance.ToBannerPresentationRequest()).Presentation;
        if (artworkChanged)
        {
            CoverSource = null;
            BannerStillSource = null;
            BannerMotionPath = null;
        }

        RaisePropertyChanged(nameof(Kind));
        RaisePropertyChanged(nameof(DisplayName));
        RaisePropertyChanged(nameof(StorageToken));
        RaisePropertyChanged(nameof(CategoryName));
        RaisePropertyChanged(nameof(Tags));
        RaisePropertyChanged(nameof(Rating));
        RaisePropertyChanged(nameof(IsFavorite));
        RaisePropertyChanged(nameof(CoverAssetId));
        RaisePropertyChanged(nameof(BannerAssetId));
        RaisePropertyChanged(nameof(MediaCount));
        RaisePropertyChanged(nameof(RelatedProfileCount));
        RaisePropertyChanged(nameof(UpdatedAtUtc));
        RaisePropertyChanged(nameof(RowVersion));
        RaisePropertyChanged(nameof(Appearance));
        RaisePropertyChanged(nameof(CoverAppearance));
        RaisePropertyChanged(nameof(BannerPresentation));
        RaisePropertyChanged(nameof(CoverStillTimestampMilliseconds));
        RaisePropertyChanged(nameof(BannerStillTimestampMilliseconds));
        RaisePropertyChanged(nameof(HasVideoBanner));
        RaisePropertyChanged(nameof(BannerManagedRelativePath));
        RaisePropertyChanged(nameof(CoverContentFingerprint));
        RaisePropertyChanged(nameof(BannerContentFingerprint));
        RaisePropertyChanged(nameof(IsUnresolvedUnknown));
        RaisePropertyChanged(nameof(HasRelatedIndicator));
        RaisePropertyChanged(nameof(MediaCountText));
        RaisePropertyChanged(nameof(RatingText));
        RaisePropertyChanged(nameof(PresentationModel));
        return artworkChanged;
    }

    public ImageRef? CoverSource
    {
        get => _coverSource;
        set => SetProperty(ref _coverSource, value);
    }

    public ImageRef? BannerStillSource
    {
        get => _bannerStillSource;
        set => SetProperty(ref _bannerStillSource, value);
    }

    public bool IsUnresolvedUnknown => Kind == ProfileKind.Unknown;

    public bool HasRelatedIndicator => RelatedProfileCount > 0;

    public string MediaCountText => MediaCount == 1
        ? SurfaceText.Get("Media.Count.One", "1 item")
        : SurfaceText.Format("Media.Count.Many", "{0} items", MediaCount);

    public string? RatingText => Rating.HasValue ? $"{Rating.Value}/5" : null;

    public ProfilePresentationModel ToPresentationModel() => new(
        ProfileId,
        DisplayName,
        CategoryName,
        Tags,
        Rating,
        IsFavorite,
        Overview: null,
        Notes: null,
        MediaCount: MediaCount,
        HasRelatedIndicator: HasRelatedIndicator,
        CardVariantId: GalleryCardVariantId);

    public ProfilePresentationModel PresentationModel => ToPresentationModel();
}
