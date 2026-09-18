using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Resources;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Media.Core;

namespace Neuterradise.App.Ui;

/// <summary>
/// The living backdrop (document 01 §13): a compiled <see cref="BackdropPlan"/> rendered as bounded
/// declarative layers. Procedural layers are drawn by Skia; video and Lottie layers are real elements
/// in the same stack. The active variant follows the ResourceGovernor tier and Reduced Motion; the
/// frame clock only runs while the owning surface is active, the window is visible and the variant is
/// animated. Nothing here is interpreted from JSON — the plan was compiled once.
/// </summary>
public sealed class BackdropView : Grid
{
    private BackdropPlan? _plan;
    private VisualTier _tier = VisualTier.Full;
    private bool _active;
    private bool _clockRunning;
    private long _lastFrame;
    private readonly long _started = Environment.TickCount64;
    private string? _ambientPath;
    private ArgbColor? _mediaTint;
    private SKImage? _ambientImage;
    private int _ambientGeneration;
    private (double X, double Y) _pointer = (0.5, 0.5);
    private readonly List<BackdropCanvas> _canvases = [];
    private readonly List<MediaPlayerElement> _videos = [];

    public BackdropView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            ResourceGovernor.Shared.Changed += OnGovernorChanged;
            ThemeRuntime.Current.Changed += OnThemeChanged;
            ReducedMotionAuthority.Changed += OnMotionChanged;
            Rebuild();
            if (!string.IsNullOrWhiteSpace(_ambientPath))
            {
                var generation = ++_ambientGeneration;
                TaskObserver.Observe(
                    LoadAmbientAsync(_ambientPath, generation),
                    "BackdropView.LoadAmbientAsync");
            }
        };
        Unloaded += (_, _) =>
        {
            ResourceGovernor.Shared.Changed -= OnGovernorChanged;
            ThemeRuntime.Current.Changed -= OnThemeChanged;
            ReducedMotionAuthority.Changed -= OnMotionChanged;
            StopClock();
            StopVideos(releaseSources: true);
            RetireCanvases();
            ++_ambientGeneration;
            Interlocked.Exchange(ref _ambientImage, null)?.Dispose();
        };
    }

    /// <summary>The compiled plan. Changing it rebuilds the layer stack once; per-frame work only reads it.</summary>
    public BackdropPlan? Plan
    {
        get => _plan;
        set
        {
            if (ReferenceEquals(_plan, value))
            {
                return;
            }

            _plan = value;
            Rebuild();
        }
    }

    /// <summary>Owning surface visibility. Inactive surfaces keep their last frame and stop the clock.</summary>
    public bool IsSurfaceActive
    {
        get => _active;
        set
        {
            _active = value;
            UpdateClock();
            if (!value)
            {
                StopVideos(releaseSources: true);
            }
            else if (_videos.Any(video => video.Source is null))
            {
                Rebuild();
            }
            else
            {
                StartVideos();
            }
        }
    }

    /// <summary>Living media: the Spotlight/Profile artwork used by "media-*" layers.</summary>
    public string? AmbientImagePath
    {
        get => _ambientPath;
        set
        {
            if (string.Equals(_ambientPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _ambientPath = value;
            var generation = ++_ambientGeneration;
            TaskObserver.Observe(
                LoadAmbientAsync(value, generation),
                "BackdropView.LoadAmbientAsync");
        }
    }

    public ArgbColor? MediaTint => _mediaTint;

    public event Action<ArgbColor?>? MediaTintChanged;

    public double ElapsedSeconds => (Environment.TickCount64 - _started) / 1000.0;

    /// <summary>Pointer position over the owning surface (0..1) for depth parallax.</summary>
    public void SetPointer(double x, double y)
    {
        _pointer = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    internal (double X, double Y) Pointer => _pointer;

    internal SKImage? AmbientImage => _ambientImage;

    internal VisualTier Tier => _tier;

    private async Task LoadAmbientAsync(string? path, int generation)
    {
        SKImage? image = null;
        ArgbColor? tint = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            (image, tint) = await Task.Run(() =>
            {
                using var bitmap = TargetSizeDecoder.Decode(path, 960);
                if (bitmap is null)
                {
                    return ((SKImage?)null, (ArgbColor?)null);
                }

                return (SKImage.FromBitmap(bitmap), SampleTint(bitmap));
            }).ConfigureAwait(false);
        }

        try
        {
            UiDispatch.Run(() =>
            {
                if (generation != _ambientGeneration)
                {
                    image?.Dispose();
                    return;
                }

                var previous = _ambientImage;
                _ambientImage = image;
                _mediaTint = tint;
                MediaTintChanged?.Invoke(tint);
                foreach (var canvas in _canvases)
                {
                    canvas.OnAmbientChanged();
                }

                previous?.Dispose();
            });
        }
        catch (InvalidOperationException)
        {
            image?.Dispose();
        }
    }

    /// <summary>
    /// A small readability-safe palette sample (R2 §34.10): average colour with saturation/brightness
    /// clamped so tinted glass never undermines text contrast.
    /// </summary>
    private static ArgbColor SampleTint(SKBitmap bitmap)
    {
        using var tiny = bitmap.Resize(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        if (tiny is null)
        {
            return default;
        }

        long r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = 0; y < tiny.Height; y++)
        {
            for (var x = 0; x < tiny.Width; x++)
            {
                var c = tiny.GetPixel(x, y);
                r += c.Red;
                g += c.Green;
                b += c.Blue;
                count++;
            }
        }

        var color = new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
        color.ToHsl(out var h, out var s, out var l);
        var clamped = SKColor.FromHsl(h, Math.Clamp(s, 20, 60), Math.Clamp(l, 28, 52));
        return new ArgbColor(0xFF, clamped.Red, clamped.Green, clamped.Blue);
    }

    private void OnGovernorChanged(object? sender, EventArgs e) => UiDispatch.Run(ApplyTier);

    private void OnThemeChanged()
    {
        foreach (var canvas in _canvases)
        {
            canvas.Prepare();
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => UiDispatch.Run(ApplyTier);

    private VisualTier CurrentTier()
    {
        if (ReducedMotionAuthority.IsReduced)
        {
            return VisualTier.Fallback;
        }

        return ResourceGovernor.Shared.Tier switch
        {
            PresentationTier.Full => VisualTier.Full,
            PresentationTier.Reduced => VisualTier.Reduced,
            _ => VisualTier.Fallback,
        };
    }

    private void ApplyTier()
    {
        var tier = CurrentTier();
        if (tier != _tier)
        {
            _tier = tier;
            Rebuild();
        }
        else
        {
            UpdateClock();
        }
    }

    private void Rebuild()
    {
        StopClock();
        StopVideos(releaseSources: true);
        RetireCanvases();
        Children.Clear();
        _videos.Clear();
        if (_plan is null)
        {
            return;
        }

        _tier = CurrentTier();
        var variant = _plan.For(_tier);
        var segment = new List<BackdropLayer>();

        void Flush()
        {
            if (segment.Count == 0)
            {
                return;
            }

            var canvas = new BackdropCanvas(this, [.. segment]);
            _canvases.Add(canvas);
            Children.Add(canvas);
            segment.Clear();
        }

        foreach (var layer in variant.Layers)
        {
            if (layer.Kind == "video")
            {
                Flush();
                if (_tier == VisualTier.Full && layer.AssetPath is { } videoPath)
                {
                    var video = new MediaPlayerElement
                    {
                        Stretch = Stretch.UniformToFill,
                        AutoPlay = false,
                        AreTransportControlsEnabled = false,
                        Opacity = layer.Opacity,
                        Source = MediaSource.CreateFromUri(new Uri(videoPath)),
                    };
                    _videos.Add(video);
                    Children.Add(video);
                }

                continue;
            }

            if (layer.Kind == "lottie")
            {
                Flush();
                if (_tier != VisualTier.Fallback && layer.AssetPath is { } lottiePath)
                {
                    var player = new AnimatedVisualPlayer
                    {
                        Source = new LottieVisualSource { UriSource = new Uri(lottiePath) },
                        AutoPlay = _active,
                        Stretch = Stretch.UniformToFill,
                        Opacity = layer.Opacity,
                    };
                    Children.Add(player);
                }

                continue;
            }

            segment.Add(layer);
        }

        Flush();
        StartVideos();
        UpdateClock();
    }

    private void RetireCanvases()
    {
        foreach (var canvas in _canvases)
        {
            canvas.Retire();
        }

        _canvases.Clear();
    }

    private void StartVideos()
    {
        if (!_active || _tier != VisualTier.Full)
        {
            return;
        }

        foreach (var video in _videos)
        {
            if (video.MediaPlayer is { } player)
            {
                player.IsMuted = true;
                player.IsLoopingEnabled = true;
                player.Play();
            }
        }
    }

    private void StopVideos(bool releaseSources = false)
    {
        foreach (var video in _videos)
        {
            var player = video.MediaPlayer;
            player?.Pause();
            if (releaseSources)
            {
                video.Source = null;
                player?.Dispose();
            }
        }
    }

    private void UpdateClock()
    {
        var animated = _plan?.For(_tier).IsAnimated == true;
        var allowed = _active && IsLoaded && animated && _tier != VisualTier.Fallback
            && ResourceGovernor.Shared.Tier != PresentationTier.Suspended;
        if (allowed && !_clockRunning)
        {
            _clockRunning = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!allowed)
        {
            StopClock();
            foreach (var canvas in _canvases)
            {
                canvas.Invalidate();
            }
        }
    }

    private void StopClock()
    {
        if (_clockRunning)
        {
            _clockRunning = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        // Full runs at ~30 fps, Reduced at ~15 fps: ambience is slow; the budget goes to interaction.
        var interval = _tier == VisualTier.Full ? 33 : 66;
        var now = Environment.TickCount64;
        if (now - _lastFrame < interval)
        {
            return;
        }

        _lastFrame = now;
        foreach (var canvas in _canvases)
        {
            canvas.Invalidate();
        }
    }
}

/// <summary>Draws one contiguous run of procedural backdrop layers.</summary>
internal sealed class BackdropCanvas : SKCanvasElement
{
    private static readonly Lazy<SKImage> NoiseTexture = new(() => CreateNoise(96, grain: true));
    private static readonly Lazy<SKImage> FogTexture = new(() => CreateNoise(128, grain: false));

    private readonly BackdropView _owner;
    private readonly BackdropLayer[] _layers;
    private readonly Dictionary<BackdropLayer, SKImage?> _images = [];
    private readonly Dictionary<BackdropLayer, SKImage?> _blurred = [];
    private Particle[] _particles = [];
    private PreparedColors[] _colors = [];
    private int _generation;
    private bool _retired;

    public BackdropCanvas(BackdropView owner, BackdropLayer[] layers)
    {
        _owner = owner;
        _layers = layers;
        IsHitTestVisible = false;
        Prepare();
        foreach (var layer in layers.Where(l => l.Kind == "image" && l.AssetPath is not null))
        {
            TaskObserver.Observe(
                LoadAssetAsync(layer),
                "BackdropCanvas.LoadAssetAsync");
        }
    }

    /// <summary>Resolves token colours once per theme; never per frame.</summary>
    public void Prepare()
    {
        var tokens = ThemeRuntime.Current.Tokens;
        _colors = [.. _layers.Select(layer => new PreparedColors(
            layer.Color is null ? null : SkiaColor.From(tokens.Resolve(layer.Color)),
            layer.Color2 is null ? null : SkiaColor.From(tokens.Resolve(layer.Color2)),
            layer.Stops?.Select(stop => SkiaColor.From(tokens.Resolve(stop.Color))).ToArray(),
            layer.Stops?.Select(stop => (float)stop.Offset).ToArray()))];

        var particleLayer = _layers.FirstOrDefault(l => l.Kind == "particles");
        if (particleLayer is not null && _particles.Length != particleLayer.Count)
        {
            var random = new Random(4242);
            _particles = [.. Enumerable.Range(0, particleLayer.Count).Select(_ => new Particle(
                (float)random.NextDouble(), (float)random.NextDouble(),
                (float)(particleLayer.SizeMin + (random.NextDouble() * (particleLayer.SizeMax - particleLayer.SizeMin))),
                (float)(0.3 + random.NextDouble()), (float)(random.NextDouble() * Math.PI * 2)))];
        }

        Invalidate();
    }

    public void OnAmbientChanged()
    {
        foreach (var layer in _layers.Where(l => l.Source is not null))
        {
            if (_blurred.Remove(layer, out var image))
            {
                image?.Dispose();
            }
        }

        Invalidate();
    }

    private async Task LoadAssetAsync(BackdropLayer layer)
    {
        var generation = _generation;
        var image = await Task.Run(() =>
        {
            using var bitmap = TargetSizeDecoder.Decode(layer.AssetPath!, 1920);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }).ConfigureAwait(false);
        try
        {
            UiDispatch.Run(() =>
            {
                if (_retired || generation != _generation)
                {
                    image?.Dispose();
                    return;
                }

                if (_images.Remove(layer, out var previous))
                {
                    previous?.Dispose();
                }

                _images[layer] = image;
                if (_blurred.Remove(layer, out var blurred))
                {
                    blurred?.Dispose();
                }
                Invalidate();
            });
        }
        catch (InvalidOperationException)
        {
            image?.Dispose();
        }
    }

    public void Retire()
    {
        if (_retired)
        {
            return;
        }

        _retired = true;
        _generation++;
        foreach (var image in _blurred.Values)
        {
            image?.Dispose();
        }

        foreach (var image in _images.Values)
        {
            image?.Dispose();
        }

        _blurred.Clear();
        _images.Clear();
    }

    protected override void RenderOverride(SKCanvas canvas, Windows.Foundation.Size area)
    {
        var w = (float)area.Width;
        var h = (float)area.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var t = _owner.ElapsedSeconds * ThemeRuntime.Current.Motion.AmbientSpeed;
        var parallax = ThemeRuntime.Current.Motion.ParallaxAmplitude;
        var (px, py) = _owner.Pointer;
        var rect = new SKRect(0, 0, w, h);

        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            var colors = i < _colors.Length ? _colors[i] : default;
            using var paint = new SKPaint { IsAntialias = true, BlendMode = Blend(layer.Blend) };
            var wave = Math.Sin(2 * Math.PI * t / Math.Max(2, layer.Period));
            var wave2 = Math.Cos(2 * Math.PI * t / Math.Max(2, layer.Period * 1.3));
            var depthX = (float)((px - 0.5) * layer.Depth * 60 * parallax);
            var depthY = (float)((py - 0.5) * layer.Depth * 40 * parallax);

            switch (layer.Kind)
            {
                case "gradient":
                {
                    if (colors.Stops is not { Length: > 0 } stops)
                    {
                        break;
                    }

                    var angle = (layer.Angle + (wave * layer.Amplitude * 90)) * Math.PI / 180;
                    var dx = (float)Math.Cos(angle) * Math.Max(w, h) / 2;
                    var dy = (float)Math.Sin(angle) * Math.Max(w, h) / 2;
                    paint.Shader = SKShader.CreateLinearGradient(new SKPoint((w / 2) - dx, (h / 2) - dy), new SKPoint((w / 2) + dx, (h / 2) + dy),
                        [.. stops.Select(c => c.WithAlpha((byte)(c.Alpha * layer.Opacity)))], colors.Offsets, SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "radial-glow":
                {
                    var color = colors.Color ?? SKColors.White;
                    var cx = (float)(layer.CenterX + (wave * layer.Amplitude)) * w + depthX;
                    var cy = (float)(layer.CenterY + (wave2 * layer.Amplitude)) * h + depthY;
                    var pulse = layer.Amplitude > 0 ? 1 + (0.25 * wave * layer.Amplitude * 4) : 1;
                    var radius = (float)(layer.Radius * Math.Max(w, h));
                    paint.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                        [color.WithAlpha((byte)Math.Clamp(color.Alpha * layer.Opacity * pulse, 0, 255)), color.WithAlpha(0)], null, SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "image":
                {
                    var image = ResolveImage(layer);
                    if (image is null)
                    {
                        break;
                    }

                    var scale = (float)(layer.Scale * (1 + (layer.Amplitude * 0.5 * (1 + wave))));
                    var baseScale = Math.Max(w / image.Width, h / image.Height) * scale;
                    var iw = image.Width * baseScale;
                    var ih = image.Height * baseScale;
                    var ox = ((w - iw) / 2) + (float)(wave2 * layer.Amplitude * w * 0.5) + depthX;
                    var oy = ((h - ih) / 2) + (float)(wave * layer.Amplitude * h * 0.3) + depthY;
                    paint.Color = SKColors.White.WithAlpha((byte)(255 * layer.Opacity));
                    canvas.DrawImage(image, SKRect.Create(ox, oy, iw, ih), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                    break;
                }

                case "fog":
                {
                    var texture = FogTexture.Value;
                    var color = colors.Color ?? SKColors.White;
                    var shift = (float)(t * layer.Speed * 12);
                    var matrix = SKMatrix.CreateScale((float)(layer.Scale * 6), (float)(layer.Scale * 6)).PostConcat(SKMatrix.CreateTranslation(shift + depthX, (shift * 0.3f) + depthY));
                    paint.Shader = SKShader.CreateImage(texture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, matrix);
                    paint.ColorFilter = SKColorFilter.CreateBlendMode(color.WithAlpha((byte)(255 * layer.Opacity)), SKBlendMode.SrcIn);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "particles":
                {
                    var color = colors.Color ?? SKColors.White;
                    foreach (var particle in _particles)
                    {
                        var y = (particle.Y - (float)(t * layer.Speed * 0.02 * particle.Speed)) % 1f;
                        if (y < 0)
                        {
                            y += 1;
                        }

                        var x = particle.X + (float)(Math.Sin((t * 0.3 * particle.Speed) + particle.Phase) * 0.015);
                        var twinkle = 0.55 + (0.45 * Math.Sin((t * 1.4 * particle.Speed) + particle.Phase));
                        paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * layer.Opacity * twinkle, 0, 255));
                        canvas.DrawCircle((x * w) + (depthX * 1.5f), (y * h) + (depthY * 1.5f), particle.Size, paint);
                    }

                    break;
                }

                case "noise":
                {
                    paint.Shader = SKShader.CreateImage(NoiseTexture.Value, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                    paint.Color = SKColors.White.WithAlpha((byte)(255 * layer.Opacity));
                    paint.BlendMode = SKBlendMode.Overlay;
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "vignette":
                {
                    paint.Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), Math.Max(w, h) * 0.75f,
                        [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha((byte)(255 * layer.Opacity))], [0.45f, 1f], SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "light-sweep":
                {
                    var color = colors.Color ?? SKColors.White;
                    var progress = (float)((t / Math.Max(2, layer.Period)) % 1.0);
                    var band = (float)layer.Width;
                    var position = (progress * (1 + (band * 2))) - band;
                    var angle = layer.Angle * Math.PI / 180;
                    var span = Math.Max(w, h);
                    var ax = (float)Math.Cos(angle) * span;
                    var ay = (float)Math.Sin(angle) * span;
                    paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(ax, ay),
                        [color.WithAlpha(0), color.WithAlpha((byte)(color.Alpha * layer.Opacity)), color.WithAlpha(0)],
                        [Math.Clamp(position - band, 0, 1), Math.Clamp(position, 0, 1), Math.Clamp(position + band, 0, 1)], SKShaderTileMode.Clamp);
                    paint.BlendMode = SKBlendMode.Plus;
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "scrim":
                {
                    var color = colors.Color ?? SKColors.Black;
                    var angle = layer.Angle * Math.PI / 180;
                    var dx = (float)Math.Cos(angle) * h;
                    var dy = (float)Math.Sin(angle) * h;
                    paint.Shader = SKShader.CreateLinearGradient(new SKPoint(w / 2, h), new SKPoint((w / 2) + (dx * 0), h - Math.Abs(dy)),
                        [color.WithAlpha((byte)(255 * layer.Opacity)), color.WithAlpha(0)], null, SKShaderTileMode.Clamp);
                    canvas.DrawRect(rect, paint);
                    break;
                }

                case "media-tint":
                {
                    if (_owner.MediaTint is not { } tint)
                    {
                        break;
                    }

                    paint.Color = SkiaColor.From(tint, layer.Opacity);
                    paint.BlendMode = SKBlendMode.SoftLight;
                    canvas.DrawRect(rect, paint);
                    break;
                }
            }
        }
    }

    private SKImage? ResolveImage(BackdropLayer layer)
    {
        var source = layer.AssetPath is not null
            ? _images.GetValueOrDefault(layer)
            : _owner.AmbientImage;
        if (source is null)
        {
            return null;
        }

        if (layer.Blur <= 0)
        {
            return source;
        }

        // Blur once and reuse: the animated frame only transforms a pre-blurred image (R2 §34.8).
        if (!_blurred.TryGetValue(layer, out var blurred) || blurred is null)
        {
            var scale = 0.5f;
            var info = new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using var surface = SKSurface.Create(info);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur((float)layer.Blur * scale, (float)layer.Blur * scale, SKShaderTileMode.Clamp) };
            surface.Canvas.DrawImage(source, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
            blurred = surface.Snapshot();
            _blurred[layer] = blurred;
        }

        return blurred;
    }

    private static SKBlendMode Blend(string blend) => blend switch
    {
        "screen" => SKBlendMode.Screen,
        "multiply" => SKBlendMode.Multiply,
        "overlay" => SKBlendMode.Overlay,
        "soft-light" => SKBlendMode.SoftLight,
        "plus" => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>Pre-rendered tileable texture: grain (per-pixel) or fog (smooth value noise). Built once.</summary>
    private static SKImage CreateNoise(int size, bool grain)
    {
        var random = new Random(grain ? 7 : 11);
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        var grid = 8;
        var lattice = new double[grid + 1, grid + 1];
        for (var y = 0; y <= grid; y++)
        {
            for (var x = 0; x <= grid; x++)
            {
                lattice[x, y] = random.NextDouble();
            }
        }

        for (var y = 0; y < grid; y++)
        {
            lattice[grid, y] = lattice[0, y];
        }

        for (var x = 0; x <= grid; x++)
        {
            lattice[x, grid] = lattice[x, 0];
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                byte value;
                if (grain)
                {
                    value = (byte)random.Next(0, 256);
                    bitmap.SetPixel(x, y, new SKColor(value, value, value, 40));
                }
                else
                {
                    var gx = (double)x / size * grid;
                    var gy = (double)y / size * grid;
                    var x0 = (int)gx;
                    var y0 = (int)gy;
                    var fx = Smooth(gx - x0);
                    var fy = Smooth(gy - y0);
                    var v = Lerp(Lerp(lattice[x0, y0], lattice[x0 + 1, y0], fx), Lerp(lattice[x0, y0 + 1], lattice[x0 + 1, y0 + 1], fx), fy);
                    value = (byte)(Math.Pow(v, 1.6) * 255);
                    bitmap.SetPixel(x, y, new SKColor(255, 255, 255, value));
                }
            }
        }

        return SKImage.FromBitmap(bitmap);

        static double Smooth(double t) => t * t * (3 - (2 * t));

        static double Lerp(double a, double b, double t) => a + ((b - a) * t);
    }

    private readonly record struct Particle(float X, float Y, float Size, float Speed, float Phase);

    private readonly record struct PreparedColors(SKColor? Color, SKColor? Color2, SKColor[]? Stops, float[]? Offsets);
}
