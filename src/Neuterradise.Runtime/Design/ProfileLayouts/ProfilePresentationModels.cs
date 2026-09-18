using Neuterradise.App.Profiles;

namespace Neuterradise.App.Design.ProfileLayouts;

/// <summary>The semantic identity payload Profile-derived surfaces present (card, hero, preview).</summary>
public sealed record ProfilePresentationModel(
    Guid ProfileId,
    string DisplayName,
    string? CategoryName = null,
    IReadOnlyList<string>? Tags = null,
    int? Rating = null,
    bool IsFavorite = false,
    string? Overview = null,
    string? Notes = null,
    long MediaCount = 0,
    bool HasRelatedIndicator = false,
    string? CardVariantId = null)
{
    public IReadOnlyList<string> TagList => Tags ?? [];

    public string? Tier => RatingTierPolicy.Resolve(Rating);
}

public enum BannerMotionMode
{
    Off,
    HoverOnly
}

public enum BannerPreviewStopReason
{
    None,
    PointerLeft,
    FocusLost,
    Scrolled,
    Hidden,
    Unloaded,
    MotionDisabled,
    ClipEnded,
    PlaybackFailed,
    MediaEnded,
    ReplacedByAnotherPreview
}

public static class BannerPresentationDiagnosticCodes
{
    public const string StartPointInvalid = "BANNER_START_POINT_INVALID";
    public const string DurationOutOfRange = "BANNER_DURATION_OUT_OF_RANGE";
    public const string FocusOutOfRange = "BANNER_FOCUS_OUT_OF_RANGE";
    public const string ZoomOutOfRange = "BANNER_ZOOM_OUT_OF_RANGE";
}

public sealed record BannerPresentationDiagnostic(string Code, string Source, string Detail);

public sealed record BannerPresentationRequest(
    double? StartPointSeconds = null,
    double? DurationSeconds = null,
    double? FocusX = null,
    double? FocusY = null,
    double? Zoom = null,
    bool Loop = true);

public sealed record BannerPresentation(
    TimeSpan StartPoint,
    TimeSpan Duration,
    double FocusX,
    double FocusY,
    double Zoom,
    bool Loop)
{
    public bool AutoplayMuted => true;
}

public sealed record BannerPresentationResolution(
    BannerPresentation Presentation,
    IReadOnlyList<BannerPresentationDiagnostic> Diagnostics);

public static class BannerPresentationPolicy
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);

    public const double MinimumFocus = 0;

    public const double MaximumFocus = 1;

    public const double DefaultFocus = 0.5;

    public const double MinimumZoom = 1;

    public const double MaximumZoom = 2;

    public static BannerPresentation Default { get; } =
        new(TimeSpan.Zero, DefaultDuration, DefaultFocus, DefaultFocus, MinimumZoom, Loop: true);

    public static BannerPresentationResolution Resolve(BannerPresentationRequest? request)
    {
        var diagnostics = new List<BannerPresentationDiagnostic>();

        if (request is null)
        {
            return new BannerPresentationResolution(Default, diagnostics);
        }

        var startPoint = TimeSpan.Zero;

        if (request.StartPointSeconds is { } start)
        {
            if (!double.IsFinite(start) || start < 0)
            {
                diagnostics.Add(new BannerPresentationDiagnostic(
                    BannerPresentationDiagnosticCodes.StartPointInvalid,
                    nameof(request.StartPointSeconds),
                    "A Banner start point must be a finite, non-negative number of seconds; 0 is used."));
            }
            else
            {
                startPoint = TimeSpan.FromSeconds(start);
            }
        }

        var duration = DefaultDuration;

        if (request.DurationSeconds is { } seconds)
        {
            var candidate = double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

            if (candidate < MinimumDuration || candidate > MaximumDuration)
            {
                diagnostics.Add(new BannerPresentationDiagnostic(
                    BannerPresentationDiagnosticCodes.DurationOutOfRange,
                    nameof(request.DurationSeconds),
                    $"A Banner clip runs between {MinimumDuration.TotalSeconds} and "
                    + $"{MaximumDuration.TotalSeconds} seconds; the {DefaultDuration.TotalSeconds} second "
                    + "default is used."));
            }
            else
            {
                duration = candidate;
            }
        }

        var focusX = ResolveFocus(request.FocusX, nameof(request.FocusX), diagnostics);
        var focusY = ResolveFocus(request.FocusY, nameof(request.FocusY), diagnostics);
        var zoom = ResolveZoom(request.Zoom, diagnostics);

        return new BannerPresentationResolution(
            new BannerPresentation(startPoint, duration, focusX, focusY, zoom, request.Loop),
            diagnostics);
    }

    private static double ResolveFocus(
        double? value,
        string name,
        List<BannerPresentationDiagnostic> diagnostics)
    {
        if (value is not { } focus)
        {
            return DefaultFocus;
        }

        if (!double.IsFinite(focus) || focus < MinimumFocus || focus > MaximumFocus)
        {
            diagnostics.Add(new BannerPresentationDiagnostic(
                BannerPresentationDiagnosticCodes.FocusOutOfRange,
                name,
                $"A Banner focus point must be between {MinimumFocus} and {MaximumFocus}; the centre is used."));

            return DefaultFocus;
        }

        return focus;
    }

    private static double ResolveZoom(double? value, List<BannerPresentationDiagnostic> diagnostics)
    {
        if (value is not { } zoom)
        {
            return MinimumZoom;
        }

        if (!double.IsFinite(zoom) || zoom < MinimumZoom || zoom > MaximumZoom)
        {
            diagnostics.Add(new BannerPresentationDiagnostic(
                BannerPresentationDiagnosticCodes.ZoomOutOfRange,
                nameof(BannerPresentation.Zoom),
                $"Banner zoom must be between {MinimumZoom} and {MaximumZoom}; no zoom is applied."));

            return MinimumZoom;
        }

        return zoom;
    }
}
