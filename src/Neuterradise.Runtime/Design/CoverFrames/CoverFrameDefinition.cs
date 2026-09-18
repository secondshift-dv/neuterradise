using System.Globalization;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Design.CoverFrames;

public enum CoverShape
{
    Circle,
    RoundedSquare,
    Square,
    Squircle,
    Portrait,
    Hexagon,
    Diamond
}

public enum CoverFrameDetailLevel
{
    Full,
    Compact,
    Hidden
}

public enum CoverFrameAnimation
{
    None,
    Glow,
    Shimmer
}

public enum CoverFrameFamily
{
    None,
    Minimal,
    SoftRing,
    DoubleRing,
    Metal,
    Silver,
    Gold,
    Brass,
    Obsidian,
    Arcane,
    Celestial,
    Royal,
    Floral,
    Cyber,
    NeonCircuit,
    Holographic,
    Hud,
    Champion,
    Mythic,
    Legendary
}

/// <summary>
/// The user-facing grouping of frames in Profile Customization. Every family belongs to exactly one
/// category; the category decides the section a frame is listed under and its sort band.
/// </summary>
public enum CoverFrameCategory
{
    Plain,
    Minimal,
    Ornate,
    ModernRing
}

/// <summary>
/// One Cover Frame. <see cref="Id"/> is the stable persisted key; <see cref="Family"/> selects the
/// original Neu Terradise stroke/accent resources in CoverFrames.xaml that CoverFrameHost renders.
/// </summary>
public sealed record CoverFrameDefinition(
    string Id,
    string DisplayName,
    CoverFrameFamily Family,
    IReadOnlyList<CoverShape> SupportedShapes,
    double DefaultScale,
    bool SupportsTint,
    bool SupportsAnimation,
    string? ReducedMotionFallback,
    CoverFrameCategory Category = CoverFrameCategory.Plain,
    int SortOrder = 0)
{
    public bool Supports(CoverShape shape) => SupportedShapes.Contains(shape);
}

public static class CoverAppearanceDiagnosticCodes
{
    public const string ShapeUnknown = "COVER_SHAPE_UNKNOWN";
    public const string FrameUnknown = "COVER_FRAME_UNKNOWN";
    public const string FrameShapeUnsupported = "COVER_FRAME_SHAPE_UNSUPPORTED";
    public const string ScaleOutOfRange = "COVER_FRAME_SCALE_OUT_OF_RANGE";
    public const string IntensityOutOfRange = "COVER_FRAME_INTENSITY_OUT_OF_RANGE";
    public const string TintInvalid = "COVER_FRAME_TINT_INVALID";
    public const string TintUnsupported = "COVER_FRAME_TINT_UNSUPPORTED";
    public const string AnimationUnknown = "COVER_FRAME_ANIMATION_UNKNOWN";
    public const string AnimationUnsupported = "COVER_FRAME_ANIMATION_UNSUPPORTED";
    public const string DetailLevelUnknown = "COVER_FRAME_DETAIL_LEVEL_UNKNOWN";
    public const string ReducedMotionFallbackApplied = "COVER_FRAME_REDUCED_MOTION_FALLBACK";
}

public sealed record CoverAppearanceDiagnostic(string Code, string Source, string Detail);

public sealed record CoverAppearanceRequest(
    string? Shape = null,
    string? FrameId = null,
    double? FrameScale = null,
    string? FrameTint = null,
    double? FrameIntensity = null,
    string? FrameAnimation = null,
    bool CoverShadow = true,
    string? DetailLevel = null);

public sealed record CoverAppearance(
    CoverShape Shape,
    CoverFrameDefinition Frame,
    double FrameScale,
    string? FrameTint,
    double FrameIntensity,
    CoverFrameAnimation FrameAnimation,
    bool CoverShadow,
    CoverFrameDetailLevel DetailLevel,
    bool ReduceMotion);

public sealed record CoverAppearanceResolution(
    CoverAppearance Appearance,
    IReadOnlyList<CoverAppearanceDiagnostic> Diagnostics);

public static class CoverFrameCatalog
{

    public const string NoneFrameId = "none";

    public const CoverShape FallbackShape = CoverShape.RoundedSquare;

    public const double MinimumFrameScale = 0.75;

    public const double MaximumFrameScale = 1.5;

    public const double MinimumFrameIntensity = 0;

