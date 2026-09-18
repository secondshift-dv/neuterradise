using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Ui;

/// <summary>
/// The Customization Center (document 01 §21). Every editor is discovered from the slot table, every
/// change goes into an in-memory <see cref="PreviewSession"/> and renders live on the real current
/// subject; nothing is written until Apply. Cancel drops the preview and restores the committed look.
/// Built-in and pack definitions are listed together and treated identically.
///
/// Every shipped slot is reachable from here, not only from its page: surface-scoped values edit their own
/// surface, and Profile/item-scoped editors get a subject picker (a Profile, then optionally one of its
/// media items) that previews on that subject's real data. Page Customize buttons are shortcuts into the
/// same session. Cover/Banner source, framing and playback are one preview transaction committed on Apply.
/// </summary>
public sealed class CustomizationCenter : IDisposable
{
    private const string PacksSection = "packs";
    private const int SubjectMediaLimit = 500;

    private readonly AppServices _services;
    private PreviewSession _session;
    private AppearanceCustomizationOverlayRequest? _request;
    private readonly Action _onClosed;
    private readonly Grid _root = new();
    private readonly StackPanel _rail = UI.V(2);
    private readonly StackPanel _slotTabs = UI.H(6);
    private readonly Grid _preview = new() { Height = 250 };
    private readonly StackPanel _scopeBar = UI.V(6);
    private readonly StackPanel _editor = UI.V(14);
    private readonly TextBlock _status = UI.Text(string.Empty, "caption");
    private readonly Button _apply;
    private string _category;
    private string? _slot;
    private ScopeKind _scope;
    private bool _appearanceTouched;
    private bool _closed;
    private SettingsViewModel? _library;
    private ImageRef? _coverSource;
    private ImageRef? _bannerSource;
    private ImageRef? _committedCoverSource;
    private ImageRef? _committedBannerSource;
    private CustomizationSubjectSnapshot? _subject;
    private readonly StackPanel _subjectBar = UI.V(8);
    private readonly TextBlock _subjectText = UI.Text(string.Empty, "caption", maxLines: 1);
    private int _subjectGeneration;
    private CancellationTokenSource? _subjectLoadCts;
    private CancellationTokenSource? _subjectSearchCts;

