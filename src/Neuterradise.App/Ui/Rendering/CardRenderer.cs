using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Presentation;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;

namespace Neuterradise.App.Ui;

/// <summary>Semantic slot values for one Profile card. Built from the Gallery/Home read models, never from tables.</summary>
public sealed record CardData(
    Guid ProfileId,
    string Name,
    string? Category,
    IReadOnlyList<string> Tags,
    int? Rating,
    string? Tier,
    bool IsFavorite,
    long MediaCount,
    int RelatedCount,
    string? Overview,
    ImageRef? Cover,
    ImageRef? Banner,
    CoverAppearance Appearance,
    MediaTransformState CoverTransform,
    MediaTransformState BannerTransform,
    FramePlan? Frame)
{
    public bool Has(string slot) => slot switch
    {
        SemanticSlots.ProfileCategory => !string.IsNullOrWhiteSpace(Category),
        SemanticSlots.ProfileTags => Tags.Count > 0,
        SemanticSlots.ProfileRating => Rating is > 0,
        SemanticSlots.ProfileTier => !string.IsNullOrWhiteSpace(Tier),
        SemanticSlots.ProfileFavorite => IsFavorite,
        SemanticSlots.ProfileOverview => !string.IsNullOrWhiteSpace(Overview),
        SemanticSlots.ProfileCover => Cover is not null,
        SemanticSlots.ProfileBanner => Banner is not null || Cover is not null,
        SemanticSlots.ProfileRelatedCount => RelatedCount > 0,
        _ => true,
    };

    public string TextFor(string slot) => slot switch
    {
        SemanticSlots.ProfileName => Name,
        SemanticSlots.ProfileCategory => Category ?? string.Empty,
        SemanticSlots.ProfileTags => string.Join(" · ", Tags.Take(4)),
        SemanticSlots.ProfileOverview => Overview ?? string.Empty,
        SemanticSlots.ProfileTier => Tier ?? string.Empty,
        SemanticSlots.ProfileRating => Rating is { } r ? string.Create(CultureInfo.CurrentCulture, $"{r} / 5") : string.Empty,
        SemanticSlots.ProfileMediaCount => MediaCount == 1 ? UI.T("Card.OneItem", "1 item") : UI.F("Card.Items", "{0} items", MediaCount),
        SemanticSlots.ProfileRelatedCount => RelatedCount == 1 ? UI.T("Card.OneConnection", "1 connection") : UI.F("Card.Connections", "{0} connections", RelatedCount),
        _ => string.Empty,
    };
}

/// <summary>
/// A realized Profile card. Its element tree is built ONCE from the compiled plan's composition
/// primitives; <see cref="Bind"/> only updates property values. Ordinary data changes and container
/// recycling never clear or reparent the tree (the WPF card's destructive recomposition is gone).
/// </summary>
public sealed class CardVisual : Grid
{
    private readonly List<Action<CardData>> _binders = [];
    private readonly List<(FrameworkElement Element, string Slot)> _conditions = [];
    private readonly List<CoverFrameView> _frames = [];
    private readonly ScaleTransform _scale = new() { ScaleX = 1, ScaleY = 1 };
    private readonly HoverLight? _light;
    private readonly Grid _content = new();

    public CardVisual(CompiledDefinition definition)
    {
        Definition = definition;
        Plan = definition.PlanAs<CardPlan>();
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        RenderTransform = _scale;
        Background = ThemeRuntime.Current.Brush("transparent");
        CornerRadius = new CornerRadius(ThemeRuntime.Current.Tokens.Number("radiusCard", 12));

        var clip = new Border { CornerRadius = CornerRadius, Child = _content };
        Children.Add(clip);
        _content.Children.Add(Build(Plan.Root));

        if (Plan.Hover is "sheen" or "spotlight")
        {
            _light = new HoverLight(Plan.Hover == "sheen");
            _content.Children.Add(_light);
        }

        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PointerCanceled += (_, _) => SetHover(false);
        PointerMoved += (_, args) =>
        {
            if (_light is not null && ActualWidth > 0)
            {
                var point = args.GetCurrentPoint(this).Position;
                _light.Move(point.X / ActualWidth, point.Y / ActualHeight);
            }
        };
    }

    public CompiledDefinition Definition { get; }

    public CardPlan Plan { get; }

    public CardData? Data { get; private set; }

    public bool IsSelected
    {
        set
        {
            var theme = ThemeRuntime.Current;
            BorderBrush = value ? theme.Brush("borderSelected") : theme.Brush("transparent");
            BorderThickness = new Thickness(value ? 2 : 0);
        }
    }

