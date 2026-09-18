using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Presentation;

/// <summary>Full / Reduced / Fallback visual tiers (R2 §44). Every signature effect has all three.</summary>
public enum VisualTier
{
    Full,
    Reduced,
    Fallback,
}

/// <summary>
/// A compiled, immutable, renderer-ready definition. Plans hold resolved values and asset handles only:
/// no database connection, no SQL, no arbitrary Vault path, no UI object (document 01 §26).
/// </summary>
public sealed record CompiledDefinition(
    DefinitionRef Ref,
    string Kind,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    PackOrigin Origin,
    string? PreviewAccent,
    DefinitionPerformance Performance,
    object Plan,
    IReadOnlyList<PackDiagnostic> Diagnostics)
{
    public T PlanAs<T>() where T : class => (T)Plan;
}

// ------------------------------------------------------------------ appearance

/// <summary>Theme material/atmosphere personality (R2 §35, §39). A theme is never palette-only.</summary>
public sealed record MaterialSpec(
    string Personality,
    string Atmosphere,
    double Grain,
    double GlassOpacity,
    double GlassBlur,
    double Specular,
    double RimLight,
    string AmbientPrimary,
    string AmbientSecondary,
    IReadOnlyList<string> FrameAffinity,
    string? PreferredBackdropId,
    string? PreferredSpotlightId)
{
    public static MaterialSpec Neutral { get; } = new("grounded", "none", 0.02, 0.92, 12, 0.3, 0.3,
        "token:accent", "token:accentSecondary", [], null, null);
}

public sealed record ThemePlan(ThemeDefinition Theme, MaterialSpec Material);

public sealed record FontFace(string Family, string? AssetPath, string Fallback);

public sealed record TypeStyle(string FontRole, double Size, int Weight, double LineHeight, string ColorRole, bool Trim, bool Wrap);

/// <summary>Semantic font roles → faces, and semantic type roles → styles, already scaled.</summary>
public sealed record TypographyPlan(
    IReadOnlyDictionary<string, FontFace> Faces,
    double Scale,
    IReadOnlyDictionary<string, TypeStyle> Styles);

public sealed record IconGlyph(string? PathData, string? AssetPath);

public sealed record IconPlan(IReadOnlyDictionary<string, IconGlyph> Icons, double StrokeWidth, bool Filled)
{
    /// <summary>Always returns a glyph: pack mapping, else the built-in semantic icon.</summary>
    public IconGlyph Resolve(string key) =>
        Icons.TryGetValue(key, out var glyph) ? glyph : new IconGlyph(IconCatalog.PathFor(key), null);
}

/// <summary>Bounded motion parameters. Reduced Motion is applied by the governor on top and cannot be disabled here.</summary>
public sealed record MotionPlan(
    double DurationScale,
    double AmbientSpeed,
    double ParallaxAmplitude,
    double SheenSpeed,
    double HoverLift,
    string RouteTransition,
    string SpotlightTransition,
    string FrameMotion);

// ------------------------------------------------------------------ environment

public sealed record GradientStopSpec(string Color, double Offset);

/// <summary>One bounded declarative backdrop layer. The renderer interprets a fixed, approved set of kinds.</summary>
public sealed record BackdropLayer(
    string Kind,
    double Opacity = 1,
    string? Color = null,
    string? Color2 = null,
    IReadOnlyList<GradientStopSpec>? Stops = null,
    double Angle = 90,
    double CenterX = 0.5,
    double CenterY = 0.5,
    double Radius = 0.6,
    double Speed = 1,
    double Amplitude = 0,
    double Period = 20,
    double Depth = 0,
    double Blur = 0,
    double Scale = 1,
    int Count = 0,
    double SizeMin = 1,
    double SizeMax = 3,
    double Width = 0.2,
    string? AssetPath = null,
    string? Source = null,
    string Blend = "normal");

public sealed record BackdropVariant(IReadOnlyList<BackdropLayer> Layers)
{
    public bool IsAnimated => Layers.Any(layer => layer.Kind is "particles" or "fog" or "light-sweep" or "video" or "lottie"
        || (layer.Amplitude > 0 && layer.Kind is "gradient" or "radial-glow" or "image"));
}

public sealed record BackdropPlan(IReadOnlyDictionary<VisualTier, BackdropVariant> Variants)
{
    public BackdropVariant For(VisualTier tier) =>
        Variants.TryGetValue(tier, out var variant) ? variant
        : Variants.TryGetValue(VisualTier.Fallback, out var fallback) ? fallback
        : Variants.Values.First();

    public static BackdropPlan Still(string canvas = "token:canvas", string glow = "token:accentSecondary")
    {
        var variant = new BackdropVariant(
        [
            new BackdropLayer("gradient", Stops: [new GradientStopSpec(canvas, 0), new GradientStopSpec(canvas, 1)]),
            new BackdropLayer("radial-glow", Opacity: 0.4, Color: glow, CenterX: 0.5, CenterY: 0, Radius: 0.9),
        ]);
        return new BackdropPlan(new Dictionary<VisualTier, BackdropVariant>
        {
            [VisualTier.Full] = variant,
            [VisualTier.Reduced] = variant,
            [VisualTier.Fallback] = variant,
        });
    }
}