    public const double MaximumFrameIntensity = 1;

    private static readonly CoverShape[] AllShapes =
    [
        CoverShape.Circle, CoverShape.RoundedSquare, CoverShape.Square, CoverShape.Squircle,
        CoverShape.Portrait, CoverShape.Hexagon, CoverShape.Diamond,
    ];

    // Ornate frames suit soft outlines; they are not drawn on Hexagon/Diamond. Square and Circle are
    // the two shapes Profile Customization offers, so every frame supports both.
    private static readonly CoverShape[] RoundedShapes =
    [
        CoverShape.Circle, CoverShape.Square, CoverShape.RoundedSquare, CoverShape.Squircle, CoverShape.Portrait,
    ];

    private static readonly CoverShape[] EmblemShapes =
    [
        CoverShape.Circle, CoverShape.Square, CoverShape.RoundedSquare, CoverShape.Squircle,
        CoverShape.Hexagon, CoverShape.Diamond,
    ];

    /// <summary>The shapes Profile Customization offers, in display order.</summary>
    public static IReadOnlyList<CoverShape> SelectableShapes { get; } = [CoverShape.Square, CoverShape.Circle];

    private static readonly CoverFrameDefinition[] BuiltInFrames =
    [
        new(NoneFrameId, "None", CoverFrameFamily.None, AllShapes, 1.0, false, false, null, CoverFrameCategory.Plain, 0),

        new("minimal", "Clean", CoverFrameFamily.Minimal, AllShapes, 1.0, true, false, null, CoverFrameCategory.Minimal, 100),
        new("silver", "Silver Line", CoverFrameFamily.Silver, AllShapes, 1.0, true, false, null, CoverFrameCategory.Minimal, 110),
        new("metal", "Brushed Metal", CoverFrameFamily.Metal, AllShapes, 1.0, true, false, null, CoverFrameCategory.Minimal, 120),

        new("gold", "Gilded", CoverFrameFamily.Gold, AllShapes, 1.0, true, false, null, CoverFrameCategory.Ornate, 200),
        new("brass", "Brass", CoverFrameFamily.Brass, AllShapes, 1.0, true, false, null, CoverFrameCategory.Ornate, 210),
        new("obsidian", "Obsidian", CoverFrameFamily.Obsidian, AllShapes, 1.0, true, false, null, CoverFrameCategory.Ornate, 220),
        new("royal", "Royal", CoverFrameFamily.Royal, RoundedShapes, 1.05, true, false, null, CoverFrameCategory.Ornate, 230),
        new("floral", "Floral", CoverFrameFamily.Floral, RoundedShapes, 1.05, true, false, null, CoverFrameCategory.Ornate, 240),
        new("arcane", "Arcane", CoverFrameFamily.Arcane, RoundedShapes, 1.05, true, true, "obsidian", CoverFrameCategory.Ornate, 250),
        new("celestial", "Celestial", CoverFrameFamily.Celestial, RoundedShapes, 1.05, true, true, "silver", CoverFrameCategory.Ornate, 260),
        new("champion", "Champion", CoverFrameFamily.Champion, EmblemShapes, 1.1, true, false, null, CoverFrameCategory.Ornate, 270),
        new("mythic", "Mythic", CoverFrameFamily.Mythic, EmblemShapes, 1.1, true, true, "champion", CoverFrameCategory.Ornate, 280),
        new("legendary", "Legendary", CoverFrameFamily.Legendary, EmblemShapes, 1.1, true, true, "gold", CoverFrameCategory.Ornate, 290),

        new("soft-ring", "Soft Ring", CoverFrameFamily.SoftRing, AllShapes, 1.0, true, false, null, CoverFrameCategory.ModernRing, 300),
        new("double-ring", "Double Ring", CoverFrameFamily.DoubleRing, AllShapes, 1.0, true, false, null, CoverFrameCategory.ModernRing, 310),
        new("cyber", "Cyber", CoverFrameFamily.Cyber, AllShapes, 1.0, true, false, null, CoverFrameCategory.ModernRing, 320),
        new("neon-circuit", "Neon Circuit", CoverFrameFamily.NeonCircuit, AllShapes, 1.0, true, true, "cyber", CoverFrameCategory.ModernRing, 330),
        new("holographic", "Holographic", CoverFrameFamily.Holographic, AllShapes, 1.0, false, true, "metal", CoverFrameCategory.ModernRing, 340),
        new("hud", "HUD Badge", CoverFrameFamily.Hud, AllShapes, 1.0, true, false, null, CoverFrameCategory.ModernRing, 350),
    ];

