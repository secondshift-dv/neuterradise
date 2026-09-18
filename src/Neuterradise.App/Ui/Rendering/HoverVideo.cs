using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Media;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Resources;
using Windows.Media.Core;

namespace Neuterradise.App.Ui;

/// <summary>
/// ONE shared hover/preview video surface for the whole window (document 01 §28). Cards and tiles never
/// own a player: they ask to borrow this one. Hover is debounced, needs the governor's permission and a
/// VideoPreview permit, and stops on leave, scroll, recycle, navigation, minimize and page change.
/// </summary>
public sealed class HoverVideoCoordinator
{
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(380);

    private static HoverVideoCoordinator? _shared;

    private readonly MediaPlayerElement _player;
    private readonly DispatcherTimer _delay;
    private readonly DispatcherTimer _window;
    private readonly EventHandler _governorChangedHandler;
    private ResourceGovernor? _governor;
    private Panel? _host;
    private Panel? _pendingHost;
    private string? _pendingPath;
    private Guid _pendingEntityId;
    private string _pendingRole = string.Empty;
    private TimeSpan _start;
    private TimeSpan _duration;
    private bool _loop;
    private bool _autoplay = true;
    private bool _mute = true;
    private TimeSpan _preferredDuration = TimeSpan.FromSeconds(6);
    private ResourcePermit? _permit;
    private IMotionLease? _motionLease;

    private HoverVideoCoordinator()
    {
        _player = new MediaPlayerElement
        {
            Stretch = Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            AutoPlay = false,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        if (_player.MediaPlayer is { } mediaPlayer)
        {
            mediaPlayer.MediaFailed += (_, _) => UiDispatch.Run(StopAll);
        }
        _delay = new DispatcherTimer { Interval = HoverDelay };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            StartPending();
        };
        _window = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _window.Tick += (_, _) => EnforceWindow();
        _governorChangedHandler = (_, _) => UiDispatch.Run(() =>
        {
            if (!ResourceGovernor.Shared.IsHoverVideoAllowed)
            {
                StopAll();
            }
        });
        EnsureGovernorSubscription();
    }

    public static HoverVideoCoordinator Shared => _shared ??= new HoverVideoCoordinator();

    public bool IsPlaying => _host is not null;

    /// <summary>Applies the durable Settings media-preview policy to the shared hover player.</summary>
    public void ApplyPreferences(MediaPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _autoplay = preferences.AutoplayVideo;
        _mute = preferences.MuteAudioOnPreview;
        _preferredDuration = TimeSpan.FromSeconds(preferences.VideoPreviewDurationSeconds);
        if (!_autoplay)
        {
            StopAll();
        }
    }

    /// <summary>Requests preview playback inside <paramref name="host"/> after the hover delay.</summary>
    public void Request(Panel host, Guid entityId, string role, string? videoPath, TimeSpan start = default, TimeSpan duration = default, bool loop = true)
        => RequestCore(host, entityId, role, videoPath, start, duration, loop, delayed: true);

    /// <summary>Starts a path already resolved after the data-layer dwell, avoiding a second timer.</summary>
    public void RequestReady(Panel host, Guid entityId, string role, string? videoPath, TimeSpan start = default, TimeSpan duration = default, bool loop = true)
        => RequestCore(host, entityId, role, videoPath, start, duration, loop, delayed: false);

    private void RequestCore(Panel host, Guid entityId, string role, string? videoPath, TimeSpan start, TimeSpan duration, bool loop, bool delayed)
    {
        EnsureGovernorSubscription();
        // J12.8: No synchronous File.Exists — the path is already resolved by derivative/media authority.
        if (!_autoplay || string.IsNullOrWhiteSpace(videoPath) || ReducedMotionAuthority.IsReduced)
        {
            return;
        }

        _pendingHost = host;
        _pendingPath = videoPath;
        _pendingEntityId = entityId;
        _pendingRole = role;
        _start = start;
        _duration = _preferredDuration > TimeSpan.Zero ? _preferredDuration : duration;
        _loop = loop;
        _delay.Stop();
        if (delayed) _delay.Start();
        else StartPending();
    }