public sealed record EnvironmentPlan(BackdropPlan Backdrop, double MediaTint, string Atmosphere, bool UsesBanner, double ScrimStrength);

// ------------------------------------------------------------------ structure

public sealed record HomeRailPlan(string Id, bool Visible, string Style, int MaxItems);

public sealed record HomeLayoutPlan(
    string HeroPlacement,
    double HeroFraction,
    double HeroMinHeight,
    double HeroMaxHeight,
    IReadOnlyList<HomeRailPlan> Rails);

public sealed record SpotlightPlan(
    string Composition,
    string Motion,
    string Transition,
    double RotationSeconds,
    bool ShowCover,
    bool ShowOverview,
    string Pagination,
    double ScrimStrength);

/// <summary>Only renderer-approved virtualized primitives exist here; no layout can request unbounded realization.</summary>
public static class CollectionPrimitives
{
    public const string Grid = "VirtualizedGrid";
    public const string UniformGrid = "VirtualizedUniformGrid";
    public const string List = "VirtualizedList";
    public const string Carousel = "VirtualizedCarousel";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Grid, UniformGrid, List, Carousel };
}

public sealed record GalleryLayoutPlan(
    string Primitive,
    double ItemMinWidth,
    double ItemMaxWidth,
    double? ItemAspect,
    double Spacing,
    double RowHeight,
    int MaxColumns,
    string? PreferredCardId);

public sealed record ProfileLayoutPlan(ProfileLayoutDefinition Layout);

public sealed record FramePlan(CoverFrameDefinition Frame, string? OverlayAssetPath);

// ------------------------------------------------------------------ media

public sealed record MediaTilePlan(string LegacyVariant, double Aspect, string Fit, string Overlay, bool ShowTypeBadge, string Hover);

public sealed record MediaBorderPlan(string Style, double Radius, double Thickness, string Color, string SelectedColor, double Glow);

public sealed record MediaInfoPlan(string Placement, IReadOnlyList<string> Fields, int Lines);

// ------------------------------------------------------------------ card composition language (v1)

/// <summary>Layout properties every composition node shares.</summary>
public sealed record NodeLayout(
    double MarginLeft = 0,
    double MarginTop = 0,
    double MarginRight = 0,
    double MarginBottom = 0,
    double? Width = null,
    double? Height = null,
    string HorizontalAlignment = "stretch",
    string VerticalAlignment = "stretch",
    int Row = 0,
    int Column = 0,
    int RowSpan = 1,
    int ColumnSpan = 1,
    double Opacity = 1,
    string? VisibleWhen = null);

/// <summary>
/// Approved declarative primitives (document 01 §14). Built-in cards are written in exactly this
/// language, so a future layout designer can emit definitions the renderer already understands.
/// </summary>
public abstract record CompositionNode(NodeLayout Layout);

public sealed record StackNode(NodeLayout Layout, string Orientation, double Spacing, double Padding, string? Background, string? CornerRadius, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record GridNode(NodeLayout Layout, IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, double Spacing, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record OverlayNode(NodeLayout Layout, IReadOnlyList<CompositionNode> Children) : CompositionNode(Layout);

public sealed record TextNode(NodeLayout Layout, string? Slot, string? TextKey, string TypeRole, string Color, int MaxLines, string TextAlignment, bool OnMedia) : CompositionNode(Layout);

public sealed record ImageNode(NodeLayout Layout, string Slot, string Stretch, string? CornerRadius, string Shape, bool Ambient) : CompositionNode(Layout);

public sealed record FrameNode(NodeLayout Layout, double Size) : CompositionNode(Layout);

public sealed record BadgeNode(NodeLayout Layout, string Slot, string Style) : CompositionNode(Layout);

public sealed record ScrimNode(NodeLayout Layout, string Direction, string From, string To) : CompositionNode(Layout);

public sealed record ShapeNode(NodeLayout Layout, string? Fill, string? Stroke, double StrokeThickness, string? CornerRadius) : CompositionNode(Layout);

public sealed record IconNode(NodeLayout Layout, string Key, double Size, string Color) : CompositionNode(Layout);

public sealed record CardPlan(
    double Aspect,
    string ArtSource,
    string Density,
    string Hover,
    string FrameMode,
    string? LegacyVariant,
    CompositionNode Root)
{
    public bool UsesBanner => ArtSource is "banner" or "cover-over-banner";

    public int NodeCount => Count(Root);

    private static int Count(CompositionNode node) => 1 + node switch
    {
        StackNode stack => stack.Children.Sum(Count),
        GridNode grid => grid.Children.Sum(Count),
        OverlayNode overlay => overlay.Children.Sum(Count),
        _ => 0,
    };
}
