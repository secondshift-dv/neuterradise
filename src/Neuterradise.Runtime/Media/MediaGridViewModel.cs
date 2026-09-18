using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Media;

public sealed class MediaActivationArbiter : IDisposable
{
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;

    private readonly uint _doubleClickTimeMs;
    private readonly int _doubleClickWidth;
    private readonly int _doubleClickHeight;
    private readonly object _lock = new();
    private readonly SynchronizationContext? _synchronizationContext;

    private PendingClickRecord? _pending;
    private int _routeGeneration;
    private int _queryGeneration;
    private bool _isDisposed;

    private sealed class PendingClickRecord
    {
        public Guid AssetId { get; init; }
        public int RouteGeneration { get; init; }
        public int QueryGeneration { get; init; }
        public MouseButton Button { get; init; }
        public ModifierKeys Modifiers { get; init; }
        public long TimestampMs { get; init; }
        public Point Position { get; init; }
        public CancellationTokenSource Cts { get; init; } = new();
    }

    public MediaActivationArbiter(uint? doubleClickTimeMs = null)
    {
        _doubleClickTimeMs = doubleClickTimeMs ?? GetSystemDoubleClickTime();
        _doubleClickWidth = GetSystemMetricSafe(SmCxDoubleClk, 4);
        _doubleClickHeight = GetSystemMetricSafe(SmCyDoubleClk, 4);
        _synchronizationContext = SynchronizationContext.Current;
    }

    public int RouteGeneration
    {
        get { lock (_lock) return _routeGeneration; }
        set
        {
            lock (_lock)
            {
                if (_routeGeneration != value)
                {
                    _routeGeneration = value;
                    CancelPendingInternal();
                }
            }
        }
    }

    public int QueryGeneration
    {
        get { lock (_lock) return _queryGeneration; }
        set
        {
            lock (_lock)
            {
                if (_queryGeneration != value)
                {
                    _queryGeneration = value;
                    CancelPendingInternal();
                }
            }
        }
    }

    public Guid? PendingAssetId
    {
        get { lock (_lock) return _pending?.AssetId; }
    }

    private static uint GetSystemDoubleClickTime()
    {
        try
        {
            var time = GetDoubleClickTime();
            return time > 0 ? time : 500;
        }
        catch
        {
            return 500;
        }
    }

    private static int GetSystemMetricSafe(int index, int fallback)
    {
        try
        {
            var metric = GetSystemMetrics(index);
            return metric > 0 ? metric : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void OnPointerDown(
        Guid assetId,
        MouseButton button,
        int clickCount,
        ModifierKeys modifiers,
        Point position,
        Action onSingleClick,
        Action onDoubleClick)
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            var nowMs = Environment.TickCount64;

            // Only unmodified left clicks trigger the single/double click race for internal detail vs default app
            if (button != MouseButton.Left || (modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) != ModifierKeys.None)
            {
                CancelPendingInternal();
                return;
            }

            // Check if this matches a double-click on the same pending asset within Windows double-click tolerances
            if (_pending != null && _pending.AssetId == assetId && _pending.RouteGeneration == _routeGeneration && _pending.QueryGeneration == _queryGeneration)
            {
                var dt = nowMs - _pending.TimestampMs;
                var dx = Math.Abs(position.X - _pending.Position.X);
                var dy = Math.Abs(position.Y - _pending.Position.Y);

                if (clickCount >= 2 || (dt <= _doubleClickTimeMs && dx <= _doubleClickWidth && dy <= _doubleClickHeight))
                {
                    // Double click cancels pending internal detail open and invokes default app action exactly once
                    CancelPendingInternal();
                    PostToUiThread(onDoubleClick);
                    return;
                }
            }

            // Otherwise, any prior pending click on another tile (or expired click) is cancelled without opening
            CancelPendingInternal();

            var record = new PendingClickRecord
            {
                AssetId = assetId,
                RouteGeneration = _routeGeneration,
                QueryGeneration = _queryGeneration,
                Button = button,
                Modifiers = modifiers,
                TimestampMs = nowMs,
                Position = position
            };

            _pending = record;
            var token = record.Cts.Token;

            _ = RunDeferredSingleClickAsync(record, token, onSingleClick);
        }
    }

