using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

/// <summary>
/// The Uno projection of the canonical token authority. One mutable brush per semantic token is shared
/// by every element, so a theme change recolours the live tree in place; nothing is rebuilt. Typography
/// and icon changes re-apply through weak registries. This is the only place Uno resources are written.
/// </summary>
public sealed class ThemeRuntime : IPresentationPreferenceApplier
{
    private static ThemeRuntime _current = new();

    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<TextBlock, TypeRegistration> _typed = new();
    private readonly List<WeakReference<TextBlock>> _typedList = [];
    private readonly ThemeLoader _loader = new();
    private LinearGradientBrush? _glass;
    private PresentationRuntime? _presentation;

    public ThemeRuntime()
    {
        var theme = _loader.LoadBuiltInTheme(ThemeLoader.FallbackThemeId).Theme
            ?? throw new InvalidOperationException("The Abyss built-in theme is missing.");
        Theme = theme;
        Material = MaterialSpec.Neutral;
        Density = DensityMode.Comfortable;
        Tokens = TokenAuthority.Resolve(theme, Material, Density, ReducedMotionAuthority.IsReduced);
        Typography = new TypographyPlan(new Dictionary<string, FontFace>(), 1, TokenAuthority.Foundations.TypeStyles);
        Icons = new IconPlan(new Dictionary<string, IconGlyph>(), 1.6, false);
        Motion = new MotionPlan(1, 1, 1, 1, 1.03, "slide-fade", "crossfade-depth", "full");
        PushBrushes();
    }

    public static ThemeRuntime Current => _current;

    public static void Install(ThemeRuntime runtime) => _current = runtime;

    public event Action? Changed;

    public ThemeDefinition Theme { get; private set; }

    public MaterialSpec Material { get; private set; }

    public DensityMode Density { get; private set; }

    public ResolvedTokenSet Tokens { get; private set; }

    public TypographyPlan Typography { get; private set; }

    public IconPlan Icons { get; private set; }

    public MotionPlan Motion { get; private set; }

    public bool ReducedMotion => ReducedMotionAuthority.IsReduced;

    public void AttachPresentation(PresentationRuntime presentation) => _presentation = presentation;

    // ---------------------------------------------------------------- lookups

    public SolidColorBrush Brush(string token)
    {
        if (!_brushes.TryGetValue(token, out var brush))
        {
            brush = new SolidColorBrush(ResolveColor(token));
            _brushes[token] = brush;
        }

        return brush;
    }

    public Windows.UI.Color Color(string token) => ResolveColor(token);

    public ArgbColor Argb(string reference) => reference.StartsWith('#') ? Tokens.Resolve(reference) : Tokens.Resolve("token:" + reference);

    /// <summary>Glass body: tint with a subtle top highlight (R2 §12 construction rule: body, edge, highlight).</summary>
    public Brush GlassBrush()
    {
        if (_glass is null)
        {
            _glass = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
            _glass.GradientStops.Add(new GradientStop { Offset = 0 });
            _glass.GradientStops.Add(new GradientStop { Offset = 0.35 });
            _glass.GradientStops.Add(new GradientStop { Offset = 1 });
            UpdateGlass();
        }

        return _glass;
    }

    public double Duration(string kind) => Tokens.Number(kind switch
    {
        "fast" => "durationFast",
        "slow" => "durationSlow",
        "cinematic" => "durationCinematic",
        _ => "durationNormal",
    }) * (ReducedMotion ? 0 : Motion.DurationScale);

    public FontFamily Font(string role)
    {
        var face = Typography.Faces.TryGetValue(role, out var f) ? f : null;
        var fallback = role == "mono" ? Tokens.Text("fontMono") : role is "display" or "heading" ? Tokens.Text("fontDisplay") : Tokens.Text("fontBody");
        if (face is null)
        {
            return new FontFamily(fallback);
        }

        if (face.AssetPath is { } asset)
        {
            // Local validated pack font: resolved by file path + family, never fetched from a network.
            return new FontFamily($"{new Uri(asset).AbsoluteUri}#{face.Family}, {face.Fallback}, {fallback}");
        }

        return new FontFamily($"{face.Family}, {face.Fallback}, {fallback}");
    }