    public void Bind(CardData data)
    {
        Data = data;
        foreach (var binder in _binders)
        {
            binder(data);
        }

        foreach (var (element, slot) in _conditions)
        {
            element.Visibility = data.Has(slot) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetHover(bool hovered)
    {
        foreach (var frame in _frames)
        {
            frame.IsHovered = hovered;
        }

        _light?.SetActive(hovered);
        if (Plan.Hover is "lift" or "sheen" && !ThemeRuntime.Current.ReducedMotion)
        {
            var target = hovered ? ThemeRuntime.Current.Motion.HoverLift : 1.0;
            Animate(_scale, "ScaleX", target);
            Animate(_scale, "ScaleY", target);
        }
    }

    private static void Animate(DependencyObject target, string property, double to)
    {
        var duration = ThemeRuntime.Current.Duration("fast");
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, duration))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ---------------------------------------------------------------- composition → elements (once)

    private FrameworkElement Build(CompositionNode node)
    {
        var theme = ThemeRuntime.Current;
        FrameworkElement element;
        switch (node)
        {
            case StackNode stack:
            {
                var panel = new StackPanel
                {
                    Orientation = stack.Orientation == "horizontal" ? Orientation.Horizontal : Orientation.Vertical,
                    Spacing = stack.Spacing,
                };
                foreach (var child in stack.Children)
                {
                    panel.Children.Add(Build(child));
                }

                element = stack.Background is not null || stack.CornerRadius is not null || stack.Padding > 0
                    ? new Border
                    {
                        Child = panel,
                        Padding = new Thickness(stack.Padding),
                        Background = stack.Background is null ? null : theme.Brush(TokenName(stack.Background)),
                        CornerRadius = new CornerRadius(theme.Tokens.ResolveRadius(stack.CornerRadius)),
                    }
                    : panel;
                break;
            }

            case GridNode gridNode:
            {
                var grid = UI.Grid(string.Join(",", gridNode.Rows), string.Join(",", gridNode.Columns));
                grid.RowSpacing = gridNode.Spacing;
                grid.ColumnSpacing = gridNode.Spacing;
                foreach (var child in gridNode.Children)
                {
                    grid.Children.Add(Build(child));
                }

                element = grid;
                break;
            }

            case OverlayNode overlay:
            {
                var grid = new Grid();
                foreach (var child in overlay.Children)
                {
                    grid.Children.Add(Build(child));
                }

                element = grid;
                break;
            }

            case TextNode text:
            {
                var block = UI.Text(null, text.TypeRole, TokenName(text.Color), text.MaxLines);
                block.TextAlignment = text.TextAlignment switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                if (text.Slot is { } slot)
                {
                    _binders.Add(data => block.Text = data.TextFor(slot));
                    block.Tip(null);
                    _binders.Add(data => ToolTipService.SetToolTip(block, data.TextFor(slot)));
                }
                else if (text.TextKey is { } key)
                {
                    block.Text = UI.T(key, key);
                }

                element = block;
                break;
            }

            case ImageNode image:
            {
                var view = new SkImageView
                {
                    CornerRadiusValue = theme.Tokens.ResolveRadius(image.CornerRadius),
                    Circle = image.Shape == "circle",
                };
                if (image.Ambient)
                {
                    view.Blur = 18;
                }

                _binders.Add(data =>
                {
                    if (image.Slot == SemanticSlots.ProfileBanner)
                    {
                        view.Source = data.Banner ?? data.Cover;
                        view.Transform = data.Banner is null ? data.CoverTransform : data.BannerTransform;
                    }
                    else
                    {
                        view.Source = data.Cover ?? data.Banner;
                        view.Transform = data.Cover is null ? data.BannerTransform : data.CoverTransform;
                    }

                    if (image.Shape == "cover-shape")
                    {
                        view.Circle = data.Appearance.Shape == CoverShape.Circle;
                    }
                });
                element = view;
                break;
            }

            case FrameNode frame:
            {
                var view = new CoverFrameView { FrameMode = Plan.FrameMode, Width = frame.Size, Height = frame.Size };
                _frames.Add(view);
                _binders.Add(data =>
                {
                    view.Source = data.Cover;
                    view.Transform = data.CoverTransform;
                    view.Plan = data.Frame;
                    view.Appearance = data.Appearance;
                });
                element = view;
                break;
            }

            case BadgeNode badge:
                element = BuildBadge(badge);
                break;

            case ScrimNode scrim:
            {
                var border = new Border { IsHitTestVisible = false };
                void Paint()
                {
                    var from = ThemeRuntime.Current.Color(TokenName(scrim.From));
                    var to = ThemeRuntime.Current.Color(TokenName(scrim.To));
                    if (scrim.Direction == "radial")
                    {
                        var radial = new RadialGradientBrush();
                        radial.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
                        radial.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
                        border.Background = radial;
                        return;
                    }

                    var (start, end) = scrim.Direction switch
                    {
                        "to-bottom" => ((0.5, 0.0), (0.5, 1.0)),
                        "to-left" => ((1.0, 0.5), (0.0, 0.5)),
                        "to-right" => ((0.0, 0.5), (1.0, 0.5)),
                        _ => ((0.5, 1.0), (0.5, 0.0)),
                    };
                    var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(start.Item1, start.Item2), EndPoint = new Windows.Foundation.Point(end.Item1, end.Item2) };
                    brush.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
                    brush.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
                    border.Background = brush;
                }

                Paint();
                ThemeRuntime.Current.Changed += Paint;
                border.Unloaded += (_, _) => ThemeRuntime.Current.Changed -= Paint;
                border.Loaded += (_, _) =>
                {
                    ThemeRuntime.Current.Changed -= Paint;
                    ThemeRuntime.Current.Changed += Paint;
                    Paint();
                };
                element = border;
                break;
            }

            case ShapeNode shape:
                element = new Border
                {
                    Background = shape.Fill is null ? null : theme.Brush(TokenName(shape.Fill)),
                    BorderBrush = shape.Stroke is null ? null : theme.Brush(TokenName(shape.Stroke)),
                    BorderThickness = new Thickness(shape.Stroke is null ? 0 : shape.StrokeThickness),
                    CornerRadius = new CornerRadius(theme.Tokens.ResolveRadius(shape.CornerRadius)),
                };
                break;

            case IconNode icon:
                element = new IconView(icon.Key, icon.Size, TokenName(icon.Color));
                break;

            default:
                element = new Grid();
                break;
        }

        ApplyLayout(element, node.Layout);
        return element;
    }

