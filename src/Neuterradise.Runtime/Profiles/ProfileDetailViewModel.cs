using Neuterradise.App.SystemServices.Cache;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Faces;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Profiles;

public sealed record ProfileCategoryOption(string? CategoryId, string DisplayName);

public sealed record ProfileTagOption(string TagId, string DisplayName);

public sealed class ProfileDetailViewModel : ScreenStateViewModel, IDisposable
{
    public const int MaxMaterializedMedia = 200;

    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly ProfileReads? _profileReads;
    private readonly ProfileOperations? _profileOperations;
    private readonly ProfileAppearanceOperations? _appearanceOperations;
    private readonly UnknownResolutionOperations? _unknownOperations;
    private readonly ImportAssignmentReviewReads? _assignmentReviewReads;
    private readonly RelatedReads? _relatedReads;
    private readonly FaceReads? _faceReads;
    private readonly FaceDecisionOperations? _faceDecisionOperations;
    private readonly MediaOperations? _mediaOperations;
    private readonly SettingsOperations? _settingsOperations;

    private bool _isDisposed;
    private ProfileDetailReadModel? _profile;
    private ProfilePresentationModel? _presentationModel;
    private IReadOnlyList<ProfileTagAssignment> _tagAssignments = [];
    private string _tagSearchText = string.Empty;
    private ProfileLayoutDefinition? _layoutDefinition;
    private string? _currentPresetId;
    private ProfileMediaFilter _selectedMediaFilter = ProfileMediaFilter.All;
    private FaceReviewItemViewModel? _selectedFace;

    private string _displayName = string.Empty;
    private ProfileKind _kind = ProfileKind.Normal;
    private long _unknownSequence;
    private long _activeOwnedAssetCount;
    private string? _categoryName;
    private string? _categoryId;
    private IReadOnlyList<string> _tags = [];
    private int? _rating;
    private bool _isFavorite;
    private string? _overview;
    private string? _notes;
    private Guid? _coverAssetId;
    private Guid? _bannerAssetId;
    private long _rowVersion;
    private Guid? _identityId;
    private int _identitySampleCount;
    private ManagedPathState _pathState = ManagedPathState.None;
    private string? _currentManagedRelativePath;

    private string? _folderStatusMessage;
    private string? _renameStatusMessage;
    private string? _unknownStatusNotice;
    private bool _isRenaming;
    private bool _isEditingMetadata;
    private string _editDisplayName = string.Empty;
    private string? _editCategoryId;
    private int? _editRating;
    private string? _editOverview;
    private string? _editNotes;
    private bool _editIsFavorite;
    private MediaDetailViewModel? _inspector;
    private readonly Neuterradise.App.Media.Model.ModelPreviewAdapterRegistry? _modelAdapters;
    private readonly OverlayHostViewModel? _overlay;
    private readonly StillExtractionCoordinator? _derivedStills;
    private readonly VideoPreviewCache? _videoPreviews;
    private string _defaultLayoutPresetId = ProfileLayoutResolver.FallbackPresetId;
    private string? _currentCardVariantId;
    private CoverAppearance? _coverAppearance;
    private BannerPresentation? _bannerPresentation;
    private ImageRef? _coverSource;
    private ImageRef? _bannerStillSource;
    private string? _coverImagePath;
    private string? _bannerImagePath;
    private bool _reduceMotion;
    private ProfileAppearanceOverrides? _appearanceOverrides;
    private readonly VideoPreviewRegenerationCoordinator? _videoPreviewRegeneration;

    public ProfileDetailViewModel(Guid profileId)
        : this(profileId, null, null, null, null, null, null, null, null, null, null, null, null, null)
    {
    }

