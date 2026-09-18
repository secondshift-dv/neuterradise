namespace Neuterradise.App.Media.Video;

public enum VideoPlaybackState
{
    Stopped,
    Playing,
    Paused,
    Failed
}

public sealed class VideoPreviewController : IDisposable
{
    private readonly VideoPreviewDescriptor _descriptor;
    private readonly Action<VideoPlaybackState>? _stateChanged;
    private VideoPlaybackState _state = VideoPlaybackState.Stopped;
    private bool _isDisposed;

    public VideoPreviewController(
        VideoPreviewDescriptor descriptor,
        Action<VideoPlaybackState>? stateChanged = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _stateChanged = stateChanged;
    }

    private bool _isMuted = true;

    public Guid AssetId => _descriptor.AssetId;
    public string? PreviewPath => _descriptor.PreviewVideoPath;
    public bool IsMuted => _isMuted;
    public VideoPlaybackState State => _state;
    public bool IsPlaying => _state == VideoPlaybackState.Playing;
    public bool IsDisposed => _isDisposed;

    public void ToggleMute()
    {
        if (_isDisposed) return;
        _isMuted = !_isMuted;
    }

    public void SetMuted(bool isMuted)
    {
        if (_isDisposed) return;
        _isMuted = isMuted;
    }

    public void Play()
    {
        if (_isDisposed) return;

        if (string.IsNullOrWhiteSpace(_descriptor.PreviewVideoPath))
        {
            SetState(VideoPlaybackState.Failed);
            return;
        }

        SetState(VideoPlaybackState.Playing);
    }

    public void Pause()
    {
        if (_isDisposed) return;
        if (_state == VideoPlaybackState.Playing)
        {
            SetState(VideoPlaybackState.Paused);
        }
    }

    public void Stop()
    {
        if (_isDisposed) return;
        if (_state != VideoPlaybackState.Stopped)
        {
            SetState(VideoPlaybackState.Stopped);
        }
    }

    private void SetState(VideoPlaybackState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            _stateChanged?.Invoke(_state);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_state != VideoPlaybackState.Stopped)
        {
            SetState(VideoPlaybackState.Stopped);
        }
    }
}