    private FrameworkElement BuildBadge(BadgeNode badge)
    {
        var theme = ThemeRuntime.Current;
        var onMedia = badge.Style == "glass";
        var textColor = onMedia ? "onMediaPrimary" : badge.Style == "solid" ? "textOnAccent" : "textSecondary";
        var icon = badge.Slot switch
        {
            SemanticSlots.ProfileFavorite => "icon.profile.favorite",
            SemanticSlots.ProfileRating => "icon.profile.rating",
            SemanticSlots.ProfileMediaCount => "icon.media.image",
            SemanticSlots.ProfileRelatedCount => "icon.profile.related",
            SemanticSlots.ProfileCategory => "icon.profile.category",
            _ => null,
        };
        var label = UI.Text(null, "badge", textColor);
        var content = UI.H(4, icon is null ? null : new IconView(icon, 12, badge.Slot == SemanticSlots.ProfileFavorite ? "danger" : textColor), label);
        _binders.Add(data => label.Text = badge.Slot == SemanticSlots.ProfileFavorite ? UI.T("Card.Favorite", "Favorite") : data.TextFor(badge.Slot));
        if (badge.Slot == SemanticSlots.ProfileFavorite)
        {
            label.Visibility = Visibility.Collapsed;
            content.Tip(UI.T("Card.Favorite", "Favorite"));
        }

        return badge.Style == "text"
            ? content
            : new Border
            {
                Child = content,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(6),
                Background = badge.Style switch
                {
                    "glass" => theme.Brush("glassStrip"),
                    "solid" => theme.Brush("accent"),
                    _ => theme.Brush("transparent"),
                },
                BorderBrush = badge.Style == "outline" ? theme.Brush("borderDefault") : null,
                BorderThickness = new Thickness(badge.Style == "outline" ? 1 : 0),
            };
    }

