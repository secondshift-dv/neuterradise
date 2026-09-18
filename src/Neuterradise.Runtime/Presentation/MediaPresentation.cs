using System.Globalization;
using System.Text.Json;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Canonical normalized presentation of one chosen media asset (document 01 §12.1). Coordinates are
/// normalized so the composition survives any slot size or aspect ratio: the layout owns the slot, the
/// media presentation owns focal point, zoom and pan.
/// </summary>
public sealed record MediaTransformState(
    string Fit,
    double FocalX,
    double FocalY,
    double Zoom,
    double OffsetX,
    double OffsetY,
    double Rotation)
{
    public static MediaTransformState Default { get; } = new("fill", 0.5, 0.5, 1, 0, 0, 0);

    public static MediaTransformState FromCover(ProfileAppearanceOverrides overrides) => new(
        overrides.CoverFit ?? "fill",
        overrides.CropX,
        overrides.CropY,
        overrides.Zoom,
        overrides.CoverOffsetX,
        overrides.CoverOffsetY,
        overrides.CoverRotation);

    public static MediaTransformState FromBanner(ProfileAppearanceOverrides overrides) => new(
        overrides.BannerFit ?? "fill",
        overrides.BannerFocusX,
        overrides.BannerFocusY,
        overrides.BannerZoom,
        overrides.BannerOffsetX,
        overrides.BannerOffsetY,
        overrides.BannerRotation);

    public ProfileAppearanceOverrides WriteCover(ProfileAppearanceOverrides overrides) => overrides with
    {
        CoverFit = Fit,
        CropX = Math.Clamp(FocalX, 0, 1),
        CropY = Math.Clamp(FocalY, 0, 1),
        Zoom = Math.Clamp(Zoom, ProfileAppearanceRules.MinimumCoverZoom, ProfileAppearanceRules.MaximumCoverZoom),
        CoverOffsetX = Math.Clamp(OffsetX, -1, 1),
        CoverOffsetY = Math.Clamp(OffsetY, -1, 1),
        CoverRotation = Math.Clamp(Rotation, -180, 180),
    };

    public ProfileAppearanceOverrides WriteBanner(ProfileAppearanceOverrides overrides) => overrides with
    {
        BannerFit = Fit,
        BannerFocusX = Math.Clamp(FocalX, 0, 1),
        BannerFocusY = Math.Clamp(FocalY, 0, 1),
        BannerZoom = Math.Clamp(Zoom, BannerPresentationPolicy.MinimumZoom, BannerPresentationPolicy.MaximumZoom),
        BannerOffsetX = Math.Clamp(OffsetX, -1, 1),
        BannerOffsetY = Math.Clamp(OffsetY, -1, 1),
        BannerRotation = Math.Clamp(Rotation, -180, 180),
    };

    /// <summary>
    /// Applies a surface-level delta (a card or Spotlight adjustment stored as a binding) over the
    /// inherited canonical state. Only keys present in the delta change; everything else inherits.
    /// </summary>
    public MediaTransformState ApplyDelta(string? deltaJson)
    {
        if (string.IsNullOrWhiteSpace(deltaJson))
        {
            return this;
        }

        try
        {
            using var document = JsonDocument.Parse(deltaJson);
            var root = document.RootElement;
            return new MediaTransformState(
                root.TryGetProperty("fit", out var fit) && fit.ValueKind == JsonValueKind.String && fit.GetString() is "fill" or "fit" ? fit.GetString()! : Fit,
                Read(root, "focalX", FocalX, 0, 1),
                Read(root, "focalY", FocalY, 0, 1),
                Read(root, "zoom", Zoom, 1, 4),
                Read(root, "offsetX", OffsetX, -1, 1),
                Read(root, "offsetY", OffsetY, -1, 1),
                Read(root, "rotation", Rotation, -180, 180));
        }
        catch (JsonException)
        {
            return this;
        }
    }

    /// <summary>The delta JSON that turns <paramref name="inherited"/> into this state (only changed keys).</summary>
    public string? DeltaFrom(MediaTransformState inherited)
    {
        var parts = new List<string>();
        if (Fit != inherited.Fit) parts.Add($"\"fit\":\"{Fit}\"");
        Add(parts, "focalX", FocalX, inherited.FocalX);
        Add(parts, "focalY", FocalY, inherited.FocalY);
        Add(parts, "zoom", Zoom, inherited.Zoom);
        Add(parts, "offsetX", OffsetX, inherited.OffsetX);
        Add(parts, "offsetY", OffsetY, inherited.OffsetY);
        Add(parts, "rotation", Rotation, inherited.Rotation);
        return parts.Count == 0 ? null : "{" + string.Join(",", parts) + "}";
    }

    /// <summary>
    /// Where the source lands inside a slot: uniform scale and translation in slot pixels. The focal
    /// point stays under the same relative slot position for every aspect ratio, and fill never leaves
    /// an empty edge.
    /// </summary>
    public MediaPlacement Place(double sourceWidth, double sourceHeight, double slotWidth, double slotHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || slotWidth <= 0 || slotHeight <= 0)
        {
            return new MediaPlacement(1, 0, 0, sourceWidth, sourceHeight, Rotation);
        }

        var baseScale = Fit == "fit"
            ? Math.Min(slotWidth / sourceWidth, slotHeight / sourceHeight)
            : Math.Max(slotWidth / sourceWidth, slotHeight / sourceHeight);
        var scale = baseScale * Math.Max(1, Zoom);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;

        double Axis(double displayed, double slot, double focal, double offset)
        {
            var overflow = displayed - slot;
            if (overflow <= 0)
            {
                return (-overflow / 2) + (offset * slot * 0.5);
            }

            var translate = -(overflow * Math.Clamp(focal, 0, 1)) + (offset * slot * 0.5);
            return Fit == "fit" ? translate : Math.Clamp(translate, -overflow, 0);
        }

        return new MediaPlacement(scale, Axis(width, slotWidth, FocalX, OffsetX), Axis(height, slotHeight, FocalY, OffsetY), width, height, Rotation);
    }

    private static double Read(JsonElement root, string name, double fallback, double min, double max) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Clamp(number, min, max)
            : fallback;

    private static void Add(List<string> parts, string name, double value, double inherited)
    {
        if (Math.Abs(value - inherited) > 0.0005)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"\"{name}\":{Math.Round(value, 4)}"));
        }
    }
}

