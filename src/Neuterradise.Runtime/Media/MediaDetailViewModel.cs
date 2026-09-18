using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Media;

/// <summary>
/// The media inspector shown beside a Profile's media grid. It is not a route: the hosting surface
/// owns its lifetime and supplies close/retarget callbacks, so the originating context stays the
/// primary one and dismissing the inspector never navigates.
/// </summary>
public sealed class MediaDetailViewModel : ScreenStateViewModel, IDisposable
{
    /// <summary>
    /// Shown when the media command boundary is not composed. The surface says so honestly instead
    /// of launching a process or writing the clipboard from the ViewModel itself (Section 7.1).
    /// </summary>
    private const string MediaActionsUnavailableMessage =
        "File actions are unavailable because the library services are not running.";

    private readonly CatalogDb? _catalog;
    private readonly Action? _closeInspector;
    private readonly Action<Guid>? _retargetInspector;
    private readonly Action? _mediaChanged;
    private readonly MediaOperations? _mediaOperations;
    private readonly TrashCoordinator? _trashCoordinator;
    private readonly ProfileAppearanceOperations? _appearanceOperations;
    private readonly MediaGridProvider? _mediaProvider;

    /// <summary>
    /// The model preview registry owned by the runtime composition root. When it is absent the model
    /// surface reports that interactive preview is unavailable instead of creating a second registry.
    /// </summary>
    private readonly ModelPreviewAdapterRegistry? _modelAdapters;
    private readonly StillExtractionCoordinator? _derivedStills;

    private MediaDetailReadModel? _detail;
    private bool? _favoriteOverride;
    private string _activeTab = "Info";
    private Guid? _previousAssetId;
    private Guid? _nextAssetId;
    private string? _actionFeedbackMessage;

    private double _zoomLevel = 1.0;
    private bool _isActualSize;
    private bool _showFaces;
    private double _volume = 1.0;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _rotationAngle;
    private IModelInteractiveSession? _activeModelSession;
    private string? _presentationPreviewPath;
    private string? _previewUnavailableReason;
    private ContentIdentity? _currentContentIdentity;
    private long _presentationPreviewGeneration;