    private void ApplyLayout(FrameworkElement element, NodeLayout layout)
    {
        element.Margin = new Thickness(layout.MarginLeft, layout.MarginTop, layout.MarginRight, layout.MarginBottom);
        if (layout.Width is { } width)
        {
            element.Width = width;
        }

        if (layout.Height is { } height)
        {
            element.Height = height;
        }

        element.HorizontalAlignment = layout.HorizontalAlignment switch
        {
            "left" => HorizontalAlignment.Left,
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        element.VerticalAlignment = layout.VerticalAlignment switch
        {
            "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        Grid.SetRow(element, layout.Row);
        Grid.SetColumn(element, layout.Column);
        Grid.SetRowSpan(element, layout.RowSpan);
        Grid.SetColumnSpan(element, layout.ColumnSpan);
        element.Opacity = layout.Opacity;
        if (layout.VisibleWhen is { } slot)
        {
            _conditions.Add((element, slot));
        }
    }

    private static string TokenName(string reference) =>
        reference.StartsWith("token:", StringComparison.Ordinal) ? reference[6..] : reference;

    /// <summary>The card's one secondary effect: a clipped sheen sweep or a pointer spotlight (R2 §11, §36).</summary>
    private sealed class HoverLight : SKCanvasElement
    {
        private readonly bool _sheen;
        private bool _active;
        private double _x = 0.5;
        private double _y = 0.3;
        private long _startedAt;
        private bool _ticking;

        public HoverLight(bool sheen)
        {
            _sheen = sheen;
            IsHitTestVisible = false;
            Unloaded += (_, _) => SetActive(false);
        }

        public void Move(double x, double y)
        {
            _x = x;
            _y = y;
            if (_active && !_sheen)
            {
                Invalidate();
            }
        }

        public void SetActive(bool active)
        {
            _active = active && !ThemeRuntime.Current.ReducedMotion;
            _startedAt = Environment.TickCount64;
            if (_sheen && _active && !_ticking)
            {
                _ticking = true;
                CompositionTarget.Rendering += OnFrame;
            }

            Invalidate();
        }

        private void OnFrame(object? sender, object e)
        {
            if (Environment.TickCount64 - _startedAt > 900)
            {
                _ticking = false;
                CompositionTarget.Rendering -= OnFrame;
            }

            Invalidate();
        }

        protected override void RenderOverride(SKCanvas canvas, Windows.Foundation.Size area)
        {
            if (!_active)
            {
                return;
            }

            var w = (float)area.Width;
            var h = (float)area.Height;
            using var paint = new SKPaint { BlendMode = SKBlendMode.Plus };
            if (_sheen)
            {
                var progress = Math.Clamp((Environment.TickCount64 - _startedAt) / 900.0, 0, 1);
                var x = (float)((progress * 1.6) - 0.3) * w;
                paint.Shader = SKShader.CreateLinearGradient(new SKPoint(x - (w * 0.2f), 0), new SKPoint(x + (w * 0.2f), h),
                    [SKColors.Transparent, SkiaColor.Token("lightSpecular", 0.55), SKColors.Transparent], null, SKShaderTileMode.Clamp);
            }
            else
            {
                paint.Shader = SKShader.CreateRadialGradient(new SKPoint((float)_x * w, (float)_y * h), Math.Max(w, h) * 0.6f,
                    [SkiaColor.Token("lightAmbient", 0.5), SKColors.Transparent], null, SKShaderTileMode.Clamp);
            }

            canvas.DrawRect(new SKRect(0, 0, w, h), paint);
        }
    }
}

/// <summary>
/// Cache of compiled card plans per definition: a Gallery with one card style realizes one plan.
/// Card element trees themselves are owned by the virtualizing repeater and recycled per plan.
/// </summary>
public static class CardDataFactory
{
    public static CardData From(Neuterradise.App.Gallery.GalleryCardViewModel card, PresentationRuntime? runtime, int coverWidth, int bannerWidth)
    {
        var overrides = card.Appearance;
        var profile = new ProfilePresentationState(card.ProfileId, 0, null, overrides);
        var media = runtime?.ResolveMedia(profile, PresentationSlots.CardCover, PresentationSlots.CardBanner) ?? profile.Media;
        var frame = runtime?.ResolvePlan<FramePlan>(PresentationSlots.ProfileFrame, PresentationContext.ForProfile(profile, "gallery"));
        return new CardData(
            card.ProfileId,
            card.DisplayName,
            card.CategoryName,
            card.Tags,
            card.Rating,
            card.PresentationModel.Tier,
            card.IsFavorite,
            card.MediaCount,
            card.RelatedProfileCount,
            null,
            card.CoverSource is { } cover ? cover with { DecodeWidth = coverWidth } : null,
            card.BannerStillSource is { } banner ? banner with { DecodeWidth = bannerWidth } : null,
            AppearanceFor(overrides, frame, ThemeRuntime.Current.ReducedMotion),
            media.Cover,
            media.Banner,
            frame);
    }

    /// <summary>Profile Cover appearance with the resolved frame definition (built-in or pack-defined).</summary>
    public static CoverAppearance AppearanceFor(Neuterradise.App.Profiles.ProfileAppearanceOverrides overrides, FramePlan? frame, bool reduceMotion)
    {
        var request = overrides.ToCoverAppearanceRequest();
        if (frame is not null && CoverFrameCatalog.TryGetFrame(frame.Frame.Id, out _))
        {
            request = request with { FrameId = frame.Frame.Id };
        }

        var resolved = CoverFrameCatalog.Resolve(request, reduceMotion).Appearance;
        return frame is not null && !CoverFrameCatalog.TryGetFrame(frame.Frame.Id, out _)
            ? resolved with { Frame = frame.Frame }
            : resolved;
    }
}
