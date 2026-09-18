using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Media;

public enum PreviewKind
{
    None,
    Image,
    Video,
    Model
}

public sealed record PreviewStateChangedEventArgs(
    Guid? PreviousAssetId,
    PreviewKind PreviousKind,
    Guid? NewAssetId,
    PreviewKind NewKind);

public sealed class MediaPreviewCoordinator : IDisposable
{
    public const int DefaultDwellDelayMs = 350;

    private readonly object _syncLock = new();
    private readonly int _dwellDelayMs;

    private Guid? _activeAssetId;
    private PreviewKind _activeKind = PreviewKind.None;

    private Guid? _pendingAssetId;
    private MediaType? _pendingMediaType;
    private CancellationTokenSource? _dwellCts;

    private IMotionLease? _activeMotionLease;
    private IDisposable? _activeModelAnimation;
    private bool _isDisposed;

    private readonly Func<bool> _isReducedMotionActive;

    public event EventHandler<PreviewStateChangedEventArgs>? StateChanged;

    public MediaPreviewCoordinator(int dwellDelayMs = DefaultDwellDelayMs, Func<bool>? isReducedMotionActive = null)
    {
        _dwellDelayMs = dwellDelayMs > 0 ? dwellDelayMs : DefaultDwellDelayMs;
        _isReducedMotionActive = isReducedMotionActive ?? (() => false);
    }

    public Guid? ActiveAssetId
    {
        get { lock (_syncLock) return _activeAssetId; }
    }

    public PreviewKind ActiveKind
    {
        get { lock (_syncLock) return _activeKind; }
    }

    public bool HasActivePreview
    {
        get { lock (_syncLock) return _activeAssetId.HasValue && _activeKind != PreviewKind.None; }
    }

    public Guid? PendingAssetId
    {
        get { lock (_syncLock) return _pendingAssetId; }
    }