/// <summary>Resolved placement of a source inside a slot, in slot pixels.</summary>
public readonly record struct MediaPlacement(double Scale, double TranslateX, double TranslateY, double Width, double Height, double Rotation);

/// <summary>Banner video presentation window and policy (document 01 §12.5). It never rewrites the source.</summary>
public sealed record PlaybackPresentationState(
    long StartMs,
    long DurationMs,
    string LoopMode,
    bool Mute,
    double Rate,
    string ReducedMotionPolicy)
{
    public static PlaybackPresentationState FromBanner(ProfileAppearanceOverrides overrides) => new(
        (long)Math.Round(overrides.BannerStartPointSeconds * 1000),
        (long)Math.Round(overrides.BannerDurationSeconds * 1000),
        overrides.BannerLoopMode ?? (overrides.BannerLoop ? "loop" : "once"),
        overrides.BannerMute,
        overrides.BannerPlaybackRate,
        overrides.BannerReducedMotion ?? "poster");

    public ProfileAppearanceOverrides Write(ProfileAppearanceOverrides overrides) => overrides with
    {
        BannerStartPointSeconds = Math.Max(0, StartMs / 1000.0),
        BannerDurationSeconds = Math.Clamp(DurationMs / 1000.0, BannerPresentationPolicy.MinimumDuration.TotalSeconds, BannerPresentationPolicy.MaximumDuration.TotalSeconds),
        BannerLoop = LoopMode == "loop",
        BannerLoopMode = LoopMode,
        BannerMute = Mute,
        BannerPlaybackRate = Math.Clamp(Rate, ProfileAppearanceOverrides.MinimumPlaybackRate, ProfileAppearanceOverrides.MaximumPlaybackRate),
        BannerReducedMotion = ReducedMotionPolicy,
    };

    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>Everything a renderer needs to present a Profile's Cover and Banner on one surface.</summary>
public sealed record ProfileMediaPresentation(
    MediaTransformState Cover,
    MediaTransformState Banner,
    PlaybackPresentationState Playback)
{
    public static ProfileMediaPresentation From(ProfileAppearanceOverrides overrides) => new(
        MediaTransformState.FromCover(overrides),
        MediaTransformState.FromBanner(overrides),
        PlaybackPresentationState.FromBanner(overrides));
}