    /// <summary>All frames, ordered by <see cref="CoverFrameDefinition.SortOrder"/>.</summary>
    public static IReadOnlyList<CoverFrameDefinition> Frames { get; } =
        [.. BuiltInFrames.OrderBy(static frame => frame.SortOrder)];

    public static CoverFrameDefinition None { get; } = BuiltInFrames[0];

    public static string CategoryDisplayName(CoverFrameCategory category) => category switch
    {
        CoverFrameCategory.Plain => "Plain",
        CoverFrameCategory.Minimal => "Minimal & Clean",
        CoverFrameCategory.Ornate => "Fantasy & Prestige",
        CoverFrameCategory.ModernRing => "Modern Ring & Badge",
        _ => category.ToString(),
    };

    /// <summary>
    /// The shape to use when <paramref name="shape"/> cannot carry <paramref name="frame"/>: the
    /// requested shape if supported, otherwise Circle for round shapes and Square for angular ones,
    /// otherwise the frame's first supported shape. Never produces an unsupported combination.
    /// </summary>
    public static CoverShape ClosestSupportedShape(CoverFrameDefinition frame, CoverShape shape)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Supports(shape))
        {
            return shape;
        }

        var preferred = shape is CoverShape.Circle or CoverShape.Squircle or CoverShape.Portrait
            ? CoverShape.Circle
            : CoverShape.Square;
        if (frame.Supports(preferred))
        {
            return preferred;
        }

        return frame.SupportedShapes.Count > 0 ? frame.SupportedShapes[0] : FallbackShape;
    }

    public static bool TryGetFrame(string? id, out CoverFrameDefinition frame)
    {
        if (id is not null)
        {
            foreach (var candidate in BuiltInFrames)
            {
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    frame = candidate;
                    return true;
                }
            }
        }

        frame = None;
        return false;
    }

    public static CoverAppearanceResolution Resolve(CoverAppearanceRequest? request, bool reduceMotion)
    {
        var diagnostics = new List<CoverAppearanceDiagnostic>();
        request ??= new CoverAppearanceRequest();

        var shape = ResolveShape(request.Shape, diagnostics);
        var frame = ResolveFrame(request.FrameId, diagnostics);

        if (frame.Family != CoverFrameFamily.None && !frame.SupportedShapes.Contains(shape))
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.FrameShapeUnsupported,
                frame.Id,
                $"'{frame.Id}' is not drawn for the {shape} shape; the Cover renders unframed."));

            frame = None;
        }

        if (reduceMotion && frame.SupportsAnimation && frame.ReducedMotionFallback is not null)
        {

            var fallbackId = frame.ReducedMotionFallback;

            if (TryGetFrame(fallbackId, out var fallback))
            {
                diagnostics.Add(new CoverAppearanceDiagnostic(
                    CoverAppearanceDiagnosticCodes.ReducedMotionFallbackApplied,
                    frame.Id,
                    $"Reduce Motion is on, so '{fallbackId}' is drawn instead."));

                frame = fallback.SupportedShapes.Contains(shape) ? fallback : None;
            }
            else
            {
                diagnostics.Add(new CoverAppearanceDiagnostic(
                    CoverAppearanceDiagnosticCodes.FrameUnknown,
                    fallbackId,
                    $"'{frame.Id}' names a Reduce Motion fallback that is not in the catalog."));

                frame = None;
            }
        }

        var scale = ResolveScale(request.FrameScale, frame, diagnostics);
        var intensity = ResolveIntensity(request.FrameIntensity, diagnostics);
        var tint = ResolveTint(request.FrameTint, frame, diagnostics);
        var animation = ResolveAnimation(request.FrameAnimation, frame, reduceMotion, diagnostics);
        var detailLevel = ResolveDetailLevel(request.DetailLevel, diagnostics);

        var appearance = new CoverAppearance(
            shape,
            frame,
            scale,
            tint,
            intensity,
            animation,
            request.CoverShadow,
            detailLevel,
            reduceMotion);

        return new CoverAppearanceResolution(appearance, diagnostics);
    }

    private static CoverShape ResolveShape(string? value, List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return FallbackShape;
        }

        if (Enum.TryParse<CoverShape>(value, ignoreCase: false, out var shape) && Enum.IsDefined(shape))
        {
            return shape;
        }

        diagnostics.Add(new CoverAppearanceDiagnostic(
            CoverAppearanceDiagnosticCodes.ShapeUnknown,
            value,
            $"'{value}' is not a Section 36 Cover shape; {FallbackShape} is used."));

        return FallbackShape;
    }

    private static CoverFrameDefinition ResolveFrame(string? value, List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return None;
        }

        if (TryGetFrame(value, out var frame))
        {
            return frame;
        }

        diagnostics.Add(new CoverAppearanceDiagnostic(
            CoverAppearanceDiagnosticCodes.FrameUnknown,
            value,
            $"'{value}' is not a shipped Cover Frame; the Cover renders unframed."));

        return None;
    }

    private static double ResolveScale(
        double? value,
        CoverFrameDefinition frame,
        List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is not { } scale)
        {
            return frame.DefaultScale;
        }

        if (!double.IsFinite(scale) || scale < MinimumFrameScale || scale > MaximumFrameScale)
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.ScaleOutOfRange,
                scale.ToString("R", CultureInfo.InvariantCulture),
                $"Frame scale must be between {MinimumFrameScale} and {MaximumFrameScale}; "
                + $"the frame default {frame.DefaultScale} is used."));

            return frame.DefaultScale;
        }

        return scale;
    }

    private static double ResolveIntensity(double? value, List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is not { } intensity)
        {
            return MaximumFrameIntensity;
        }

        if (!double.IsFinite(intensity) || intensity < MinimumFrameIntensity || intensity > MaximumFrameIntensity)
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.IntensityOutOfRange,
                intensity.ToString("R", CultureInfo.InvariantCulture),
                $"Frame intensity must be between {MinimumFrameIntensity} and {MaximumFrameIntensity}; "
                + "full intensity is used."));

            return MaximumFrameIntensity;
        }

        return intensity;
    }

    private static string? ResolveTint(
        string? value,
        CoverFrameDefinition frame,
        List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return null;
        }

        if (!ThemeColorText.IsValid(value))
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.TintInvalid,
                value,
                "A frame tint must be an #AARRGGBB value; the frame keeps its own colour."));

            return null;
        }

        if (!frame.SupportsTint)
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.TintUnsupported,
                frame.Id,
                $"'{frame.Id}' is not tintable; the frame keeps its own colour."));

            return null;
        }

        return value;
    }

    private static CoverFrameAnimation ResolveAnimation(
        string? value,
        CoverFrameDefinition frame,
        bool reduceMotion,
        List<CoverAppearanceDiagnostic> diagnostics)
    {
        var requested = CoverFrameAnimation.None;

        if (value is not null)
        {
            if (Enum.TryParse<CoverFrameAnimation>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            {
                requested = parsed;
            }
            else
            {
                diagnostics.Add(new CoverAppearanceDiagnostic(
                    CoverAppearanceDiagnosticCodes.AnimationUnknown,
                    value,
                    $"'{value}' is not a Section 38 frame animation; the frame is drawn static."));
            }
        }

        if (requested == CoverFrameAnimation.None)
        {
            return CoverFrameAnimation.None;
        }

        if (!frame.SupportsAnimation)
        {
            diagnostics.Add(new CoverAppearanceDiagnostic(
                CoverAppearanceDiagnosticCodes.AnimationUnsupported,
                frame.Id,
                $"'{frame.Id}' has no animated decoration; the frame is drawn static."));

            return CoverFrameAnimation.None;
        }

        return reduceMotion ? CoverFrameAnimation.None : requested;
    }

    private static CoverFrameDetailLevel ResolveDetailLevel(
        string? value,
        List<CoverAppearanceDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return CoverFrameDetailLevel.Full;
        }

        if (Enum.TryParse<CoverFrameDetailLevel>(value, ignoreCase: false, out var level) && Enum.IsDefined(level))
        {
            return level;
        }

        diagnostics.Add(new CoverAppearanceDiagnostic(
            CoverAppearanceDiagnosticCodes.DetailLevelUnknown,
            value,
            $"'{value}' is not a Section 37 detail level; {CoverFrameDetailLevel.Full} is used."));

        return CoverFrameDetailLevel.Full;
    }
}