    public void ScheduleHover(
        Guid assetId,
        MediaType mediaType,
        Func<CancellationToken, Task<VideoPreviewDescriptor?>>? videoDescriptorLoader = null,
        Func<CancellationToken, Task<ModelPreviewDescriptor?>>? modelDescriptorLoader = null,
        Action<object?>? onActivated = null)
    {
        if (assetId == Guid.Empty) throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));

        lock (_syncLock)
        {
            if (_isDisposed) return;

            if (_activeAssetId == assetId && _activeKind != PreviewKind.None)
            {
                return;
            }

            StopActivePreviewInternal();
            CancelPendingDwellInternal();

            if (mediaType == MediaType.Image)
            {
                TransitionState(assetId, PreviewKind.Image);
                onActivated?.Invoke(new ImagePreviewDescriptor(assetId));
                return;
            }

            if (_isReducedMotionActive())
            {
                return;
            }

            _pendingAssetId = assetId;
            _pendingMediaType = mediaType;
            var dwellCts = new CancellationTokenSource();
            _dwellCts = dwellCts;

            TaskObserver.Observe(
                RunDwellActivationAsync(
                    assetId,
                    mediaType,
                    videoDescriptorLoader,
                    modelDescriptorLoader,
                    onActivated,
                    dwellCts),
                "MediaPreviewCoordinator.RunDwellActivationAsync",
                _ => StopAll());
        }
    }

    private async Task RunDwellActivationAsync(
        Guid assetId,
        MediaType mediaType,
        Func<CancellationToken, Task<VideoPreviewDescriptor?>>? videoDescriptorLoader,
        Func<CancellationToken, Task<ModelPreviewDescriptor?>>? modelDescriptorLoader,
        Action<object?>? onActivated,
        CancellationTokenSource dwellCts)
    {
        var token = dwellCts.Token;
        try
        {
            await Task.Delay(_dwellDelayMs, token).ConfigureAwait(false);

            lock (_syncLock)
            {
                if (_isDisposed
                    || token.IsCancellationRequested
                    || !ReferenceEquals(_dwellCts, dwellCts)
                    || _pendingAssetId != assetId
                    || _pendingMediaType != mediaType)
                {
                    return;
                }

                // Keep the request identity pending while its descriptor resolves. Pointer-leave,
                // scroll, recycle, or a newer hover can then cancel/replace this exact operation.
            }

            if (mediaType == MediaType.Video)
            {
                // J13.1: Load descriptor only — do NOT create invisible VideoPreviewController here.
                // HoverVideoCoordinator owns the actual visible player and motion lease.
                VideoPreviewDescriptor? desc = null;
                if (videoDescriptorLoader != null)
                {
                    try
                    {
                        desc = await videoDescriptorLoader(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        desc = new VideoPreviewDescriptor(assetId, null, null, HasPreview: false);
                    }
                }
                else
                {
                    return;
                }

                // J13.1: If no prepared preview, do not activate video motion.
                if (desc is null || !desc.HasPreview || string.IsNullOrWhiteSpace(desc.PreviewVideoPath))
                {
                    return;
                }

                lock (_syncLock)
                {
                    if (_isDisposed
                        || token.IsCancellationRequested
                        || !ReferenceEquals(_dwellCts, dwellCts)
                        || _pendingAssetId != assetId
                        || _pendingMediaType != mediaType)
                    {
                        return;
                    }

                    _pendingAssetId = null;
                    _pendingMediaType = null;
                    StopActivePreviewInternal();

                    // Transition logical state; the actual visible playback is handled by
                    // HoverVideoCoordinator acquiring its own motion lease.
                    TransitionState(assetId, PreviewKind.Video);
                    onActivated?.Invoke(desc);
                }
            }
            else if (mediaType == MediaType.Model)
            {
                ModelPreviewDescriptor? desc = null;
                if (modelDescriptorLoader != null)
                {
                    try
                    {
                        desc = await modelDescriptorLoader(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        desc = new ModelPreviewDescriptor(assetId, null, HasTurntable: false);
                    }
                }
                else
                {
                    return;
                }

                lock (_syncLock)
                {
                    if (_isDisposed
                        || token.IsCancellationRequested
                        || !ReferenceEquals(_dwellCts, dwellCts)
                        || _pendingAssetId != assetId
                        || _pendingMediaType != mediaType)
                    {
                        return;
                    }

                    _pendingAssetId = null;
                    _pendingMediaType = null;
                    StopActivePreviewInternal();

                    TransitionState(assetId, PreviewKind.Model);
                    onActivated?.Invoke(desc);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_dwellCts, dwellCts))
                {
                    _dwellCts = null;
                    if (_pendingAssetId == assetId)
                    {
                        _pendingAssetId = null;
                        _pendingMediaType = null;
                    }
                    dwellCts.Dispose();
                }
            }
        }
    }

    public void CancelHover(Guid? assetId = null)
    {
        lock (_syncLock)
        {
            if (assetId.HasValue)
            {
                if (_pendingAssetId == assetId.Value)
                {
                    CancelPendingDwellInternal();
                }

                if (_activeAssetId == assetId.Value)
                {
                    StopActivePreviewInternal();
                }
            }
            else
            {
                CancelPendingDwellInternal();
                StopActivePreviewInternal();
            }
        }
    }

    public void NotifyScroll()
    {
        MotionPreviewArbiter.Instance.NotifyScroll();
        StopAll();
    }

    public void StopAll()
    {
        lock (_syncLock)
        {
            CancelPendingDwellInternal();
            StopActivePreviewInternal();
        }
    }

    private void CancelPendingDwellInternal()
    {
        _pendingAssetId = null;
        _pendingMediaType = null;
        if (_dwellCts != null)
        {
            _dwellCts.Cancel();
            _dwellCts.Dispose();
            _dwellCts = null;
        }
    }

    private void StopActivePreviewInternal()
    {
        if (_activeMotionLease != null)
        {
            _activeMotionLease.Dispose();
            _activeMotionLease = null;
        }

        if (_activeAssetId.HasValue || _activeKind != PreviewKind.None)
        {
            if (_activeModelAnimation != null)
            {
                _activeModelAnimation.Dispose();
                _activeModelAnimation = null;
            }

            TransitionState(null, PreviewKind.None);
        }
    }

    private void TransitionState(Guid? newAssetId, PreviewKind newKind)
    {
        var prevAsset = _activeAssetId;
        var prevKind = _activeKind;

        _activeAssetId = newAssetId;
        _activeKind = newKind;

        if (prevAsset != newAssetId || prevKind != newKind)
        {
            StateChanged?.Invoke(this, new PreviewStateChangedEventArgs(prevAsset, prevKind, newAssetId, newKind));
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            CancelPendingDwellInternal();
            StopActivePreviewInternal();
        }
    }
}