    public void ApplyType(TextBlock block, string role, string? colorToken = null)
    {
        var style = Typography.Styles.TryGetValue(role, out var s) ? s : TokenAuthority.Foundations.TypeStyles["body"];
        block.FontFamily = Font(style.FontRole);
        block.FontSize = style.Size;
        block.LineHeight = style.LineHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.FontWeight = new Windows.UI.Text.FontWeight((ushort)style.Weight);
        block.Foreground = Brush(colorToken ?? style.ColorRole);
        if (style.Trim)
        {
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (style.Wrap)
        {
            block.TextWrapping = TextWrapping.WrapWholeWords;
        }

        if (!_typed.TryGetValue(block, out _))
        {
            _typed.Add(block, new TypeRegistration(role, colorToken));
            _typedList.Add(new WeakReference<TextBlock>(block));
        }
        else
        {
            _typed.AddOrUpdate(block, new TypeRegistration(role, colorToken));
        }
    }

    // ---------------------------------------------------------------- application

    /// <summary>Applies a resolved presentation (called on start, Apply, and during live preview).</summary>
    public void Apply(ThemePlan theme, TypographyPlan typography, IconPlan icons, MotionPlan motion, DensityMode density)
    {
        var typographyChanged = !ReferenceEquals(typography, Typography);
        Theme = theme.Theme;
        Material = theme.Material;
        Typography = typography;
        Icons = icons;
        Motion = motion;
        Density = density;
        Tokens = TokenAuthority.Resolve(Theme, Material, density, ReducedMotion, motion.DurationScale);
        PushBrushes();
        if (typographyChanged)
        {
            ReapplyTypography();
        }

        Changed?.Invoke();
    }

    public void RefreshMotion()
    {
        Tokens = TokenAuthority.Resolve(Theme, Material, Density, ReducedMotion, Motion.DurationScale);
        ResourceGovernor.Shared.SetReducedMotion(ReducedMotion);
        Changed?.Invoke();
    }

    private void ReapplyTypography()
    {
        _typedList.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _typedList)
        {
            if (reference.TryGetTarget(out var block) && _typed.TryGetValue(block, out var registration))
            {
                ApplyType(block, registration.Role, registration.Color);
            }
        }
    }

