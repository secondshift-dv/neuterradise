using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Windows.Media.Core;

namespace Neuterradise.App.Ui;

/// <summary>
/// Home: cinematic lobby with a dominant spotlight, integrated library pulse, meaningful content rails,
/// and purposeful empty states. Uses the existing BackdropView, presentation system, and theme tokens
/// throughout — no private design system.
/// </summary>
public sealed class HomeSurface : Surface
{
    private readonly HomeViewModel _vm;
    private readonly Grid _root = new();
    private readonly BackdropView _backdrop = new();
    private readonly Grid _hero = new();
    private readonly StackPanel _rails = new() { Spacing = 0 };
    private readonly TextBlock _notice = UI.Text(string.Empty, "body", "danger", 3);
    private readonly SkImageView _heroImage = new() { PlaceholderToken = "surface1" };
    private readonly CompositeTransform _heroMotion = new();
    private readonly MediaPlayerElement _heroVideo = new() { Stretch = Stretch.UniformToFill, AreTransportControlsEnabled = false, AutoPlay = false, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Grid _identityHost = new();
    private readonly TextBlock _counter = UI.Text(string.Empty, "numeric", "onHeroSecondary");
    private readonly Border _progressFill = new() { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly DispatcherTimer _rotation = new();
    private readonly Dictionary<Guid, ProfileAppearanceOverrides> _appearance = [];
    private Storyboard? _kenBurns;
    private Storyboard? _progress;
    private HomeLayoutPlan _layout = null!;
    private SpotlightPlan _spotlight = null!;
    private bool _pointerOverHero;
    private bool _railRenderQueued;
    private bool _retired;

    public HomeSurface(AppServices services, HomeViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _root.Children.Add(_backdrop);
        _heroImage.RenderTransform = _heroMotion;
        _heroImage.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        _notice.Visibility = Visibility.Collapsed;
        var page = UI.V(0, _hero, _notice.Margin(24, 16, 24, 0), _rails.Margin(0, 0, 0, 48));
        var scroll = UI.Scroll(page);
        scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.StopAll();
        _root.Children.Add(scroll);
        _root.PointerMoved += (_, args) =>
        {
            if (_root.ActualWidth > 0)
            {
                var point = args.GetCurrentPoint(_root).Position;
                _backdrop.SetPointer(point.X / _root.ActualWidth, point.Y / Math.Max(1, _root.ActualHeight));
            }
        };
        _root.SizeChanged += (_, _) => LayoutHero();
        _hero.PointerEntered += (_, _) => _pointerOverHero = true;
        _hero.PointerExited += (_, _) => _pointerOverHero = false;
        _rotation.Tick += (_, _) =>
        {
            if (IsActive && !_pointerOverHero && !ReducedMotionAuthority.IsReduced && _vm.HasMultipleSpotlightCandidates)
            {
                _vm.NextSpotlightCommand.Execute(null);
            }
        };

        ResolvePlans();
        Bag.Add(Observe.Props(_vm, OnSpotlightChanged, nameof(HomeViewModel.CurrentSpotlight), nameof(HomeViewModel.SpotlightIndex), nameof(HomeViewModel.IsLibraryEmpty)));
        Bag.Add(Observe.Props(_vm, UpdateNotice, nameof(HomeViewModel.Status), nameof(HomeViewModel.ErrorMessage)));
        Bag.Add(Observe.Collection(_vm.SpotlightCandidates, () => { OnSpotlightChanged(); QueueRebuildRails(); }));
        Bag.Add(Observe.Collection(_vm.RecentlyActive, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.NeedsAttention, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.RecentActivity, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.Discovery, QueueRebuildRails));
        Bag.Add(Observe.Collection(_vm.ActiveImports, QueueRebuildRails));
        Bag.Add(Observe.Props(_vm, QueueRebuildRails, nameof(HomeViewModel.VaultPulse)));
        UpdateNotice();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    private PresentationContext Context => new("home");

    private void UpdateNotice()
    {
        _notice.Text = _vm.HasError ? _vm.ErrorMessage ?? string.Empty : string.Empty;
        _notice.Visibility = string.IsNullOrWhiteSpace(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResolvePlans()
    {
        var presentation = Services.Presentation;
        _layout = presentation.ResolvePlan<HomeLayoutPlan>(PresentationSlots.HomeLayout, Context);
        _spotlight = presentation.ResolvePlan<SpotlightPlan>(PresentationSlots.HomeSpotlight, Context);
        _backdrop.Plan = presentation.ResolvePlan<BackdropPlan>(PresentationSlots.HomeBackdrop, Context);
        _rotation.Interval = TimeSpan.FromSeconds(_spotlight.RotationSeconds);
        BuildHero();
        RebuildRails();
    }

    protected override void OnActivated(AppRoute route)
    {
        _backdrop.IsSurfaceActive = true;
        _rotation.Start();
        StartSpotlightMotion();
        Services.RunUserAction(
            Task.WhenAll(_vm.RefreshAsync(), LoadCollectionsAsync()),
            "HomeSurface.ActivateAsync",
            UI.T("Home.RefreshFailed", "Home could not be refreshed."));
    }

    protected override void OnSuspended()
    {
        _backdrop.IsSurfaceActive = false;
        _rotation.Stop();
        _kenBurns?.Stop();
        _progress?.Stop();
        StopHeroVideo();
    }

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("home.", StringComparison.Ordinal) || s.StartsWith("appearance.", StringComparison.Ordinal) || s.StartsWith("surface.spotlight", StringComparison.Ordinal)))
        {
            _appearance.Clear();
            ResolvePlans();
            OnSpotlightChanged();
        }
    }

    // ─────────────────────────────────────────── hero

    private void BuildHero()
    {
        _hero.Children.Clear();
        var inset = _layout.HeroPlacement == "inset";
        var art = new Grid();
        art.Children.Add(_heroImage);
        art.Children.Add(_heroVideo);
        var clip = new Border
        {
            Child = art,
            CornerRadius = new CornerRadius(inset ? ThemeRuntime.Current.Tokens.Number("radiusHero", 20) : 0),
            Margin = inset ? new Thickness(24, 16, 24, 0) : new Thickness(0),
        };

        if (_spotlight.Composition == "split-glass")
        {
            clip.HorizontalAlignment = HorizontalAlignment.Left;
        }

        _hero.Children.Add(clip);
        _hero.Children.Add(Scrim());
        _hero.Children.Add(_identityHost);
        _hero.Children.Add(Pagination());
        LayoutHero();
        OnSpotlightChanged();
    }

    private UIElement Scrim()
    {
        var theme = ThemeRuntime.Current;
        var strength = _spotlight.ScrimStrength;
        var grid = new Grid { IsHitTestVisible = false };
        var bottom = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 1), EndPoint = new Windows.Foundation.Point(0.5, 0.2) };
        bottom.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimStrong"), strength), Offset = 0 });
        bottom.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimStrong"), 0), Offset = 1 });
        grid.Children.Add(new Border { Background = bottom });
        if (_spotlight.Composition is "cinematic-left" or "editorial")
        {
            var left = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(0.65, 0.5) };
            left.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimSoft"), strength * 0.85), Offset = 0 });
            left.GradientStops.Add(new GradientStop { Color = WithAlpha(theme.Color("heroScrimSoft"), 0), Offset = 1 });
            grid.Children.Add(new Border { Background = left });
        }