    /// <summary>The pointer left, the item was recycled, or the host is going away.</summary>
    public void Release(Panel host)
    {
        if (ReferenceEquals(_pendingHost, host))
        {
            _delay.Stop();
            _pendingHost = null;
        }

        if (ReferenceEquals(_host, host))
        {
            StopPlayerInternal(releaseLease: true);
        }
    }

    /// <summary>Scroll, navigation, minimize: stop whatever is playing.</summary>
    public void StopAll()
    {
        _delay.Stop();
        _window.Stop();
        _pendingHost = null;
        _pendingPath = null;
        _motionLease?.Release();
        _motionLease = null;
        if (_player.MediaPlayer is { } media)
        {
            media.Pause();
        }

        _player.Opacity = 0;
        _player.Source = null;
        if (_host is not null)
        {
            _host.Children.Remove(_player);
            _host = null;
        }

        _permit?.Dispose();
        _permit = null;
    }

    private void StartPending()
    {
        EnsureGovernorSubscription();
        if (_pendingHost is not { } host || _pendingPath is not { } path)
        {
            return;
        }

        if (!ResourceGovernor.Shared.IsHoverVideoAllowed)
        {
            return;
        }

        var entityId = _pendingEntityId;
        var role = _pendingRole;

        // Stop previous playback and release old lease.
        StopAll();

        // J12.3: Acquire motion lease before playback.
        var lease = MotionPreviewArbiter.Instance.TryAcquire(entityId, role, onEvicted: () =>
        {
            // J12.4: Eviction callback — marshal to UI thread.
            UiDispatch.Run(() => StopPlayerInternal(releaseLease: false));
        });
        if (lease is null)
        {
            return;
        }

        if (!ResourceGovernor.Shared.TryAcquire(ResourceClass.VideoPreview, out var permit))
        {
            lease.Release();
            return;
        }

        _motionLease = lease;
        _permit = permit;
        _host = host;
        host.Children.Add(_player);
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
        if (_player.MediaPlayer is { } media)
        {
            media.IsMuted = _mute;
            media.IsLoopingEnabled = _loop && _duration <= TimeSpan.Zero;
            media.PlaybackSession.Position = _start;
            media.Play();
        }

        _player.Opacity = 1;
        if (_duration > TimeSpan.Zero)
        {
            _window.Start();
        }
    }

    /// <summary>
    /// Internal player stop. When called from eviction callback, the lease is already being
    /// released by the arbiter, so we must NOT call Release() again (avoids recursion).
    /// </summary>
    private void StopPlayerInternal(bool releaseLease)
    {
        _window.Stop();
        if (_player.MediaPlayer is { } media)
        {
            media.Pause();
        }

        _player.Opacity = 0;
        _player.Source = null;
        if (_host is not null)
        {
            _host.Children.Remove(_player);
            _host = null;
        }

        _permit?.Dispose();
        _permit = null;

        if (releaseLease)
        {
            _motionLease?.Release();
        }
        _motionLease = null;
    }

    private void EnsureGovernorSubscription()
    {
        var current = ResourceGovernor.Shared;
        if (ReferenceEquals(_governor, current))
        {
            return;
        }

        if (_governor is not null)
        {
            _governor.Changed -= _governorChangedHandler;
        }

        _governor = current;
        _governor.Changed += _governorChangedHandler;
    }

    private void EnforceWindow()
    {
        if (_player.MediaPlayer is not { } media || _duration <= TimeSpan.Zero)
        {
            return;
        }

        if (media.PlaybackSession.Position >= _start + _duration)
        {
            if (_loop)
            {
                media.PlaybackSession.Position = _start;
            }
            else
            {
                StopAll();
            }
        }
    }
}