    private Windows.UI.Color ResolveColor(string token)
    {
        if (token == "transparent")
        {
            return Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }

        var color = token.StartsWith('#') ? Tokens.Resolve(token) : Tokens.Color(token);
        return Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void PushBrushes()
    {
        foreach (var (token, brush) in _brushes)
        {
            brush.Color = ResolveColor(token);
        }

        UpdateGlass();
        PushControlResources();
    }

    private void UpdateGlass()
    {
        if (_glass is null)
        {
            return;
        }

        _glass.GradientStops[0].Color = Blend("glassFill", "glassHighlight", 0.5);
        _glass.GradientStops[1].Color = Color("glassFill");
        _glass.GradientStops[2].Color = Color("glassDeepFill");
    }

    private Windows.UI.Color Blend(string a, string b, double t)
    {
        var blended = ArgbColor.Lerp(Tokens.Color(a), Tokens.Color(b), t);
        return Windows.UI.Color.FromArgb(blended.A, blended.R, blended.G, blended.B);
    }

    /// <summary>WinUI lightweight styling: every stock control picks up the token brushes.</summary>
    private void PushControlResources()
    {
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        void Set(string key, string token) => resources[key] = Brush(token);

        try
        {
            Set("ApplicationPageBackgroundThemeBrush", "canvas");
            Set("ButtonBackground", "surface3");
            Set("ButtonBackgroundPointerOver", "surfaceHover");
            Set("ButtonBackgroundPressed", "surfacePressed");
            Set("ButtonBackgroundDisabled", "surface2");
            Set("ButtonForeground", "textPrimary");
            Set("ButtonForegroundPointerOver", "textPrimary");
            Set("ButtonForegroundPressed", "textPrimary");
            Set("ButtonForegroundDisabled", "textDisabled");
            Set("ButtonBorderBrush", "borderDefault");
            Set("ButtonBorderBrushPointerOver", "borderInteractive");
            Set("ButtonBorderBrushPressed", "borderStrong");
            Set("ButtonBorderBrushDisabled", "borderSubtle");
            Set("AccentButtonBackground", "accent");
            Set("AccentButtonBackgroundPointerOver", "accentHover");
            Set("AccentButtonBackgroundPressed", "accentPressed");
            Set("AccentButtonForeground", "textOnAccent");
            Set("TextControlBackground", "surface2");
            Set("TextControlBackgroundPointerOver", "surface3");
            Set("TextControlBackgroundFocused", "surface2");
            Set("TextControlForeground", "textPrimary");
            Set("TextControlForegroundPointerOver", "textPrimary");
            Set("TextControlForegroundFocused", "textPrimary");
            Set("TextControlBorderBrush", "borderDefault");
            Set("TextControlBorderBrushPointerOver", "borderInteractive");
            Set("TextControlBorderBrushFocused", "focus");
            Set("TextControlPlaceholderForeground", "textMuted");
            Set("TextControlPlaceholderForegroundFocused", "textMuted");
            Set("TextControlPlaceholderForegroundPointerOver", "textMuted");
            Set("ComboBoxBackground", "surface2");
            Set("ComboBoxBackgroundPointerOver", "surface3");
            Set("ComboBoxForeground", "textPrimary");
            Set("ComboBoxBorderBrush", "borderDefault");
            Set("ComboBoxDropDownBackground", "surfaceOverlay");
            Set("ComboBoxItemForeground", "textPrimary");
            Set("ComboBoxItemBackgroundPointerOver", "surfaceHover");
            Set("ComboBoxItemBackgroundSelected", "surfaceSelected");
            Set("ToggleSwitchFillOn", "accent");
            Set("ToggleSwitchFillOnPointerOver", "accentHover");
            Set("ToggleSwitchStrokeOff", "borderStrong");
            Set("ToggleSwitchKnobFillOff", "textSecondary");
            Set("ToggleSwitchKnobFillOn", "textOnAccent");
            Set("ToggleSwitchContentForeground", "textPrimary");
            Set("SliderTrackValueFill", "accent");
            Set("SliderTrackValueFillPointerOver", "accentHover");
            Set("SliderTrackFill", "surface3");
            Set("SliderThumbBackground", "accent");
            Set("SliderThumbBackgroundPointerOver", "accentHover");
            Set("CheckBoxForegroundUnchecked", "textPrimary");
            Set("CheckBoxForegroundChecked", "textPrimary");
            Set("CheckBoxCheckBackgroundFillChecked", "accent");
            Set("CheckBoxCheckBackgroundStrokeUnchecked", "borderStrong");
            Set("RadioButtonForeground", "textPrimary");
            Set("RadioButtonOuterEllipseCheckedFill", "accent");
            Set("RadioButtonOuterEllipseCheckedStroke", "accent");
            Set("ScrollBarThumbFill", "borderStrong");
            Set("ScrollBarThumbFillPointerOver", "textMuted");
            Set("ToolTipBackground", "surfaceOverlay");
            Set("ToolTipForeground", "textPrimary");
            Set("ToolTipBorderBrush", "borderDefault");
            Set("ProgressBarForeground", "accent");
            Set("ProgressBarBackground", "surface3");
            Set("ProgressRingForeground", "accent");
            Set("FlyoutPresenterBackground", "surfaceOverlay");
            Set("MenuFlyoutPresenterBackground", "surfaceOverlay");
            Set("MenuFlyoutItemForeground", "textPrimary");
            Set("MenuFlyoutItemBackgroundPointerOver", "surfaceHover");
            Set("ListViewItemBackgroundPointerOver", "surfaceHover");
            Set("ListViewItemBackgroundSelected", "surfaceSelected");
            Set("ListViewItemForeground", "textPrimary");
            Set("TextFillColorPrimaryBrush", "textPrimary");
            Set("TextFillColorSecondaryBrush", "textSecondary");
            Set("TextFillColorDisabledBrush", "textDisabled");
            Set("FocusStrokeColorOuterBrush", "focus");
            Set("SystemControlFocusVisualPrimaryBrush", "focus");
            resources["ControlCornerRadius"] = new CornerRadius(Tokens.Number("radiusControl", 8));
            resources["OverlayCornerRadius"] = new CornerRadius(Tokens.Number("radiusCard", 12));
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Control resource projection failed: {0}", exception.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- IPresentationPreferenceApplier

    /// <summary>
    /// Settings-driven theme change. The Presentation binding is the authority, so this writes the
    /// Global theme binding (same path as the Customization Center) and applies it live.
    /// </summary>
    public async Task<PresentationPreferenceApplyResult> ApplyThemeAsync(
        string themeId,
        CancellationToken cancellationToken = default)
    {
        var selection = _loader.ResolveActiveTheme(themeId, Theme, null);
        if (!selection.RequestedThemeApplied || _presentation is not { } presentation)
        {
            return new PresentationPreferenceApplyResult(false, selection, "That theme is not available.");
        }

        var reference = DefinitionRef.BuiltIn($"builtin.neuterradise.theme.{selection.Theme.Id}");
        if (presentation.Find(reference) is null)
        {
            return new PresentationPreferenceApplyResult(false, selection, "That theme is not available in the Presentation catalogue.");
        }

        var session = presentation.BeginPreview(PresentationContext.Global);
        session.Select(PresentationSlots.Theme, ScopeKind.Global, reference);
        var result = await session.ApplyAsync(cancellationToken).ConfigureAwait(true);
        return new PresentationPreferenceApplyResult(result.Succeeded, selection, result.Message);
    }

    public void ApplyDensity(DensityMode density)
    {
        Density = density;
        Tokens = TokenAuthority.Resolve(Theme, Material, Density, ReducedMotion, Motion.DurationScale);
        PushBrushes();
        Changed?.Invoke();
    }

    public void ApplyReduceMotion(bool userReduceMotion)
    {
        ReducedMotionAuthority.ApplyUser(userReduceMotion);
        RefreshMotion();
    }

    private sealed record TypeRegistration(string Role, string? Color);
}