    public CustomizationCenter(AppServices services, string category, PresentationContext context, string? slot, object? subject, Action onClosed)
    {
        _services = services;
        _onClosed = onClosed;
        _request = subject as AppearanceCustomizationOverlayRequest;
        _coverSource = _request?.PreviewCoverSource;
        _bannerSource = _request?.PreviewBannerStillSource;
        _committedCoverSource = _coverSource;
        _committedBannerSource = _bannerSource;
        _session = services.Presentation.BeginPreview(context);
        _session.Changed += OnSessionChanged;
        _category = CustomizationCategories.Ordered.Contains(category) || category == PacksSection ? category : CustomizationCategories.Appearance;
        _slot = slot;
        if (slot is not null && PresentationSlots.TryGet(slot, out var descriptor))
        {
            _category = descriptor.Category;
        }

        var theme = ThemeRuntime.Current;
        var scrim = new Border { Background = theme.Brush("scrim") };
        scrim.Tapped += (_, _) => Close(apply: false);
        _root.Children.Add(scrim);

        _apply = UI.Button(UI.T("Customize.Apply", "Apply"), () => Run(ApplyAsync(), "CustomizationCenter.ApplyAsync", UI.T("Customize.ApplyFailed", "Could not apply customization.")), ButtonKind.Primary);
        var header = UI.Grid("auto", "*,auto",
            UI.V(2, UI.Text(UI.T("Customize.Title", "Customize"), "section-title"), _subjectText).At(0, 0),
            UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), () => Close(apply: false)).At(0, 1));
        var footer = UI.Grid("auto", "*,auto",
            _status.Align(vertical: VerticalAlignment.Center).At(0, 0),
            UI.H(8,
                UI.Button(UI.T("Customize.Cancel", "Cancel"), () => Close(apply: false), ButtonKind.Ghost),
                _apply).At(0, 1));

        var content = UI.V(14, _subjectBar, _preview, _slotTabs, _scopeBar, _editor);
        var body = UI.Grid("*", "190,*",
            UI.Scroll(_rail).At(0, 0),
            UI.Scroll(content.Margin(18, 0, 6, 0)).At(0, 1));
        var panel = UI.Surface(UI.Grid("auto,*,auto", "*",
            header.Margin(0, 0, 0, 12).At(0),
            body.At(1),
            footer.Margin(0, 12, 0, 0).At(2)), Material.Deep, theme.Tokens.Number("radiusHero", 20), 20);
        panel.MaxWidth = 1240;
        panel.Margin = new Thickness(24, 20, 24, 20);
        panel.Tapped += (_, e) => e.Handled = true;
        _root.Children.Add(panel);
        _root.SizeChanged += (_, _) => body.ColumnDefinitions[0].Width = new GridLength(_root.ActualWidth < 900 ? 140 : 190);

        _subjectText.Text = SubjectLine();
        BuildRail();
        ShowCategory(_category, _slot);
        if (context.ProfileId is { } profileId)
        {
            Run(LoadSubjectAsync(profileId, ++_subjectGeneration, preserveRequest: _request is not null), "CustomizationCenter.LoadSubjectAsync", UI.T("Customize.SubjectLoadFailed", "This Profile could not be loaded for customization."));
        }
    }

    public FrameworkElement View => _root;

    private PresentationContext Context => _session.LiveContext;

    private string SubjectLine() =>
        _session.Context.ItemId is not null && _request is not null ? UI.F("Customize.ForItem", "Editing one media item of {0}", _request.ProfileDisplayName)
        : _request is not null ? UI.F("Customize.ForProfile", "Editing {0}", _request.ProfileDisplayName)
        : _session.Context.ProfileId is not null ? UI.T("Customize.ForThisProfile", "Editing this Profile")
        : _session.Context.Surface is { } surface ? UI.F("Customize.ForSurface", "Editing {0}", surface)
        : UI.T("Customize.Everywhere", "Changes apply everywhere unless a page or Profile overrides them");

    private void Run(Task task, string context, string failureMessage) => _services.RunUserAction(task, context, failureMessage);

    // ---------------------------------------------------------------- navigation

    private void BuildRail()
    {
        _rail.Children.Clear();
        foreach (var category in CustomizationCategories.Ordered.Append(PacksSection))
        {
            var key = category;
            var entry = new Border
            {
                Child = UI.Text(CategoryName(key), "control"),
                Padding = new Thickness(12, 9, 12, 9),
                CornerRadius = new CornerRadius(8),
                Tag = key,
            };
            entry.Tapped += (_, _) => ShowCategory(key, null);
            _rail.Children.Add(entry);
        }
    }

    private static string CategoryName(string category) => category switch
    {
        CustomizationCategories.Appearance => UI.T("Customize.Appearance", "Appearance"),
        CustomizationCategories.Home => UI.T("Nav.Home", "Home"),
        CustomizationCategories.Gallery => UI.T("Nav.Gallery", "Gallery"),
        CustomizationCategories.Profile => UI.T("Customize.Profile", "Profile"),
        CustomizationCategories.Media => UI.T("Customize.Media", "Media"),
        CustomizationCategories.Library => UI.T("Customize.Library", "Library structure"),
        _ => UI.T("Customize.Packs", "Presentation packs"),
    };

    /// <summary>Every shipped slot of the category. Nothing is hidden because the Center was opened elsewhere.</summary>
    private static IEnumerable<SlotDescriptor> SlotsOf(string category) =>
        PresentationSlots.All.Where(s => s.Category == category);

    /// <summary>A slot that only exists for one Profile (its Cover, Banner or a surface framing delta).</summary>
    private static bool NeedsProfile(SlotDescriptor slot) =>
        !slot.AllowsScope(ScopeKind.Global) && !slot.AllowsScope(ScopeKind.Surface);

    private ScopeKind DefaultScope(SlotDescriptor slot)
    {
        if (slot.AllowsScope(ScopeKind.Item) && _session.Context.ItemId is not null) return ScopeKind.Item;
        if (slot.AllowsScope(ScopeKind.Profile) && _session.ProfileWorking is not null) return ScopeKind.Profile;
        if (slot.AllowsScope(ScopeKind.Surface) && _session.Context.Surface == slot.Surface) return ScopeKind.Surface;
        return ScopeKind.Global;
    }

    private void ShowCategory(string category, string? slot)
    {
        _category = category;
        var theme = ThemeRuntime.Current;
        foreach (var entry in _rail.Children.OfType<Border>())
        {
            entry.Background = (string?)entry.Tag == category ? theme.Brush("surfaceSelected") : theme.Brush("transparent");
        }

        _slotTabs.Children.Clear();
        _scopeBar.Children.Clear();
        _editor.Children.Clear();
        _preview.Visibility = Visibility.Visible;
        RenderSubjectBar();
        if (category == PacksSection)
        {
            _preview.Visibility = Visibility.Collapsed;
            _slot = null;
            RenderPacks();
            return;
        }

        if (category == CustomizationCategories.Library)
        {
            _preview.Visibility = Visibility.Collapsed;
            _slot = null;
            RenderLibrary();
            return;
        }

        var slots = SlotsOf(category).ToList();
        if (slots.Count == 0)
        {
            _slot = null;
            RenderPreview();
            _editor.Children.Add(UI.Text(category == CustomizationCategories.Profile
                ? UI.T("Customize.OpenFromProfile", "Open a Profile and choose Customize Profile to edit its Cover, Banner and frame. Global Profile defaults are below once a Profile is open.")
                : UI.T("Customize.NothingHere", "Nothing to customize here yet."), "body-muted"));
            return;
        }

        foreach (var descriptor in slots)
        {
            var id = descriptor.Id;
            _slotTabs.Children.Add(UI.Chip(UI.T(descriptor.LabelKey, descriptor.LabelFallback), false, () => SelectSlot(id)));
        }

        SelectSlot(slot is not null && slots.Any(s => s.Id == slot) ? slot : slots[0].Id);
    }

    private void SelectSlot(string slot)
    {
        _slot = slot;
        var descriptor = PresentationSlots.Get(slot);
        _scope = DefaultScope(descriptor);
        var index = 0;
        foreach (var chip in _slotTabs.Children.OfType<Border>())
        {
            var selected = SlotsOf(_category).ElementAtOrDefault(index++)?.Id == slot;
            chip.Background = ThemeRuntime.Current.Brush(selected ? "accentSoft" : "surface2");
            chip.BorderBrush = ThemeRuntime.Current.Brush(selected ? "accent" : "borderSubtle");
        }

        RenderAll();
    }

    private void OnSessionChanged(object? sender, string slot)
    {
        if (slot.StartsWith("appearance.", StringComparison.Ordinal))
        {
            _appearanceTouched = true;
            PresentationBinder.ApplyGlobal(_services.Presentation, _session.Working);
        }

        SyncSourcePreviews();
        UpdateStatus();
        RenderPreview();
        RenderScopeBar();
    }

    /// <summary>After Reset/Use Global the committed source is back, so its committed image returns too.</summary>
    private void SyncSourcePreviews()
    {
        if (_session.ProfileWorking is not { Sources: { } working } profile || _session.OriginalProfile is not { Sources: { } original } committed)
        {
            return;
        }

        if (working.CoverAssetId == original.CoverAssetId
            && profile.Overrides.CoverVideoTimestampMilliseconds == committed.Overrides.CoverVideoTimestampMilliseconds)
        {
            _coverSource = _committedCoverSource;
        }

        if (working.BannerAssetId == original.BannerAssetId)
        {
            _bannerSource = _committedBannerSource;
        }
    }

    private void RenderAll()
    {
        RenderPreview();
        RenderScopeBar();
        RenderEditor();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _status.Text = _session.HasChanges
            ? UI.T("Customize.Previewing", "Previewing changes. Nothing is saved until Apply.")
            : UI.T("Customize.NoChanges", "No changes.");
        _apply.IsEnabled = _session.HasChanges && !_session.IsClosed;
    }

    // ---------------------------------------------------------------- scope bar

    private void RenderScopeBar()
    {
        _scopeBar.Children.Clear();
        if (_slot is null)
        {
            return;
        }

        var descriptor = PresentationSlots.Get(_slot);
        var selection = _session.Resolve(_slot);
        var scopes = UI.H(6);
        foreach (var scope in descriptor.Scopes.Where(s => IsScopeAvailable(descriptor, s)))
        {
            var value = scope;
            scopes.Children.Add(UI.Chip(ScopeName(scope, descriptor), scope == _scope, () =>
            {
                _scope = value;
                RenderScopeBar();
                RenderEditor();
            }));
        }

        var source = selection.Source switch
        {
            ResolutionSource.BuiltInDefault => UI.T("Customize.Source.Default", "Using the default"),
            ResolutionSource.Global => UI.T("Customize.Source.Global", "Set for everything"),
            ResolutionSource.Surface => UI.T("Customize.Source.Surface", "Set for this page"),
            ResolutionSource.Profile => UI.T("Customize.Source.Profile", "Set for this Profile"),
            _ => UI.T("Customize.Source.Item", "Set for this item"),
        };
        var inherited = selection.IsInheritedAt(_scope);
        var actions = UI.H(6,
            UI.Button(UI.T("Customize.ResetScope", "Reset to inherited"), () =>
            {
                _session.ResetToParent(_slot, _scope);
                RenderEditor();
            }, ButtonKind.Ghost),
            _scope != ScopeKind.Global ? UI.Button(UI.T("Customize.UseGlobal", "Use global"), () =>
            {
                _session.UseGlobal(_slot);
                RenderEditor();
            }, ButtonKind.Ghost) : null);
        foreach (var button in actions.Children.OfType<Button>())
        {
            button.IsEnabled = !inherited || _scope != ScopeKind.Global && !selection.IsInheritedAt(ScopeKind.Global);
        }

        _scopeBar.Children.Add(UI.Text(UI.T(descriptor.LabelKey, descriptor.LabelFallback), "card-title"));
        _scopeBar.Children.Add(UI.Text(descriptor.DescriptionFallback, "body-muted", maxLines: 3));
        _scopeBar.Children.Add(new VariableWrap(10, scopes, UI.Badge(inherited ? UI.F("Customize.Inherited", "Inherited · {0}", source) : source, inherited ? "neutral" : "accent"), actions));
    }

    private bool IsScopeAvailable(SlotDescriptor descriptor, ScopeKind scope) => scope switch
    {
        ScopeKind.Global => true,
        ScopeKind.Surface => descriptor.Surface is not null,
        ScopeKind.Profile => _session.ProfileWorking is not null,
        _ => _session.Context.ItemId is not null,
    };

    private static string ScopeName(ScopeKind scope, SlotDescriptor descriptor) => scope switch
    {
        ScopeKind.Global => UI.T("Customize.Scope.Global", "Everywhere"),
        ScopeKind.Surface => UI.F("Customize.Scope.Surface", "Only {0}", descriptor.Surface ?? string.Empty),
        ScopeKind.Profile => UI.T("Customize.Scope.Profile", "This Profile"),
        _ => UI.T("Customize.Scope.Item", "This item"),
    };

    // ---------------------------------------------------------------- editors

    private void RenderEditor()
    {
        _editor.Children.Clear();
        if (_slot is null)
        {
            return;
        }

        var descriptor = PresentationSlots.Get(_slot);
        if (NeedsProfile(descriptor) && _session.ProfileWorking is null)
        {
            _editor.Children.Add(UI.Text(UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."), "body-muted"));
            return;
        }

        switch (descriptor.Editor)
        {
            case EditorKind.DefinitionChooser:
                _editor.Children.Add(DefinitionChooser(descriptor));
                if (descriptor.Id == PresentationSlots.ProfileFrame && _session.ProfileWorking is not null && _scope == ScopeKind.Profile)
                {
                    _editor.Children.Add(FrameOptions());
                }

                break;
            case EditorKind.StateEditor when descriptor.StateSchema == "gallery-density":
                _editor.Children.Add(GalleryDensityEditor(descriptor));
                break;
            case EditorKind.CoverEditor:
            case EditorKind.BannerEditor:
                _editor.Children.Add(MediaEditor(descriptor));
                break;
        }
    }

    private FrameworkElement DefinitionChooser(SlotDescriptor descriptor)
    {
        var theme = ThemeRuntime.Current;
        var selected = _session.Resolve(descriptor.Id).Definition;
        var wrap = new VariableWrap(10);
        foreach (var definition in _services.Presentation.DefinitionsOf(descriptor.Kind!))
        {
            var reference = definition.Ref;
            var isSelected = selected == reference;
            var swatch = RenderDefinitionThumbnail(descriptor, definition);
            var labels = UI.V(2,
                UI.Text(definition.Name, "control", maxLines: 1),
                definition.Description is { Length: > 0 } description ? UI.Text(description, "caption", maxLines: 2) : null,
                UI.H(4,
                    definition.Origin == PackOrigin.User ? UI.Badge(UI.T("Customize.FromPack", "Pack")) : null,
                    PerformanceLabel(definition) is { } performance ? UI.Badge(performance) : null,
                    definition.Diagnostics.Count > 0 ? UI.Badge(UI.T("Customize.Warnings", "Warnings"), "warning") : null));
            var card = UI.Surface(UI.V(8, swatch, labels), Material.Raised, 12, 10);
            card.Width = 176;
            card.BorderBrush = theme.Brush(isSelected ? "borderSelected" : "borderSubtle");
            card.BorderThickness = new Thickness(isSelected ? 2 : 1);
            ToolTipService.SetToolTip(card, $"{definition.Name} · {reference}");
            card.Tapped += (_, _) =>
            {
                _session.Select(descriptor.Id, _scope, reference);
                RenderEditor();
            };
            wrap.Children.Add(card);
        }

        return wrap;
    }

    private static string? PerformanceLabel(CompiledDefinition definition) => definition.Performance.ToString() switch
    {
        "ReducedMotionFallback" => UI.T("Customize.Performance.Static", "Static fallback"),
        "HighCost" => UI.T("Customize.Performance.Demanding", "More demanding"),
        "Standard" => null,
        _ => null,
    };

    private FrameworkElement RenderDefinitionThumbnail(SlotDescriptor descriptor, CompiledDefinition definition)
    {
        var theme = ThemeRuntime.Current;
        var host = new Grid
        {
            Height = 82,
            Background = theme.Brush("surface2"),
        };

        switch (descriptor.Id)
        {
            case PresentationSlots.GalleryCard when SampleData(PresentationSlots.CardCover, PresentationSlots.CardBanner) is { } data:
                var card = new CardVisual(definition);
                card.Bind(data);
                var cardAspect = Math.Max(0.5, definition.PlanAs<CardPlan>().Aspect);
                card.Height = Math.Min(76, 142 / cardAspect);
                card.Width = card.Height * cardAspect;
                card.HorizontalAlignment = HorizontalAlignment.Center;
                card.VerticalAlignment = VerticalAlignment.Center;
                host.Children.Add(card);
                break;
            case PresentationSlots.ProfileFrame:
                var frame = definition.PlanAs<FramePlan>();
                host.Children.Add(new CoverFrameView
                {
                    Source = _coverSource,
                    Transform = MediaTransformState.Default,
                    Plan = frame,
                    Appearance = CardDataFactory.AppearanceFor(_session.ProfileWorking?.Overrides ?? ProfileAppearanceOverrides.Default, frame, true),
                    FrameMode = "full",
                    Width = 70,
                    Height = 70,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case PresentationSlots.ProfileEnvironment:
                host.Children.Add(new BackdropView { Plan = definition.PlanAs<EnvironmentPlan>().Backdrop, IsSurfaceActive = true });
                host.Children.Add(UI.Surface(UI.V(1, UI.Text("Aa", "control"), UI.Text(definition.PlanAs<EnvironmentPlan>().Atmosphere, "micro", maxLines: 1)), Material.Frost, 8, 7)
                    .Margin(10).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom));
                break;
            case PresentationSlots.MediaTile:
                host.Children.Add(MediaThumbnail(definition.PlanAs<MediaTilePlan>(),
                    _session.ResolveCompiled(PresentationSlots.MediaBorder).PlanAs<MediaBorderPlan>(),
                    _session.ResolveCompiled(PresentationSlots.MediaInfo).PlanAs<MediaInfoPlan>()));
                break;
            case PresentationSlots.MediaBorder:
                host.Children.Add(MediaThumbnail(
                    _session.ResolveCompiled(PresentationSlots.MediaTile).PlanAs<MediaTilePlan>(),
                    definition.PlanAs<MediaBorderPlan>(),
                    _session.ResolveCompiled(PresentationSlots.MediaInfo).PlanAs<MediaInfoPlan>()));
                break;
            case PresentationSlots.MediaInfo:
                host.Children.Add(MediaThumbnail(
                    _session.ResolveCompiled(PresentationSlots.MediaTile).PlanAs<MediaTilePlan>(),
                    _session.ResolveCompiled(PresentationSlots.MediaBorder).PlanAs<MediaBorderPlan>(),
                    definition.PlanAs<MediaInfoPlan>()));
                break;
            case PresentationSlots.GalleryLayout:
                var layout = definition.PlanAs<GalleryLayoutPlan>();
                var strip = UI.H(Math.Clamp(layout.Spacing / 3, 3, 8));
                strip.HorizontalAlignment = HorizontalAlignment.Center;
                strip.VerticalAlignment = VerticalAlignment.Center;
                var count = layout.Primitive == CollectionPrimitives.List ? 3 : 4;
                for (var i = 0; i < count; i++)
                {
                    strip.Children.Add(UI.Surface(UI.V(2, new IconView("icon.profile.person", 16), UI.Text("Profile", "micro", maxLines: 1)), Material.Raised, 5, 5));
                }
                host.Children.Add(strip);
                break;
            default:
                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(8),
                    Background = definition.PreviewAccent is { } accent && ThemeColorText.TryParse(accent, out var color) ? UI.Solid(color) : theme.Brush("surface2"),
                });
                host.Children.Add(UI.Text(definition.Name, "caption", "onMediaPrimary", 1).Margin(12).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom));
                break;
        }

        return new Border { Child = host, Height = 82, CornerRadius = new CornerRadius(8) };
    }

    private FrameworkElement MediaThumbnail(MediaTilePlan tile, MediaBorderPlan border, MediaInfoPlan info)
    {
        var theme = ThemeRuntime.Current;
        var content = new Grid { Width = 68, Height = Math.Clamp(68 / Math.Max(0.5, tile.Aspect), 45, 68) };
        content.Children.Add(new SkImageView
        {
            Source = _coverSource,
            Transform = MediaTransformState.Default with { Fit = tile.Fit },
            PlaceholderToken = "surface3",
            CornerRadiusValue = Math.Max(0, border.Radius - border.Thickness),
        });
        if (tile.ShowTypeBadge)
        {
            content.Children.Add(new IconView("icon.media.image", 12, "onMediaPrimary").Margin(4).Align(HorizontalAlignment.Right, VerticalAlignment.Top));
        }
        if (info.Placement is "overlay-bottom" or "hover")
        {
            content.Children.Add(new Border
            {
                Child = UI.Text("IMG_24", "micro", "onMediaPrimary", 1),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = theme.Brush(tile.Overlay == "glass-strip" ? "glassStrip" : "mediaOverlay"),
            });
        }
        var framed = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(border.Radius),
            BorderThickness = new Thickness(Math.Max(1, border.Thickness)),
            BorderBrush = theme.Brush(border.Color.Replace("token:", string.Empty, StringComparison.Ordinal)),
        };
        FrameworkElement result = info.Placement == "below"
            ? UI.V(2, framed, UI.Text("IMG_24", "micro", maxLines: 1))
            : framed;
        result.HorizontalAlignment = HorizontalAlignment.Center;
        result.VerticalAlignment = VerticalAlignment.Center;
        return result;
    }

    private FrameworkElement FrameOptions()
    {
        var overrides = _session.ProfileWorking!.Overrides;
        var scale = Slider(UI.T("Customize.Frame.Scale", "Frame size"), 0.8, 1.3, overrides.CoverFrameScale ?? 1, v => _session.EditProfile(o => o with { CoverFrameScale = v }));
        var intensity = Slider(UI.T("Customize.Frame.Intensity", "Frame intensity"), 0, 1, overrides.CoverFrameIntensity ?? 1, v => _session.EditProfile(o => o with { CoverFrameIntensity = v }));
        var tint = UI.Input("#RRGGBB", overrides.CoverFrameTint, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) _session.EditProfile(o => o with { CoverFrameTint = null });
            else if (ThemeColorText.TryParse(text, out _)) _session.EditProfile(o => o with { CoverFrameTint = text.Trim() });
        });
        var shadow = new ToggleSwitch { Header = UI.T("Customize.Frame.Shadow", "Cover shadow"), IsOn = overrides.CoverShadow };
        shadow.Toggled += (_, _) => _session.EditProfile(o => o with { CoverShadow = shadow.IsOn });
        var shapes = UI.H(6);
        foreach (var shape in AppearanceCustomizationOverlayRequest.ShapeOptions)
        {
            var id = shape.Id;
            shapes.Children.Add(UI.Chip(shape.DisplayName, string.Equals(overrides.CoverShape, id, StringComparison.OrdinalIgnoreCase), () =>
            {
                _session.EditProfile(o => o with { CoverShape = id });
                RenderEditor();
            }));
        }

        var animations = UI.H(6);
        foreach (var animation in AppearanceCustomizationOverlayRequest.AnimationOptions)
        {
            var value = animation.ToString();
            animations.Children.Add(UI.Chip(value, string.Equals(overrides.CoverFrameAnimation, value, StringComparison.OrdinalIgnoreCase), () =>
            {
                _session.EditProfile(o => o with { CoverFrameAnimation = value });
                RenderEditor();
            }));
        }

        return UI.Section(UI.T("Customize.Frame.Options", "Frame options"), null,
            UI.Text(UI.T("Customize.Shape", "Shape"), "control"), shapes, scale, intensity, UI.Text(UI.T("Customize.Frame.Tint", "Tint"), "control"), tint,
            UI.Text(UI.T("Customize.Frame.Animation", "Animation"), "control"), animations, shadow);
    }

    private FrameworkElement GalleryDensityEditor(SlotDescriptor descriptor)
    {
        var state = GalleryDensityState.Parse(_session.Resolve(descriptor.Id).StateJson);
        void Write(GalleryDensityState next) => _session.SetState(descriptor.Id, _scope, next.ToJson());

        FrameworkElement Choice<T>(string label, T current, Func<T, GalleryDensityState> with) where T : struct, Enum
        {
            var row = UI.H(6);
            foreach (var value in Enum.GetValues<T>())
            {
                var v = value;
                row.Children.Add(UI.Chip(v.ToString(), EqualityComparer<T>.Default.Equals(v, current), () =>
                {
                    Write(with(v));
                    RenderEditor();
                }));
            }

            return UI.V(6, UI.Text(label, "control"), row);
        }

        FrameworkElement Toggle(string label, bool value, Func<bool, GalleryDensityState> with)
        {
            var toggle = new ToggleSwitch { Header = label, IsOn = value };
            toggle.Toggled += (_, _) => Write(with(toggle.IsOn));
            return toggle;
        }

        return UI.V(14,
            Choice(UI.T("Customize.CardSize", "Card size"), state.CardSize, v => state with { CardSize = v }),
            Choice(UI.T("Customize.InfoDensity", "Information density"), state.InformationDensity, v => state with { InformationDensity = v }),
            Choice(UI.T("Customize.BannerMotion", "Banner motion on cards"), state.BannerMotion, v => state with { BannerMotion = v }),
            new VariableWrap(18,
                Toggle(UI.T("Customize.ShowCategory", "Category"), state.ShowCategory, v => state with { ShowCategory = v }),
                Toggle(UI.T("Customize.ShowTags", "Tags"), state.ShowTags, v => state with { ShowTags = v }),
                Toggle(UI.T("Customize.ShowRating", "Rating"), state.ShowRating, v => state with { ShowRating = v }),
                Toggle(UI.T("Customize.ShowTier", "Tier"), state.ShowTier, v => state with { ShowTier = v }),
                Toggle(UI.T("Customize.ShowMediaCount", "Media count"), state.ShowMediaCount, v => state with { ShowMediaCount = v }),
                Toggle(UI.T("Customize.ShowRelated", "Related"), state.ShowRelated, v => state with { ShowRelated = v })));
    }

    // ---------------------------------------------------------------- Cover / Banner

    private bool IsBanner(SlotDescriptor descriptor) => descriptor.Editor == EditorKind.BannerEditor;

    private bool IsCanonical(SlotDescriptor descriptor) => descriptor.Id is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner;

    /// <summary>The canonical Profile transform, and the surface-delta result when editing a card/Spotlight slot.</summary>
    private (MediaTransformState Inherited, MediaTransformState Current) TransformFor(SlotDescriptor descriptor)
    {
        var overrides = _session.ProfileWorking!.Overrides;
        var canonical = IsBanner(descriptor) ? MediaTransformState.FromBanner(overrides) : MediaTransformState.FromCover(overrides);
        if (IsCanonical(descriptor))
        {
            return (canonical, canonical);
        }

        return (canonical, canonical.ApplyDelta(_session.Resolve(descriptor.Id).StateJson));
    }

    private void WriteTransform(SlotDescriptor descriptor, MediaTransformState next)
    {
        if (IsCanonical(descriptor))
        {
            _session.EditProfile(o => IsBanner(descriptor) ? next.WriteBanner(o) : next.WriteCover(o));
        }
        else
        {
            var (inherited, _) = TransformFor(descriptor);
            _session.SetState(descriptor.Id, ScopeKind.Profile, next.DeltaFrom(inherited));
        }
    }

    private FrameworkElement MediaEditor(SlotDescriptor descriptor)
    {
        if (_session.ProfileWorking is null)
        {
            return UI.Text(UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."), "body-muted");
        }

        var banner = IsBanner(descriptor);
        var (_, current) = TransformFor(descriptor);
        var stage = new SkImageView
        {
            Source = banner ? _bannerSource ?? _coverSource : _coverSource,
            Transform = current,
            CornerRadiusValue = 12,
            Height = banner ? 200 : 240,
            Width = banner ? double.NaN : 240,
            PlaceholderToken = "surface2",
        };
        var hint = UI.Text(UI.T("Customize.DragHint", "Drag to move the focal point · scroll to zoom"), "caption");
        var state = current;
        var dragging = false;
        Windows.Foundation.Point last = default;
        stage.PointerPressed += (_, e) =>
        {
            dragging = true;
            last = e.GetCurrentPoint(stage).Position;
            stage.CapturePointer(e.Pointer);
        };
        stage.PointerMoved += (_, e) =>
        {
            if (!dragging || stage.ActualWidth <= 0 || stage.ActualHeight <= 0)
            {
                return;
            }

            var point = e.GetCurrentPoint(stage).Position;
            var dx = (point.X - last.X) / stage.ActualWidth / Math.Max(1, state.Zoom);
            var dy = (point.Y - last.Y) / stage.ActualHeight / Math.Max(1, state.Zoom);
            last = point;
            state = state with { FocalX = Math.Clamp(state.FocalX - dx, 0, 1), FocalY = Math.Clamp(state.FocalY - dy, 0, 1) };
            stage.Transform = state;
        };
        stage.PointerReleased += (_, e) =>
        {
            if (dragging)
            {
                dragging = false;
                stage.ReleasePointerCapture(e.Pointer);
                WriteTransform(descriptor, state);
            }
        };
        stage.PointerWheelChanged += (_, e) =>
        {
            var delta = e.GetCurrentPoint(stage).Properties.MouseWheelDelta;
            state = state with { Zoom = Math.Clamp(state.Zoom + (delta > 0 ? 0.1 : -0.1), 1, 4) };
            stage.Transform = state;
            WriteTransform(descriptor, state);
            e.Handled = true;
        };

        var fit = UI.H(6);
        foreach (var mode in ProfileAppearanceOverrides.FitModes)
        {
            var value = mode;
            fit.Children.Add(UI.Chip(value == "fill" ? UI.T("Customize.Fill", "Fill") : UI.T("Customize.Fit", "Fit"), state.Fit == value, () =>
            {
                WriteTransform(descriptor, state with { Fit = value });
                RenderEditor();
            }));
        }

        var controls = UI.V(10,
            fit,
            Slider(UI.T("Customize.Zoom", "Zoom"), 1, 4, state.Zoom, v => WriteTransform(descriptor, state = state with { Zoom = v })),
            Slider(UI.T("Customize.OffsetX", "Horizontal offset"), -1, 1, state.OffsetX, v => WriteTransform(descriptor, state = state with { OffsetX = v })),
            Slider(UI.T("Customize.OffsetY", "Vertical offset"), -1, 1, state.OffsetY, v => WriteTransform(descriptor, state = state with { OffsetY = v })),
            Slider(UI.T("Customize.Rotation", "Rotation"), -180, 180, state.Rotation, v => WriteTransform(descriptor, state = state with { Rotation = v })),
            UI.Button(UI.T("Customize.ResetFraming", "Reset framing"), () =>
            {
                if (IsCanonical(descriptor)) WriteTransform(descriptor, MediaTransformState.Default);
                else _session.SetState(descriptor.Id, ScopeKind.Profile, null);
                RenderEditor();
            }, ButtonKind.Ghost));

        var editor = UI.V(14,
            UI.Text(IsCanonical(descriptor)
                ? UI.T("Customize.CanonicalNote", "This is the Profile's own framing. Cards and the Spotlight inherit it; changing a layout never resets it.")
                : UI.T("Customize.DeltaNote", "Only this surface changes. Unset values inherit the Profile framing."), "caption"),
            UI.Grid("auto", banner ? "*" : "auto,*", stage.At(0, 0), banner ? null : controls.Margin(18, 0, 0, 0).At(0, 1)),
            hint,
            banner ? controls : null);

        if (IsCanonical(descriptor) && _request is not null)
        {
            editor.Children.Insert(0, Candidates(banner));
        }

        if (descriptor.Id == PresentationSlots.ProfileBanner)
        {
            editor.Children.Add(PlaybackEditor());
        }

        return editor;
    }

    private FrameworkElement Candidates(bool banner)
    {
        var row = UI.H(8);
        var working = _session.ProfileWorking;
        var sources = working?.Sources;
        IEnumerable<(Guid AssetId, string? Preview, string Name, bool Recommended, bool VideoFrame, long? Timestamp, bool Current, double FocusX, double FocusY, BannerVisualSourceKind? BannerKind, double? Start, double? Duration)> items = banner
            ? _request!.BannerCandidates.Select(c => (c.AssetId, c.PreviewPath, c.DisplayTitle, c.IsRecommended, c.SourceKind == BannerVisualSourceKind.VideoFrame, c.FrameTimestampMilliseconds,
                c.AssetId == sources?.BannerAssetId && c.SourceKind == working?.Overrides.ResolvedBannerSourceKind && c.FrameTimestampMilliseconds == working?.Overrides.BannerVideoFrameTimestampMilliseconds,
                c.SuggestedFocusX, c.SuggestedFocusY, (BannerVisualSourceKind?)c.SourceKind, (double?)c.StartPointSeconds, (double?)c.DurationSeconds))
            : _request!.CoverCandidates.Select(c => (c.AssetId, c.PreviewPath, c.FileName, c.IsRecommended, c.SourceKind == CoverVisualSourceKind.VideoFrame, c.TimestampMilliseconds,
                c.AssetId == sources?.CoverAssetId && c.TimestampMilliseconds == working?.Overrides.CoverVideoTimestampMilliseconds, c.SuggestedCropX, c.SuggestedCropY,
                (BannerVisualSourceKind?)null, (double?)null, (double?)null));
        foreach (var candidate in items.Take(40))
        {
            var thumb = new SkImageView { Source = ImageRef.FromPath(candidate.Preview, 240), Width = banner ? 150 : 96, Height = banner ? 84 : 96, CornerRadiusValue = 8, PlaceholderToken = "surface2" };
            var tile = UI.Surface(UI.V(4, thumb, UI.Text(candidate.Name, "micro", maxLines: 1), candidate.Recommended ? UI.Badge(UI.T("Customize.Recommended", "Suggested"), "accent") : null), Material.Raised, 10, 6);
            tile.Width = banner ? 164 : 110;
            tile.BorderThickness = new Thickness(candidate.Current ? 2 : 1);
            tile.BorderBrush = ThemeRuntime.Current.Brush(candidate.Current ? "borderSelected" : "borderSubtle");
            var value = candidate;
            tile.Tapped += (_, _) => SelectSource(banner, value.AssetId, value.VideoFrame, value.Timestamp, value.Preview, value.FocusX, value.FocusY, value.BannerKind, value.Start, value.Duration);
            row.Children.Add(tile);
        }

        return UI.Section(banner ? UI.T("Customize.BannerSource", "Banner media") : UI.T("Customize.CoverSource", "Cover image"),
            UI.T("Customize.SourceNote", "Choosing new media only changes the preview; the source and its framing are saved together when you Apply."),
            UI.Scroll(row, horizontal: true));
    }

    /// <summary>
    /// Previews a new Cover/Banner source. It joins the same preview transaction as framing and playback:
    /// nothing is written until Apply, and Cancel or Reset restores the committed source.
    /// </summary>
    private void SelectSource(
        bool banner,
        Guid assetId,
        bool videoFrame,
        long? timestamp,
        string? previewPath,
        double suggestedX,
        double suggestedY,
        BannerVisualSourceKind? bannerKind,
        double? start,
        double? duration)
    {
        if (banner)
        {
            _session.SelectBannerSource(
                assetId,
                bannerKind ?? BannerVisualSourceKind.Image,
                timestamp,
                start,
                duration);
            _bannerSource = ImageRef.FromPath(previewPath, 1600) ?? _bannerSource;
            _session.EditProfile(o => o with { BannerFocusX = suggestedX, BannerFocusY = suggestedY });
        }
        else
        {
            _session.SelectCoverSource(assetId, videoFrame, timestamp);
            _coverSource = ImageRef.FromPath(previewPath, 800) ?? _coverSource;
            _session.EditProfile(o => o with { CropX = suggestedX, CropY = suggestedY });
        }

        RenderAll();
    }

    private FrameworkElement PlaybackEditor()
    {
        var overrides = _session.ProfileWorking!.Overrides;
        var playback = PlaybackPresentationState.FromBanner(overrides);
        var loop = UI.H(6);
        foreach (var mode in ProfileAppearanceOverrides.LoopModes)
        {
            var value = mode;
            loop.Children.Add(UI.Chip(value == "loop" ? UI.T("Customize.Loop", "Loop") : UI.T("Customize.PlayOnce", "Play once"), playback.LoopMode == value, () =>
            {
                _session.EditProfile(o => o with { BannerLoopMode = value, BannerLoop = value == "loop" });
                RenderEditor();
            }));
        }

        var reduced = UI.H(6);
        foreach (var policy in ProfileAppearanceOverrides.ReducedMotionPolicies)
        {
            var value = policy;
            reduced.Children.Add(UI.Chip(policy switch { "poster" => UI.T("Customize.RM.Poster", "Show poster"), "still-frame" => UI.T("Customize.RM.Still", "Hold first frame"), _ => UI.T("Customize.RM.Once", "Play once") }, playback.ReducedMotionPolicy == value, () =>
            {
                _session.EditProfile(o => o with { BannerReducedMotion = value });
                RenderEditor();
            }));
        }

        var mute = new ToggleSwitch { Header = UI.T("Customize.Mute", "Muted"), IsOn = playback.Mute };
        mute.Toggled += (_, _) => _session.EditProfile(o => o with { BannerMute = mute.IsOn });
        return UI.Section(UI.T("Customize.Playback", "Playback window"), UI.T("Customize.PlaybackNote", "Which part of a Banner video plays. The source file is never changed."),
            Slider(UI.T("Customize.Start", "Start (seconds)"), 0, Math.Max(30, overrides.BannerStartPointSeconds + 30), overrides.BannerStartPointSeconds, v => _session.EditProfile(o => o with { BannerStartPointSeconds = v })),
            Slider(UI.T("Customize.Duration", "Length (seconds)"), 1, 60, Math.Max(1, overrides.BannerDurationSeconds), v => _session.EditProfile(o => o with { BannerDurationSeconds = v })),
            Slider(UI.T("Customize.Rate", "Speed"), ProfileAppearanceOverrides.MinimumPlaybackRate, ProfileAppearanceOverrides.MaximumPlaybackRate, playback.Rate, v => _session.EditProfile(o => o with { BannerPlaybackRate = v })),
            loop, mute, UI.Text(UI.T("Customize.ReducedMotion", "With Reduce motion"), "control"), reduced);
    }

    private static Slider Slider(string header, double min, double max, double value, Action<double> changed)
    {
        var slider = new Slider { Header = header, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), StepFrequency = (max - min) / 200 };
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        return slider;
    }

    // ---------------------------------------------------------------- live preview

    private void RenderPreview()
    {
        _preview.Children.Clear();
        if (_category is PacksSection or CustomizationCategories.Library)
        {
            return;
        }

        var theme = ThemeRuntime.Current;
        var frame = new Border { CornerRadius = new CornerRadius(14), BorderBrush = theme.Brush("borderSubtle"), BorderThickness = new Thickness(1), Background = theme.Brush("canvas") };
        var stage = new Grid();
        frame.Child = stage;
        _preview.Children.Add(frame);
        switch (_category)
        {
            case CustomizationCategories.Appearance:
                stage.Children.Add(new BackdropView { Plan = _session.ResolveCompiled(PresentationSlots.HomeBackdrop).PlanAs<BackdropPlan>(), IsSurfaceActive = true, Opacity = 0.6 });
                stage.Children.Add(UI.Surface(UI.V(10,
                    UI.Text(UI.T("Customize.Sample.Title", "The quiet library"), "hero"),
                    UI.Text(UI.T("Customize.Sample.Body", "Body text, metadata and actions update as you choose."), "body"),
                    UI.H(8, UI.Button(UI.T("Customize.Sample.Primary", "Primary"), null, ButtonKind.Primary, "icon.navigation.home"), UI.Button(UI.T("Customize.Sample.Secondary", "Secondary"), null, ButtonKind.Secondary, "icon.action.edit"), UI.Chip("Chip", true), UI.Badge("Badge", "accent"))), Material.Frost, 16, 18).Margin(24));
                break;
            case CustomizationCategories.Home:
                stage.Children.Add(new BackdropView { Plan = _session.ResolveCompiled(PresentationSlots.HomeBackdrop).PlanAs<BackdropPlan>(), IsSurfaceActive = true });
                AddCard(stage, PresentationSlots.SpotlightCover, PresentationSlots.SpotlightBanner, 250, HorizontalAlignment.Left);
                var homeLayout = _session.ResolveCompiled(PresentationSlots.HomeLayout).PlanAs<HomeLayoutPlan>();
                var rails = UI.V(7);
                foreach (var railPlan in homeLayout.Rails.Where(static rail => rail.Visible).Take(3))
                {
                    rails.Children.Add(UI.V(2,
                        UI.Text(railPlan.Id.Replace('-', ' '), "micro", maxLines: 1),
                        UI.H(5,
                            UI.Surface(new IconView("icon.profile.person", 15), Material.Raised, 5, 6),
                            UI.Surface(new IconView("icon.media.image", 15), Material.Raised, 5, 6),
                            UI.Surface(UI.Text("12", "micro"), Material.Raised, 5, 6))));
                }
                stage.Children.Add(UI.Surface(rails, Material.Frost, 10, 10).Margin(292, 18, 18, 38));
                stage.Children.Add(UI.Text(_session.ResolveCompiled(PresentationSlots.HomeSpotlight).Name + " · " + _session.ResolveCompiled(PresentationSlots.HomeLayout).Name, "caption").Margin(16).Align(HorizontalAlignment.Right, VerticalAlignment.Bottom));
                break;
            case CustomizationCategories.Gallery:
                GalleryPreview(stage);
                break;
            case CustomizationCategories.Profile:
                ProfilePreview(stage);
                break;
            case CustomizationCategories.Media:
                MediaPreview(stage);
                break;
        }
    }

    private void GalleryPreview(Grid stage)
    {
        var layout = _session.ResolveCompiled(PresentationSlots.GalleryLayout).PlanAs<GalleryLayoutPlan>();
        var density = GalleryDensityState.Parse(_session.Resolve(PresentationSlots.GalleryDensity).StateJson);
        var definition = _session.ResolveCompiled(PresentationSlots.GalleryCard);
        var data = SampleData(PresentationSlots.CardCover, PresentationSlots.CardBanner);
        if (data is null)
        {
            stage.Children.Add(UI.Text(UI.T("Customize.NoSample", "Add a Profile to see a live preview here."), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return;
        }

        var count = layout.Primitive == CollectionPrimitives.List ? 3 : 5;
        var collection = layout.Primitive == CollectionPrimitives.List ? UI.V(6) : UI.H(Math.Clamp(layout.Spacing / 3, 4, 12));
        collection.HorizontalAlignment = HorizontalAlignment.Center;
        collection.VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < count; i++)
        {
            var visual = new CardVisual(definition);
            visual.Bind(data with
            {
                Name = i == 0 ? data.Name : UI.F("Customize.Sample.Profile", "Profile {0}", i + 1),
                IsFavorite = i % 2 == 0,
                MediaCount = data.MediaCount + i,
            });
            var width = layout.Primitive == CollectionPrimitives.List ? 330 : Math.Clamp(92 * density.CardScale, 72, 125);
            visual.Width = width;
            visual.Height = layout.Primitive == CollectionPrimitives.List ? 58 : width / Math.Max(0.5, layout.ItemAspect ?? visual.Plan.Aspect);
            collection.Children.Add(visual);
        }
        stage.Children.Add(collection);
    }

    private CardData? SampleData(string coverSlot, string bannerSlot)
    {
        var frame = _session.ResolveCompiled(PresentationSlots.ProfileFrame).PlanAs<FramePlan>();

        if (_subject is { } subject && _session.ProfileWorking is { } working && working.ProfileId == subject.Detail.ProfileId
            && _session.ResolveMedia(coverSlot, bannerSlot) is { } subjectMedia)
        {
            return new CardData(
                subject.Detail.ProfileId,
                subject.Detail.DisplayName,
                subject.Detail.CategoryName,
                subject.Detail.Tags.ToList(),
                subject.Detail.Rating,
                null,
                subject.Detail.IsFavorite,
                subject.MediaItems.Count,
                0,
                subject.Detail.Overview,
                _coverSource,
                _bannerSource,
                CardDataFactory.AppearanceFor(working.Overrides, frame, ReducedMotionAuthority.IsReduced),
                subjectMedia.Cover,
                subjectMedia.Banner,
                frame);
        }

        var card = _services.Root?.SampleCard(_session.Context.ProfileId);
        if (card is null)
        {
            return null;
        }

        var data = CardDataFactory.From(card, _services.Presentation, 480, 900);
        if (_session.ProfileWorking is { } profile && profile.ProfileId == data.ProfileId && _session.ResolveMedia(coverSlot, bannerSlot) is { } media)
        {
            data = data with { CoverTransform = media.Cover, BannerTransform = media.Banner };
        }

        return data with { Frame = frame };
    }

    private void AddCard(Grid stage, string coverSlot, string bannerSlot, double width, HorizontalAlignment alignment)
    {
        if (SampleData(coverSlot, bannerSlot) is not { } data)
        {
            stage.Children.Add(UI.Text(UI.T("Customize.NoSample", "Add a Profile to see a live preview here."), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return;
        }

        var visual = new CardVisual(_session.ResolveCompiled(PresentationSlots.GalleryCard));
        visual.Bind(data);
        visual.Width = Math.Min(width, 420);
        visual.Height = visual.Width / Math.Max(0.4, visual.Plan.Aspect);
        visual.HorizontalAlignment = alignment;
        visual.VerticalAlignment = VerticalAlignment.Center;
        visual.Margin = new Thickness(24, 12, 24, 12);
        stage.Children.Add(visual);
    }

    private void ProfilePreview(Grid stage)
    {
        if (_session.ProfileWorking is not { } profile || _session.ResolveMedia(null, null) is not { } media)
        {
            stage.Children.Add(UI.Text(UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."), "body-muted").Align(HorizontalAlignment.Center, VerticalAlignment.Center));
            return;
        }

        var environment = _session.ResolveCompiled(PresentationSlots.ProfileEnvironment).PlanAs<EnvironmentPlan>();
        stage.Children.Add(new BackdropView { Plan = environment.Backdrop, IsSurfaceActive = true });
        stage.Children.Add(new SkImageView { Source = _bannerSource ?? _coverSource, Transform = media.Banner, Height = 170, VerticalAlignment = VerticalAlignment.Top, PlaceholderToken = "surface2" });
        var framePlan = _session.ResolveCompiled(PresentationSlots.ProfileFrame).PlanAs<FramePlan>();
        stage.Children.Add(new CoverFrameView
        {
            Source = _coverSource,
            Transform = media.Cover,
            Plan = framePlan,
            Appearance = CardDataFactory.AppearanceFor(profile.Overrides, framePlan, ReducedMotionAuthority.IsReduced),
            FrameMode = "full",
            Width = 132,
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(28, 0, 0, 18),
        });
        var layout = _session.ResolveCompiled(PresentationSlots.ProfileLayout);
        stage.Children.Add(UI.Surface(UI.V(2, UI.Text(_request?.ProfileDisplayName ?? _subject?.Detail.DisplayName ?? string.Empty, "card-title"), UI.Text(layout.Name + " · " + _session.ResolveCompiled(PresentationSlots.ProfileEnvironment).Name, "caption")), Material.Deep, 12, 12)
            .Margin(176, 0, 16, 24).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom));
    }

    private void MediaPreview(Grid stage)
    {
        var theme = ThemeRuntime.Current;
        var tile = _session.ResolveCompiled(PresentationSlots.MediaTile).PlanAs<MediaTilePlan>();
        var border = _session.ResolveCompiled(PresentationSlots.MediaBorder).PlanAs<MediaBorderPlan>();
        var info = _session.ResolveCompiled(PresentationSlots.MediaInfo).PlanAs<MediaInfoPlan>();
        var row = UI.H(14);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        var source = SampleData(PresentationSlots.CardCover, PresentationSlots.CardBanner);
        for (var i = 0; i < 3; i++)
        {
            var selected = i == 1;
            var image = new SkImageView { Source = i == 2 ? source?.Banner ?? source?.Cover : source?.Cover, Transform = tile.Fit == "fit" ? MediaTransformState.Default with { Fit = "fit" } : MediaTransformState.Default, CornerRadiusValue = Math.Max(0, border.Radius - border.Thickness), PlaceholderToken = "surface2" };
            var content = new Grid { Width = 150, Height = 150 / tile.Aspect };
            content.Children.Add(image);
            if (info.Placement is "overlay-bottom" or "hover")
            {
                content.Children.Add(new Border { Child = UI.Text("IMG_20" + (24 + i) + ".jpg", "micro", "onMediaPrimary", 1), Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Bottom, Background = theme.Brush(tile.Overlay == "glass-strip" ? "glassStrip" : "mediaOverlay") });
            }

            var framed = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(border.Radius),
                BorderThickness = new Thickness(selected ? Math.Max(2, border.Thickness) : border.Thickness),
                BorderBrush = theme.Brush((selected ? border.SelectedColor : border.Color).Replace("token:", string.Empty, StringComparison.Ordinal)),
            };
            row.Children.Add(info.Placement == "below" ? UI.V(4, framed, UI.Text("IMG_20" + (24 + i) + ".jpg", "caption", maxLines: 1)) : framed);
        }

        stage.Children.Add(row);
    }

    // ---------------------------------------------------------------- subject (Profile / media item)

    private sealed record ProfileChoice(Guid ProfileId, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ItemChoice(Guid? AssetId, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record CustomizationSubjectSnapshot(
        ProfilePresentationState State,
        ProfileDetailReadModel Detail,
        IReadOnlyList<ProfileMediaItemReadModel> MediaItems,
        AppearanceCustomizationOverlayRequest Request);

    private void RenderSubjectBar()
    {
        _subjectBar.Children.Clear();
        var slots = SlotsOf(_category).ToList();
        var usesProfile = slots.Any(s => s.AllowsScope(ScopeKind.Profile));
        var usesItem = slots.Any(s => s.AllowsScope(ScopeKind.Item));
        if (_category is PacksSection or CustomizationCategories.Library || (!usesProfile && !usesItem))
        {
            _subjectBar.Visibility = Visibility.Collapsed;
            return;
        }

        _subjectBar.Visibility = Visibility.Visible;
        var search = new AutoSuggestBox { PlaceholderText = UI.T("Customize.ChooseProfile", "Choose a Profile to customize…"), MinWidth = 240, MaxWidth = 420 };
        search.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            Run(SearchProfilesAsync(search, search.Text), "CustomizationCenter.SearchProfilesAsync", UI.T("Customize.SearchFailed", "Profiles could not be searched."));
        };
        search.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is ProfileChoice choice)
            {
                Run(SelectProfileAsync(choice.ProfileId), "CustomizationCenter.SelectProfileAsync", UI.T("Customize.SubjectLoadFailed", "This Profile could not be loaded for customization."));
            }
        };

        var current = _request?.ProfileDisplayName is { } name
            ? UI.Chip(name, true)
            : UI.Chip(UI.T("Customize.Everyone", "No Profile · everywhere"), true);
        var row = new VariableWrap(10,
            UI.Text(UI.T("Customize.Subject", "Subject"), "control").Align(vertical: VerticalAlignment.Center),
            current,
            search);
        if (_session.Context.ProfileId is not null)
        {
            row.Children.Add(UI.Button(UI.T("Customize.ClearSubject", "Stop editing this Profile"), () => Run(ClearSubjectAsync(), "CustomizationCenter.ClearSubjectAsync", UI.T("Customize.SubjectChangeFailed", "The customization subject could not be changed.")), ButtonKind.Ghost));
        }

        _subjectBar.Children.Add(row);

        if (usesItem && _session.Context.ProfileId is not null)
        {
            if (_subject is { } subject)
            {
                var items = new List<ItemChoice> { new(null, UI.T("Customize.AllMedia", "All media of this Profile")) };
                items.AddRange(subject.MediaItems.Take(SubjectMediaLimit).Select(m => new ItemChoice(m.AssetId, m.ManagedFileName ?? m.AssetId.ToString())));
                var picker = new ComboBox { ItemsSource = items, MinWidth = 280, Header = UI.T("Customize.MediaItem", "Media item") };
                picker.SelectedIndex = Math.Max(0, items.FindIndex(i => i.AssetId == _session.Context.ItemId));
                picker.SelectionChanged += (_, _) =>
                {
                    if (picker.SelectedItem is ItemChoice choice && choice.AssetId != _session.Context.ItemId)
                    {
                        Run(SelectItemAsync(choice.AssetId), "CustomizationCenter.SelectItemAsync", UI.T("Customize.SubjectChangeFailed", "The media customization subject could not be changed."));
                    }
                };
                _subjectBar.Children.Add(picker);
            }
            else
            {
                _subjectBar.Children.Add(UI.Text(UI.T("Customize.LoadingSubject", "Loading this Profile's media…"), "caption"));
            }
        }
    }

    private async Task SearchProfilesAsync(AutoSuggestBox search, string text)
    {
        _subjectSearchCts?.Cancel();
        _subjectSearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _subjectSearchCts = cts;
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(true);
            var normalized = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            var page = await _services.Catalog.GalleryReads.GetGalleryPageAsync(new GalleryQuery(
                SearchText: normalized,
                PageSize: GalleryPageSizes.Small), cts.Token).ConfigureAwait(true);
            if (!_closed && !cts.IsCancellationRequested && ReferenceEquals(_subjectSearchCts, cts) && search.Text == text)
            {
                search.ItemsSource = page.Items.Select(item => new ProfileChoice(item.ProfileId, item.DisplayName)).ToList();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ConfirmDiscardAsync() =>
        !_session.HasChanges || await _services.ConfirmAsync(
            UI.T("Customize.DiscardTitle", "Discard changes?"),
            UI.T("Customize.DiscardBody", "Switching what you are customizing discards the changes you are previewing."),
            UI.T("Customize.Discard", "Discard"),
            destructive: true).ConfigureAwait(true);

    private async Task SelectProfileAsync(Guid profileId)
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var generation = ++_subjectGeneration;
        var snapshot = await LoadSubjectSnapshotAsync(profileId, generation).ConfigureAwait(true);
        if (snapshot is null || generation != _subjectGeneration || _closed)
        {
            return;
        }

        _subject = snapshot;
        Rebind(PresentationContext.ForProfile(snapshot.State, _session.Context.Surface ?? "profile"), snapshot.Request);
    }

    private async Task SelectItemAsync(Guid? assetId)
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            RenderSubjectBar();
            return;
        }

        if (_subject is not { } subject || _session.Context.ProfileId != subject.State.ProfileId)
        {
            return;
        }

        var context = PresentationContext.ForProfile(subject.State, assetId is null ? _session.Context.Surface ?? "profile" : "media") with { ItemId = assetId };
        Rebind(context, _request ?? subject.Request);
    }

    private async Task ClearSubjectAsync()
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        ++_subjectGeneration;
        CancelSubjectLoad();
        _subject = null;
        Rebind(new PresentationContext(_session.Context.Surface), null);
    }

    /// <summary>Starts a fresh preview for another subject. The previous preview is discarded, never applied.</summary>
    private void Rebind(PresentationContext context, AppearanceCustomizationOverlayRequest? request)
    {
        _session.Changed -= OnSessionChanged;
        if (!_session.IsClosed)
        {
            _session.Cancel();
        }

        if (_appearanceTouched)
        {
            PresentationBinder.ApplyGlobal(_services.Presentation);
            _appearanceTouched = false;
        }

        _session = _services.Presentation.BeginPreview(context);
        _session.Changed += OnSessionChanged;
        _request = request;
        _coverSource = _committedCoverSource = request?.PreviewCoverSource;
        _bannerSource = _committedBannerSource = request?.PreviewBannerStillSource;
        _subjectText.Text = SubjectLine();
        ShowCategory(_category, _slot);
    }

    private async Task LoadSubjectAsync(Guid profileId, int generation, bool preserveRequest)
    {
        var snapshot = await LoadSubjectSnapshotAsync(profileId, generation).ConfigureAwait(true);
        if (snapshot is null || generation != _subjectGeneration || _closed || _session.Context.ProfileId != profileId)
        {
            return;
        }

        _subject = snapshot;
        if (!preserveRequest)
        {
            _request = snapshot.Request;
            _coverSource = _committedCoverSource = snapshot.Request.PreviewCoverSource;
            _bannerSource = _committedBannerSource = snapshot.Request.PreviewBannerStillSource;
            _subjectText.Text = SubjectLine();
        }
        RenderSubjectBar();
        RenderPreview();
    }

    private async Task<CustomizationSubjectSnapshot?> LoadSubjectSnapshotAsync(Guid profileId, int generation)
    {
        CancelSubjectLoad();
        var cts = new CancellationTokenSource();
        _subjectLoadCts = cts;
        var cancellationToken = cts.Token;
        try
        {
            var detailTask = _services.Catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken);
            var mediaTask = _services.Catalog.ProfileReads.GetMediaPageAsync(profileId, pageSize: SubjectMediaLimit, cancellationToken: cancellationToken);
            await Task.WhenAll(detailTask, mediaTask).ConfigureAwait(true);
            if (generation != _subjectGeneration || cancellationToken.IsCancellationRequested || _closed)
            {
                return null;
            }

            var detail = await detailTask.ConfigureAwait(true);
            if (detail is null)
            {
                _services.Toast(UI.T("Customize.ProfileMissing", "That Profile is no longer available."), "warning");
                return null;
            }

            var mediaPage = await mediaTask.ConfigureAwait(true);
            var overrides = ProfileAppearanceOverrides.Parse(detail.AppearanceOverridesJson);
            var state = new ProfilePresentationState(
                detail.ProfileId,
                detail.RowVersion,
                detail.LayoutPresetId,
                overrides,
                new ProfileMediaSources(detail.CoverAssetId, detail.BannerAssetId));

            IReadOnlyDictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>> evidence;
            if (mediaPage.Items.Count == 0)
            {
                evidence = new Dictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>();
            }
            else
            {
                evidence = await _services.Catalog.FaceReads.GetAppearanceFaceEvidenceAsync(
                    [.. mediaPage.Items.Select(static item => item.AssetId)],
                    profileId,
                    cancellationToken).ConfigureAwait(true);
            }

            var evaluationInputs = mediaPage.Items.Select(item =>
            {
                var faces = evidence.GetValueOrDefault(item.AssetId, []);
                return new AppearanceCandidateEvaluationInput(
                    item.AssetId,
                    item.MediaType,
                    item.ManagedFileName ?? item.AssetId.ToString(),
                    item.PixelWidth,
                    item.PixelHeight,
                    item.DurationMs,
                    HasDerivedPreview: !string.IsNullOrEmpty(HomeViewModel.ResolveDerivedPresentationPath(_services.Paths, item.AssetId, preferBanner: false)),
                    HasConfirmedFaceForProfile: faces.Any(static face => face.IsConfirmedForTargetProfile),
                    HasFaceDetection: faces.Count > 0,
                    item.CreatedAtUtc.ToUnixTimeMilliseconds(),
                    IsExistingCover: item.AssetId == detail.CoverAssetId,
                    IsExistingBanner: item.AssetId == detail.BannerAssetId,
                    FaceEvidence: faces);
            }).ToList();
            var evaluated = ProfileAppearanceRules.EvaluateVisualCandidates(evaluationInputs);
            var namesByAsset = mediaPage.Items.ToDictionary(
                static item => item.AssetId,
                item => item.ManagedFileName ?? item.AssetId.ToString());

            async Task<string?> CandidatePreviewAsync(Guid assetId, long? timestampMilliseconds, bool preferBanner)
            {
                if (timestampMilliseconds is not { } timestamp)
                {
                    return HomeViewModel.ResolveDerivedPresentationPath(_services.Paths, assetId, preferBanner);
                }

                var request = new DerivedStillRequest(assetId, timestamp);
                var fingerprint = mediaPage.Items.FirstOrDefault(item => item.AssetId == assetId)?.ContentFingerprint;
                var published = _services.Runtime.DerivedStills.TryResolvePublishedPath(request, fingerprint);
                if (published is not null)
                {
                    return published;
                }

                return await _services.Runtime.DerivedStills.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(true);
            }

            var coverCandidates = new List<AppearanceCoverCandidate>(evaluated.Covers.Count);
            foreach (var candidate in evaluated.Covers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previewPath = await CandidatePreviewAsync(
                    candidate.SourceAssetId,
                    candidate.TimestampMilliseconds,
                    preferBanner: false).ConfigureAwait(true);
                coverCandidates.Add(new AppearanceCoverCandidate(
                    candidate.CandidateId,
                    candidate.SourceAssetId,
                    candidate.SourceMediaType,
                    candidate.SourceKind,
                    candidate.TimestampMilliseconds,
                    candidate.SuggestedCropX,
                    candidate.SuggestedCropY,
                    namesByAsset.GetValueOrDefault(candidate.SourceAssetId, candidate.DisplayName),
                    previewPath,
                    candidate.Rank,
                    candidate.IsRecommended,
                    candidate.Reason));
            }

            var bannerCandidates = new List<AppearanceBannerCandidate>(evaluated.Banners.Count);
            foreach (var candidate in evaluated.Banners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previewPath = await CandidatePreviewAsync(
                    candidate.SourceAssetId,
                    timestampMilliseconds: candidate.FrameTimestampMilliseconds,
                    preferBanner: true).ConfigureAwait(true);
                bannerCandidates.Add(new AppearanceBannerCandidate(
                    candidate.CandidateId,
                    candidate.SourceAssetId,
                    candidate.SourceMediaType,
                    candidate.SourceKind,
                    candidate.FrameTimestampMilliseconds,
                    candidate.StartPointSeconds,
                    candidate.DurationSeconds,
                    candidate.FocusX,
                    candidate.FocusY,
                    namesByAsset.GetValueOrDefault(candidate.SourceAssetId, candidate.DisplayName),
                    previewPath,
                    candidate.Rank,
                    candidate.IsRecommended,
                    candidate.Reason));
            }

            var coverPath = HomeViewModel.ResolveDerivedPresentationPath(_services.Paths, detail.CoverAssetId, preferBanner: false);
            var bannerPath = HomeViewModel.ResolveDerivedPresentationPath(_services.Paths, detail.BannerAssetId, preferBanner: true);
            var requestModel = new AppearanceCustomizationOverlayRequest(
                profileId: detail.ProfileId,
                profileDisplayName: detail.DisplayName,
                currentPresetId: detail.LayoutPresetId,
                currentCardVariantId: overrides.GalleryCardVariantId,
                currentShape: overrides.CoverShape,
                currentFrameId: overrides.CoverFrameId,
                coverCandidates: coverCandidates,
                bannerCandidates: bannerCandidates,
                currentCoverAssetId: detail.CoverAssetId,
                currentCoverSourceKind: overrides.ResolvedCoverSourceKind,
                currentCoverTimestampMilliseconds: overrides.CoverVideoTimestampMilliseconds,
                currentCoverZoom: overrides.Zoom,
                currentCoverCropX: overrides.CropX,
                currentCoverCropY: overrides.CropY,
                currentFrameScale: overrides.CoverFrameScale,
                currentFrameTint: overrides.CoverFrameTint,
                currentFrameIntensity: overrides.CoverFrameIntensity,
                currentFrameAnimation: overrides.CoverFrameAnimation,
                currentCoverShadow: overrides.CoverShadow,
                currentBannerAssetId: detail.BannerAssetId,
                currentBannerSourceKind: overrides.ResolvedBannerSourceKind,
                currentBannerVideoFrameTimestampMilliseconds: overrides.BannerVideoFrameTimestampMilliseconds,
                currentBannerStartSeconds: overrides.BannerStartPointSeconds,
                currentBannerDurationSeconds: overrides.BannerDurationSeconds,
                currentBannerFocusX: overrides.BannerFocusX,
                currentBannerFocusY: overrides.BannerFocusY,
                currentBannerZoom: overrides.BannerZoom,
                currentBannerLoop: overrides.BannerLoop,
                reduceMotion: ReducedMotionAuthority.IsReduced)
            {
                PreviewModel = new ProfilePresentationModel(
                    detail.ProfileId,
                    detail.DisplayName,
                    detail.CategoryName,
                    detail.Tags,
                    detail.Rating,
                    detail.IsFavorite,
                    detail.Overview,
                    detail.Notes,
                    detail.ActiveOwnedAssetCount,
                    false,
                    overrides.GalleryCardVariantId),
                PreviewCoverSource = ImageRef.FromPath(coverPath, 900),
                PreviewBannerStillSource = ImageRef.FromPath(bannerPath, 1600),
            };

            return new CustomizationSubjectSnapshot(state, detail, mediaPage.Items, requestModel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_subjectLoadCts, cts))
            {
                _subjectLoadCts = null;
            }
            cts.Dispose();
        }
    }

    private void CancelSubjectLoad()
    {
        var cts = _subjectLoadCts;
        _subjectLoadCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    // ---------------------------------------------------------------- packs

    private void RenderPacks()
    {
        var list = UI.V(10);
        foreach (var pack in _services.Presentation.Packs)
        {
            var manifest = pack.Manifest;
            var usages = pack.Origin == PackOrigin.User ? _services.Presentation.CountUsages(manifest.PackId) : 0;
            var errors = pack.Diagnostics.Count(d => d.IsError);
            var details = UI.V(2,
                UI.H(8, UI.Text(manifest.Name, "card-title"), UI.Badge(pack.Origin == PackOrigin.BuiltIn ? UI.T("Customize.BuiltIn", "Built-in") : UI.T("Customize.Installed", "Installed")), errors > 0 ? UI.Badge(UI.F("Customize.Errors", "{0} problems", errors), "danger") : null),
                UI.Text($"{manifest.PackId} · v{manifest.Version}{(manifest.Author is { } author ? " · " + author : string.Empty)}", "mono"),
                UI.Text(UI.F("Customize.PackCounts", "{0} definitions · {1} assets · used in {2} places", manifest.Definitions.Count, manifest.Assets.Count, usages), "caption"),
                manifest.Description is { Length: > 0 } description ? UI.Text(description, "body-muted", maxLines: 3) : null);
            var packId = manifest.PackId;
            var actions = UI.H(6,
                pack.Origin == PackOrigin.User ? UI.Button(UI.T("Customize.Export", "Export"), () => Run(ExportAsync(packId), "CustomizationCenter.ExportAsync", UI.T("Customize.ExportFailed", "The presentation pack could not be exported.")), ButtonKind.Secondary) : null,
                pack.Origin == PackOrigin.User ? UI.Button(UI.T("Customize.Remove", "Remove"), () => Run(RemoveAsync(packId, manifest.Name, usages), "CustomizationCenter.RemoveAsync", UI.T("Customize.RemoveFailed", "The presentation pack could not be removed.")), ButtonKind.Destructive) : null);
            var problems = UI.V(2);
            foreach (var diagnostic in pack.Diagnostics.Take(6))
            {
                problems.Children.Add(UI.Text($"{diagnostic.Code} · {diagnostic.Subject}: {diagnostic.Detail}", "caption", diagnostic.IsError ? "danger" : "warning", 2));
            }

            list.Children.Add(UI.Surface(UI.V(8, UI.Grid("auto", "*,auto", details.At(0, 0), actions.At(0, 1)), problems), Material.Raised, 12, 14));
        }

        _editor.Children.Add(UI.Section(UI.T("Customize.Packs", "Presentation packs"),
            UI.T("Customize.PacksDesc", "Packs add themes, backdrops, cards, layouts, frames and media styles. They contain data and art only — never code — and are checked before they are installed. Pack files are kept outside your library and are never imported as media."),
            UI.H(8,
                UI.Button(UI.T("Customize.InstallFile", "Install from file…"), () => Run(InstallAsync(folder: false), "CustomizationCenter.InstallFileAsync", UI.T("Customize.InstallFailed", "This pack could not be installed.")), ButtonKind.Primary, "icon.action.add"),
                UI.Button(UI.T("Customize.InstallFolder", "Install from folder…"), () => Run(InstallAsync(folder: true), "CustomizationCenter.InstallFolderAsync", UI.T("Customize.InstallFailed", "This pack could not be installed.")), ButtonKind.Secondary)),
            list));
    }

    private async Task InstallAsync(bool folder)
    {
        string? source = folder
            ? await _services.PickFolderAsync().ConfigureAwait(true)
            : (await _services.PickFilesAsync(false, ".zip", ".ntpack").ConfigureAwait(true)).FirstOrDefault();
        if (source is null)
        {
            return;
        }

        var result = await _services.Presentation.InstallPackAsync(source, replaceExisting: false).ConfigureAwait(true);
        if (!result.Succeeded && result.Diagnostics.Any(d => d.Code == PackDiagnosticCodes.AlreadyInstalled)
            && await _services.ConfirmAsync(UI.T("Customize.ReplaceTitle", "Replace pack?"), UI.T("Customize.ReplaceBody", "A pack with this id is already installed. Replace it with this version?"), UI.T("Customize.Replace", "Replace")).ConfigureAwait(true))
        {
            result = await _services.Presentation.InstallPackAsync(source, replaceExisting: true).ConfigureAwait(true);
        }

        if (result.Succeeded)
        {
            _services.Toast(UI.F("Customize.Installed", "Installed {0}", result.Pack?.Manifest.Name ?? string.Empty), "success");
        }
        else
        {
            var reason = result.Message ?? string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.IsError).Take(4).Select(d => d.Detail));
            await _services.ConfirmAsync(UI.T("Customize.InstallFailed", "This pack can't be installed"), reason, UI.T("Common.Ok", "OK")).ConfigureAwait(true);
        }

        ShowCategory(PacksSection, null);
    }

    private async Task ExportAsync(string packId)
    {
        var destination = await _services.PickSaveFileAsync(packId, ".zip", UI.T("Customize.PackFile", "Presentation pack")).ConfigureAwait(true);
        if (destination is null)
        {
            return;
        }

        try
        {
            await _services.Presentation.ExportPackAsync(packId, destination).ConfigureAwait(true);
            _services.Toast(UI.T("Customize.Exported", "Pack exported"), "success");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Toast(exception.Message, "danger");
        }
    }

    private async Task RemoveAsync(string packId, string name, int usages)
    {
        var message = usages > 0
            ? UI.F("Customize.RemoveUsed", "{0} is used in {1} places. Those places will go back to their inherited look. Your Profiles and media are not affected.", name, usages)
            : UI.F("Customize.RemoveBody", "Remove {0}? Your Profiles and media are not affected.", name);
        if (!await _services.ConfirmAsync(UI.T("Customize.RemoveTitle", "Remove pack?"), message, UI.T("Customize.Remove", "Remove"), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var result = await _services.Presentation.RemovePackAsync(packId).ConfigureAwait(true);
        _services.Toast(result.Removed ? UI.T("Customize.Removed", "Pack removed") : result.Message ?? string.Empty, result.Removed ? "success" : "danger");
        PresentationBinder.ApplyGlobal(_services.Presentation, _session.Working);
        ShowCategory(PacksSection, null);
    }

    // ---------------------------------------------------------------- library structure

    private SettingsViewModel Library()
    {
        if (_library is null)
        {
            _library = (SettingsViewModel)_services.Factory.Create(new SettingsRoute(SettingsSection.Organization));
            _library.Categories.CollectionChanged += (_, _) => UiDispatch.Run(RefreshLibrary);
            _library.Tags.CollectionChanged += (_, _) => UiDispatch.Run(RefreshLibrary);
        }

        return _library;
    }

    private void RefreshLibrary()
    {
        if (_category == CustomizationCategories.Library && !_closed)
        {
            _editor.Children.Clear();
            RenderLibrary();
        }
    }

    private void RenderLibrary()
    {
        var vm = Library();
        var categories = UI.V(6);
        foreach (var item in vm.Categories)
        {
            var category = item;
            categories.Children.Add(UI.Grid("auto", "*,auto",
                UI.V(0, UI.Text(category.Name, "body-strong"), UI.Text(UI.F("Customize.UsedBy", "{0} Profiles", category.UsageCount), "caption")).At(0, 0),
                UI.H(4,
                    UI.Button(UI.T("Common.Rename", "Rename"), () => Run(RenameAsync(category.Name, name => vm.RenameCategoryAsync(category, name)), "CustomizationCenter.RenameCategoryAsync", UI.T("Customize.RenameFailed", "The category could not be renamed.")), ButtonKind.Ghost),
                    UI.Button(UI.T("Common.Delete", "Delete"), () => Run(DeleteAsync(category.Name, category.UsageCount, () => vm.DeleteCategoryAsync(category)), "CustomizationCenter.DeleteCategoryAsync", UI.T("Customize.DeleteFailed", "The category could not be deleted.")), ButtonKind.Ghost)).At(0, 1)));
        }

        var tags = UI.V(6);
        foreach (var item in vm.Tags)
        {
            var tag = item;
            tags.Children.Add(UI.Grid("auto", "*,auto",
                UI.V(0, UI.Text(tag.Name, "body-strong"), UI.Text(UI.F("Customize.UsedBy", "{0} Profiles", tag.UsageCount), "caption")).At(0, 0),
                UI.H(4,
                    UI.Button(UI.T("Common.Rename", "Rename"), () => Run(RenameAsync(tag.Name, name => vm.RenameTagAsync(tag, name)), "CustomizationCenter.RenameTagAsync", UI.T("Customize.RenameFailed", "The tag could not be renamed.")), ButtonKind.Ghost),
                    UI.Button(UI.T("Customize.Merge", "Merge into…"), () => Run(MergeAsync(tag), "CustomizationCenter.MergeTagAsync", UI.T("Customize.MergeFailed", "The tag could not be merged.")), ButtonKind.Ghost),
                    UI.Button(UI.T("Common.Delete", "Delete"), () => Run(DeleteAsync(tag.Name, tag.UsageCount, () => vm.DeleteTagAsync(tag)), "CustomizationCenter.DeleteTagAsync", UI.T("Customize.DeleteFailed", "The tag could not be deleted.")), ButtonKind.Ghost)).At(0, 1)));
        }

        var newCategory = UI.Input(UI.T("Customize.NewCategory", "New category"), vm.NewCategoryName, text => vm.NewCategoryName = text);
        var newTag = UI.Input(UI.T("Customize.NewTag", "New tag"), vm.NewTagName, text => vm.NewTagName = text);
        _editor.Children.Add(UI.Text(UI.T("Customize.LibraryNote", "Categories and tags keep stable ids, so renaming never breaks a Profile. Deleting removes only the label — never a Profile or its media."), "body-muted"));
        _editor.Children.Add(UI.Section(UI.T("Customize.Categories", "Categories"), null,
            UI.Grid("auto", "*,auto", newCategory.At(0, 0), UI.Button(UI.T("Common.Add", "Add"), () => Run(vm.CreateCategoryAsync(), "CustomizationCenter.CreateCategoryAsync", UI.T("Customize.CreateFailed", "The category could not be created.")), ButtonKind.Primary).Margin(8, 0, 0, 0).At(0, 1)), categories));
        _editor.Children.Add(UI.Section(UI.T("Customize.Tags", "Tags"), null,
            UI.Grid("auto", "*,auto", newTag.At(0, 0), UI.Button(UI.T("Common.Add", "Add"), () => Run(vm.CreateTagAsync(), "CustomizationCenter.CreateTagAsync", UI.T("Customize.CreateFailed", "The tag could not be created.")), ButtonKind.Primary).Margin(8, 0, 0, 0).At(0, 1)), tags));
    }

    private async Task RenameAsync(string current, Func<string, Task> rename)
    {
        var name = await _services.Root!.Dialogs.PromptAsync(UI.T("Common.Rename", "Rename"), UI.T("Customize.Name", "Name"), current, UI.T("Common.Save", "Save")).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(name) && name != current)
        {
            await rename(name.Trim()).ConfigureAwait(true);
            RefreshLibrary();
        }
    }

    private async Task DeleteAsync(string name, int usages, Func<Task> delete)
    {
        var message = usages > 0
            ? UI.F("Customize.DeleteUsed", "{0} is used by {1} Profiles. They keep all their media; only this label is removed from them.", name, usages)
            : UI.F("Customize.DeleteUnused", "Delete {0}?", name);
        if (await _services.ConfirmAsync(UI.T("Customize.DeleteTitle", "Delete label?"), message, UI.T("Common.Delete", "Delete"), destructive: true).ConfigureAwait(true))
        {
            await delete().ConfigureAwait(true);
            RefreshLibrary();
        }
    }

    private async Task MergeAsync(TagItemViewModel source)
    {
        var vm = Library();
        var targets = vm.Tags.Where(t => t.TagId != source.TagId).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var picker = new ComboBox { ItemsSource = targets.Select(t => t.Name).ToList(), SelectedIndex = 0, MinWidth = 260 };
        var merge = UI.Button(UI.T("Customize.Merge", "Merge"), null, ButtonKind.Primary);
        var cancel = UI.Button(UI.T("Customize.Cancel", "Cancel"), null, ButtonKind.Ghost);
        var dialogs = _services.Root!.Dialogs;
        var dialog = dialogs.Show(UI.V(12,
            UI.Text(UI.F("Customize.MergeTitle", "Merge {0} into…", source.Name), "section-title"),
            UI.Text(UI.T("Customize.MergeNote", "Every Profile tagged with this tag gets the chosen tag instead. The merged tag is then removed."), "body-muted"),
            picker, UI.H(8, cancel, merge).Align(HorizontalAlignment.Right)), 460);
        var completion = new TaskCompletionSource<TagItemViewModel?>();
        merge.Click += (_, _) => dialogs.Close(dialog, () => completion.TrySetResult(picker.SelectedIndex >= 0 ? targets[picker.SelectedIndex] : null));
        cancel.Click += (_, _) => dialogs.Close(dialog, () => completion.TrySetResult(null));
        if (await completion.Task.ConfigureAwait(true) is not { } target)
        {
            return;
        }

        try
        {
            var moved = await _services.Catalog.SettingsWrites.MergeTagAsync(source.TagId, target.TagId).ConfigureAwait(true);
            _services.Toast(UI.F("Customize.Merged", "Merged into {0} · {1} Profiles updated", target.Name, moved), "success");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _services.Toast(exception.Message, "danger");
        }

        (_library as IDisposable)?.Dispose();
        _library = null;
        RefreshLibrary();
    }

    // ---------------------------------------------------------------- apply / cancel

    private async Task ApplyAsync()
    {
        if (_session.IsClosed || !_session.HasChanges)
        {
            Close(apply: false);
            return;
        }

        _apply.IsEnabled = false;
        _status.Text = UI.T("Customize.Applying", "Applying…");
        try
        {
            var result = await _session.ApplyAsync().ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _status.Text = result.Message ?? UI.T("Customize.ApplyFailed", "Could not apply. Nothing was changed.");
                _apply.IsEnabled = !_session.IsClosed && _session.HasChanges;
                return;
            }

            _appearanceTouched = false;
            PresentationBinder.ApplyGlobal(_services.Presentation);
            _services.Toast(UI.T("Customize.Applied", "Customization applied"), "success");
            Finish();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            _status.Text = UI.F("Customize.ApplyFailedDetail", "Could not apply. {0}", OperationExecution.SafeMessage(exception));
            _apply.IsEnabled = !_session.IsClosed && _session.HasChanges;
        }
    }

    /// <summary>Closes the Center. Apply commits the preview; otherwise the preview is discarded.</summary>
    public void Close(bool apply)
    {
        if (_closed)
        {
            return;
        }

        if (apply)
        {
            Run(ApplyAsync(), "CustomizationCenter.ApplyAsync", UI.T("Customize.ApplyFailed", "Could not apply customization."));
            return;
        }

        if (!_session.IsClosed)
        {
            _session.Cancel();
        }

        if (_appearanceTouched)
        {
            PresentationBinder.ApplyGlobal(_services.Presentation);
            _appearanceTouched = false;
        }

        Finish();
    }

    private void Finish()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CancelSubjectLoad();
        _subjectSearchCts?.Cancel();
        _subjectSearchCts?.Dispose();
        _subjectSearchCts = null;
        _subject = null;
        _onClosed();
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        if (!_closed)
        {
            Close(apply: false);
        }

        (_library as IDisposable)?.Dispose();
        _library = null;
        CancelSubjectLoad();
        _subjectSearchCts?.Cancel();
        _subjectSearchCts?.Dispose();
        _subjectSearchCts = null;
        _subject = null;
    }
}