    private async Task RunDeferredSingleClickAsync(PendingClickRecord record, CancellationToken token, Action onSingleClick)
    {
        try
        {
            await Task.Delay((int)_doubleClickTimeMs, token).ConfigureAwait(false);

            lock (_lock)
            {
                if (_isDisposed || token.IsCancellationRequested || !ReferenceEquals(_pending, record))
                {
                    return;
                }

                _pending = null;
            }

            PostToUiThread(onSingleClick);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Deferred media activation failed: {0}", exception);
        }
    }

    private void PostToUiThread(Action action)
    {
        if (_synchronizationContext != null && !ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            _synchronizationContext.Post(
                static state =>
                {
                    try
                    {
                        ((Action)state!).Invoke();
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceError("Media activation callback failed: {0}", exception);
                    }
                },
                action);
        }
        else
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Trace.TraceError("Media activation callback failed: {0}", exception);
            }
        }
    }

    public void CancelPending()
    {
        lock (_lock)
        {
            CancelPendingInternal();
        }
    }

    public void CancelPendingForAsset(Guid assetId)
    {
        lock (_lock)
        {
            if (_pending?.AssetId == assetId)
            {
                CancelPendingInternal();
            }
        }
    }

    private void CancelPendingInternal()
    {
        if (_pending != null)
        {
            try
            {
                if (!_pending.Cts.IsCancellationRequested)
                {
                    _pending.Cts.Cancel();
                }
                _pending.Cts.Dispose();
            }
            catch { }
            _pending = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            CancelPendingInternal();
        }
    }
}