    public ProfileDetailViewModel(
        Guid profileId,
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        ProfileReads? profileReads = null,
        ProfileOperations? profileOperations = null,
        ProfileAppearanceOperations? appearanceOperations = null,
        UnknownResolutionOperations? unknownOperations = null,
        RelatedReads? relatedReads = null,
        FaceReads? faceReads = null,
        FaceDecisionOperations? faceDecisionOperations = null,
        SettingsOperations? settingsOperations = null,
        string? defaultLayoutPresetId = null,
        OverlayHostViewModel? overlay = null,
        StillExtractionCoordinator? derivedStills = null,
        MediaOriginState? initialOrigin = null,
        Neuterradise.App.Media.Model.ModelPreviewAdapterRegistry? modelAdapters = null,
        Guid? initialInspectAssetId = null,
        VideoPreviewCache? videoPreviews = null,
        VideoPreviewRegenerationCoordinator? videoPreviewRegeneration = null)
    {
        ProfileId = profileId;
        _catalog = catalog;
        _modelAdapters = modelAdapters;
        _navigation = navigation;
        _derivedStills = derivedStills;
        _videoPreviews = videoPreviews;
        _videoPreviewRegeneration = videoPreviewRegeneration;
        _profileReads = profileReads ?? (_catalog is not null ? new ProfileReads(_catalog) : null);
        _profileOperations = profileOperations ?? (_catalog is not null ? new ProfileOperations(_catalog) : null);
        _appearanceOperations = appearanceOperations ?? (_catalog is not null ? new ProfileAppearanceOperations(_catalog) : null);
        _unknownOperations = unknownOperations ?? (_catalog is not null ? new UnknownResolutionOperations(_catalog) : null);
        _assignmentReviewReads = _catalog is not null ? new ImportAssignmentReviewReads(_catalog) : null;
        _relatedReads = relatedReads ?? _catalog?.RelatedReads;
        _faceReads = faceReads ?? (_catalog is not null ? new FaceReads(_catalog) : null);
        _faceDecisionOperations = faceDecisionOperations ?? (_catalog is not null ? new FaceDecisionOperations(_catalog) : null);
        _mediaOperations = _catalog is not null ? new MediaOperations(_catalog) : null;
        _settingsOperations = settingsOperations ?? (_catalog is not null ? new SettingsOperations(_catalog) : null);
        _defaultLayoutPresetId = defaultLayoutPresetId ?? ProfileLayoutResolver.FallbackPresetId;
        _overlay = overlay;

        MediaGrid = new MediaGridViewModel();
        if (initialOrigin is not null)
        {
            MediaGrid.RestoreState(initialOrigin);
        }
        MediaGrid.OpenMediaDetailAction = OpenMediaDetail;
        MediaGrid.OpenDefaultAppAction = assetId => TaskObserver.Observe(
            OpenMediaInDefaultAppAsync(assetId),
            "Opening media in its default app",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
        MediaGrid.QueryChangedAction = OnMediaGridQueryChanged;
        MediaGrid.ResolveVideoPreviewPathAsync = ResolveVideoPreviewPathAsync;
        MediaGrid.ReportRecoverableErrorAction = message => FolderStatusMessage = message;
        CloseInspectorCommand = new RelayCommand(_ => CloseInspector());

        if (_catalog is not null)
        {
            var assetWrites = _catalog.AssetWrites;
            MediaGrid.PersistFavoriteAction = (assetId, isFavorite) =>
                assetWrites.SetFavoriteAsync(assetId, isFavorite);
        }
        Related = new RelatedProfilesViewModel(
            _relatedReads,
            _catalog is not null ? new RelatedProfileOperations(_catalog) : null,
            ProfileId,
            _navigation,
            overlay: _overlay);

        GoBackCommand = new RelayCommand(_ => _navigation?.GoBack());
        OpenProfileFolderCommand = new AsyncRelayCommand(() => OpenProfileFolderAsync());

        ToggleFavoriteCommand = new AsyncRelayCommand(async () =>
        {
            await ToggleFavoriteAsync();
        });

        OpenMediaDetailCommand = new RelayCommand(param =>
        {
            if (param is Guid assetId && assetId != Guid.Empty)
            {
                OpenMediaDetail(assetId);
            }
        });

        ConfirmFaceCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFace is { } face)
            {
                await ConfirmFaceAsync(face);
            }
        });

        RejectFaceCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFace is { } face)
            {
                await RejectFaceAsync(face);
            }
        });

        OpenFaceReviewWorkspaceCommand = new RelayCommand(_ =>
        {
            _navigation?.Navigate(new FaceReviewRoute(ProfileId));
        });

        var resolver = new ProfileLayoutResolver();
        var initialSelection = resolver.Resolve(_defaultLayoutPresetId, null);
        _layoutDefinition = initialSelection.Definition;
        _currentPresetId = initialSelection.Definition.Id;

        var defaultCoverRequest = new CoverAppearanceRequest();
        _coverAppearance = CoverFrameCatalog.Resolve(defaultCoverRequest, _reduceMotion).Appearance;
        var defaultBannerRequest = new BannerPresentationRequest();
        _bannerPresentation = BannerPresentationPolicy.Resolve(defaultBannerRequest).Presentation;

        OpenCustomizeOverlayCommand = new RelayCommand(_ => OpenCustomizeOverlay());
        ChangeFaceProfileCommand = new AsyncRelayCommand(async param =>
        {
            var face = param as FaceReviewItemViewModel ?? SelectedFace;
            if (face is not null)
            {
                await OpenChangeFaceProfilePickerAsync(face).ConfigureAwait(true);
            }
        });

        if (initialInspectAssetId is { } inspectAssetId && inspectAssetId != Guid.Empty)
        {
            OpenInspector(inspectAssetId);
        }

        if (_profileReads is not null)
        {
            StartRouteTask(LoadAsync, "This profile could not be opened.");
            if (_catalog is not null)
            {
                _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
            }
        }
        else
        {
            ShowReady();
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_isDisposed || !IsRouteActive)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Appearance:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                UiDispatch.Run(SignalAppearance);
                break;
            case CatalogInvalidationDomain.Profile:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                UiDispatch.Run(SignalPage);
                break;
            case CatalogInvalidationDomain.Media:
                if (!IsMediaRelevant(invalidation.EntityIds))
                {
                    return;
                }
                UiDispatch.Run(SignalMedia);
                break;
            case CatalogInvalidationDomain.Related:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                UiDispatch.Run(SignalRelated);
                break;
        }
    }

    private bool IsMediaRelevant(IReadOnlyList<Guid> entityIds)
    {
        if (entityIds.Count == 0)
        {
            return true;
        }

        foreach (var id in entityIds)
        {
            if (MediaItems.Any(item => item.AssetId == id))
            {
                return true;
            }

            if (_inspector is { } inspector && inspector.AssetId == id)
            {
                return true;
            }
        }

        return false;
    }

    private void SignalPage()
    {
        if (!_isDisposed)
        {
            PageRefresh.Signal();
        }
    }

    private void SignalAppearance()
    {
        if (!_isDisposed)
        {
            AppearanceRefresh.Signal();
        }
    }

    private void SignalMedia()
    {
        if (!_isDisposed)
        {
            MediaRefresh.Signal();
        }
    }

    private void SignalRelated()
    {
        if (!_isDisposed)
        {
            RelatedRefresh.Signal();
        }
    }

    private RefreshCoalescer? _pageRefresh;
    private RefreshCoalescer? _appearanceRefresh;
    private RefreshCoalescer? _mediaRefresh;
    private RefreshCoalescer? _relatedRefresh;
    private CancellationTokenSource? _thumbnailLoads;
    private long _loadGeneration;
    private long _mediaLoadGeneration;
    private long _assignmentReviewLoadGeneration;
    private long _faceLoadGeneration;
    private readonly SemaphoreSlim _profileTagCommitGate = new(1, 1);
    private Task _pendingProfileTagCommit = Task.CompletedTask;

    private RefreshCoalescer PageRefresh => _pageRefresh ??= new RefreshCoalescer(
        ct =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                StartRouteTask(LoadAsync, "This profile could not be refreshed.");
            }

            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer AppearanceRefresh => _appearanceRefresh ??= new RefreshCoalescer(
        ct =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                StartRouteTask(async token =>
                {
                    if (_profileReads is null) return;
                    var detail = await _profileReads.GetDetailAsync(ProfileId, token).ConfigureAwait(true);
                    if (detail is not null)
                    {
                        PopulateProfile(detail);
                    }
                }, "Profile appearance could not be refreshed.");
            }

            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer MediaRefresh => _mediaRefresh ??= new RefreshCoalescer(
        _ => _isDisposed || !IsRouteActive ? Task.CompletedTask : LoadMediaAsync(),
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer RelatedRefresh => _relatedRefresh ??= new RefreshCoalescer(
        _ => _isDisposed || !IsRouteActive ? Task.CompletedTask : LoadRelatedAsync(),
        TimeSpan.FromMilliseconds(500));

    private const int CoverDecodeWidth = 640;
    private const int BannerDecodeWidth = 1600;
    private const int TileDecodeWidth = 256;

    private void ApplyCoverImage(string? path)
    {
        CoverSource = ImageRef.FromPath(path, CoverDecodeWidth);
    }

    private void ApplyBannerImage(string? path)
    {
        BannerStillSource = ImageRef.FromPath(path, BannerDecodeWidth);
    }

    private void LoadGridThumbnails()
    {
        _thumbnailLoads?.Cancel();
        _thumbnailLoads?.Dispose();
        if (_catalog?.Paths is not { } paths || _isDisposed)
        {
            _thumbnailLoads = null;
            return;
        }

        _thumbnailLoads = CancellationTokenSource.CreateLinkedTokenSource(RouteCancellationToken);
        var token = _thumbnailLoads.Token;
        var cards = MediaGrid.Cards.ToList();
        TaskObserver.Observe(LoadGridThumbnailsAsync(paths, cards, token), "Profile media thumbnails");
    }

    private async Task LoadGridThumbnailsAsync(Neuterradise.App.SystemServices.Storage.VaultPaths paths, IReadOnlyList<MediaGridCardViewModel> cards, CancellationToken token)
    {
        using var concurrency = new SemaphoreSlim(4, 4);
        var loads = cards.Select(async card =>
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var path = HomeViewModel.ResolveDerivedPresentationPath(paths, card.AssetId, preferBanner: false);
                if (path is null && card.MediaType is MediaType.Image or MediaType.Video && _derivedStills is not null)
                {
                    var timestamp = card.MediaType == MediaType.Video
                        ? Math.Min(5_000, Math.Max(0, (card.DurationMs ?? 0) / 10))
                        : (long?)null;
                    path = await _derivedStills.GetOrCreateAsync(
                        new DerivedStillRequest(card.AssetId, timestamp, TileDecodeWidth), token).ConfigureAwait(false);
                }
                else if (path is null && card.MediaType == MediaType.Model && _modelAdapters is not null && _catalog is not null)
                {
                    var source = await _catalog.MediaReads.GetAssetSourceAsync(card.AssetId, token).ConfigureAwait(false);
                    var readable = source?.ResolveReadablePath(paths);
                    if (readable is not null)
                    {
                        var preview = await _modelAdapters.GetOrGeneratePreviewAsync(
                            new Neuterradise.App.Media.Model.ModelPreviewRequest(card.AssetId, readable, TargetWidth: TileDecodeWidth, TargetHeight: TileDecodeWidth),
                            token).ConfigureAwait(false);
                        path = preview.StaticThumbnailPath;
                        card.ModelPreview = preview;
                    }
                }

                if (token.IsCancellationRequested || !cards.Contains(card)) return;
                if (path is not null)
                {
                    card.ThumbnailPath = path;
                    card.ThumbnailSource = ImageRef.FromPath(path, TileDecodeWidth);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Media thumbnail generation failed for {0}: {1}", card.AssetId, ex.Message);
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(loads).ConfigureAwait(false);
    }

    private async Task<string?> ResolveVideoPreviewPathAsync(MediaGridCardViewModel card, CancellationToken token)
    {
        if (_videoPreviews is null || card.MediaType != MediaType.Video)
        {
            return null;
        }

        var source = await _catalog!.MediaReads.GetAssetSourceAsync(card.AssetId, token).ConfigureAwait(false);
        if (source is null || string.IsNullOrWhiteSpace(source.Sha256))
        {
            return null;
        }

        var key = new VideoPreviewCacheKey(
            card.AssetId,
            source.Sha256,
            CacheVersionSet.Default.VideoPreviewVersion,
            SystemServices.Jobs.Handlers.ProductionMediaToolPlanSource.VideoPreviewVariantKey);
        var cached = _videoPreviews.Lookup(key);
        if (cached.Status == CacheLookupStatus.Hit)
        {
            return cached.PhysicalPath;
        }

        if (_videoPreviewRegeneration is not null)
        {
            await _videoPreviewRegeneration
                .RequeueMissingCurrentAsync(card.AssetId, token)
                .ConfigureAwait(false);
        }

        return null;
    }

    public bool CanSaveMetadata => !string.IsNullOrWhiteSpace(_editDisplayName);

    public bool HasUnsavedMetadataChanges => IsEditingMetadata;

    public bool IsEditingMetadata
    {
        get => _isEditingMetadata;
        set => SetProperty(ref _isEditingMetadata, value);
    }

    public string EditDisplayName
    {
        get => _editDisplayName;
        set
        {
            if (SetProperty(ref _editDisplayName, value))
            {
                RaisePropertyChanged(nameof(CanSaveMetadata));
            }
        }
    }

    public string? EditCategoryId
    {
        get => _editCategoryId;
        set => SetProperty(ref _editCategoryId, value);
    }

    public int? EditRating
    {
        get => _editRating;
        set => SetProperty(ref _editRating, value);
    }

    public string? EditOverview
    {
        get => _editOverview;
        set => SetProperty(ref _editOverview, value);
    }

    public string? EditNotes
    {
        get => _editNotes;
        set => SetProperty(ref _editNotes, value);
    }

    public bool EditIsFavorite
    {
        get => _editIsFavorite;
        set => SetProperty(ref _editIsFavorite, value);
    }

    public ObservableCollection<ProfileCategoryOption> AvailableCategories { get; } = [];
    public ObservableCollection<ProfileTagOption> AvailableTags { get; } = [];
    public ObservableCollection<ProfileTagOption> EditTags { get; } = [];

    public IReadOnlyList<ProfileTagOption> TagSearchResults
    {
        get
        {
            var assigned = EditTags.Select(static tag => tag.TagId).ToHashSet(StringComparer.Ordinal);
            var query = TagSearchText?.Trim() ?? string.Empty;
            var normalizedQuery = Settings.TaxonomyNamePolicy.TryNormalize(query);

            return
            [
                .. AvailableTags
                    .Where(option => !assigned.Contains(option.TagId))
                    .Where(option => normalizedQuery is null
                        || Settings.TaxonomyNamePolicy.TryNormalize(option.DisplayName) is { } nk
                           && nk.Contains(normalizedQuery, StringComparison.Ordinal))
                    .OrderBy(option =>
                    {
                        var nk = Settings.TaxonomyNamePolicy.TryNormalize(option.DisplayName);
                        return normalizedQuery is not null && nk is not null
                            && nk.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1;
                    })
                    .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(MaximumTagSuggestions),
            ];
        }
    }

    public const int MaximumTagSuggestions = 12;

    public string TagSearchText
    {
        get => _tagSearchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _tagSearchText, next))
            {
                if (next.Contains(',') || next.Contains('\n') || next.Contains('\r'))
                {
                    var task = CommitTagTokensAsync();
                    _pendingProfileTagCommit = task;
                    TaskObserver.Observe(task, "ProfileDetailViewModel.CommitTagTokensAsync");
                    return;
                }
                RaisePropertyChanged(nameof(TagSearchResults));
            }
        }
    }

    public Task CommitTagTokensAsync() => CommitProfileTagTokensCoreAsync(isPartial: true);

    public Task OnTagInputEnterAsync() => CommitProfileTagTokensCoreAsync(isPartial: false);

    private async Task CommitProfileTagTokensCoreAsync(bool isPartial)
    {
        await _profileTagCommitGate.WaitAsync(RouteCancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<Settings.TaxonomyNamePolicy.TaxonomyTagToken> tokens = [];
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                if (isPartial)
                {
                    var (committed, remaining) = Settings.TaxonomyNamePolicy.TryParsePartialTagInput(_tagSearchText);
                    tokens = committed;
                    _tagSearchText = remaining ?? string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
                else
                {
                    var remaining = _tagSearchText.Trim();
                    if (string.IsNullOrWhiteSpace(remaining))
                    {
                        tokens = [];
                        return;
                    }
                    tokens = Settings.TaxonomyNamePolicy.ParseTagTokens(remaining);
                    _tagSearchText = string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
            }).ConfigureAwait(false);

            foreach (var token in tokens)
            {
                await EnsureEditTagAsync(token.CanonicalName, token.DisplayName).ConfigureAwait(false);
            }

            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive)
                {
                    RaisePropertyChanged(nameof(TagSearchResults));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _profileTagCommitGate.Release();
        }
    }

    private async Task EnsureEditTagAsync(string canonicalName, string displayName)
    {
        var alreadyAssigned = false;
        ProfileTagOption? existing = null;
        await UiDispatch.InvokeAsync(() =>
        {
            if (_isDisposed || !IsRouteActive)
            {
                return;
            }

            alreadyAssigned = EditTags.Any(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
            existing = AvailableTags.FirstOrDefault(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
        }).ConfigureAwait(false);

        if (_isDisposed || !IsRouteActive || alreadyAssigned)
        {
            return;
        }

        if (existing is not null)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive)
                {
                    AddEditTag(existing);
                }
            }).ConfigureAwait(false);
            return;
        }

        if (_catalog is null)
        {
            return;
        }

        try
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            await _catalog.SettingsWrites.CreateTagAsync(id, displayName, RouteCancellationToken).ConfigureAwait(false);
            var option = new ProfileTagOption(id, displayName);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                AvailableTags.Add(option);
                AddEditTag(option);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var vocabulary = await _catalog.SettingsReads.GetAllTagsAsync(RouteCancellationToken).ConfigureAwait(false);
            var resolvedDuplicate = false;
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                PopulateAvailableTags(vocabulary.Select(tag => new ProfileTagOption(tag.TagId, tag.Name)));
                var match = AvailableTags.FirstOrDefault(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
                if (match is not null)
                {
                    AddEditTag(match);
                    resolvedDuplicate = true;
                }
            }).ConfigureAwait(false);

            if (!resolvedDuplicate && !_isDisposed && IsRouteActive)
            {
                throw;
            }
        }
    }

    public async Task CommitPendingProfileTagsForSaveAsync()
    {
        await _pendingProfileTagCommit.ConfigureAwait(false);

        var hasPendingText = false;
        await UiDispatch.InvokeAsync(() =>
        {
            hasPendingText = !_isDisposed && IsRouteActive && !string.IsNullOrWhiteSpace(_tagSearchText);
        }).ConfigureAwait(false);

        if (!hasPendingText)
        {
            return;
        }

        var task = CommitProfileTagTokensCoreAsync(isPartial: false);
        _pendingProfileTagCommit = task;
        await task.ConfigureAwait(false);
    }

    public void AddEditTag(ProfileTagOption? option)
    {
        if (option is null || EditTags.Any(tag => string.Equals(tag.TagId, option.TagId, StringComparison.Ordinal)))
        {
            return;
        }

        EditTags.Add(option);
        TagSearchText = string.Empty;
        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public void RemoveEditTag(ProfileTagOption? option)
    {
        if (option is null)
        {
            return;
        }

        var existing = EditTags.FirstOrDefault(
            tag => string.Equals(tag.TagId, option.TagId, StringComparison.Ordinal));
        if (existing is not null)
        {
            EditTags.Remove(existing);
            RaisePropertyChanged(nameof(TagSearchResults));
        }
    }

    public void PopulateAvailableTags(IEnumerable<ProfileTagOption> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        AvailableTags.Clear();
        foreach (var tag in tags)
        {
            AvailableTags.Add(tag);
        }

        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public AsyncRelayCommand ChangeFaceProfileCommand { get; }

    public Guid ProfileId { get; }
    public Guid? CoverAssetId => _coverAssetId;
    public Guid? BannerAssetId => _bannerAssetId;

    public long RowVersion
    {
        get => _rowVersion;
        private set
        {
            if (_rowVersion != value)
            {
                _rowVersion = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileDetailReadModel? Profile => _profile;
    public ProfilePresentationModel? PresentationModel => _presentationModel;
    public ProfileLayoutDefinition? LayoutDefinition
    {
        get => _layoutDefinition;
        private set
        {
            if (_layoutDefinition != value)
            {
                _layoutDefinition = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CurrentPresetId
    {
        get => _currentPresetId;
        set
        {
            if (_currentPresetId != value)
            {
                _currentPresetId = value;
                RaisePropertyChanged();
            }
        }
    }

    public IReadOnlyList<string> AvailablePresets { get; } =
        [.. ProfileLayoutResolver.BuiltInPresets.Select(static preset => preset.Id)];

    public string? CurrentCardVariantId
    {
        get => _currentCardVariantId;
        set
        {
            if (_currentCardVariantId != value)
            {
                _currentCardVariantId = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ResolvedCardVariantId));
            }
        }
    }

    public string ResolvedCardVariantId => GalleryCardCatalog.ResolveVariant(CurrentCardVariantId, (string?)null).Id;

    public IReadOnlyList<string> AvailableCardVariants { get; } = GalleryCardCatalog.Variants.Select(v => v.Id).ToList();

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName != value)
            {
                _displayName = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileKind Kind
    {
        get => _kind;
        private set
        {
            if (_kind != value)
            {
                _kind = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsUnknownProfile));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
            }
        }
    }

    public bool IsUnknownProfile => Kind == ProfileKind.Unknown;

    public long UnknownSequence
    {
        get => _unknownSequence;
        private set
        {
            if (_unknownSequence != value)
            {
                _unknownSequence = value;
                RaisePropertyChanged();
            }
        }
    }

    public long ActiveOwnedAssetCount
    {
        get => _activeOwnedAssetCount;
        private set
        {
            if (_activeOwnedAssetCount != value)
            {
                _activeOwnedAssetCount = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsActiveUnresolved));
            }
        }
    }

    public bool IsActiveUnresolved => IsUnknownProfile && UnknownProfileRules.IsActiveUnresolved(ActiveOwnedAssetCount);

    public string? CategoryName
    {
        get => _categoryName;
        private set
        {
            if (_categoryName != value)
            {
                _categoryName = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CategoryId => _categoryId;
    public IReadOnlyList<string> Tags => _tags;

    public int? Rating
    {
        get => _rating;
        private set
        {
            if (_rating != value)
            {
                _rating = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(RatingValue));
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FavoriteTooltip));
            }
        }
    }

    public string FavoriteTooltip => IsFavorite
        ? Localization.SurfaceText.Get("Profile.Favorite.Remove", "Remove from favorites")
        : Localization.SurfaceText.Get("Profile.Favorite.Add", "Add to favorites");

    public bool HasMediaSelection => MediaGrid.Selection.SelectedAssetIds.Count > 0;

    public string? Overview
    {
        get => _overview;
        private set
        {
            if (_overview != value)
            {
                _overview = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? Notes
    {
        get => _notes;
        private set
        {
            if (_notes != value)
            {
                _notes = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasNotes));
            }
        }
    }

    public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);

    public MediaDetailViewModel? Inspector
    {
        get => _inspector;
        private set
        {
            if (!ReferenceEquals(_inspector, value))
            {
                _inspector = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsInspectorOpen));
            }
        }
    }

    public bool IsInspectorOpen => _inspector is not null;
    public ICommand CloseInspectorCommand { get; }
    public Guid? IdentityId => _identityId;
    public int IdentitySampleCount => _identitySampleCount;

    public ManagedPathState PathState
    {
        get => _pathState;
        private set
        {
            if (_pathState != value)
            {
                _pathState = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsPathNeedsAttention));
                RaisePropertyChanged(nameof(IsPathPending));
            }
        }
    }

    public bool IsPathNeedsAttention => _pathState == ManagedPathState.NeedsAttention;
    public bool IsPathPending => _pathState == ManagedPathState.Pending;
    public string? CurrentManagedRelativePath => _currentManagedRelativePath;

    public string? FolderStatusMessage
    {
        get => _folderStatusMessage;
        private set
        {
            if (_folderStatusMessage != value)
            {
                _folderStatusMessage = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? RenameStatusMessage
    {
        get => _renameStatusMessage;
        private set
        {
            if (_renameStatusMessage != value)
            {
                _renameStatusMessage = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? UnknownStatusNotice
    {
        get => _unknownStatusNotice;
        private set
        {
            if (_unknownStatusNotice != value)
            {
                _unknownStatusNotice = value;
                RaisePropertyChanged();
            }
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (_isRenaming != value)
            {
                _isRenaming = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileMediaFilter SelectedMediaFilter
    {
        get => _selectedMediaFilter;
        set
        {
            if (_selectedMediaFilter != value)
            {
                _selectedMediaFilter = value;
                RaisePropertyChanged();
                var gridFilter = value switch
                {
                    ProfileMediaFilter.Owned => MediaRelationFilter.Owned,
                    ProfileMediaFilter.AppearsIn => MediaRelationFilter.AppearsIn,
                    ProfileMediaFilter.Manual => MediaRelationFilter.Manual,
                    _ => MediaRelationFilter.All,
                };
                if (MediaGrid.RelationFilter != gridFilter)
                {
                    MediaGrid.RelationFilter = gridFilter;
                    return;
                }
                TaskObserver.Observe(LoadMediaAsync(), "ProfileDetailViewModel.LoadMediaAsync");
            }
        }
    }

    public ObservableCollection<ProfileMediaItemViewModel> MediaItems { get; } = [];
    public ObservableCollection<ProfileMediaItemViewModel> RecentMedia { get; } = [];
    public ObservableCollection<ImportAssignmentReviewCluster> AssignmentReviewClusters { get; } = [];
    public MediaGridViewModel MediaGrid { get; }
    public RelatedProfilesViewModel Related { get; }
    public ObservableCollection<RelatedProfileSummaryReadModel> RelatedProfiles => Related.RelatedProfiles;
    public ObservableCollection<FaceReviewItemViewModel> Faces { get; } = [];

    public FaceReviewItemViewModel? SelectedFace
    {
        get => _selectedFace;
        set
        {
            if (_selectedFace != value)
            {
                _selectedFace = value;
                RaisePropertyChanged();
            }
        }
    }

    public IReadOnlyList<Guid> SelectedUnknownAssetIds => MediaItems.Where(i => i.IsSelected).Select(i => i.AssetId).ToList();
    public bool HasMedia => MediaItems.Count > 0;
    public bool HasRelatedProfiles => RelatedProfiles.Count > 0;
    public bool HasFaces => Faces.Count > 0;
    public int UnresolvedFaceCount => Faces.Count(f => f.IsUnresolved);

    public ICommand GoBackCommand { get; }
    public ICommand OpenProfileFolderCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand OpenMediaDetailCommand { get; }
    public ICommand ConfirmFaceCommand { get; }
    public ICommand RejectFaceCommand { get; }
    public ICommand OpenFaceReviewWorkspaceCommand { get; }
    public ICommand OpenCustomizeOverlayCommand { get; }
    public ProfileAppearanceOverrides? AppearanceOverrides => _appearanceOverrides;

    public CoverAppearance? CoverAppearance
    {
        get => _coverAppearance;
        private set
        {
            if (_coverAppearance != value)
            {
                _coverAppearance = value;
                RaisePropertyChanged();
            }
        }
    }

    public BannerPresentation? BannerPresentation
    {
        get => _bannerPresentation;
        private set
        {
            if (_bannerPresentation != value)
            {
                _bannerPresentation = value;
                RaisePropertyChanged();
            }
        }
    }

    public ImageRef? CoverSource
    {
        get => _coverSource;
        private set
        {
            if (_coverSource != value)
            {
                _coverSource = value;
                RaisePropertyChanged();
            }
        }
    }

    public ImageRef? BannerStillSource
    {
        get => _bannerStillSource;
        private set
        {
            if (_bannerStillSource != value)
            {
                _bannerStillSource = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        private set
        {
            if (_coverImagePath != value)
            {
                _coverImagePath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasCoverImage));
            }
        }
    }

    public string? BannerImagePath
    {
        get => _bannerImagePath;
        private set
        {
            if (_bannerImagePath != value)
            {
                _bannerImagePath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasBannerImage));
            }
        }
    }

    public bool HasCoverImage => !string.IsNullOrWhiteSpace(CoverImagePath);
    public bool HasBannerImage => !string.IsNullOrWhiteSpace(BannerImagePath);

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion != value)
            {
                _reduceMotion = value;
                RaisePropertyChanged();
                if (_coverAppearance is not null)
                {
                    var coverReq = new CoverAppearanceRequest(_appearanceOverrides?.CoverShape, _appearanceOverrides?.CoverFrameId);
                    CoverAppearance = CoverFrameCatalog.Resolve(coverReq, value).Appearance;
                }
            }
        }
    }

    public IReadOnlyList<string> AvailableCoverShapes { get; } =
        ["circle", "rounded-square", "square", "squircle", "portrait", "hexagon", "diamond"];

    public IReadOnlyList<string> AvailableCoverFrames { get; } = CoverFrameCatalog.Frames.Select(f => f.Id).ToList();

    public void PopulateProfile(ProfileDetailReadModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        DisplayName = profile.DisplayName;
        Kind = profile.Kind;
        UnknownSequence = profile.UnknownSequence ?? 0;
        ActiveOwnedAssetCount = profile.ActiveOwnedAssetCount;
        CategoryName = profile.CategoryName;
        _categoryId = profile.CategoryId;
        _tags = profile.Tags ?? [];
        _tagAssignments = profile.AssignedTags;
        Rating = profile.Rating;
        IsFavorite = profile.IsFavorite;
        Overview = profile.Overview;
        Notes = profile.Notes;
        _identityId = profile.IdentityId;
        _identitySampleCount = profile.IdentitySampleCount;
        _coverAssetId = profile.CoverAssetId;
        _bannerAssetId = profile.BannerAssetId;
        RowVersion = profile.RowVersion;
        RaisePropertyChanged(nameof(IdentityId));
        RaisePropertyChanged(nameof(IdentitySampleCount));

        var resolver = new ProfileLayoutResolver();
        var selection = resolver.Resolve(_defaultLayoutPresetId, profile.LayoutPresetId);
        LayoutDefinition = selection.Definition;
        CurrentPresetId = selection.Definition.Id;

        _appearanceOverrides = ProfileAppearanceRules.NormalizeBannerSourceKind(
            ProfileAppearanceOverrides.Parse(profile.AppearanceOverridesJson),
            profile.BannerMediaType);
        var appearanceOverrides = _appearanceOverrides;
        CurrentCardVariantId = appearanceOverrides.GalleryCardVariantId;
        RaisePropertyChanged(nameof(AppearanceOverrides));

        var coverRequest = new CoverAppearanceRequest(
            Shape: appearanceOverrides.CoverShape,
            FrameId: appearanceOverrides.CoverFrameId,
            FrameScale: appearanceOverrides.CoverFrameScale,
            FrameTint: appearanceOverrides.CoverFrameTint,
            FrameIntensity: appearanceOverrides.CoverFrameIntensity,
            FrameAnimation: appearanceOverrides.CoverFrameAnimation,
            CoverShadow: appearanceOverrides.CoverShadow);

        CoverAppearance = CoverFrameCatalog.Resolve(coverRequest, ReduceMotion).Appearance;

        var bannerRequest = new BannerPresentationRequest(
            StartPointSeconds: appearanceOverrides.BannerStartPointSeconds,
            DurationSeconds: appearanceOverrides.BannerDurationSeconds,
            FocusX: appearanceOverrides.BannerFocusX,
            FocusY: appearanceOverrides.BannerFocusY,
            Zoom: appearanceOverrides.BannerZoom,
            Loop: appearanceOverrides.BannerLoop);

        BannerPresentation = BannerPresentationPolicy.Resolve(bannerRequest).Presentation;

        CoverImagePath = appearanceOverrides.CoverStillTimestampMilliseconds is null
            ? HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, profile.CoverAssetId, preferBanner: false)
            : null;
        BannerImagePath = appearanceOverrides.BannerStillTimestampMilliseconds is null
            ? HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, profile.BannerAssetId, preferBanner: true)
            : null;

        ApplyCoverImage(CoverImagePath);
        ApplyBannerImage(BannerImagePath);
        if (appearanceOverrides.CoverStillTimestampMilliseconds is { } coverTimestamp
            && profile.CoverAssetId is { } coverAssetId
            && _derivedStills is not null)
        {
            TaskObserver.Observe(
                ResolveSelectedCoverFrameAsync(coverAssetId, coverTimestamp, RouteCancellationToken),
                "Profile cover frame");
        }
        if (appearanceOverrides.BannerStillTimestampMilliseconds is { } bannerTimestamp
            && profile.BannerAssetId is { } bannerAssetId
            && _derivedStills is not null)
        {
            TaskObserver.Observe(
                ResolveSelectedBannerFrameAsync(bannerAssetId, bannerTimestamp, RouteCancellationToken),
                "Profile banner frame");
        }

        _presentationModel = new ProfilePresentationModel(
            ProfileId: profile.ProfileId,
            DisplayName: profile.DisplayName,
            CategoryName: profile.CategoryName,
            Tags: profile.Tags,
            Rating: profile.Rating,
            IsFavorite: profile.IsFavorite,
            Overview: profile.Overview,
            Notes: profile.Notes,
            MediaCount: profile.ActiveOwnedAssetCount,
            HasRelatedIndicator: RelatedProfiles.Count > 0,
            CardVariantId: appearanceOverrides.GalleryCardVariantId);

        RaisePropertyChanged(nameof(PresentationModel));
    }

    public void PopulateMedia(IEnumerable<ProfileMediaItemReadModel> items, int? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        MediaItems.Clear();
        RecentMedia.Clear();

        var count = 0;
        var gridItems = new List<MediaGridItem>();
        foreach (var item in items)
        {
            if (count >= MaxMaterializedMedia)
            {
                break;
            }

            var vm = new ProfileMediaItemViewModel(item);
            MediaItems.Add(vm);
            gridItems.Add(new MediaGridItem(
                item.AssetId,
                item.MediaType,
                item.RelationType switch
                {
                    ProfileAssetRelation.Owner => MediaRelationBadge.Owned,
                    ProfileAssetRelation.Appears => MediaRelationBadge.AppearsIn,
                    _ => MediaRelationBadge.Manual,
                },
                item.ManagedFileName,
                item.PixelWidth,
                item.PixelHeight,
                item.DurationMs,
                item.PathState == ManagedPathState.NeedsAttention,
                item.IsFavorite));
            if (count < 12)
            {
                RecentMedia.Add(vm);
            }

            count++;
        }

        MediaGrid.SetItems(gridItems, totalCount: totalCount);
        LoadGridThumbnails();
        RaisePropertyChanged(nameof(HasMedia));
    }

    public void PopulateRelated(IEnumerable<RelatedProfileSummaryReadModel> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        Related.Populate(summaries);
        RaisePropertyChanged(nameof(HasRelatedProfiles));
    }

    public void PopulateFaces(IEnumerable<FaceReviewReadModel> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        Faces.Clear();
        foreach (var f in faces)
        {
            var item = new FaceReviewItemViewModel(f);
            if (_catalog?.Paths.CachePath is { } cachePath)
            {
                item.FaceCropPath = FaceReviewItemViewModel.ResolveFaceCropPath(cachePath, f.FaceId);
            }

            Faces.Add(item);
            EnsureFaceCrop(item);
        }

        if (Faces.Count > 0 && SelectedFace is null)
        {
            SelectedFace = Faces[0];
        }

        RaisePropertyChanged(nameof(HasFaces));
        RaisePropertyChanged(nameof(UnresolvedFaceCount));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_profileReads is null)
        {
            return;
        }

        var generation = ++_loadGeneration;
        ShowLoading();
        try
        {
            var layoutTask = _settingsOperations is not null
                ? _settingsOperations.GetDefaultProfileLayoutAsync(cancellationToken)
                : Task.FromResult<string?>(null);
            var detailTask = _profileReads.GetDetailAsync(ProfileId, cancellationToken);
            var folderTask = _profileReads.GetFolderAsync(ProfileId, cancellationToken);
            var categoriesTask = _catalog is not null
                ? _catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<CategoryRecord>>([]);
            var tagsTask = _catalog is not null
                ? _catalog.SettingsReads.GetAllTagsAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<TagRecord>>([]);

            await Task.WhenAll(layoutTask, detailTask, folderTask, categoriesTask, tagsTask).ConfigureAwait(false);

            if (generation != _loadGeneration)
            {
                return;
            }

            var categories = categoriesTask.Result;
            var vocabulary = tagsTask.Result;
            var layoutResult = layoutTask.Result;
            var detail = detailTask.Result;
            var folder = folderTask.Result;

            var applySucceeded = false;
            UiDispatch.Run(() =>
            {
                if (generation != _loadGeneration) return;
                try
                {
                    PopulateAvailableCategories(categories.Select(c => new ProfileCategoryOption(c.CategoryId, c.Name)));
                    PopulateAvailableTags(vocabulary.Select(t => new ProfileTagOption(t.TagId, t.Name)));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Trace.TraceWarning("Profile metadata vocabulary could not be loaded: {0}", ex.Message);
                }

                try
                {
                    if (layoutResult is not null)
                    {
                        _defaultLayoutPresetId = layoutResult;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Trace.TraceWarning("Profile layout preference could not be loaded: {0}", ex.Message);
                }

                if (detail is null)
                {
                    ShowCriticalError("Profile not found.");
                    return;
                }

                PopulateProfile(detail);
                if (folder is not null)
                {
                    PathState = folder.PathState;
                    _currentManagedRelativePath = folder.CurrentManagedRelativePath;
                    if (folder.PathState == ManagedPathState.Pending)
                    {
                        IsRenaming = true;
                        RenameStatusMessage = "Renaming files... (reconciliation pending)";
                    }
                }

                applySucceeded = true;
            });

            if (!applySucceeded || generation != _loadGeneration)
            {
                return;
            }

            var mediaTask = LoadMediaAsync(cancellationToken);
            var assignmentTask = LoadAssignmentReviewClustersAsync(cancellationToken);
            var relatedTask = LoadRelatedAsync(cancellationToken);
            var facesTask = LoadFacesAsync(cancellationToken);
            await Task.WhenAll(mediaTask, assignmentTask, relatedTask, facesTask).ConfigureAwait(false);

            if (generation != _loadGeneration)
            {
                return;
            }

            UiDispatch.Run(() => ShowReady());
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() => ShowRecoverableError(OperationExecution.SafeMessage(ex)));
        }
    }

    public async Task LoadMediaAsync(CancellationToken cancellationToken = default)
    {
        if (_profileReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _mediaLoadGeneration);
        ProfileMediaFilter relationFilter = default;
        MediaType? typeFilter = null;
        int pageSize = 50;
        bool isFavoriteOnly = false;
        MediaGridSort sort = default;
        int currentPage = 1;

        UiDispatch.Run(() =>
        {
            relationFilter = MediaGrid.RelationFilter switch
            {
                MediaRelationFilter.Owned => ProfileMediaFilter.Owned,
                MediaRelationFilter.AppearsIn => ProfileMediaFilter.AppearsIn,
                MediaRelationFilter.Manual => ProfileMediaFilter.Manual,
                _ => ProfileMediaFilter.All,
            };
            typeFilter = MediaGrid.TypeFilter switch
            {
                MediaTypeFilter.Images => MediaType.Image,
                MediaTypeFilter.Videos => MediaType.Video,
                MediaTypeFilter.Models => MediaType.Model,
                _ => null,
            };
            pageSize = MediaGrid.PageSize;
            isFavoriteOnly = MediaGrid.IsFavoriteOnly;
            sort = MediaGrid.Sort;
            currentPage = MediaGrid.CurrentPage;
            MediaGrid.State = MediaGridState.Loading;
        });

        try
        {
            var page = await _profileReads.GetMediaPageAsync(
                ProfileId,
                relationFilter,
                typeFilter,
                pageSize: pageSize,
                cancellationToken: cancellationToken,
                isFavoriteOnly: isFavoriteOnly,
                sort: sort,
                pageIndex: currentPage).ConfigureAwait(false);

            UiDispatch.Run(() =>
            {
                if (generation != _mediaLoadGeneration || _isDisposed)
                {
                    return;
                }
                PopulateMedia(page.Items, page.TotalCount);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() =>
            {
                if (generation != _mediaLoadGeneration || _isDisposed)
                {
                    return;
                }
                MediaGrid.State = MediaGridState.RecoverableQueryError;
                FolderStatusMessage = OperationExecution.SafeMessage(ex);
            });
        }
    }

    private async Task LoadAssignmentReviewClustersAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _assignmentReviewLoadGeneration);
        if (!IsUnknownProfile || _assignmentReviewReads is null)
        {
            UiDispatch.Run(() =>
            {
                if (generation != _assignmentReviewLoadGeneration) return;
                AssignmentReviewClusters.Clear();
            });
            return;
        }

        var clusters = await _assignmentReviewReads.GetPendingForUnknownProfileAsync(ProfileId, cancellationToken).ConfigureAwait(false);
        UiDispatch.Run(() =>
        {
            if (generation != _assignmentReviewLoadGeneration || _isDisposed)
            {
                return;
            }
            AssignmentReviewClusters.Clear();
            foreach (var cluster in clusters)
            {
                AssignmentReviewClusters.Add(cluster);
            }
        });
    }

    public async Task AcceptAssignmentClusterAsync(ImportAssignmentReviewCluster cluster, Guid destinationProfileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_unknownOperations is null)
        {
            return;
        }

        var request = new AssignUnknownAssetsToProfileRequest(
            ProfileId,
            [],
            destinationProfileId,
            ExpectedUnknownRowVersion: RowVersion,
            AssignmentClusterId: cluster.ClusterId,
            ExpectedAssignmentClusterRowVersion: cluster.RowVersion);
        var result = await _unknownOperations.AssignUnknownAssetsToProfileAsync(request, cancellationToken).ConfigureAwait(true);
        UnknownStatusNotice = result.IsSuccess
            ? SurfaceText.Format("Assignment.Cluster.Accepted", "Assigned {0} grouped media items.", cluster.MemberCount)
            : result.UserMessage ?? SurfaceText.Get("Assignment.Cluster.Failed", "The grouped assignment could not be applied.");
        if (result.IsSuccess)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task KeepAssignmentClusterUnknownAsync(ImportAssignmentReviewCluster cluster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_unknownOperations is null)
        {
            return;
        }

        var result = await _unknownOperations.KeepAssignmentClusterUnknownAsync(ProfileId, cluster.ClusterId, cluster.RowVersion, cancellationToken).ConfigureAwait(true);
        UnknownStatusNotice = result.IsSuccess
            ? SurfaceText.Get("Assignment.Cluster.KeptUnknown", "Media remains safely unassigned.")
            : result.UserMessage ?? SurfaceText.Get("Assignment.Cluster.Failed", "The grouped assignment could not be applied.");
        if (result.IsSuccess)
        {
            await LoadAssignmentReviewClustersAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task OpenAssignmentClusterPickerAsync(ImportAssignmentReviewCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_overlay is null || _profileReads is null)
        {
            return;
        }

        IReadOnlyList<ProfilePickerItem> candidates = [];
        if (_catalog is not null)
        {
            candidates = await new ProfilePickerReads(_catalog).GetAllCandidatesAsync().ConfigureAwait(true);
        }
        _overlay.Push(new ProfilePickerOverlayRequest(
            candidates,
            picked => TaskObserver.Observe(AcceptAssignmentClusterAsync(cluster, picked.ProfileId), "ProfileDetailViewModel.AcceptAssignmentClusterAsync"),
            title: SurfaceText.Get("Assignment.Cluster.ChooseTitle", "Assign grouped media"),
            prompt: SurfaceText.Get("Assignment.Cluster.ChoosePrompt", "Choose the Profile for this evidence group:")));
    }

    private void OnMediaGridQueryChanged()
    {
        _selectedMediaFilter = MediaGrid.RelationFilter switch
        {
            MediaRelationFilter.Owned => ProfileMediaFilter.Owned,
            MediaRelationFilter.AppearsIn => ProfileMediaFilter.AppearsIn,
            MediaRelationFilter.Manual => ProfileMediaFilter.Manual,
            _ => ProfileMediaFilter.All,
        };
        RaisePropertyChanged(nameof(SelectedMediaFilter));
        CloseInspector();
        TaskObserver.Observe(
            LoadMediaAsync(),
            "Reloading profile media",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
    }

    public async Task LoadRelatedAsync(CancellationToken cancellationToken = default)
    {
        await Related.LoadRelatedProfilesAsync(cancellationToken).ConfigureAwait(false);
        UiDispatch.Run(() => RaisePropertyChanged(nameof(HasRelatedProfiles)));
    }

    public async Task LoadFacesAsync(CancellationToken cancellationToken = default)
    {
        if (_faceReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _faceLoadGeneration);
        try
        {
            var reviews = await _faceReads.GetFaceReviewsForProfileAsync(ProfileId, 50, cancellationToken).ConfigureAwait(false);
            UiDispatch.Run(() =>
            {
                if (generation != _faceLoadGeneration || _isDisposed || !IsRouteActive)
                {
                    return;
                }
                PopulateFaces(reviews);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiDispatch.Run(() =>
            {
                if (generation != _faceLoadGeneration || _isDisposed)
                {
                    return;
                }
                FolderStatusMessage = OperationExecution.SafeMessage(ex);
            });
        }
    }

    public async Task OpenProfileFolderAsync(CancellationToken cancellationToken = default)
    {
        if (_profileOperations is null)
        {
            FolderStatusMessage = "Profile operations service unavailable.";
            return;
        }

        FolderStatusMessage = null;
        try
        {
            var result = await _profileOperations.OpenProfileFolderAsync(ProfileId, cancellationToken).ConfigureAwait(true);
            FolderStatusMessage = result.IsSuccess ? "Opened in Windows Explorer." : result.UserMessage ?? "Could not open Profile folder.";
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task ApplyPresetAsync(string? presetId, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(presetId) ? null : presetId.Trim();
        var resolver = new ProfileLayoutResolver();
        var selection = resolver.Resolve(_defaultLayoutPresetId, normalized);
        LayoutDefinition = selection.Definition;
        CurrentPresetId = selection.Definition.Id;

        if (_appearanceOperations is not null)
        {
            var request = new SetProfileLayoutOverrideRequest(ProfileId, normalized, RowVersion);
            var result = await _appearanceOperations.SetProfileLayoutOverrideAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
            }
        }
    }

    public async Task ApplyCardVariantAsync(string? variantId, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(variantId) ? null : variantId.Trim();
        CurrentCardVariantId = normalized;
        if (_appearanceOverrides is not null)
        {
            _appearanceOverrides = _appearanceOverrides with { GalleryCardVariantId = normalized };
            RaisePropertyChanged(nameof(AppearanceOverrides));
        }

        if (_appearanceOperations is not null)
        {
            var request = new SetProfileGalleryCardOverrideRequest(ProfileId, normalized, RowVersion);
            var result = await _appearanceOperations.SetProfileGalleryCardOverrideAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
            }
        }

        if (_presentationModel is not null)
        {
            _presentationModel = _presentationModel with { CardVariantId = normalized };
            RaisePropertyChanged(nameof(PresentationModel));
        }
    }

    public async Task ApplyCoverAssetAsync(Guid? assetId, long? coverVideoTimestampMilliseconds = null, CancellationToken cancellationToken = default)
    {
        if (_appearanceOperations is null)
        {
            _coverAssetId = assetId;
            CoverImagePath = assetId is { } coverAssetId
                && coverVideoTimestampMilliseconds is { } coverTimestamp
                && _derivedStills is not null
                    ? await _derivedStills.GetOrCreateAsync(
                        new DerivedStillRequest(coverAssetId, coverTimestamp),
                        cancellationToken).ConfigureAwait(true)
                    : HomeViewModel.ResolveDerivedPresentationPath(
                        _catalog?.MediaReads,
                        _catalog?.Paths,
                        assetId,
                        preferBanner: false);
            ApplyCoverImage(CoverImagePath);
            RaisePropertyChanged(nameof(CoverAssetId));
            RaisePropertyChanged(nameof(CoverImagePath));
            RaisePropertyChanged(nameof(CoverSource));
            RaisePropertyChanged(nameof(HasCoverImage));
            return;
        }

        try
        {
            var request = new SetCoverAssetRequest(ProfileId, assetId, RowVersion, coverVideoTimestampMilliseconds);
            var result = await _appearanceOperations.SetCoverAssetAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
                _coverAssetId = assetId;
                CoverImagePath = assetId is { } coverAssetId
                    && coverVideoTimestampMilliseconds is { } coverTimestamp
                    && _derivedStills is not null
                        ? await _derivedStills.GetOrCreateAsync(
                            new DerivedStillRequest(coverAssetId, coverTimestamp),
                            cancellationToken).ConfigureAwait(true)
                        : HomeViewModel.ResolveDerivedPresentationPath(
                            _catalog?.MediaReads,
                            _catalog?.Paths,
                            assetId,
                            preferBanner: false);
                ApplyCoverImage(CoverImagePath);
                RaisePropertyChanged(nameof(CoverAssetId));
                RaisePropertyChanged(nameof(CoverImagePath));
                RaisePropertyChanged(nameof(CoverSource));
                RaisePropertyChanged(nameof(HasCoverImage));
                FolderStatusMessage = assetId.HasValue ? "Cover set." : "Cover cleared.";
            }
            else
            {
                FolderStatusMessage = result.UserMessage ?? "Failed to set Cover.";
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task ApplyBannerAssetAsync(
        Guid? assetId,
        CancellationToken cancellationToken = default,
        BannerVisualSourceKind? sourceKind = null,
        long? frameTimestampMilliseconds = null,
        double? startPointSeconds = null,
        double? durationSeconds = null)
    {
        if (_appearanceOperations is null)
        {
            _bannerAssetId = assetId;
            BannerImagePath = HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, assetId, preferBanner: true);
            ApplyBannerImage(BannerImagePath);
            RaisePropertyChanged(nameof(BannerAssetId));
            RaisePropertyChanged(nameof(BannerImagePath));
            RaisePropertyChanged(nameof(BannerStillSource));
            RaisePropertyChanged(nameof(HasBannerImage));
            return;
        }

        try
        {
            var request = new SetBannerAssetRequest(
                ProfileId,
                assetId,
                RowVersion,
                sourceKind,
                frameTimestampMilliseconds,
                startPointSeconds,
                durationSeconds);
            var result = await _appearanceOperations.SetBannerAssetAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
                _bannerAssetId = assetId;
                _appearanceOverrides = await _appearanceOperations.ReadOverridesAsync(ProfileId, cancellationToken).ConfigureAwait(true);
                BannerImagePath = sourceKind == BannerVisualSourceKind.VideoFrame
                    && assetId is { } frameAssetId
                    && frameTimestampMilliseconds is { } frameTimestamp
                    && _derivedStills is not null
                        ? await _derivedStills.GetOrCreateAsync(new DerivedStillRequest(frameAssetId, frameTimestamp), cancellationToken).ConfigureAwait(true)
                        : HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, assetId, preferBanner: true);
                ApplyBannerImage(BannerImagePath);
                RaisePropertyChanged(nameof(BannerAssetId));
                RaisePropertyChanged(nameof(BannerImagePath));
                RaisePropertyChanged(nameof(BannerStillSource));
                RaisePropertyChanged(nameof(HasBannerImage));
                RaisePropertyChanged(nameof(AppearanceOverrides));
                FolderStatusMessage = assetId.HasValue ? "Banner set." : "Banner cleared.";
            }
            else
            {
                FolderStatusMessage = result.UserMessage ?? "Failed to set Banner.";
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    private async Task ResolveSelectedCoverFrameAsync(Guid assetId, long timestampMilliseconds, CancellationToken cancellationToken)
    {
        var path = await _derivedStills!.GetOrCreateAsync(
                new DerivedStillRequest(assetId, timestampMilliseconds),
                cancellationToken)
            .ConfigureAwait(true);
        if (path is null
            || CoverAssetId != assetId
            || _appearanceOverrides?.CoverVideoTimestampMilliseconds != timestampMilliseconds)
        {
            return;
        }

        CoverImagePath = path;
        ApplyCoverImage(path);
        RaisePropertyChanged(nameof(CoverImagePath));
        RaisePropertyChanged(nameof(CoverSource));
        RaisePropertyChanged(nameof(HasCoverImage));
    }

    private async Task ResolveSelectedBannerFrameAsync(Guid assetId, long timestampMilliseconds, CancellationToken cancellationToken)
    {
        var path = await _derivedStills!.GetOrCreateAsync(new DerivedStillRequest(assetId, timestampMilliseconds), cancellationToken).ConfigureAwait(true);
        if (path is null
            || BannerAssetId != assetId
            || _appearanceOverrides?.BannerStillTimestampMilliseconds != timestampMilliseconds)
        {
            return;
        }

        BannerImagePath = path;
        ApplyBannerImage(path);
        RaisePropertyChanged(nameof(BannerImagePath));
        RaisePropertyChanged(nameof(BannerStillSource));
        RaisePropertyChanged(nameof(HasBannerImage));
    }

    public async Task ApplyCoverAppearanceAsync(string? shape, string? frameId, CancellationToken cancellationToken = default)
    {
        if (_appearanceOverrides is not null)
        {
            _appearanceOverrides = _appearanceOverrides with { CoverShape = shape, CoverFrameId = frameId };
            RaisePropertyChanged(nameof(AppearanceOverrides));
        }

        var coverRequest = new CoverAppearanceRequest(Shape: shape, FrameId: frameId);
        CoverAppearance = CoverFrameCatalog.Resolve(coverRequest, ReduceMotion).Appearance;

        if (_appearanceOperations is not null)
        {
            try
            {
                var request = new SetProfileCoverAppearanceRequest(ProfileId, RowVersion, CoverShape: shape, CoverFrameId: frameId);
                var result = await _appearanceOperations.SetProfileCoverAppearanceAsync(request, cancellationToken).ConfigureAwait(true);
                if (result.IsSuccess && result.Value is { } outcome)
                {
                    RowVersion = outcome.RowVersion;
                }
            }
            catch (Exception ex)
            {
                FolderStatusMessage = OperationExecution.SafeMessage(ex);
            }
        }
    }

    public void OpenCustomizeOverlay() => TaskObserver.Observe(OpenCustomizeOverlayAsync(), "ProfileDetailViewModel.OpenCustomizeOverlayAsync");

    public async Task SetCoverFromMediaAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var item = MediaItems.FirstOrDefault(m => m.AssetId == assetId);
        if (item is not null && item.MediaType == MediaType.Video)
        {
            CoverFrameSelectionAssetId = assetId;
            FolderStatusMessage = "Choose which frame of this video the Cover should use.";
            await OpenCustomizeOverlayAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await ApplyCoverAssetAsync(assetId, cancellationToken: cancellationToken);
    }

    public Guid? CoverFrameSelectionAssetId { get; private set; }
    public const int CoverSectionIndex = 2;

    public async Task OpenCustomizeOverlayAsync(CancellationToken cancellationToken = default)
    {
        if (_overlay is null)
        {
            FolderStatusMessage = "Customize is unavailable without the application shell.";
            return;
        }

        try
        {
            var request = await BuildAppearanceRequestAsync(cancellationToken).ConfigureAwait(true);
            if (_isDisposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _overlay.Push(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task<AppearanceCustomizationOverlayRequest> BuildAppearanceRequestAsync(CancellationToken cancellationToken = default)
    {
        var evidence = await LoadAppearanceFaceEvidenceAsync(cancellationToken).ConfigureAwait(true);
        var evaluationInputs = MediaItems.Select(m => new AppearanceCandidateEvaluationInput(
            AssetId: m.AssetId,
            MediaType: m.MediaType,
            DisplayName: m.ManagedFileName ?? m.AssetId.ToString(),
            PixelWidth: m.PixelWidth,
            PixelHeight: m.PixelHeight,
            DurationMs: m.DurationMs,
            HasDerivedPreview: !string.IsNullOrEmpty(HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, m.AssetId, preferBanner: false)),
            HasConfirmedFaceForProfile: Faces.Any(f => f.AssetId == m.AssetId && f.IsConfirmed),
            HasFaceDetection: Faces.Any(f => f.AssetId == m.AssetId),
            CreatedAtMs: m.CreatedAtUtc.ToUnixTimeMilliseconds(),
            IsExistingCover: m.AssetId == CoverAssetId,
            IsExistingBanner: m.AssetId == BannerAssetId,
            FaceEvidence: evidence.TryGetValue(m.AssetId, out var faces) ? faces : null)).ToList();

        var evaluated = ProfileAppearanceRules.EvaluateVisualCandidates(evaluationInputs);
        var namesByAsset = MediaItems.ToDictionary(static m => m.AssetId, m => m.ManagedFileName ?? m.AssetId.ToString());

        var coverCandidates = new List<AppearanceCoverCandidate>(evaluated.Covers.Count);
        foreach (var candidate in evaluated.Covers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previewPath = await ResolveCandidatePreviewPathAsync(candidate.SourceAssetId, candidate.TimestampMilliseconds, false, cancellationToken).ConfigureAwait(true);
            coverCandidates.Add(new AppearanceCoverCandidate(
                candidate.CandidateId,
                candidate.SourceAssetId,
                candidate.SourceMediaType,
                candidate.SourceKind,
                candidate.TimestampMilliseconds,
                candidate.SuggestedCropX,
                candidate.SuggestedCropY,
                namesByAsset.GetValueOrDefault(candidate.SourceAssetId, candidate.DisplayName),
                previewPath,
                candidate.Rank,
                candidate.IsRecommended,
                candidate.Reason));
        }

        var bannerCandidates = new List<AppearanceBannerCandidate>(evaluated.Banners.Count);
        foreach (var candidate in evaluated.Banners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previewPath = await ResolveCandidatePreviewPathAsync(candidate.SourceAssetId, candidate.FrameTimestampMilliseconds, true, cancellationToken).ConfigureAwait(true);
            bannerCandidates.Add(new AppearanceBannerCandidate(
                candidate.CandidateId,
                candidate.SourceAssetId,
                candidate.SourceMediaType,
                candidate.SourceKind,
                candidate.FrameTimestampMilliseconds,
                candidate.StartPointSeconds,
                candidate.DurationSeconds,
                candidate.FocusX,
                candidate.FocusY,
                namesByAsset.GetValueOrDefault(candidate.SourceAssetId, candidate.DisplayName),
                previewPath,
                candidate.Rank,
                candidate.IsRecommended,
                candidate.Reason));
        }

        var overrides = _appearanceOverrides ?? ProfileAppearanceOverrides.Default;
        var focusCoverAssetId = CoverFrameSelectionAssetId;
        CoverFrameSelectionAssetId = null;

        var request = new AppearanceCustomizationOverlayRequest(
            profileId: ProfileId,
            profileDisplayName: DisplayName,
            currentPresetId: CurrentPresetId,
            currentCardVariantId: CurrentCardVariantId,
            currentShape: overrides.CoverShape ?? "circle",
            currentFrameId: overrides.CoverFrameId ?? CoverFrameCatalog.NoneFrameId,
            onSave: null,
            coverCandidates: coverCandidates,
            bannerCandidates: bannerCandidates,
            currentCoverAssetId: CoverAssetId,
            currentCoverSourceKind: overrides.ResolvedCoverSourceKind,
            currentCoverTimestampMilliseconds: overrides.CoverVideoTimestampMilliseconds,
            currentCoverZoom: overrides.Zoom,
            currentCoverCropX: overrides.CropX,
            currentCoverCropY: overrides.CropY,
            currentFrameScale: overrides.CoverFrameScale,
            currentFrameTint: overrides.CoverFrameTint,
            currentFrameIntensity: overrides.CoverFrameIntensity,
            currentFrameAnimation: overrides.CoverFrameAnimation,
            currentCoverShadow: overrides.CoverShadow,
            currentBannerAssetId: BannerAssetId,
            currentBannerSourceKind: overrides.ResolvedBannerSourceKind,
            currentBannerVideoFrameTimestampMilliseconds: overrides.BannerVideoFrameTimestampMilliseconds,
            currentBannerStartSeconds: overrides.BannerStartPointSeconds,
            currentBannerDurationSeconds: overrides.BannerDurationSeconds,
            currentBannerFocusX: overrides.BannerFocusX,
            currentBannerFocusY: overrides.BannerFocusY,
            currentBannerZoom: overrides.BannerZoom,
            currentBannerLoop: overrides.BannerLoop,
            initialSectionIndex: focusCoverAssetId is null ? 0 : CoverSectionIndex,
            onApplyResult: result => UiDispatch.Run(() => TaskObserver.Observe(ApplyAppearanceCustomizationAsync(result), "ProfileDetailViewModel.ApplyAppearanceCustomizationAsync")),
            reduceMotion: ReduceMotion)
        {
            PreviewModel = PresentationModel,
            PreviewCoverSource = CoverSource,
            PreviewBannerStillSource = BannerStillSource,
        };

        if (focusCoverAssetId is { } focusAssetId)
        {
            var focused = coverCandidates.FirstOrDefault(candidate => candidate.AssetId == focusAssetId);
            if (focused is not null)
            {
                request.SelectedCoverCandidate = focused;
            }
        }

        return request;
    }

    private void EnsureFaceCrop(FaceReviewItemViewModel item)
    {
        if (_derivedStills is null || item.HasFaceCrop)
        {
            return;
        }

        if (FaceBoundingBox.TryParse(item.BoundingBoxJson) is not { } bounds)
        {
            return;
        }

        var padded = bounds.WithMargin(FaceReviewItemViewModel.FaceCropMarginFraction);
        var request = new DerivedStillRequest(
            item.AssetId,
            item.SampledTimestampMilliseconds,
            StillExtractionCoordinator.FaceCropMaxEdgePixels,
            item.FaceId,
            item.DetectionKey,
            padded.X,
            padded.Y,
            padded.Width,
            padded.Height);

        TaskObserver.Observe(
            LoadFaceCropAsync(item, request),
            "ProfileDetailViewModel.LoadFaceCropAsync",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
    }

    private async Task LoadFaceCropAsync(FaceReviewItemViewModel item, DerivedStillRequest request)
    {
        var path = await _derivedStills!.GetOrCreateAsync(request, RouteCancellationToken).ConfigureAwait(true);
        if (!_isDisposed
            && !RouteCancellationToken.IsCancellationRequested
            && Faces.Contains(item)
            && item.FaceId == request.FaceId
            && !string.IsNullOrWhiteSpace(path))
        {
            item.FaceCropPath = path;
        }
    }

    public string? BannerMotionPath
    {
        get
        {
            if (BannerAssetId is not { } bannerAssetId || _catalog?.Paths is not { } paths)
            {
                return null;
            }

            var item = MediaItems.FirstOrDefault(m => m.AssetId == bannerAssetId);
            if (item is null || item.MediaType != MediaType.Video || string.IsNullOrWhiteSpace(item.ManagedRelativePath))
            {
                return null;
            }

            try
            {
                var absolute = Path.Combine(paths.Root, item.ManagedRelativePath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(absolute) ? absolute : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool HasBannerMotion => BannerMotionPath is not null;

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>> LoadAppearanceFaceEvidenceAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null || MediaItems.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>();
        }

        return await _catalog.FaceReads.GetAppearanceFaceEvidenceAsync(
            [.. MediaItems.Select(static m => m.AssetId)],
            ProfileId,
            cancellationToken).ConfigureAwait(true);
    }

    private async Task<string?> ResolveCandidatePreviewPathAsync(Guid assetId, long? timestampMilliseconds, bool preferBanner, CancellationToken cancellationToken)
    {
        if (timestampMilliseconds is null)
        {
            return HomeViewModel.ResolveDerivedPresentationPath(_catalog?.MediaReads, _catalog?.Paths, assetId, preferBanner);
        }

        if (_derivedStills is null || _catalog is null)
        {
            return null;
        }

        var request = new DerivedStillRequest(assetId, timestampMilliseconds);
        var fingerprint = MediaItems.FirstOrDefault(item => item.AssetId == assetId)?.ContentFingerprint;
        var published = _derivedStills.TryResolvePublishedPath(request, fingerprint);
        if (published is not null)
        {
            return published;
        }

        return await _derivedStills.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public async Task ApplyAppearanceCustomizationAsync(AppearanceCustomizationResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.PresetId != CurrentPresetId)
        {
            await ApplyPresetAsync(result.PresetId, cancellationToken).ConfigureAwait(true);
        }

        if (result.CardVariantId != CurrentCardVariantId)
        {
            await ApplyCardVariantAsync(result.CardVariantId, cancellationToken).ConfigureAwait(true);
        }

        var currentOverrides = _appearanceOverrides ?? ProfileAppearanceOverrides.Default;
        var coverSourceChanged = result.CoverAssetId != CoverAssetId
            || result.CoverSourceKind != currentOverrides.ResolvedCoverSourceKind
            || result.CoverVideoTimestampMilliseconds != currentOverrides.CoverVideoTimestampMilliseconds;
        if (coverSourceChanged)
        {
            await ApplyCoverAssetAsync(result.CoverAssetId, result.CoverVideoTimestampMilliseconds, cancellationToken).ConfigureAwait(true);
            currentOverrides = _appearanceOverrides ?? currentOverrides;
        }

        var coverAppearanceChanged =
            !string.Equals(result.CoverShape.ToString(), currentOverrides.CoverShape, StringComparison.Ordinal)
            || !string.Equals(result.CoverFrameId, currentOverrides.CoverFrameId, StringComparison.Ordinal)
            || result.CoverFrameScale != currentOverrides.CoverFrameScale
            || !string.Equals(result.CoverFrameTint, currentOverrides.CoverFrameTint, StringComparison.Ordinal)
            || result.CoverFrameIntensity != currentOverrides.CoverFrameIntensity
            || !string.Equals(result.CoverFrameAnimation.ToString(), currentOverrides.CoverFrameAnimation, StringComparison.Ordinal)
            || result.CoverShadow != currentOverrides.CoverShadow
            || Math.Abs(result.CoverCropX - currentOverrides.CropX) > 0.0001
            || Math.Abs(result.CoverCropY - currentOverrides.CropY) > 0.0001
            || Math.Abs(result.CoverZoom - currentOverrides.Zoom) > 0.0001;

        if (coverAppearanceChanged)
        {
            CoverAppearance = CoverFrameCatalog.Resolve(
                new CoverAppearanceRequest(
                    result.CoverShape.ToString(),
                    result.CoverFrameId,
                    result.CoverFrameScale,
                    result.CoverFrameTint,
                    result.CoverFrameIntensity,
                    result.CoverFrameAnimation.ToString(),
                    result.CoverShadow),
                ReduceMotion).Appearance;

            if (_appearanceOperations is not null)
            {
                try
                {
                    var coverRequest = new SetProfileCoverAppearanceRequest(
                        ProfileId,
                        RowVersion,
                        CoverShape: result.CoverShape.ToString(),
                        CoverFrameId: result.CoverFrameId ?? CoverFrameCatalog.NoneFrameId,
                        CoverFrameScale: result.CoverFrameScale,
                        CoverFrameTint: result.CoverFrameTint,
                        CoverFrameIntensity: result.CoverFrameIntensity,
                        CoverFrameAnimation: result.CoverFrameAnimation.ToString(),
                        CoverShadow: result.CoverShadow,
                        CropX: result.CoverCropX,
                        CropY: result.CoverCropY,
                        Zoom: result.CoverZoom);
                    var applied = await _appearanceOperations.SetProfileCoverAppearanceAsync(coverRequest, cancellationToken).ConfigureAwait(true);
                    if (applied.IsSuccess && applied.Value is { } outcome)
                    {
                        RowVersion = outcome.RowVersion;
                        _appearanceOverrides = currentOverrides with
                        {
                            CoverShape = result.CoverShape.ToString(),
                            CoverFrameId = result.CoverFrameId,
                            CoverFrameScale = result.CoverFrameScale,
                            CoverFrameTint = result.CoverFrameTint,
                            CoverFrameIntensity = result.CoverFrameIntensity,
                            CoverFrameAnimation = result.CoverFrameAnimation.ToString(),
                            CoverShadow = result.CoverShadow,
                            CropX = result.CoverCropX,
                            CropY = result.CoverCropY,
                            Zoom = result.CoverZoom,
                        };
                        currentOverrides = _appearanceOverrides;
                        RaisePropertyChanged(nameof(AppearanceOverrides));
                    }
                    else
                    {
                        FolderStatusMessage = applied.UserMessage ?? "The Cover appearance could not be saved.";
                    }
                }
                catch (Exception ex)
                {
                    FolderStatusMessage = OperationExecution.SafeMessage(ex);
                }
            }
        }

        var bannerSourceChanged = result.BannerAssetId != BannerAssetId
            || (result.BannerAssetId is not null && result.BannerSourceKind != currentOverrides.ResolvedBannerSourceKind)
            || result.BannerVideoFrameTimestampMilliseconds != currentOverrides.BannerVideoFrameTimestampMilliseconds;
        if (bannerSourceChanged)
        {
            await ApplyBannerAssetAsync(
                result.BannerAssetId,
                cancellationToken,
                result.BannerSourceKind,
                result.BannerVideoFrameTimestampMilliseconds,
                result.BannerStartPointSeconds,
                result.BannerDurationSeconds).ConfigureAwait(true);
            currentOverrides = _appearanceOverrides ?? currentOverrides;
        }

        var bannerPresentationChanged =
            Math.Abs(result.BannerStartPointSeconds - currentOverrides.BannerStartPointSeconds) > 0.0001 ||
            Math.Abs(result.BannerDurationSeconds - currentOverrides.BannerDurationSeconds) > 0.0001 ||
            Math.Abs(result.BannerFocusX - currentOverrides.BannerFocusX) > 0.0001 ||
            Math.Abs(result.BannerFocusY - currentOverrides.BannerFocusY) > 0.0001 ||
            Math.Abs(result.BannerZoom - currentOverrides.BannerZoom) > 0.0001 ||
            result.BannerLoop != currentOverrides.BannerLoop;

        if (bannerPresentationChanged)
        {
            var bannerRequest = new BannerPresentationRequest(
                StartPointSeconds: result.BannerStartPointSeconds,
                DurationSeconds: result.BannerDurationSeconds,
                FocusX: result.BannerFocusX,
                FocusY: result.BannerFocusY,
                Zoom: result.BannerZoom,
                Loop: result.BannerLoop);
            BannerPresentation = BannerPresentationPolicy.Resolve(bannerRequest).Presentation;
        }

        if (_appearanceOperations is not null && bannerPresentationChanged)
        {
            try
            {
                var bannerReq = new SetProfileBannerPresentationRequest(
                    ProfileId,
                    RowVersion,
                    StartPointSeconds: result.BannerStartPointSeconds,
                    DurationSeconds: result.BannerDurationSeconds,
                    FocusX: result.BannerFocusX,
                    FocusY: result.BannerFocusY,
                    Zoom: result.BannerZoom,
                    Loop: result.BannerLoop);
                var banRes = await _appearanceOperations.SetProfileBannerPresentationAsync(bannerReq, cancellationToken).ConfigureAwait(true);
                if (banRes.IsSuccess && banRes.Value is { } banOutcome)
                {
                    RowVersion = banOutcome.RowVersion;
                    _appearanceOverrides = currentOverrides with
                    {
                        BannerStartPointSeconds = result.BannerStartPointSeconds,
                        BannerDurationSeconds = result.BannerDurationSeconds,
                        BannerFocusX = result.BannerFocusX,
                        BannerFocusY = result.BannerFocusY,
                        BannerZoom = result.BannerZoom,
                        BannerLoop = result.BannerLoop
                    };
                    currentOverrides = _appearanceOverrides;
                    RaisePropertyChanged(nameof(AppearanceOverrides));
                }
            }
            catch (Exception ex)
            {
                FolderStatusMessage = OperationExecution.SafeMessage(ex);
            }
        }

        if (_appearanceOperations is null)
        {
            _appearanceOverrides = currentOverrides with
            {
                CropX = result.CoverCropX,
                CropY = result.CoverCropY,
                Zoom = result.CoverZoom,
                CoverShape = result.CoverShape.ToString(),
                CoverFrameId = result.CoverFrameId,
                CoverFrameScale = result.CoverFrameScale,
                CoverFrameTint = result.CoverFrameTint,
                CoverFrameIntensity = result.CoverFrameIntensity,
                CoverFrameAnimation = result.CoverFrameAnimation.ToString(),
                CoverShadow = result.CoverShadow,
                CoverSourceKind = result.CoverAssetId is null ? null : result.CoverSourceKind.ToString(),
                CoverVideoTimestampMilliseconds = result.CoverVideoTimestampMilliseconds,
                BannerStartPointSeconds = result.BannerStartPointSeconds,
                BannerDurationSeconds = result.BannerDurationSeconds,
                BannerSourceKind = result.BannerAssetId is null ? null : result.BannerSourceKind?.ToString(),
                BannerVideoFrameTimestampMilliseconds = result.BannerVideoFrameTimestampMilliseconds,
                BannerFocusX = result.BannerFocusX,
                BannerFocusY = result.BannerFocusY,
                BannerZoom = result.BannerZoom,
                BannerLoop = result.BannerLoop,
                GalleryCardVariantId = result.CardVariantId ?? CurrentCardVariantId
            };
            RaisePropertyChanged(nameof(AppearanceOverrides));
        }
    }

    private static ImageRef? LoadImageSource(string? path) =>
        string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : new ImageRef(path, 0);

    public async Task RenameAsync(string newDisplayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            RenameStatusMessage = "Display name cannot be empty.";
            return;
        }

        if (_profileOperations is null)
        {
            DisplayName = newDisplayName.Trim();
            RenameStatusMessage = "Name updated.";
            return;
        }

        try
        {
            var request = new RenameProfileRequest(ProfileId, RowVersion, newDisplayName);
            var result = await _profileOperations.RenameProfileAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                DisplayName = outcome.DisplayName;
                RowVersion = outcome.RowVersion;
                PathState = outcome.PathState;
                if (outcome.PathState == ManagedPathState.Pending)
                {
                    IsRenaming = true;
                    RenameStatusMessage = "Renaming files... (reconciliation pending)";
                }
                else if (outcome.PathState == ManagedPathState.NeedsAttention)
                {
                    RenameStatusMessage = "Folder renaming needs attention.";
                }
                else
                {
                    IsRenaming = false;
                    RenameStatusMessage = "Profile renamed successfully.";
                }
            }
            else
            {
                RenameStatusMessage = result.UserMessage ?? "Failed to rename Profile.";
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task SetRatingAsync(int? rating, CancellationToken cancellationToken = default)
    {
        int? normalized = rating is null or <= 0 ? null : Math.Clamp(rating.Value, 1, 5);
        if (normalized == Rating)
        {
            return;
        }

        var previous = Rating;
        Rating = normalized;
        try
        {
            if (_profileOperations is not null)
            {
                var request = new UpdateProfileMetadataRequest(
                    ProfileId,
                    RowVersion,
                    CategoryId,
                    [.. _tagAssignments.Select(static assignment => assignment.TagId)],
                    normalized,
                    IsFavorite,
                    Overview,
                    Notes);

                var result = await _profileOperations.UpdateProfileMetadataAsync(request, cancellationToken).ConfigureAwait(true);
                if (!result.IsSuccess || result.Value is not { } outcome)
                {
                    Rating = previous;
                    FolderStatusMessage = result.UserMessage ?? "That rating could not be saved.";
                    return;
                }

                RowVersion = outcome.RowVersion;
            }

            if (_presentationModel is not null)
            {
                _presentationModel = _presentationModel with { Rating = normalized };
                RaisePropertyChanged(nameof(PresentationModel));
            }
        }
        catch (Exception ex)
        {
            Rating = previous;
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public int RatingValue
    {
        get => Rating ?? 0;
        set
        {
            if (value != (Rating ?? 0))
            {
                TaskObserver.Observe(SetRatingAsync(value), "ProfileDetailViewModel.SetRatingAsync");
            }
        }
    }

    public async Task ToggleFavoriteAsync(CancellationToken cancellationToken = default)
    {
        var newFavorite = !IsFavorite;
        IsFavorite = newFavorite;
        try
        {
            if (_profileOperations is not null)
            {
                var request = new UpdateProfileMetadataRequest(
                    ProfileId,
                    RowVersion,
                    CategoryId,
                    [.. _tagAssignments.Select(static assignment => assignment.TagId)],
                    Rating,
                    newFavorite,
                    Overview,
                    Notes);
                var result = await _profileOperations.UpdateProfileMetadataAsync(request, cancellationToken).ConfigureAwait(true);
                if (!result.IsSuccess || result.Value is not { } outcome)
                {
                    IsFavorite = !newFavorite;
                    FolderStatusMessage = result.UserMessage ?? "Failed to update the favorite state.";
                    return;
                }

                RowVersion = outcome.RowVersion;
            }

            if (_presentationModel is not null)
            {
                _presentationModel = _presentationModel with { IsFavorite = newFavorite };
                RaisePropertyChanged(nameof(PresentationModel));
            }
        }
        catch (Exception ex)
        {
            IsFavorite = !newFavorite;
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public void BeginEditMetadata()
    {
        EditDisplayName = DisplayName;
        EditCategoryId = CategoryId;
        EditTags.Clear();
        foreach (var assignment in _tagAssignments)
        {
            EditTags.Add(new ProfileTagOption(assignment.TagId, assignment.DisplayName));
        }

        TagSearchText = string.Empty;
        EditRating = Rating;
        EditIsFavorite = IsFavorite;
        EditOverview = Overview ?? string.Empty;
        EditNotes = Notes ?? string.Empty;
        IsEditingMetadata = true;
        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public void CancelEditMetadata()
    {
        IsEditingMetadata = false;
    }

    public void PopulateAvailableCategories(IEnumerable<ProfileCategoryOption> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        AvailableCategories.Clear();
        AvailableCategories.Add(new ProfileCategoryOption(null, "(None)"));
        foreach (var c in categories)
        {
            AvailableCategories.Add(c);
        }
    }

    public async Task SaveMetadataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CommitPendingProfileTagsForSaveAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (RouteCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
            return;
        }

        var trimmedName = EditDisplayName?.Trim() ?? string.Empty;
        var stagedAssignments = EditTags.DistinctBy(static tag => tag.TagId, StringComparer.Ordinal).ToList();
        var parsedTags = stagedAssignments.Select(static tag => tag.TagId).ToList();
        var stagedTagNames = stagedAssignments.Select(static tag => tag.DisplayName).ToList();
        var targetCategoryId = string.IsNullOrWhiteSpace(EditCategoryId) ? null : EditCategoryId;

        if (_profileOperations is null)
        {
            if (!string.IsNullOrWhiteSpace(trimmedName))
            {
                DisplayName = trimmedName;
            }
            _categoryId = targetCategoryId;
            CategoryName = AvailableCategories.FirstOrDefault(c => c.CategoryId == targetCategoryId)?.DisplayName;
            if (CategoryName == "(None)")
            {
                CategoryName = null;
            }
            _tags = stagedTagNames;
            _tagAssignments = [.. stagedAssignments.Select(static tag => new ProfileTagAssignment(tag.TagId, tag.DisplayName))];
            Rating = EditRating;
            IsFavorite = EditIsFavorite;
            Overview = string.IsNullOrWhiteSpace(EditOverview) ? null : EditOverview.Trim();
            Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim();
            RowVersion++;
            if (_presentationModel is not null)
            {
                _presentationModel = _presentationModel with
                {
                    DisplayName = DisplayName,
                    CategoryName = CategoryName,
                    Tags = _tags,
                    Rating = Rating,
                    Overview = Overview,
                    Notes = Notes
                };
                RaisePropertyChanged(nameof(PresentationModel));
            }
            RaisePropertyChanged(nameof(Tags));
            RaisePropertyChanged(nameof(CategoryId));
            RaisePropertyChanged(nameof(CategoryName));
            FolderStatusMessage = "Profile metadata updated.";
            IsEditingMetadata = false;
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(trimmedName) && !string.Equals(trimmedName, DisplayName, StringComparison.Ordinal))
            {
                await RenameAsync(trimmedName, cancellationToken).ConfigureAwait(true);
            }

            var request = new UpdateProfileMetadataRequest(
                ProfileId: ProfileId,
                ExpectedRowVersion: RowVersion,
                CategoryId: targetCategoryId,
                TagIds: parsedTags,
                Rating: EditRating,
                IsFavorite: EditIsFavorite,
                Overview: string.IsNullOrWhiteSpace(EditOverview) ? null : EditOverview.Trim(),
                Notes: string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim());
            var result = await _profileOperations.UpdateProfileMetadataAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
                _categoryId = targetCategoryId;
                CategoryName = AvailableCategories.FirstOrDefault(c => c.CategoryId == targetCategoryId)?.DisplayName;
                if (CategoryName == "(None)")
                {
                    CategoryName = null;
                }
                _tags = stagedTagNames;
                _tagAssignments = [.. stagedAssignments.Select(static tag => new ProfileTagAssignment(tag.TagId, tag.DisplayName))];
                Rating = request.Rating;
                IsFavorite = request.IsFavorite;
                Overview = request.Overview;
                Notes = request.Notes;
                FolderStatusMessage = "Profile metadata updated.";

                if (_presentationModel is not null)
                {
                    _presentationModel = _presentationModel with
                    {
                        DisplayName = DisplayName,
                        CategoryName = CategoryName,
                        Tags = _tags,
                        Rating = Rating,
                        IsFavorite = IsFavorite,
                        Overview = Overview,
                        Notes = Notes
                    };
                    RaisePropertyChanged(nameof(PresentationModel));
                }
                RaisePropertyChanged(nameof(Tags));
                RaisePropertyChanged(nameof(CategoryId));
                RaisePropertyChanged(nameof(CategoryName));
                IsEditingMetadata = false;
            }
            else
            {
                FolderStatusMessage = result.UserMessage ?? "Failed to update profile metadata.";
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task OpenChangeFaceProfilePickerAsync(FaceReviewItemViewModel face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_overlay is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<ProfilePickerItem> candidates = [];
            if (_catalog is not null)
            {
                candidates = await new ProfilePickerReads(_catalog).GetAllCandidatesAsync().ConfigureAwait(true);
            }
            else if (AvailableCategories.Count > 0)
            {
                candidates = [new ProfilePickerItem(ProfileId, DisplayName, CategoryName)];
            }

            var request = new ProfilePickerOverlayRequest(
                candidates,
                picked => OnReassignFacePicked(face, picked),
                title: "Reassign Face to Profile",
                prompt: "Select a Profile to assign this face detection to:");
            _overlay.Push(request);
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    private void OnReassignFacePicked(FaceReviewItemViewModel face, ProfilePickerItem picked)
    {
        TaskObserver.Observe(ReassignFaceAsync(face, picked), "ProfileDetailViewModel.ReassignFaceAsync");
    }

    private async Task ReassignFaceAsync(FaceReviewItemViewModel face, ProfilePickerItem picked)
    {
        try
        {
            if (_faceDecisionOperations is not null)
            {
                var command = new ConfirmFaceCommand(face.FaceId, picked.ProfileId, face.RowVersion);
                var result = await _faceDecisionOperations.ConfirmFaceAsync(command).ConfigureAwait(true);
                if (!result.IsSuccess || result.Value is not { } outcome)
                {
                    FolderStatusMessage = result.UserMessage ?? "Failed to reassign the face detection.";
                    return;
                }

                face.ApplyDecision(outcome.DecisionState, picked.ProfileId, picked.DisplayName, outcome.RowVersion);
            }
            else
            {
                face.ApplyDecision(FaceDecisionState.Confirmed, picked.ProfileId, picked.DisplayName, face.RowVersion + 1);
            }

            RaisePropertyChanged(nameof(UnresolvedFaceCount));
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task ConfirmFaceAsync(FaceReviewItemViewModel face, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_faceDecisionOperations is null)
        {
            face.ApplyDecision(FaceDecisionState.Confirmed, ProfileId, DisplayName, face.RowVersion + 1);
            RaisePropertyChanged(nameof(UnresolvedFaceCount));
            return;
        }

        try
        {
            var command = new ConfirmFaceCommand(face.FaceId, ProfileId, face.RowVersion);
            var result = await _faceDecisionOperations.ConfirmFaceAsync(command, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                face.ApplyDecision(outcome.DecisionState, ProfileId, DisplayName, outcome.RowVersion);
                RaisePropertyChanged(nameof(UnresolvedFaceCount));
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task RejectFaceAsync(FaceReviewItemViewModel face, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_faceDecisionOperations is null)
        {
            face.ApplyDecision(FaceDecisionState.Rejected, null, null, face.RowVersion + 1);
            RaisePropertyChanged(nameof(UnresolvedFaceCount));
            return;
        }

        try
        {
            var command = new RejectFaceCommand(face.FaceId, face.RowVersion);
            var result = await _faceDecisionOperations.RejectFaceAsync(command, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                face.ApplyDecision(outcome.DecisionState, null, null, outcome.RowVersion);
                RaisePropertyChanged(nameof(UnresolvedFaceCount));
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public void OpenMediaDetail(Guid assetId) => OpenInspector(assetId);

    public void OpenInspector(Guid assetId)
    {
        if (_isDisposed || assetId == Guid.Empty || _inspector?.AssetId == assetId)
        {
            return;
        }

        var origin = MediaGrid.CaptureState(ProfileId);
        if (origin.SelectedAssetIds.Count == 0)
        {
            origin = origin with { SelectedAssetIds = [assetId], AnchorAssetId = assetId };
        }

        RetireInspector();
        Inspector = new MediaDetailViewModel(
            assetId,
            origin,
            _catalog,
            modelAdapters: _modelAdapters,
            derivedStills: _derivedStills,
            closeInspector: CloseInspector,
            retargetInspector: OpenInspector,
            mediaChanged: OnInspectedMediaChanged);
    }

    public void CloseInspector()
    {
        if (_inspector is null)
        {
            return;
        }

        RetireInspector();
        Inspector = null;
    }

    private void RetireInspector()
    {
        if (_inspector is { } current)
        {
            current.RetireRoute();
            current.Dispose();
        }
    }

    private void OnInspectedMediaChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        TaskObserver.Observe(
            LoadMediaAsync(),
            "Refreshing profile media after an inspector change",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
    }

    private async Task OpenMediaInDefaultAppAsync(Guid assetId)
    {
        if (_mediaOperations is null)
        {
            FolderStatusMessage = "The media file is not available until the library is open.";
            return;
        }

        var result = await _mediaOperations.OpenInDefaultAppAsync(assetId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            FolderStatusMessage = result.UserMessage ?? "Windows could not open this media item with its default app.";
        }
    }

    public async Task AssignUnknownAssetsToProfileAsync(Guid destinationProfileId, IReadOnlyList<Guid>? assetIds = null, CancellationToken cancellationToken = default)
    {
        var selected = (assetIds ?? SelectedUnknownAssetIds).ToList();
        if (selected.Count == 0)
        {
            UnknownStatusNotice = "No media items selected for assignment.";
            return;
        }

        if (_unknownOperations is null)
        {
            ActiveOwnedAssetCount = Math.Max(0, ActiveOwnedAssetCount - selected.Count);
            RaisePropertyChanged(nameof(ActiveOwnedAssetCount));
            RaisePropertyChanged(nameof(IsActiveUnresolved));
            UnknownStatusNotice = $"Assigned {selected.Count} items to Profile.";
            return;
        }

        try
        {
            var request = new AssignUnknownAssetsToProfileRequest(ProfileId, selected, destinationProfileId, RowVersion);
            var result = await _unknownOperations.AssignUnknownAssetsToProfileAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                ActiveOwnedAssetCount = outcome.RemainingUnknownAssetCount;
                UnknownStatusNotice = outcome.SourceUnknownLeavesActiveUnresolved
                    ? $"Assigned {outcome.ResolvedAssetCount} items. UNKNOWN Profile is now fully resolved and leaves active queue."
                    : $"Assigned {outcome.ResolvedAssetCount} items. UNKNOWN Profile has {outcome.RemainingUnknownAssetCount} active items remaining.";
                RaisePropertyChanged(nameof(ActiveOwnedAssetCount));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
                await LoadAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                UnknownStatusNotice = result.UserMessage ?? "Failed to assign media.";
            }
        }
        catch (Exception ex)
        {
            UnknownStatusNotice = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task CreateProfileFromUnknownAssetsAsync(string newDisplayName, IReadOnlyList<Guid>? assetIds = null, CancellationToken cancellationToken = default)
    {
        var selected = (assetIds ?? SelectedUnknownAssetIds).ToList();
        if (selected.Count == 0)
        {
            UnknownStatusNotice = "No media items selected for assignment.";
            return;
        }

        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            UnknownStatusNotice = "New Profile name is required.";
            return;
        }

        if (_unknownOperations is null)
        {
            ActiveOwnedAssetCount = Math.Max(0, ActiveOwnedAssetCount - selected.Count);
            RaisePropertyChanged(nameof(ActiveOwnedAssetCount));
            RaisePropertyChanged(nameof(IsActiveUnresolved));
            UnknownStatusNotice = $"Created Profile '{newDisplayName}' with {selected.Count} items.";
            return;
        }

        try
        {
            var request = new CreateProfileFromUnknownAssetsRequest(ProfileId, selected, newDisplayName, ExpectedUnknownRowVersion: RowVersion);
            var result = await _unknownOperations.CreateProfileFromUnknownAssetsAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                ActiveOwnedAssetCount = outcome.RemainingUnknownAssetCount;
                UnknownStatusNotice = outcome.SourceUnknownLeavesActiveUnresolved
                    ? $"Created Profile '{outcome.DestinationDisplayName}'. UNKNOWN Profile is now fully resolved and leaves active queue."
                    : $"Created Profile '{outcome.DestinationDisplayName}'. UNKNOWN has {outcome.RemainingUnknownAssetCount} active items remaining.";
                RaisePropertyChanged(nameof(ActiveOwnedAssetCount));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
                _navigation?.Navigate(new ProfileRoute(outcome.DestinationProfileId));
            }
            else
            {
                UnknownStatusNotice = result.UserMessage ?? "Failed to create Profile from Unknown.";
            }
        }
        catch (Exception ex)
        {
            UnknownStatusNotice = OperationExecution.SafeMessage(ex);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }

        RetireInspector();
        _inspector = null;
        _pageRefresh?.Dispose();
        _appearanceRefresh?.Dispose();
        _mediaRefresh?.Dispose();
        _relatedRefresh?.Dispose();
        _thumbnailLoads?.Cancel();
        _thumbnailLoads?.Dispose();
        MediaGrid.Dispose();
        MediaItems.Clear();
        RecentMedia.Clear();
        RelatedProfiles.Clear();
        Related.EvidenceEntries.Clear();
        Faces.Clear();
    }
}

public sealed class ProfileMediaItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ProfileMediaItemViewModel(ProfileMediaItemReadModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public ProfileMediaItemReadModel Model { get; }
    public Guid AssetId => Model.AssetId;
    public MediaType MediaType => Model.MediaType;
    public ProfileAssetRelation RelationType => Model.RelationType;
    public string? ManagedRelativePath => Model.ManagedRelativePath;
    public string? ManagedFileName => Model.ManagedFileName;
    public DateTimeOffset CreatedAtUtc => Model.CreatedAtUtc;
    public int? PixelWidth => Model.PixelWidth;
    public string? ContentFingerprint => Model.ContentFingerprint;
    public int? PixelHeight => Model.PixelHeight;
    public int? DurationMs => Model.DurationMs;
    public ManagedPathState PathState => Model.PathState;
    public bool IsNeedsAttention => PathState == ManagedPathState.NeedsAttention;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                RaisePropertyChanged();
            }
        }
    }

    public string RelationLabel => RelationType switch
    {
        ProfileAssetRelation.Owner => "Owner",
        ProfileAssetRelation.Appears => "Appears In",
        ProfileAssetRelation.Manual => "Manual",
        _ => RelationType.ToString()
    };

    public string DurationFormatted => DurationMs.HasValue && DurationMs.Value > 0
        ? TimeSpan.FromMilliseconds(DurationMs.Value).ToString(@"m\:ss")
        : string.Empty;
}