    public MediaDetailViewModel(
        Guid assetId,
        MediaOriginState? origin = null,
        CatalogDb? catalog = null,
        MediaOperations? mediaOperations = null,
        TrashCoordinator? trashCoordinator = null,
        ProfileAppearanceOperations? appearanceOperations = null,
        MediaGridProvider? mediaProvider = null,
        ModelPreviewAdapterRegistry? modelAdapters = null,
        StillExtractionCoordinator? derivedStills = null,
        Action? closeInspector = null,
        Action<Guid>? retargetInspector = null,
        Action? mediaChanged = null)
    {
        AssetId = assetId;
        Origin = origin;
        _catalog = catalog;
        _closeInspector = closeInspector;
        _retargetInspector = retargetInspector;
        _mediaChanged = mediaChanged;
        _modelAdapters = modelAdapters;
        _derivedStills = derivedStills;

        if (catalog is not null)
        {
            _mediaOperations = mediaOperations ?? new MediaOperations(catalog);
            var moveExecutor = new ManagedMoveExecutor(
                catalog.Paths,
                new WindowsVolumeIdentityProvider(),
                new ManagedFileVerifier(),
                catalog.AssetWrites);
            _trashCoordinator = trashCoordinator ?? new TrashCoordinator(catalog, moveExecutor, _mediaOperations);
            _appearanceOperations = appearanceOperations ?? new ProfileAppearanceOperations(catalog);
            _mediaProvider = mediaProvider ?? new MediaGridProvider(catalog);
        }
        else
        {
            _mediaOperations = mediaOperations;
            _trashCoordinator = trashCoordinator;
            _appearanceOperations = appearanceOperations;
            _mediaProvider = mediaProvider;
        }

        CloseCommand = new RelayCommand(_ => Close());
        PreviousCommand = new AsyncRelayCommand(() => NavigatePreviousAsync(), () => HasPrevious);
        NextCommand = new AsyncRelayCommand(() => NavigateNextAsync(), () => HasNext);

        OpenInDefaultAppCommand = new AsyncRelayCommand(() => OpenInDefaultAppAsync());
        ShowInFolderCommand = new AsyncRelayCommand(() => ShowInFolderAsync());
        CopyPathCommand = new AsyncRelayCommand(_ => CopyPathAsync());
        CopyFilenameCommand = new AsyncRelayCommand(_ => CopyFilenameAsync());

        SetAsCoverCommand = new AsyncRelayCommand(() => SetAsCoverAsync(), () => CanSetCover);
        SetAsBannerCommand = new AsyncRelayCommand(() => SetAsBannerAsync(), () => CanSetBanner);
        ChangePrimaryProfileCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Guid newOwnerId)
            {
                await ChangePrimaryProfileAsync(newOwnerId);
            }
        });
        AddAssociationCommand = new AsyncRelayCommand(async param =>
        {
            if (param is ValueTuple<Guid, ProfileAssetRelation> association)
            {
                await AddAssociationAsync(association.Item1, association.Item2);
            }
        });
        RemoveAssociationCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Guid targetProfileId)
            {
                await RemoveAssociationAsync(targetProfileId);
            }
        });
        MoveToTrashCommand = new AsyncRelayCommand(() => MoveToTrashAsync(), () => CanMoveToTrash);

        ZoomInCommand = new RelayCommand(_ => ZoomIn(), _ => ZoomLevel < 4.0);
        ZoomOutCommand = new RelayCommand(_ => ZoomOut(), _ => ZoomLevel > 0.25);
        FitCommand = new RelayCommand(_ => Fit());
        ActualSizeCommand = new RelayCommand(_ => ActualSize());
        RotateCommand = new RelayCommand(_ => Rotate());
        ResetCameraCommand = new RelayCommand(_ => ResetCamera());
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);

        SelectTabCommand = new RelayCommand(param =>
        {
            if (param is string tab)
            {
                ActiveTab = tab;
            }
        });

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }

        StartRouteTask(LoadAsync, "Media could not be loaded.");
    }

    public Guid AssetId { get; }
    public MediaOriginState? Origin { get; }

    public MediaDetailReadModel? Detail => _detail;

    public string FileName => _detail?.FileName ?? "Unknown";
    public string OriginalName => _detail?.OriginalName ?? "Unknown";
    public MediaType MediaType => _detail?.MediaType ?? MediaType.Image;
    public string MediaTypeText => MediaType.ToString().ToUpperInvariant();
    public AssetState AssetStatus => _detail?.Status ?? AssetState.Active;
    public string StatusText => AssetStatus.ToString();

    public string CurrentLibraryLocation => _detail?.CurrentLibraryLocation ?? string.Empty;
    public string? TargetLibraryLocation => _detail?.TargetLibraryLocation;
    public bool HasPendingReconciliation => _detail?.ReconciliationState != ManagedPathState.None || !string.IsNullOrWhiteSpace(TargetLibraryLocation);
    public string ReconciliationStateText => _detail?.ReconciliationState.ToString() ?? string.Empty;

    public string FileFingerprint => _detail?.FileFingerprint ?? string.Empty;
    public long ByteLength => _detail?.ByteLength ?? 0;
    public string ByteLengthText => MediaFileDetailsBuilder.FormatByteLength(ByteLength);
    public DateTimeOffset AddedToLibraryAt => _detail?.AddedToLibraryAt ?? DateTimeOffset.MinValue;
    public string AddedToLibraryAtText => AddedToLibraryAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public AssetDependencyStatus DependencyStatus => _detail?.DependencyStatus ?? AssetDependencyStatus.SelfContained;
    public string? BundleSha256 => _detail?.BundleSha256;
    public bool HasMissingDependencies => DependencyStatus == AssetDependencyStatus.DependenciesMissing;
    public bool HasUnknownDependencies => DependencyStatus == AssetDependencyStatus.DependenciesUnknown;
    public bool IsFullPreviewReady => !HasMissingDependencies && !HasUnknownDependencies;

    public MediaClues Clues => _detail?.Clues ?? MediaCluesBuilder.Build();
    public string Who => Clues.Who;
    public string Where => Clues.Where;
    public string When => Clues.When;
    public string How => Clues.How;

    public MediaOwnerProfile? OwnerProfile => _detail?.OwnerProfile;
    public string PrimaryProfileName => OwnerProfile?.DisplayName ?? "\u2014";
    public bool HasPrimaryProfile => OwnerProfile is not null;

    public IReadOnlyList<MediaPersonItem> PeopleInMedia => _detail?.PeopleInMedia ?? [];
    public bool HasPeopleInMedia => PeopleInMedia.Count > 0;
    public bool IsPeopleTabVisible => MediaType != MediaType.Model && HasPeopleInMedia;

    public IReadOnlyList<MediaLinkedProfileItem> LinkedProfiles => _detail?.LinkedProfiles ?? [];
    public bool HasLinkedProfiles => LinkedProfiles.Count > 0;

    public IReadOnlyList<MediaFileDetailGroup> FileDetailGroups => _detail?.FileDetails ?? [];
    public object? Info => _detail?.Info;
    public string? PresentationPreviewPath => _presentationPreviewPath;
    public string? PreviewUnavailableReason => _previewUnavailableReason;

    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab != value)
            {
                _activeTab = value;
                RaisePropertyChanged(nameof(ActiveTab));
                RaisePropertyChanged(nameof(IsInfoTabActive));
                RaisePropertyChanged(nameof(IsCluesTabActive));
                RaisePropertyChanged(nameof(IsPeopleTabActive));
                RaisePropertyChanged(nameof(IsConnectionsTabActive));
                RaisePropertyChanged(nameof(IsFileDetailsTabActive));
            }
        }
    }

    public bool IsInfoTabActive => ActiveTab == "Info";
    public bool IsCluesTabActive => ActiveTab == "Clues";
    public bool IsPeopleTabActive => ActiveTab == "People";
    public bool IsConnectionsTabActive => ActiveTab == "Connections";
    public bool IsFileDetailsTabActive => ActiveTab == "File Details";

    public Guid? PreviousAssetId
    {
        get => _previousAssetId;
        private set
        {
            if (_previousAssetId != value)
            {
                _previousAssetId = value;
                RaisePropertyChanged(nameof(PreviousAssetId));
                RaisePropertyChanged(nameof(HasPrevious));
            }
        }
    }

    public Guid? NextAssetId
    {
        get => _nextAssetId;
        private set
        {
            if (_nextAssetId != value)
            {
                _nextAssetId = value;
                RaisePropertyChanged(nameof(NextAssetId));
                RaisePropertyChanged(nameof(HasNext));
            }
        }
    }

    public bool HasPrevious => _previousAssetId.HasValue;
    public bool HasNext => _nextAssetId.HasValue;

    public string? ActionFeedbackMessage
    {
        get => _actionFeedbackMessage;
        set
        {
            if (_actionFeedbackMessage != value)
            {
                _actionFeedbackMessage = value;
                RaisePropertyChanged(nameof(ActionFeedbackMessage));
                RaisePropertyChanged(nameof(HasActionFeedback));
            }
        }
    }

    public bool HasActionFeedback => !string.IsNullOrWhiteSpace(_actionFeedbackMessage);

    /// <summary>
    /// The absolute path to the canonical media file, resolved by the data layer through the single
    /// media-location authority (TRUNK_AUTHORITY_MASTER §12). Returns the precomputed value from
    /// MediaReads; no synchronous filesystem probing in the property getter.
    /// </summary>
    public string? PreviewFilePath
    {
        get
        {
            // I09.1: Return precomputed state from MediaReads; no File.Exists in getter.
            return _detail?.AbsoluteFilePath;
        }
    }

    public bool HasPreviewFile => !string.IsNullOrWhiteSpace(PreviewFilePath);

    public string? ThumbnailPath => PreviewFilePath;
    public bool IsActivePreview => HasPreviewFile;
    public string FormattedDuration => PositionText;
    public bool HasActiveModelSession => _activeModelSession != null && !_activeModelSession.IsDisposed;

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            var clamped = Math.Clamp(value, 0.25, 4.0);
            if (Math.Abs(_zoomLevel - clamped) > 0.001)
            {
                _zoomLevel = clamped;
                RaisePropertyChanged(nameof(ZoomLevel));
                RaisePropertyChanged(nameof(ZoomText));
            }
        }
    }

    public string ZoomText => $"{(int)(ZoomLevel * 100)}%";

    public bool IsActualSize
    {
        get => _isActualSize;
        set
        {
            if (_isActualSize != value)
            {
                _isActualSize = value;
                RaisePropertyChanged(nameof(IsActualSize));
            }
        }
    }

    public bool ShowFaces
    {
        get => _showFaces;
        set
        {
            if (_showFaces != value)
            {
                _showFaces = value;
                RaisePropertyChanged(nameof(ShowFaces));
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_volume - clamped) > 0.001)
            {
                _volume = clamped;
                RaisePropertyChanged(nameof(Volume));
            }
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) > 0.01)
            {
                _positionSeconds = value;
                RaisePropertyChanged(nameof(PositionSeconds));
                RaisePropertyChanged(nameof(PositionText));
            }
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Math.Abs(_durationSeconds - value) > 0.01)
            {
                _durationSeconds = value;
                RaisePropertyChanged(nameof(DurationSeconds));
                RaisePropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText
    {
        get
        {
            var pos = TimeSpan.FromSeconds(PositionSeconds);
            var dur = TimeSpan.FromSeconds(DurationSeconds);
            return $"{pos:mm\\:ss} / {dur:mm\\:ss}";
        }
    }

    public double RotationAngle
    {
        get => _rotationAngle;
        set
        {
            if (Math.Abs(_rotationAngle - value) > 0.1)
            {
                _rotationAngle = value % 360.0;
                RaisePropertyChanged(nameof(RotationAngle));
            }
        }
    }

    public bool CanSetCover => MediaType is MediaType.Image or MediaType.Video && HasPrimaryProfile && AssetStatus == AssetState.Active;
    public bool CanSetBanner => MediaType is MediaType.Image or MediaType.Video && HasPrimaryProfile && AssetStatus == AssetState.Active;
    public bool CanMoveToTrash => AssetStatus == AssetState.Active;

    /// <summary>
    /// Section 20 Media Favorite, shown to the user as the fire mark. The override mirrors the grid:
    /// the mark flips immediately and reconciles to the value the catalog actually stored.
    /// </summary>
    public bool IsFavorite => _favoriteOverride ?? _detail?.IsFavorite ?? false;

    public bool CanToggleFavorite => AssetStatus == AssetState.Active && _catalog is not null;

    public string FavoriteToggleTooltip => IsFavorite
        ? Localization.SurfaceText.Get("Media.Favorite.Remove", "Remove from favorites")
        : Localization.SurfaceText.Get("Media.Favorite.Add", "Mark as favorite");

    /// <summary>Dismisses the inspector. The Profile/Gallery surface underneath is unchanged.</summary>
    public ICommand CloseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    public ICommand OpenInDefaultAppCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand CopyFilenameCommand { get; }

    public ICommand SetAsCoverCommand { get; }
    public ICommand SetAsBannerCommand { get; }
    public ICommand ChangePrimaryProfileCommand { get; }
    public ICommand AddAssociationCommand { get; }
    public ICommand RemoveAssociationCommand { get; }
    public ICommand MoveToTrashCommand { get; }

    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ActualSizeCommand { get; }
    public ICommand RotateCommand { get; }
    public ICommand ResetCameraCommand { get; }
    public ICommand SelectTabCommand { get; }

    public ICommand ToggleFavoriteCommand { get; }

    private async Task ToggleFavoriteAsync()
    {
        if (_catalog is null || !CanToggleFavorite)
        {
            return;
        }

        var previous = IsFavorite;
        SetFavoriteDisplay(!previous);

        try
        {
            var stored = await _catalog.AssetWrites.SetFavoriteAsync(AssetId, !previous, RouteCancellationToken);
            SetFavoriteDisplay(stored);
        }
        catch (Exception exception)
        {
            SetFavoriteDisplay(previous);
            System.Diagnostics.Trace.TraceWarning(
                "Media favorite could not be saved: {0}", exception.GetType().Name);

            ActionFeedbackMessage = Localization.SurfaceText.Get(
                "Media.Favorite.Failed",
                "That favorite could not be saved. Please try again.");
        }
    }

    private void SetFavoriteDisplay(bool isFavorite)
    {
        _favoriteOverride = isFavorite;
        RaisePropertyChanged(nameof(IsFavorite));
        RaisePropertyChanged(nameof(FavoriteToggleTooltip));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ShowLoading();

        if (_catalog is not null)
        {
            var model = await _catalog.MediaReads.GetMediaDetailAsync(AssetId, cancellationToken)
                ;

            if (model is null)
            {
                ShowRecoverableError("Media asset not found or is no longer active in library.");
                return;
            }

            SetDetail(model);

            if (_mediaProvider is not null && Origin is not null)
            {
                var (prev, next) = await _mediaProvider.GetAdjacentAssetsAsync(AssetId, Origin, cancellationToken)
                    ;
                PreviousAssetId = prev;
                NextAssetId = next;
            }

            ShowReady();
        }
        else
        {
            ShowReady();
        }
    }

    public void SetDetail(MediaDetailReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // I09.3/09.4: Compare content identity before rebuilding heavy preview sessions.
        var newIdentity = new ContentIdentity(
            model.AssetId,
            model.MediaType,
            model.AbsoluteFilePath,
            model.CurrentLibraryLocation,
            model.FileName,
            model.FileFingerprint);

        var contentChanged = _currentContentIdentity is null
            || !_currentContentIdentity.Equals(newIdentity);

        _detail = model;
        _favoriteOverride = null;
        _currentContentIdentity = newIdentity;

        RaisePropertyChanged(nameof(Detail));
        RaisePropertyChanged(nameof(IsFavorite));
        RaisePropertyChanged(nameof(CanToggleFavorite));
        RaisePropertyChanged(nameof(FavoriteToggleTooltip));
        RaisePropertyChanged(nameof(FileName));
        RaisePropertyChanged(nameof(OriginalName));
        RaisePropertyChanged(nameof(MediaType));
        RaisePropertyChanged(nameof(MediaTypeText));
        RaisePropertyChanged(nameof(AssetStatus));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(CurrentLibraryLocation));
        RaisePropertyChanged(nameof(TargetLibraryLocation));
        RaisePropertyChanged(nameof(HasPendingReconciliation));
        RaisePropertyChanged(nameof(ReconciliationStateText));
        RaisePropertyChanged(nameof(FileFingerprint));
        RaisePropertyChanged(nameof(ByteLength));
        RaisePropertyChanged(nameof(ByteLengthText));
        RaisePropertyChanged(nameof(AddedToLibraryAt));
        RaisePropertyChanged(nameof(AddedToLibraryAtText));
        RaisePropertyChanged(nameof(Clues));
        RaisePropertyChanged(nameof(Who));
        RaisePropertyChanged(nameof(Where));
        RaisePropertyChanged(nameof(When));
        RaisePropertyChanged(nameof(How));
        RaisePropertyChanged(nameof(OwnerProfile));
        RaisePropertyChanged(nameof(PrimaryProfileName));
        RaisePropertyChanged(nameof(HasPrimaryProfile));
        RaisePropertyChanged(nameof(PeopleInMedia));
        RaisePropertyChanged(nameof(HasPeopleInMedia));
        RaisePropertyChanged(nameof(IsPeopleTabVisible));
        RaisePropertyChanged(nameof(LinkedProfiles));
        RaisePropertyChanged(nameof(HasLinkedProfiles));
        RaisePropertyChanged(nameof(FileDetailGroups));
        RaisePropertyChanged(nameof(Info));
        RaisePropertyChanged(nameof(CanSetCover));
        RaisePropertyChanged(nameof(CanSetBanner));
        RaisePropertyChanged(nameof(CanMoveToTrash));
        (SetAsCoverCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SetAsBannerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MoveToTrashCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(PreviewFilePath));
        RaisePropertyChanged(nameof(HasPreviewFile));
        RaisePropertyChanged(nameof(DependencyStatus));
        RaisePropertyChanged(nameof(BundleSha256));
        RaisePropertyChanged(nameof(HasMissingDependencies));
        RaisePropertyChanged(nameof(HasUnknownDependencies));
        RaisePropertyChanged(nameof(IsFullPreviewReady));

        // I09.4: Only rebuild preview sessions when content identity changed.
        if (contentChanged)
        {
            _presentationPreviewPath = null;
            _previewUnavailableReason = null;
            RaisePropertyChanged(nameof(PresentationPreviewPath));
            RaisePropertyChanged(nameof(PreviewUnavailableReason));

            if (model.MediaType == MediaType.Video)
            {
                // Playback and mute are owned by the shipping MediaPlayerElement transport controls.
                // The ViewModel only provides the canonical media path and metadata.
                RaisePropertyChanged(nameof(ThumbnailPath));
                RaisePropertyChanged(nameof(IsActivePreview));
                RaisePropertyChanged(nameof(FormattedDuration));
            }
            else if (model.MediaType == MediaType.Model)
            {
                _activeModelSession?.Dispose();
                _activeModelSession = null;
                if (HasPreviewFile)
                {
                    var previewPath = PreviewFilePath!;
                    StartRouteTask(async cancellationToken =>
                    {
                        if (_modelAdapters is null)
                        {
                            return;
                        }

                        var session = await _modelAdapters.CreateInteractiveSessionAsync(
                            new ModelInteractiveRequest(AssetId, previewPath),
                            cancellationToken);
                        if (session is null)
                        {
                            return;
                        }

                        if (!IsRouteActive)
                        {
                            session.Dispose();
                            return;
                        }

                        ActiveModelSession = session;
                        RaisePropertyChanged(nameof(HasActiveModelSession));
                    }, "Model preview could not be opened.");
                }
                RaisePropertyChanged(nameof(ActiveModelSession));
                RaisePropertyChanged(nameof(HasActiveModelSession));
                RaisePropertyChanged(nameof(ThumbnailPath));
            }

            StartRouteTask(LoadPresentationPreviewAsync, "Media preview could not be prepared.");
        }
        else
        {
            // I09.4: Content unchanged — just update labels/metadata without tearing down preview.
            RaisePropertyChanged(nameof(PresentationPreviewPath));
            RaisePropertyChanged(nameof(PreviewUnavailableReason));
            RaisePropertyChanged(nameof(ThumbnailPath));
            RaisePropertyChanged(nameof(IsActivePreview));
            RaisePropertyChanged(nameof(FormattedDuration));
            RaisePropertyChanged(nameof(ActiveModelSession));
            RaisePropertyChanged(nameof(HasActiveModelSession));
        }
    }

    /// <summary>
    /// I09.3: Content identity sufficient to determine whether preview resources must be rebuilt.
    /// Metadata-only changes (display name, favorite, relations) do not change content identity.
    /// </summary>
    private sealed record ContentIdentity(
        Guid AssetId,
        MediaType MediaType,
        string? AbsoluteFilePath,
        string? CurrentLibraryLocation,
        string FileName,
        string FileFingerprint);

    private async Task LoadPresentationPreviewAsync(CancellationToken cancellationToken)
    {
        var generation = ++_presentationPreviewGeneration;
        string? preview = null;
        string? reason = null;
        try
        {
            if (MediaType is MediaType.Image or MediaType.Video && _derivedStills is not null)
            {
                var timestamp = MediaType == MediaType.Video ? (long?)Math.Min(5_000, Math.Max(0, (long)(DurationSeconds * 100))) : null;
                preview = await _derivedStills.GetOrCreateAsync(
                    new DerivedStillRequest(AssetId, timestamp, 1200), cancellationToken).ConfigureAwait(true);
            }
            else if (MediaType == MediaType.Model && _modelAdapters is not null && PreviewFilePath is { } modelPath)
            {
                var descriptor = await _modelAdapters.GetOrGeneratePreviewAsync(
                    new ModelPreviewRequest(AssetId, modelPath, TargetWidth: 1200, TargetHeight: 900), cancellationToken).ConfigureAwait(true);
                preview = descriptor.StaticThumbnailPath;
                reason = preview is null ? descriptor.ErrorDetail : null;
            }

            if (MediaType == MediaType.Image)
            {
                preview ??= PreviewFilePath;
            }
            if (preview is null)
            {
                reason ??= "Preview unavailable";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Media presentation preview failed: {0}", ex.GetType().Name);
            reason = OperationExecution.SafeMessage(ex);
        }

        if (!IsRouteActive || cancellationToken.IsCancellationRequested) return;
        if (generation != _presentationPreviewGeneration) return;
        _presentationPreviewPath = preview;
        _previewUnavailableReason = reason;
        RaisePropertyChanged(nameof(PresentationPreviewPath));
        RaisePropertyChanged(nameof(PreviewUnavailableReason));
    }

    public void SetAdjacentAssets(Guid? previousAssetId, Guid? nextAssetId)
    {
        PreviousAssetId = previousAssetId;
        NextAssetId = nextAssetId;
    }

    /// <summary>Stops playback and asks the host to dismiss the inspector.</summary>
    public void Close()
    {
        StopPreviewSessions();
        _closeInspector?.Invoke();
    }

    private void StopPreviewSessions()
    {
        _activeModelSession?.Dispose();
        _activeModelSession = null;
    }

    private void Retarget(Guid assetId)
    {
        StopPreviewSessions();
        _retargetInspector?.Invoke(assetId);
    }

    public Task NavigatePreviousAsync()
    {
        if (PreviousAssetId is { } previous)
        {
            Retarget(previous);
        }
        return Task.CompletedTask;
    }

    public Task NavigateNextAsync()
    {
        if (NextAssetId is { } next)
        {
            Retarget(next);
        }
        return Task.CompletedTask;
    }

    public async Task OpenInDefaultAppAsync()
    {
        if (_mediaOperations is null)
        {
            ShowRecoverableError(MediaActionsUnavailableMessage);
            return;
        }

        var result = await _mediaOperations.OpenInDefaultAppAsync(AssetId);
        ReportExternalActionResult(result, "Failed to open in default application.");
    }

    public async Task ShowInFolderAsync()
    {
        if (_mediaOperations is null)
        {
            ShowRecoverableError(MediaActionsUnavailableMessage);
            return;
        }

        var result = await _mediaOperations.ShowInFolderAsync(AssetId);
        ReportExternalActionResult(result, "Failed to show in folder.");
    }

    public Task CopyPathAsync(CancellationToken cancellationToken = default) =>
        CopyCurrentLocationAsync(
            copyPath: true,
            successMessage: "File path copied to clipboard.",
            failureMessage: "The file path could not be copied.",
            cancellationToken);

    public Task CopyFilenameAsync(CancellationToken cancellationToken = default) =>
        CopyCurrentLocationAsync(
            copyPath: false,
            successMessage: "File name copied to clipboard.",
            failureMessage: "The file name could not be copied.",
            cancellationToken);

    /// <summary>
    /// Copies the current managed location through the media command boundary. The ViewModel never
    /// touches the clipboard, the filesystem or a process itself, and it never reports a copy that
    /// did not actually happen.
    /// </summary>
    private async Task CopyCurrentLocationAsync(
        bool copyPath,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (_mediaOperations is null)
        {
            ShowRecoverableError(MediaActionsUnavailableMessage);
            return;
        }

        var effectiveToken = cancellationToken.CanBeCanceled ? cancellationToken : RouteCancellationToken;
        var location = await _mediaOperations.ResolveCurrentLocationAsync(AssetId, effectiveToken);
        if (location.IsCancelled)
        {
            return;
        }

        if (!location.IsSuccess || location.Value is null)
        {
            ShowRecoverableError(location.UserMessage ?? failureMessage);
            return;
        }

        var result = copyPath
            ? _mediaOperations.CopyCurrentPath(location.Value)
            : _mediaOperations.CopyCurrentFilename(location.Value);

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = successMessage;
            return;
        }

        ShowRecoverableError(result.UserMessage ?? failureMessage);
    }

    private void ReportExternalActionResult(
        OperationResult result,
        string fallbackMessage)
    {
        if (result.IsSuccess || result.IsCancelled)
        {
            return;
        }

        ShowRecoverableError(result.UserMessage ?? fallbackMessage);
    }

    public async Task SetAsCoverAsync()
    {
        if (_appearanceOperations is null || OwnerProfile is null) return;

        long rowVersion = 0;
        if (_catalog is not null)
        {
            var p = await _catalog.ProfileReads.GetDetailAsync(OwnerProfile.ProfileId);
            if (p is not null) rowVersion = p.RowVersion;
        }

        var result = await _appearanceOperations.SetCoverAssetAsync(
            new SetCoverAssetRequest(OwnerProfile.ProfileId, AssetId, rowVersion));

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Set as Profile cover.";
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to set as profile cover.");
        }
    }

    public async Task SetAsBannerAsync()
    {
        if (_appearanceOperations is null || OwnerProfile is null) return;

        long rowVersion = 0;
        if (_catalog is not null)
        {
            var p = await _catalog.ProfileReads.GetDetailAsync(OwnerProfile.ProfileId);
            if (p is not null) rowVersion = p.RowVersion;
        }

        var result = await _appearanceOperations.SetBannerAssetAsync(
            new SetBannerAssetRequest(OwnerProfile.ProfileId, AssetId, rowVersion));

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Set as Profile banner.";
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to set as profile banner.");
        }
    }

    public async Task ChangePrimaryProfileAsync(Guid newOwnerProfileId)
    {
        if (_mediaOperations is null) return;

        var result = await _mediaOperations.ChangePrimaryProfileAsync(
            new ChangePrimaryProfileRequest(AssetId, newOwnerProfileId, _detail?.RowVersion ?? 0));

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Primary Profile changed.";
            await LoadAsync();
            _mediaChanged?.Invoke();
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to change primary profile.");
        }
    }

    public async Task AddAssociationAsync(Guid targetProfileId, ProfileAssetRelation relation)
    {
        if (_mediaOperations is null) return;

        var result = await _mediaOperations.AddProfileAssetAssociationAsync(
            new ProfileAssetAssociationRequest(targetProfileId, AssetId, relation));

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Association added.";
            await LoadAsync();
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to add association.");
        }
    }

    public async Task RemoveAssociationAsync(Guid targetProfileId, ProfileAssetRelation relation = ProfileAssetRelation.Appears)
    {
        if (OwnerProfile is not null && targetProfileId == OwnerProfile.ProfileId)
        {
            ShowRecoverableError("Cannot remove primary owner from active asset. In an Owned context, use Change Primary Profile or Move to Trash.");
            return;
        }

        if (_mediaOperations is null) return;

        var result = await _mediaOperations.RemoveProfileAssetAssociationAsync(
            new ProfileAssetAssociationRequest(targetProfileId, AssetId, relation));

        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Association removed.";
            await LoadAsync();
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to remove association.");
        }
    }

    public async Task MoveToTrashAsync()
    {
        if (_trashCoordinator is null) return;

        var planResult = await _trashCoordinator.PrepareAssetTrashAsync(AssetId);
        if (!planResult.IsSuccess || planResult.Value is null)
        {
            ShowRecoverableError(planResult.UserMessage ?? "Failed to prepare asset trash plan.");
            return;
        }

        var result = await _trashCoordinator.ExecuteAssetTrashAsync(planResult.Value);
        if (result.IsSuccess)
        {
            ActionFeedbackMessage = "Moved to Trash (reversible).";
            _mediaChanged?.Invoke();
            if (NextAssetId is { } next)
            {
                Retarget(next);
            }
            else if (PreviousAssetId is { } previous)
            {
                Retarget(previous);
            }
            else
            {
                Close();
            }
        }
        else
        {
            ShowRecoverableError(result.UserMessage ?? "Failed to move asset to trash.");
        }
    }

    public IModelInteractiveSession? ActiveModelSession
    {
        get => _activeModelSession;
        set
        {
            if (!ReferenceEquals(_activeModelSession, value))
            {
                _activeModelSession?.Dispose();
                _activeModelSession = value;
                RaisePropertyChanged(nameof(ActiveModelSession));
            }
        }
    }

    public void ZoomIn()
    {
        ZoomLevel += 0.25;
        _activeModelSession?.Zoom(1.25);
    }

    public void ZoomOut()
    {
        ZoomLevel -= 0.25;
        _activeModelSession?.Zoom(0.8);
    }

    public void Fit() { ZoomLevel = 1.0; IsActualSize = false; }
    public void ActualSize() { ZoomLevel = 1.0; IsActualSize = true; }

    public void Rotate()
    {
        RotationAngle = (RotationAngle + 45.0) % 360.0;
        _activeModelSession?.Rotate(45.0, 0.0);
    }

    public void ResetCamera()
    {
        RotationAngle = 0.0;
        _activeModelSession?.ResetCamera();
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Media:
                if (invalidation.EntityIds.Count == 0 || invalidation.EntityIds.Contains(AssetId))
                {
                    ScheduleRefresh();
                }
                break;

            case CatalogInvalidationDomain.Profile:
                if (invalidation.EntityIds.Count == 0 || IsProfileRelevant(invalidation.EntityIds))
                {
                    ScheduleRefresh();
                }
                break;
        }
    }

    private bool IsProfileRelevant(IReadOnlyList<Guid> entityIds)
    {
        if (OwnerProfile is not null && entityIds.Contains(OwnerProfile.ProfileId))
        {
            return true;
        }

        foreach (var linked in LinkedProfiles)
        {
            if (entityIds.Contains(linked.ProfileId))
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleRefresh()
    {
        UiDispatch.Run(() => Coalescer.Signal());
    }

    private RefreshCoalescer? _refreshCoalescer;

    // I09.2: Forward coalescer cancellation token to LoadAsync.
    private RefreshCoalescer Coalescer => _refreshCoalescer ??= new RefreshCoalescer(
        ct => LoadAsync(ct),
        TimeSpan.FromMilliseconds(500));

    public void Dispose()
    {
        _refreshCoalescer?.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
        _activeModelSession?.Dispose();
        _activeModelSession = null;
    }
}