public sealed class MediaGridCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isHovered;
    private bool _isActivePreview;
    private string? _thumbnailPath;
    private string? _previewPath;
    private ImagePreviewDescriptor? _imagePreview;
    private VideoPreviewDescriptor? _videoPreview;
    private ModelPreviewDescriptor? _modelPreview;
    private bool? _favoriteOverride;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaGridCardViewModel(MediaGridItem item, string? thumbnailPath = null, string? previewPath = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        _thumbnailPath = thumbnailPath;
        _previewPath = previewPath;
    }

    public MediaGridItem Item { get; }
    public Guid AssetId => Item.AssetId;
    public MediaType MediaType => Item.MediaType;
    public MediaRelationBadge Relation => Item.Relation;
    public string? CurrentManagedFileName => Item.CurrentManagedFileName;
    public int? PixelWidth => Item.PixelWidth;
    public int? PixelHeight => Item.PixelHeight;
    public int? DurationMs => Item.DurationMs;
    public bool HasAttention => Item.HasAttention;

    /// <summary>
    /// Section 20 Media Favorite. The override lets the fire mark flip the instant the user clicks it
    /// while the catalog write is still in flight; <see cref="SetFavorite"/> then reconciles it to
    /// whatever the catalog actually stored.
    /// </summary>
    public bool IsFavorite => _favoriteOverride ?? Item.IsFavorite;

    public bool ShowFavoriteBadge => IsFavorite;
    public bool IsVideoAndFavorite => IsVideo && IsFavorite;

    public void SetFavorite(bool isFavorite)
    {
        if (IsFavorite == isFavorite)
        {
            return;
        }

        _favoriteOverride = isFavorite;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFavoriteBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVideoAndFavorite)));
    }

    public bool IsImage => MediaType == MediaType.Image;
    public bool IsVideo => MediaType == MediaType.Video;
    public bool IsModel => MediaType == MediaType.Model;

    public string FormattedDuration => FormatDuration(DurationMs);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsHovered
    {
        get => _isHovered;
        set => SetField(ref _isHovered, value);
    }

    public bool IsActivePreview
    {
        get => _isActivePreview;
        set
        {
            if (SetField(ref _isActivePreview, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePreviewPath)));
            }
        }
    }

    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetField(ref _thumbnailPath, value);
    }

    private ImageRef? _thumbnailSource;

    /// <summary>
    /// The tile image: a derived thumbnail decoded at tile size off the UI thread and shared through the
    /// bounded memory cache. Binding a path string instead made the UI decode the file synchronously, at
    /// full resolution, on the UI thread for every tile.
    /// </summary>
    public ImageRef? ThumbnailSource
    {
        get => _thumbnailSource;
        set => SetField(ref _thumbnailSource, value);
    }

    public string? PreviewPath
    {
        get => _previewPath;
        set
        {
            if (SetField(ref _previewPath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePreviewPath)));
            }
        }
    }

    public string? ActivePreviewPath => _isActivePreview ? _previewPath : null;

    public ImagePreviewDescriptor? ImagePreview
    {
        get => _imagePreview;
        set => SetField(ref _imagePreview, value);
    }

    public VideoPreviewDescriptor? VideoPreview
    {
        get => _videoPreview;
        set => SetField(ref _videoPreview, value);
    }

    public ModelPreviewDescriptor? ModelPreview
    {
        get => _modelPreview;
        set => SetField(ref _modelPreview, value);
    }

    private static string FormatDuration(int? durationMs)
    {
        if (!durationMs.HasValue || durationMs.Value <= 0) return string.Empty;
        var ts = TimeSpan.FromMilliseconds(durationMs.Value);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class MediaGridViewModel : INotifyPropertyChanged, IDisposable
{
    public const int DefaultPageSize = 48;
    public static readonly IReadOnlyList<int> PageSizeOptions = [24, 48, 96];

    private MediaGridState _state = MediaGridState.Ready;
    private MediaRelationFilter _relationFilter = MediaRelationFilter.All;
    private MediaTypeFilter _typeFilter = MediaTypeFilter.All;
    private MediaGridSort _sort = MediaGridSort.NewestFirst;
    private bool _isFavoriteOnly;
    private int _pageSize = DefaultPageSize;
    private int _currentPage = 1;
    private int _totalCount;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaGridViewModel(
        MediaSelectionModel? selection = null,
        MediaPreviewCoordinator? previewCoordinator = null,
        MediaActivationArbiter? arbiter = null)
    {
        Selection = selection ?? new MediaSelectionModel();
        PreviewCoordinator = previewCoordinator ?? new MediaPreviewCoordinator();
        Arbiter = arbiter ?? new MediaActivationArbiter();

        Selection.Changed += OnSelectionChanged;
        PreviewCoordinator.StateChanged += OnCoordinatorStateChanged;

        FirstPageCommand = new RelayCommand(_ => GoToPage(1), _ => CanFirstPage);
        PreviousPageCommand = new RelayCommand(_ => GoToPage(CurrentPage - 1), _ => CanPreviousPage);
        NextPageCommand = new RelayCommand(_ => GoToPage(CurrentPage + 1), _ => CanNextPage);
        LastPageCommand = new RelayCommand(_ => GoToPage(TotalPages), _ => CanLastPage);
        ToggleFavoriteFilterCommand = new RelayCommand(_ => IsFavoriteOnly = !IsFavoriteOnly);
        // Awaited through the async command. ToggleFavoriteAsync handles its own failures, and the
        // onError hook keeps anything unexpected from ever becoming an unobserved task.
        ToggleFavoriteCommand = new AsyncRelayCommand(
            parameter => parameter is MediaGridCardViewModel card ? ToggleFavoriteAsync(card) : Task.CompletedTask,
            onError: exception => Trace.TraceError("Media favorite command failed: {0}", exception));
    }

    /// <summary>
    /// Persists a Media Favorite change. Supplied by the hosting view model, which owns the catalog;
    /// the grid stays free of persistence concerns exactly as it does for opening media.
    /// Returns the value the catalog actually stored.
    /// </summary>
    public Func<Guid, bool, Task<bool>>? PersistFavoriteAction { get; set; }

    /// <summary>
    /// Receives a safe, user-facing sentence when a recoverable grid action (such as saving a
    /// favorite) fails. The hosting surface decides where to show it.
    /// </summary>
    public Action<string>? ReportRecoverableErrorAction { get; set; }

    private readonly HashSet<Guid> _favoriteWritesInFlight = [];

    /// <summary>
    /// Flips the fire mark optimistically, then reconciles with the stored value. If the write fails
    /// the mark returns to where it was and the failure is reported, never rethrown: a favorite that
    /// could not be saved is a recoverable problem, not a reason to shut the application down.
    /// Returns true when the catalog accepted the requested value.
    /// </summary>
    public async Task<bool> ToggleFavoriteAsync(MediaGridCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (_isDisposed || PersistFavoriteAction is null)
        {
            return false;
        }

        // One write per card at a time, so fast repeated clicks cannot race contradictory values.
        if (!_favoriteWritesInFlight.Add(card.AssetId))
        {
            return false;
        }

        var previous = card.IsFavorite;
        var desired = !previous;
        card.SetFavorite(desired);

        try
        {
            var stored = await PersistFavoriteAction(card.AssetId, desired).ConfigureAwait(true);
            card.SetFavorite(stored);
            return stored == desired;
        }
        catch (Exception exception)
        {
            card.SetFavorite(previous);
            Trace.TraceWarning("Media favorite could not be saved: {0}", exception);
            ReportRecoverableErrorAction?.Invoke(Localization.SurfaceText.Get(
                "Media.Favorite.Failed",
                "That favorite could not be saved. Please try again."));
            return false;
        }
        finally
        {
            _favoriteWritesInFlight.Remove(card.AssetId);
        }
    }

    public ObservableCollection<MediaGridCardViewModel> Cards { get; } = [];

    public MediaSelectionModel Selection { get; }
    public MediaPreviewCoordinator PreviewCoordinator { get; }
    public MediaActivationArbiter Arbiter { get; }

    public ICommand FirstPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand LastPageCommand { get; }
    public ICommand ToggleFavoriteFilterCommand { get; }

    /// <summary>Toggles the Media Favorite mark for the card passed as the command parameter.</summary>
    public ICommand ToggleFavoriteCommand { get; }

    public Action<Guid>? OpenMediaDetailAction { get; set; }
    public Action<Guid>? OpenDefaultAppAction { get; set; }
    public Action? QueryChangedAction { get; set; }
    public Func<MediaGridCardViewModel, CancellationToken, Task<string?>>? ResolveVideoPreviewPathAsync { get; set; }

    public MediaGridState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBlockingState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBackgroundState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowEmptyState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateMessage)));
            }
        }
    }

    public bool ShowBlockingState => State is MediaGridState.Loading or MediaGridState.RecoverableQueryError;
    public bool ShowBackgroundState => State is MediaGridState.BackgroundUpdating;
    public bool ShowEmptyState => State is MediaGridState.EmptyProfileMedia or MediaGridState.FilteredNoResults;

    public string StateMessage => State switch
    {
        MediaGridState.Loading => "Loading media…",
        MediaGridState.EmptyProfileMedia => "This Profile does not have media yet.",
        MediaGridState.FilteredNoResults => "No media matches these filters.",
        MediaGridState.BackgroundUpdating => "Media is updating in the background.",
        MediaGridState.RecoverablePreviewFailure => "A preview could not be shown. Open the media item for details.",
        MediaGridState.RecoverableQueryError => "Media could not be loaded. Try refreshing the Profile.",
        _ => string.Empty,
    };

    public MediaRelationFilter RelationFilter
    {
        get => _relationFilter;
        set
        {
            if (SetField(ref _relationFilter, value))
            {
                Arbiter.CancelPending();
                Arbiter.QueryGeneration++;
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public MediaTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetField(ref _typeFilter, value))
            {
                Arbiter.CancelPending();
                Arbiter.QueryGeneration++;
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public MediaGridSort Sort
    {
        get => _sort;
        set
        {
            if (SetField(ref _sort, value))
            {
                Arbiter.CancelPending();
                Arbiter.QueryGeneration++;
                _currentPage = 1;
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public bool IsFavoriteOnly
    {
        get => _isFavoriteOnly;
        set
        {
            if (SetField(ref _isFavoriteOnly, value))
            {
                Arbiter.CancelPending();
                Arbiter.QueryGeneration++;
                _currentPage = 1;
                Selection.Clear();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            var valid = PageSizeOptions.Contains(value) ? value : DefaultPageSize;
            if (SetField(ref _pageSize, valid))
            {
                Arbiter.CancelPending();
                _currentPage = 1;
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var clamped = Math.Clamp(value, 1, TotalPages);
            if (SetField(ref _currentPage, clamped))
            {
                Arbiter.CancelPending();
                NotifyPaginationChanged();
                QueryChangedAction?.Invoke();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (SetField(ref _totalCount, value))
            {
                NotifyPaginationChanged();
            }
        }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / Math.Max(1, PageSize)));

    public string RangeText
    {
        get
        {
            if (TotalCount == 0) return "0–0 of 0";
            var start = (CurrentPage - 1) * PageSize + 1;
            var end = Math.Min(CurrentPage * PageSize, TotalCount);
            return $"{start}–{end} of {TotalCount}";
        }
    }

    public bool CanFirstPage => CurrentPage > 1 && TotalCount > 0;
    public bool CanPreviousPage => CurrentPage > 1 && TotalCount > 0;
    public bool CanNextPage => CurrentPage < TotalPages && TotalCount > 0;
    public bool CanLastPage => CurrentPage < TotalPages && TotalCount > 0;

    public int SelectionCount => Selection.SelectionCount;
    public bool HasSelection => SelectionCount > 0;

    public void GoToPage(int page)
    {
        if (_isDisposed) return;
        Arbiter.CancelPending();
        var target = Math.Clamp(page, 1, TotalPages);
        if (target != CurrentPage)
        {
            PreviewCoordinator.StopAll();
            CurrentPage = target;
        }
    }

    private void NotifyPaginationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalPages)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFirstPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPreviousPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNextPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanLastPage)));
        (FirstPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LastPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public MediaOriginState CaptureState(Guid profileId)
    {
        return new MediaOriginState(
            ProfileId: profileId,
            RelationFilter: RelationFilter.ToString(),
            TypeFilter: TypeFilter.ToString(),
            Sort: Sort.ToString(),
            AnchorAssetId: Selection.AnchorAssetId,
            AnchorOffsetDip: 0,
            SelectedAssetIds: Selection.SelectedAssetIds.ToList(),
            Page: CurrentPage,
            PageSize: PageSize,
            IsFavoriteOnly: IsFavoriteOnly);
    }

    public void RestoreState(MediaOriginState? state)
    {
        if (state == null) return;

        Arbiter.CancelPending();
        _relationFilter = Enum.TryParse<MediaRelationFilter>(state.RelationFilter, ignoreCase: true, out var relationFilter)
            ? relationFilter
            : MediaRelationFilter.All;
        _typeFilter = Enum.TryParse<MediaTypeFilter>(state.TypeFilter, ignoreCase: true, out var typeFilter)
            ? typeFilter
            : MediaTypeFilter.All;
        _sort = Enum.TryParse<MediaGridSort>(state.Sort, ignoreCase: true, out var sort)
            ? sort
            : MediaGridSort.NewestFirst;
        _pageSize = PageSizeOptions.Contains(state.PageSize) ? state.PageSize : DefaultPageSize;
        _currentPage = Math.Max(1, state.Page);
        _isFavoriteOnly = state.IsFavoriteOnly;

        Selection.Clear();
        if (state.SelectedAssetIds is { Count: > 0 } selected)
        {
            foreach (var id in selected)
            {
                Selection.Toggle(id);
            }
        }

        Selection.RestoreAnchor(state.AnchorAssetId);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelationFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeFilter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavoriteOnly)));
        NotifyPaginationChanged();
    }

    public void SetItems(
        IReadOnlyList<MediaGridItem> items,
        Func<MediaGridItem, (string? ThumbnailPath, string? PreviewPath)>? pathResolver = null,
        int? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        Arbiter.CancelPending();
        PreviewCoordinator.StopAll();
        Cards.Clear();

        foreach (var item in items)
        {
            var (thumb, prev) = pathResolver?.Invoke(item) ?? (null, null);
            var card = new MediaGridCardViewModel(item, thumb, prev)
            {
                IsSelected = Selection.IsSelected(item.AssetId)
            };
            Cards.Add(card);
        }

        TotalCount = totalCount ?? Cards.Count;
        Selection.ReconcileTo(Cards.Select(c => c.AssetId).ToList());

        if (Cards.Count == 0)
        {
            State = RelationFilter != MediaRelationFilter.All || TypeFilter != MediaTypeFilter.All || IsFavoriteOnly
                ? MediaGridState.FilteredNoResults
                : MediaGridState.EmptyProfileMedia;
        }
        else
        {
            State = MediaGridState.Ready;
        }

        NotifyPaginationChanged();
    }

    public void NotifyScrolled()
    {
        Arbiter.CancelPending();
        // J13.6: PreviewCoordinator.NotifyScroll already calls MotionPreviewArbiter.NotifyScroll.
        PreviewCoordinator.NotifyScroll();
    }

    public void HandleCardPointerEnter(MediaGridCardViewModel card)
    {
        if (_isDisposed) return;
        card.IsHovered = true;

        if (card.IsImage)
        {
            PreviewCoordinator.ScheduleHover(card.AssetId, MediaType.Image);
        }
        else if (card.IsVideo)
        {
            PreviewCoordinator.ScheduleHover(
                card.AssetId,
                MediaType.Video,
                videoDescriptorLoader: async token =>
                {
                    var capturedAssetId = card.AssetId;
                    var previewPath = card.PreviewPath;
                    if (string.IsNullOrWhiteSpace(previewPath) && ResolveVideoPreviewPathAsync is not null)
                    {
                        previewPath = await ResolveVideoPreviewPathAsync(card, token).ConfigureAwait(false);
                        // J13.3: Marshal card property mutation to UI thread.
                        if (card.IsHovered && !token.IsCancellationRequested && !string.IsNullOrWhiteSpace(previewPath))
                        {
                            var capturedPath = previewPath;
                            UiDispatch.Run(() =>
                            {
                                if (card.IsHovered && card.AssetId == capturedAssetId)
                                {
                                    card.PreviewPath = capturedPath;
                                }
                            });
                        }
                    }
                    return new VideoPreviewDescriptor(card.AssetId, card.ThumbnailPath, previewPath, card.DurationMs,
                        HasPreview: !string.IsNullOrWhiteSpace(previewPath));
                },
                onActivated: desc =>
                {
                    // J13.1: onActivated now receives VideoPreviewDescriptor, not a controller.
                    // The actual visible playback is handled by HoverVideoCoordinator.
                    if (desc is VideoPreviewDescriptor vpd)
                    {
                        UiDispatch.Run(() =>
                        {
                            if (card.IsHovered && card.AssetId == vpd.AssetId)
                            {
                                card.VideoPreview = vpd;
                                card.IsActivePreview = vpd.HasPreview;
                            }
                        });
                    }
                });
        }
        else if (card.IsModel)
        {
            PreviewCoordinator.ScheduleHover(
                card.AssetId,
                MediaType.Model,
                modelDescriptorLoader: _ => Task.FromResult<ModelPreviewDescriptor?>(
                    new ModelPreviewDescriptor(card.AssetId, card.ThumbnailPath, HasTurntable: !string.IsNullOrWhiteSpace(card.PreviewPath))),
                onActivated: desc =>
                {
                    if (desc is ModelPreviewDescriptor mpd)
                    {
                        UiDispatch.Run(() =>
                        {
                            if (card.IsHovered && card.AssetId == mpd.AssetId)
                            {
                                card.ModelPreview = mpd;
                            }
                        });
                    }
                });
        }
    }

    public void HandleCardPointerLeave(MediaGridCardViewModel card)
    {
        if (_isDisposed) return;
        card.IsHovered = false;
        PreviewCoordinator.CancelHover(card.AssetId);
    }

    public void HandleCardPointerDown(
        MediaGridCardViewModel card,
        MouseButton button,
        int clickCount,
        ModifierKeys modifiers,
        Point position)
    {
        if (_isDisposed) return;

        // Right-click semantics: preserve an existing multi-selection when the right-clicked card
        // is already part of the current selection. If not, select only this card.
        // Right-click never opens Media Detail or default app.
        if (button == MouseButton.Right)
        {
            Arbiter.CancelPending();
            if (!card.IsSelected)
            {
                Selection.SelectOnly(card.AssetId);
            }
            return;
        }

        if (button != MouseButton.Left)
        {
            return;
        }

        // Ctrl selection never opens Media Detail; toggles individual item selection
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            Arbiter.CancelPending();
            Selection.Toggle(card.AssetId);
            return;
        }

        // Shift selection never opens Media Detail; selects range from anchor to clicked item
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            Arbiter.CancelPending();
            var currentOrder = Cards.Select(c => c.AssetId).ToList();
            Selection.SelectRange(Selection.AnchorAssetId ?? card.AssetId, card.AssetId, currentOrder);
            return;
        }

        // Unmodified single click becomes sole selection and triggers deferred Media Detail opening
        Selection.SelectOnly(card.AssetId);

        Arbiter.OnPointerDown(
            card.AssetId,
            button,
            clickCount,
            modifiers,
            position,
            onSingleClick: () => OpenMediaDetailAction?.Invoke(card.AssetId),
            onDoubleClick: () => OpenDefaultAppAction?.Invoke(card.AssetId));
    }

    public void HandleCardClick(MediaGridCardViewModel card, int clickCount = 1, bool isCtrl = false, bool isShift = false)
    {
        var modifiers = ModifierKeys.None;
        if (isCtrl) modifiers |= ModifierKeys.Control;
        if (isShift) modifiers |= ModifierKeys.Shift;
        HandleCardPointerDown(card, MouseButton.Left, clickCount, modifiers, default);
    }

    public void ToggleCardSelection(MediaGridCardViewModel card)
    {
        if (_isDisposed) return;
        Arbiter.CancelPending();
        Selection.Toggle(card.AssetId);
    }

    public void HandleCardRecycled(MediaGridCardViewModel card)
    {
        if (_isDisposed) return;
        card.IsHovered = false;
        Arbiter.CancelPendingForAsset(card.AssetId);
        if (card.IsActivePreview || PreviewCoordinator.PendingAssetId == card.AssetId)
        {
            PreviewCoordinator.CancelHover(card.AssetId);
        }
    }

    public void HandleKeyDown(Key key, MediaGridCardViewModel? focusedCard, bool isCtrl = false, bool isShift = false)
    {
        if (_isDisposed) return;

        switch (key)
        {
            case Key.Space when focusedCard != null:
                Arbiter.CancelPending();
                Selection.Toggle(focusedCard.AssetId);
                break;

            case Key.Enter:
                var targetId = focusedCard?.AssetId ?? Selection.SelectedAssetIds.FirstOrDefault();
                if (targetId != Guid.Empty)
                {
                    Arbiter.CancelPending();
                    OpenMediaDetailAction?.Invoke(targetId);
                }
                break;

            case Key.A when isCtrl:
                Arbiter.CancelPending();
                Selection.SelectLoaded(Cards.Select(c => c.AssetId).ToList());
                break;

            case Key.Escape:
                Arbiter.CancelPending();
                Selection.Clear();
                PreviewCoordinator.StopAll();
                break;
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var card in Cards)
        {
            card.IsSelected = Selection.IsSelected(card.AssetId);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
    }

    private void OnCoordinatorStateChanged(object? sender, PreviewStateChangedEventArgs e)
    {
        // J13.4: Marshal card mutations to UI thread since StateChanged can fire from background.
        UiDispatch.Run(() =>
        {
            foreach (var card in Cards)
            {
                card.IsActivePreview = card.AssetId == e.NewAssetId;
                if (card.AssetId == e.PreviousAssetId && e.NewAssetId != e.PreviousAssetId)
                {
                    card.VideoPreview = null;
                }
            }
        });
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Selection.Changed -= OnSelectionChanged;
        PreviewCoordinator.StateChanged -= OnCoordinatorStateChanged;

        Arbiter.Dispose();
        PreviewCoordinator.Dispose();
        Cards.Clear();
    }
}