        return grid;
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, double factor) =>
        Windows.UI.Color.FromArgb((byte)Math.Clamp(color.A * factor, 0, 255), color.R, color.G, color.B);

    private UIElement Pagination()
    {
        var theme = ThemeRuntime.Current;
        _progressFill.Background = theme.Brush("accent");
        var track = new Grid { Height = 2, Width = 120, Background = theme.Brush("glassBorder") };
        track.Children.Add(_progressFill);
        var panel = UI.H(10,
            UI.IconButton("icon.action.back", UI.T("Home.Previous", "Previous"), () => _vm.PreviousSpotlightCommand.Execute(null), 30),
            _counter,
            _spotlight.Pagination == "counter-rail" ? track.Align(vertical: VerticalAlignment.Center) : null,
            new Button
            {
                Content = new IconView("icon.action.back", 15, "onHeroPrimary") { RenderTransform = new ScaleTransform { ScaleX = -1, CenterX = 7.5 } },
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = theme.Brush("transparent"),
                BorderBrush = theme.Brush("transparent"),
                Command = _vm.NextSpotlightCommand,
            }.Tip(UI.T("Home.Next", "Next")));
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0, 0, 28, 22);
        return _spotlight.Pagination == "none" ? new Grid() : panel;
    }

    private void LayoutHero()
    {
        var height = _root.ActualHeight > 0 ? _root.ActualHeight : 720;
        var width = _root.ActualWidth > 0 ? _root.ActualWidth : 1280;
        var target = Math.Clamp(height * _layout.HeroFraction, _layout.HeroMinHeight, Math.Min(_layout.HeroMaxHeight, height - 180));
        _hero.Height = Math.Max(_layout.HeroMinHeight, target);
        if (_spotlight.Composition == "split-glass" && _hero.Children.Count > 0 && _hero.Children[0] is Border art)
        {
            art.Width = width * 0.62;
        }
    }

    private void OnSpotlightChanged()
    {
        var spotlight = _vm.CurrentSpotlight;
        _counter.Text = _vm.SpotlightCandidates.Count == 0 ? string.Empty : $"{_vm.SpotlightIndex + 1:00} / {_vm.SpotlightCandidates.Count:00}";
        _identityHost.Children.Clear();

        if (spotlight is null)
        {
            _heroImage.Source = null;
            _backdrop.AmbientImagePath = null;
            _identityHost.Children.Add(EmptyWelcome());
            StopHeroVideo();
            return;
        }

        _heroImage.Source = ImageRef.FromPath(spotlight.HeroImagePath, 1600);
        _backdrop.AmbientImagePath = spotlight.HeroImagePath ?? spotlight.CoverImagePath;
        _identityHost.Children.Add(Identity(spotlight));
        Services.RunUserAction(
            ApplyMediaStateAsync(spotlight),
            "HomeSurface.ApplyMediaStateAsync",
            UI.T("Home.MediaFailed", "Spotlight media could not be loaded."));
        FadeIn(_identityHost);
        FadeIn(_heroImage);
        StartSpotlightMotion();
    }

    private async Task ApplyMediaStateAsync(HomeSpotlightViewModel spotlight)
    {
        if (!_appearance.TryGetValue(spotlight.ProfileId, out var overrides))
        {
            var summary = await Services.Catalog.GalleryReads.GetGalleryProfileSummaryAsync(spotlight.ProfileId).ConfigureAwait(true);
            overrides = summary?.EffectiveAppearance ?? ProfileAppearanceOverrides.Default;
            _appearance[spotlight.ProfileId] = overrides;
        }

        if (_vm.CurrentSpotlight?.ProfileId != spotlight.ProfileId)
        {
            return;
        }

        var media = Services.Presentation.ResolveMedia(new ProfilePresentationState(spotlight.ProfileId, 0, null, overrides), PresentationSlots.SpotlightCover, PresentationSlots.SpotlightBanner);
        _heroImage.Transform = spotlight.BannerImagePath is not null ? media.Banner : media.Cover;
        PlayHeroVideo(spotlight, media.Playback);
    }

    private void PlayHeroVideo(HomeSpotlightViewModel spotlight, PlaybackPresentationState playback)
    {
        StopHeroVideo();
        if (!IsActive || !spotlight.HasBannerVideo || ResourceGovernor.Shared.Tier != PresentationTier.Full || ReducedMotionAuthority.IsReduced)
        {
            return;
        }

        _heroVideo.Source = MediaSource.CreateFromUri(new Uri(spotlight.BannerVideoPath!));
        _heroVideo.Visibility = Visibility.Visible;
        if (_heroVideo.MediaPlayer is { } player)
        {
            player.IsMuted = true;
            player.IsLoopingEnabled = playback.LoopMode == "loop";
            player.PlaybackSession.Position = playback.Start;
            player.PlaybackSession.PlaybackRate = playback.Rate;
            player.Play();
        }
    }

    private void StopHeroVideo()
    {
        _heroVideo.MediaPlayer?.Pause();
        _heroVideo.Source = null;
        _heroVideo.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement Identity(HomeSpotlightViewModel spotlight)
    {
        var name = UI.Text(spotlight.DisplayName, _spotlight.Composition == "editorial" ? "display" : "hero", "onHeroPrimary", 2);
        var meta = UI.Text(string.Join(" · ", new[] { spotlight.CategoryName, spotlight.TagSummary, spotlight.MediaCountText }.Where(s => !string.IsNullOrWhiteSpace(s))), "metadata", "onHeroSecondary", 1);
        var overview = _spotlight.ShowOverview && !string.IsNullOrWhiteSpace(spotlight.OverviewExcerpt)
            ? UI.Text(spotlight.OverviewExcerpt, "body", "onHeroSecondary", 3)
            : null;
        var actions = UI.H(8,
            UI.Button(UI.T("Home.OpenProfile", "Open Profile"), () => Services.Navigation.Navigate(new ProfileRoute(spotlight.ProfileId)), ButtonKind.Primary, "icon.action.open"),
            UI.Button(UI.T("Home.Customize", "Customize Home"), () => Services.OpenCustomization(CustomizationCategories.Home, Context), ButtonKind.Ghost, "icon.navigation.customize"));

        CoverFrameView? cover = null;
        if (_spotlight.ShowCover && spotlight.CoverImagePath is not null)
        {
            var overrides = _appearance.GetValueOrDefault(spotlight.ProfileId) ?? ProfileAppearanceOverrides.Default;
            cover = new CoverFrameView
            {
                Source = ImageRef.FromPath(spotlight.CoverImagePath, 480),
                Appearance = Neuterradise.App.Design.CoverFrames.CoverFrameCatalog.Resolve(overrides.ToCoverAppearanceRequest(), ReducedMotionAuthority.IsReduced).Appearance,
                FrameMode = "full",
                Width = _spotlight.Composition == "centered-poster" ? 200 : 132,
                Height = _spotlight.Composition == "centered-poster" ? 200 : 132,
            };
        }

        switch (_spotlight.Composition)
        {
            case "centered-poster":
                return UI.V(14, cover?.Align(HorizontalAlignment.Center), name.Align(HorizontalAlignment.Center), meta.Align(HorizontalAlignment.Center), actions.Align(HorizontalAlignment.Center))
                    .Align(HorizontalAlignment.Center, VerticalAlignment.Center).Margin(24);
            case "split-glass":
            {
                var panel = UI.Surface(UI.V(12, cover, name, meta, overview, actions), Material.Glass, ThemeRuntime.Current.Tokens.Number("radiusHero", 20), 24);
                panel.Width = 400;
                panel.MaxWidth = 460;
                panel.HorizontalAlignment = HorizontalAlignment.Right;
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.Margin = new Thickness(0, 0, 32, 0);
                return panel;
            }

            case "editorial":
                return UI.V(10, meta, name, overview, actions).Align(HorizontalAlignment.Left, VerticalAlignment.Bottom).Margin(40, 0, 40, 64).Size(width: 760);
            default:
            {
                var content = UI.Grid("auto", cover is null ? "*" : "auto,*");
                if (cover is not null)
                {
                    content.Children.Add(cover.Margin(0, 0, 18, 0).At(0, 0));
                }

                content.Children.Add(UI.V(8, name, meta, overview, actions).At(0, cover is null ? 0 : 1));
                var panel = UI.Surface(content, Material.Deep, ThemeRuntime.Current.Tokens.Number("radiusHero", 20), 22);
                panel.MaxWidth = 640;
                panel.HorizontalAlignment = HorizontalAlignment.Left;
                panel.VerticalAlignment = VerticalAlignment.Bottom;
                panel.Margin = new Thickness(32, 0, 32, 60);
                return panel;
            }
        }
    }

    private FrameworkElement EmptyWelcome()
    {
        var theme = ThemeRuntime.Current;
        var title = UI.Text(UI.T("Home.Empty.Title", "Your collection starts here"), "display", "onHeroPrimary");
        var body = UI.Text(UI.T("Home.Empty.Body", "Import photos, videos or 3D models to build your first Profiles."), "body", "onHeroSecondary", 2);
        var importBtn = UI.Button(UI.T("Home.Empty.Import", "Import media"), () => Services.Navigation.ResetToTopLevel(new ImportRoute()), ButtonKind.Primary, "icon.navigation.import");
        var customizeBtn = UI.Button(UI.T("Home.Customize", "Customize Home"), () => Services.OpenCustomization(CustomizationCategories.Home, Context), ButtonKind.Ghost, "icon.navigation.customize");

        var content = UI.V(16, title, body, UI.H(10, importBtn, customizeBtn));
        content.MaxWidth = 480;
        var panel = UI.Surface(content, Material.Deep, theme.Tokens.Number("radiusHero", 20), 32);
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(40, 0, 40, 64);
        return panel;
    }

    private void StartSpotlightMotion()
    {
        _kenBurns?.Stop();
        _progress?.Stop();
        var seconds = _spotlight.RotationSeconds;
        if (IsActive && _spotlight.Pagination == "counter-rail")
        {
            _progress = new Storyboard();
            var grow = new DoubleAnimation { From = 0, To = 120, Duration = new Duration(TimeSpan.FromSeconds(seconds)), EnableDependentAnimation = true };
            Storyboard.SetTarget(grow, _progressFill);
            Storyboard.SetTargetProperty(grow, "Width");
            _progress.Children.Add(grow);
            _progress.Begin();
        }

        _heroMotion.ScaleX = 1;
        _heroMotion.ScaleY = 1;
        _heroMotion.TranslateX = 0;
        if (!IsActive || ReducedMotionAuthority.IsReduced || _spotlight.Motion == "still" || ThemeRuntime.Current.Motion.AmbientSpeed <= 0)
        {
            return;
        }

        _kenBurns = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var amplitude = ThemeRuntime.Current.Motion.ParallaxAmplitude;
        void Add(string property, double to)
        {
            var animation = new DoubleAnimation { From = property.StartsWith("Scale", StringComparison.Ordinal) ? 1 : 0, To = to, Duration = new Duration(TimeSpan.FromSeconds(seconds)) };
            Storyboard.SetTarget(animation, _heroMotion);
            Storyboard.SetTargetProperty(animation, property);
            _kenBurns.Children.Add(animation);
        }

        if (_spotlight.Motion == "ken-burns")
        {
            Add("ScaleX", 1 + (0.08 * amplitude));
            Add("ScaleY", 1 + (0.08 * amplitude));
            Add("TranslateX", -24 * amplitude);
        }
        else
        {
            Add("TranslateX", -18 * amplitude);
            Add("TranslateY", -8 * amplitude);
        }

        _kenBurns.Begin();
    }

    private static void FadeIn(UIElement element)
    {
        var duration = ThemeRuntime.Current.Duration("slow");
        if (duration <= 0)
        {
            element.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation { From = 0.25, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ─────────────────────────────────────────── rails

    private Task LoadCollectionsAsync()
    {
        QueueRebuildRails();
        return Task.CompletedTask;
    }

    private void QueueRebuildRails()
    {
        if (_retired || _railRenderQueued)
        {
            return;
        }

        _railRenderQueued = true;
        if (!_root.DispatcherQueue.TryEnqueue(() =>
        {
            _railRenderQueued = false;
            if (!_retired)
            {
                RebuildRails();
            }
        }))
        {
            _railRenderQueued = false;
            if (!_retired)
            {
                RebuildRails();
            }
        }
    }

    private void RebuildRails()
    {
        if (_layout is null || _retired)
        {
            return;
        }

        _rails.Children.Clear();
        var padding = UI.PagePadding(_root.ActualWidth > 0 ? _root.ActualWidth : 1280);

        // Section 1: Library pulse — compact horizontal bar, not boxed stats
        var pulse = _vm.VaultPulse;
        if (pulse.ActiveProfileCount > 0 || pulse.ActiveAssetCount > 0)
        {
            _rails.Children.Add(PulseBar(padding));
        }

        // Section 2: Recently active profiles — horizontal scroll
        if (_vm.RecentlyActive.Count > 0)
        {
            _rails.Children.Add(RecentProfilesRail(padding));
        }

        // Section 3: Featured connection — subtle highlight
        if (_vm.Discovery.Count > 0)
        {
            _rails.Children.Add(FeaturedConnectionRail(padding));
        }

        // Section 4: Needs attention — contextual warning cards
        if (_vm.NeedsAttention.Count > 0)
        {
            _rails.Children.Add(AttentionRail(padding));
        }

        // Section 5: Monthly chronicle — compact activity summary
        if (_vm.RecentActivity.Count > 0)
        {
            _rails.Children.Add(ChronicleRail(padding));
        }

        // Section 6: Active imports
        if (_vm.ActiveImports.Count > 0)
        {
            _rails.Children.Add(ActiveImportsRail(padding));
        }
    }

    // ─── Pulse bar: library stats as an integrated horizontal strip

    private FrameworkElement PulseBar(double padding)
    {
        var theme = ThemeRuntime.Current;
        var pulse = _vm.VaultPulse;

        var profiles = PulseStat(
            pulse.ActiveProfileCount.ToString("N0"),
            UI.T("Home.Stat.Profiles", "Profiles"));
        var media = PulseStat(
            pulse.ActiveAssetCount.ToString("N0"),
            UI.T("Home.Stat.Media", "Media"));
        var storage = PulseStat(
            pulse.StorageSummary,
            UI.T("Home.Stat.Storage", "Space"));
        var condition = PulseStat(
            pulse.HealthState,
            UI.T("Home.Stat.Condition", "Condition"));

        var bar = UI.H(0, profiles, PulseDivider(), media, PulseDivider(), storage, PulseDivider(), condition);
        bar.VerticalAlignment = VerticalAlignment.Center;

        var shell = new Grid { Margin = new Thickness(padding, 20, padding, 0) };
        var content = UI.Grid("*", "auto,*,auto");
        content.Children.Add(bar.At(0, 0));
        content.Children.Add(
            UI.Button(UI.T("Home.Gallery", "Browse all"), () => Services.Navigation.Navigate(new GalleryRoute()), ButtonKind.Ghost, "icon.navigation.gallery")
                .Align(HorizontalAlignment.Right, VerticalAlignment.Center)
                .At(0, 2));
        shell.Children.Add(content);
        return shell;
    }

    private static FrameworkElement PulseStat(string value, string label)
    {
        return UI.V(1,
            UI.Text(value, "section-title", maxLines: 1),
            UI.Text(label, "micro", "textMuted"))
            .Margin(16, 8, 16, 8);
    }

    private static FrameworkElement PulseDivider()
    {
        return new Border
        {
            Width = 1,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.15,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)),
        };
    }

    // ─── Recent profiles: horizontal poster-scroll

    private FrameworkElement? RecentProfilesRail(double padding)
    {
        var items = _vm.RecentlyActive.Take(12)
            .Select(t => (t.ProfileId, t.DisplayName, t.CoverImagePath, t.MediaCountText, t.IsFavorite))
            .ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var title = UI.H(8,
            UI.Text(UI.T("Home.Rail.Recent", "Continue"), "section-title"),
            UI.Text(UI.F("Home.Rail.Recent.Count", "{0} profiles", _vm.RecentlyActive.Count), "micro", "textMuted").Align(vertical: VerticalAlignment.Center));
        return UI.V(10, title, ProfileCarousel(items, 140, 210))
            .Margin(padding, 28, padding, 0);
    }

    private FrameworkElement ProfileCarousel(
        IReadOnlyList<(Guid ProfileId, string DisplayName, string? CoverImagePath, string MediaCountText, bool IsFavorite)> items,
        double width, double height)
    {
        var theme = ThemeRuntime.Current;
        var radius = theme.Tokens.Number("radiusCard", 12);
        var source = new ObservableCollection<object>(items.Select(i => (object)i));
        var factory = new PooledElementFactory(
            _ => "poster-card",
            (_, _) =>
            {
                var image = new SkImageView { CornerRadiusValue = radius, PlaceholderToken = "surface2" };
                var name = UI.Text(null, "control", maxLines: 1);
                var count = UI.Text(null, "micro", "textMuted");
                var fav = new IconView("icon.profile.favorite", 10, "warning") { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 6, 0) };
                var overlay = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 1), EndPoint = new Windows.Foundation.Point(0.5, 0.5) };
                overlay.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(180, 0, 0, 0), Offset = 0 });
                overlay.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 });
                var scrim = new Border { Background = overlay, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, radius, radius) };
                var textStack = UI.V(1, name, count).Margin(8, 0, 8, 8);
                var body = new Grid { Width = width, Height = height };
                body.Children.Add(image);
                body.Children.Add(scrim);
                body.Children.Add(textStack);
                body.Children.Add(fav);

                var tile = new Grid();
                tile.Children.Add(body);
                var scale = new ScaleTransform();
                tile.RenderTransform = scale;
                tile.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                tile.PointerEntered += (_, _) => { if (!ReducedMotionAuthority.IsReduced) { scale.ScaleX = scale.ScaleY = 1.04; } };
                tile.PointerExited += (_, _) => scale.ScaleX = scale.ScaleY = 1;
                tile.Tapped += (_, _) =>
                {
                    if (tile.DataContext is ValueTuple<Guid, string, string?, string, bool> data)
                    {
                        Services.Navigation.Navigate(new ProfileRoute(data.Item1));
                    }
                };
                return tile;
            },
            (element, data) =>
            {
                if (data is not ValueTuple<Guid, string, string?, string, bool> item)
                {
                    return;
                }

                element.DataContext = item;
                var body = (Grid)((Grid)element).Children[0];
                var image = body.Children.OfType<SkImageView>().First();
                image.Source = ImageRef.FromPath(item.Item3, 320);
                var texts = ((StackPanel)body.Children[2]).Children.OfType<TextBlock>().ToList();
                texts[0].Text = item.Item2;
                texts[1].Text = item.Item4;
                var fav = body.Children.OfType<IconView>().First();
                fav.Visibility = item.Item5 ? Visibility.Visible : Visibility.Collapsed;
                ToolTipService.SetToolTip(element, item.Item2);
            });
        var (scroll, repeater) = Repeaters.Virtualized(factory, Repeaters.Stack(true, 14), horizontal: true);
        repeater.ItemsSource = source;
        scroll.Height = height + 4;
        return scroll;
    }

    // ─── Featured connection

    private FrameworkElement? FeaturedConnectionRail(double padding)
    {
        var item = _vm.FeaturedConnection;
        if (item is null)
        {
            return null;
        }

        var theme = ThemeRuntime.Current;
        var icon = new IconView("icon.profile.related", 18, "accent");
        var label = UI.Text(UI.T("Home.Connection.Featured", "Connected"), "micro", "accent");
        var names = UI.Text(
            UI.F("Home.Connection.Pair", "{0}  ↔  {1}", item.ProfileDisplayName, item.RelatedDisplayName),
            "body-strong", maxLines: 1);
        var evidence = UI.Text(item.EvidenceSummary, "caption", "textSecondary", 1);

        var content = UI.H(12,
            icon.Align(vertical: VerticalAlignment.Center),
            UI.V(2, label, names, evidence));
        var card = UI.Surface(content, Material.Grounded, theme.Tokens.Number("radiusCard", 12), 14);
        card.MaxWidth = 520;
        card.Tapped += (_, _) => Services.Navigation.Navigate(new ProfileRoute(item.ProfileId));
        card.PointerEntered += (_, _) => card.BorderBrush = theme.Brush("borderSubtle");
        card.PointerExited += (_, _) => card.BorderBrush = theme.Brush("transparent");
        card.BorderThickness = new Thickness(1);
        card.BorderBrush = theme.Brush("transparent");

        var row = UI.H(12, card);
        return row.Margin(padding, 28, padding, 0);
    }

    // ─── Attention items

    private FrameworkElement AttentionRail(double padding)
    {
        var theme = ThemeRuntime.Current;
        var cards = UI.H(12);
        foreach (var item in _vm.NeedsAttention.Take(3))
        {
            var icon = new IconView("icon.status.warning", 16, "warning");
            var title = UI.Text(item.Title, "body-strong", maxLines: 1);
            var detail = UI.Text(item.Detail, "caption", "textSecondary", 2);
            var card = UI.Surface(UI.H(10, icon.Align(vertical: VerticalAlignment.Center), UI.V(3, title, detail)), Material.Raised, theme.Tokens.Number("radiusCard", 12), 14);
            card.Width = 320;
            card.MinWidth = 260;
            if (item.Route is { } route)
            {
                card.Tapped += (_, _) => Services.Navigation.Navigate(route);
                    }

            cards.Children.Add(card);
        }

        return UI.V(10,
            UI.Text(UI.T("Home.Rail.Attention", "Needs your attention"), "section-title"),
            UI.Scroll(cards, horizontal: true))
            .Margin(padding, 28, padding, 0);
    }

    // ─── Monthly chronicle

    private FrameworkElement ChronicleRail(double padding)
    {
        var theme = ThemeRuntime.Current;
        var chips = UI.H(8);
        foreach (var item in _vm.RecentActivity.Take(6))
        {
            var chip = UI.Chip(item.Description, onClick: null);
            chip.Opacity = 0.85;
            chips.Children.Add(chip);
        }

        return UI.V(10,
            UI.Text(UI.T("Home.Rail.Activity", "This month"), "section-title"),
            UI.Scroll(chips, horizontal: true))
            .Margin(padding, 28, padding, 0);
    }

    // ─── Active imports

    private FrameworkElement ActiveImportsRail(double padding)
    {
        var items = UI.H(12);
        foreach (var import in _vm.ActiveImports.Take(3))
        {
            var icon = new IconView("icon.navigation.import", 16, "accent");
            var name = UI.Text(System.Net.WebUtility.HtmlDecode(import.SourceDisplayName), "body-strong", maxLines: 1);
            var stage = UI.Text(import.StageText, "caption", "textSecondary", 1);
            var card = UI.Surface(UI.H(10, icon.Align(vertical: VerticalAlignment.Center), UI.V(2, name, stage)), Material.Raised, 10, 12);
            card.Width = 280;
            card.Tapped += (_, _) => Services.Navigation.Navigate(new ImportRoute());
                items.Children.Add(card);
        }

        return UI.V(10,
            UI.Text(UI.T("Home.Rail.Imports", "Importing"), "section-title"),
            UI.Scroll(items, horizontal: true))
            .Margin(padding, 28, padding, 0);
    }

    public override void Dispose()
    {
        _retired = true;
        _railRenderQueued = false;
        var heroPlayer = _heroVideo.MediaPlayer;
        OnSuspended();
        heroPlayer?.Dispose();
        base.Dispose();
    }
}
