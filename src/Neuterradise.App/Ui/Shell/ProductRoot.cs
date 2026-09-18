using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Input;
using Neuterradise.App.Faces;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.Trash;
using Windows.System;

namespace Neuterradise.App.Ui;

/// <summary>
/// The product shell below the native Windows title bar: primary navigation (Home, Gallery, Import,
/// Settings — no global
/// sidebar), a content host that keeps the four top-level surfaces warm, a bounded LRU for contextual
/// surfaces, and the overlay / Customization / toast layers. Interaction feeds the ResourceGovernor.
/// </summary>
public sealed class ProductRoot : Grid
{
    private const int ContextualCapacity = 3;

    private readonly AppServices _services;
    private readonly Grid _content = new();
    private readonly Grid _overlayLayer = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _toasts = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 20) };
    private readonly Dictionary<Type, Surface> _topLevel = [];
    private readonly LinkedList<(AppRoute Route, Surface Surface)> _contextual = new();
    private readonly List<(AppRoute Route, Border Button, TextBlock Label, string LabelKey, string Fallback)> _navButtons = [];
    private readonly ScaleHost _scaleHost = new();
    private readonly Grid _customizationLayer = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _breadcrumb = UI.H(4);
    private Surface? _current;
    private CustomizationCenter? _customization;
    private TextBlock? _activityHeadline;
    private Border? _activityPill;
    private ProgressBar? _activityProgress;
    private bool _languageRefreshQueued;

    public ProductRoot(AppServices services)
    {
        _services = services;
        services.Root = this;
        Dialogs = new DialogService(this);
        Overlays = new OverlayPresenter(services, this);
        Background = ThemeRuntime.Current.Brush("canvas");
        WindowChrome.UseNativeTitleBar(services.Window);

        var shell = UI.Grid("auto,auto,*", "*");
        shell.Children.Add(BuildPrimaryNavigation().At(0));
        shell.Children.Add(BuildBreadcrumb().At(1));
        shell.Children.Add(_content.At(2));
        _scaleHost.Children.Add(shell);
        _scaleHost.Children.Add(_customizationLayer);
        _scaleHost.Children.Add(_overlayLayer);
        _scaleHost.Children.Add(_toasts);
        Children.Add(_scaleHost);

        _scaleHost.UiScale = services.UiScale.Scale;
        services.UiScale.ScaleChanged += (_, _) => UiDispatch.Run(() => _scaleHost.UiScale = services.UiScale.Scale);
        services.Navigation.Navigated += OnNavigated;
        services.Overlay.PropertyChanged += (_, _) => UiDispatch.Run(Overlays.Sync);
        services.Status.PropertyChanged += (_, _) => UiDispatch.Run(UpdateActivity);
        services.Presentation.Changed += (_, args) => UiDispatch.Run(() => OnPresentationChanged(args));
        SurfaceText.LanguageChanged += (_, _) => QueueLanguageRefresh();

        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Pointer)), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll)), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        SizeChanged += (_, _) => ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Resize);

        RegisterAccelerators();
        UpdateActivity();
        Show(services.Navigation.CurrentRoute);
    }

    public AppServices Services => _services;

    public DialogService Dialogs { get; }

    public OverlayPresenter Overlays { get; }

    internal Grid OverlayLayer => _overlayLayer;

    private FrameworkElement BuildPrimaryNavigation()
    {
        var theme = ThemeRuntime.Current;
        var height = theme.Tokens.Number("chromeHeight", 50);
        var navigation = UI.Grid("*", "auto,*,auto");
        navigation.Height = height;
        navigation.Background = theme.Brush("chromeBackground");
        navigation.BorderBrush = theme.Brush("chromeBorder");
        navigation.BorderThickness = new Thickness(0, 0, 0, theme.Tokens.Number("chromeBorderThickness", 1));

        var brand = UI.H(10, new IconView("icon.brand", 22, "accent"), UI.Text("Neu Terradise", "control", "chromeForeground"))
            .Margin(16, 0, 20, 0).Align(HorizontalAlignment.Left, VerticalAlignment.Center);
        navigation.Children.Add(brand.At(0, 0));

        var nav = UI.H(4);
        nav.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (route, key, fallback, icon) in new (AppRoute, string, string, string)[]
        {
            (new HomeRoute(), "Nav.Home", "Home", "icon.navigation.home"),
            (new GalleryRoute(), "Nav.Gallery", "Gallery", "icon.navigation.gallery"),
            (new ImportRoute(), "Nav.Import", "Import", "icon.navigation.import"),
            (new SettingsRoute(), "Nav.Settings", "Settings", "icon.navigation.settings"),
        })
        {
            var label = UI.Text(UI.T(key, fallback), "control", "textSecondary");
            var button = new Border
            {
                Child = UI.H(8, new IconView(icon, 16, "textSecondary"), label).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
                Padding = new Thickness(14, 0, 14, 0),
                Height = theme.Tokens.Number("topNavHeight", 40) - 4,
                CornerRadius = new CornerRadius(theme.Tokens.Number("navCornerRadius", 6)),
                BorderBrush = theme.Brush("navIndicator"),
                Background = theme.Brush("transparent"),
            };
            button.Tip(UI.T(key, fallback));
            button.Tapped += (_, _) => NavigateTop(route);
            button.PointerEntered += (_, _) =>
            {
                if (!IsSelectedNav(route))
                {
                    button.Background = theme.Brush("navHoverBackground");
                }
            };
            button.PointerExited += (_, _) => UpdateNavSelection();
            _navButtons.Add((route, button, label, key, fallback));
            nav.Children.Add(button);
        }

        navigation.Children.Add(nav.At(0, 1));

        _activityHeadline = UI.Text(string.Empty, "caption", "textSecondary", 1);
        _activityProgress = new ProgressBar { Width = 72, Height = 3, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };
        _activityPill = UI.Surface(UI.H(8, new IconView("icon.status.activity", 14, "accent"), UI.V(0, _activityHeadline, _activityProgress)), Material.Frost, 8, 6);
        _activityPill.Padding = new Thickness(10, 4, 10, 4);
        _activityPill.MaxWidth = 260;
        _activityPill.Margin = new Thickness(0, 0, 8, 0);
        _activityPill.VerticalAlignment = VerticalAlignment.Center;
        _activityPill.Tapped += (_, _) => NavigateTop(new ImportRoute());
        var utilities = UI.H(4,
            _activityPill,
            UI.IconButton("icon.navigation.customize", UI.T("Nav.Customize", "Customize"), () => _services.OpenCustomization(CustomizationCategories.Appearance)));
        utilities.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(utilities.At(0, 2));
        return navigation;
    }

    private FrameworkElement BuildBreadcrumb()
    {
        var theme = ThemeRuntime.Current;
        var host = new Border
        {
            Child = _breadcrumb,
            Padding = new Thickness(16, 6, 16, 6),
            Background = theme.Brush("surface1"),
            BorderBrush = theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        return host;
    }

    private void UpdateBreadcrumb(AppRoute route)
    {
        _breadcrumb.Children.Clear();
        var items = NavigationContextResolver.Breadcrumb(route);
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                _breadcrumb.Children.Add(UI.Text("/", "caption", "textTertiary").Align(vertical: VerticalAlignment.Center));
            }

            var item = items[index];
            if (item.Target is null)
            {
                _breadcrumb.Children.Add(UI.Text(item.Label, "caption", "textPrimary").Align(vertical: VerticalAlignment.Center));
            }
            else
            {
                _breadcrumb.Children.Add(UI.Button(item.Label, () => _services.Navigation.Navigate(item.Target), ButtonKind.Ghost));
            }
        }
    }

    private void UpdateShellLanguage()
    {
        foreach (var (_, button, label, key, fallback) in _navButtons)
        {
            label.Text = UI.T(key, fallback);
            button.Tip(label.Text);
        }

        UpdateBreadcrumb(_services.Navigation.CurrentRoute);
    }

    private void QueueLanguageRefresh()
    {
        if (_languageRefreshQueued)
        {
            return;
        }

        _languageRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshLanguage))
        {
            _languageRefreshQueued = false;
            UiDispatch.Run(RefreshLanguage);
        }
    }

    private void RefreshLanguage()
    {
        _languageRefreshQueued = false;
        UpdateShellLanguage();

        var route = _services.Navigation.CurrentRoute;
        if (_customization is not null)
        {
            _customization.Close(apply: false);
        }

        _current?.Suspend();
        _current = null;

        foreach (var surface in _topLevel.Values)
        {
            _content.Children.Remove(surface.View);
            surface.Dispose();
        }
        _topLevel.Clear();

        foreach (var (_, surface) in _contextual)
        {
            _content.Children.Remove(surface.View);
            surface.Dispose();
        }
        _contextual.Clear();
        _content.Children.Clear();

        Show(route);
        Overlays.Sync();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var status = _services.Status;
        if (_activityPill is null || _activityHeadline is null || _activityProgress is null)
        {
            return;
        }

        _activityPill.Visibility = status.HasVisibleStatus ? Visibility.Visible : Visibility.Collapsed;
        _activityHeadline.Text = status.Headline;
        _activityPill.Tip(status.HeadlineDetail);
        _activityProgress.IsIndeterminate = status.IsProgressIndeterminate;
        _activityProgress.Value = status.Progress ?? 0;
        _activityProgress.Visibility = status.HasRunningWork ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigateTop(AppRoute route)
    {
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Navigation);
        HideCustomization();
        if (_current is GallerySurface gallery && _services.Navigation.CurrentRoute is GalleryRoute)
        {
            _services.Navigation.UpdateCurrentRoute(new GalleryRoute(gallery.CaptureOrigin()));
        }

        _services.Navigation.ResetToTopLevel(route);
    }

    private bool IsSelectedNav(AppRoute route) =>
        NavigationContextResolver.OwningTopLevel(_services.Navigation.CurrentRoute)?.GetType() == route.GetType();

    private void UpdateNavSelection()
    {
        var theme = ThemeRuntime.Current;
        foreach (var (route, button, _, _, _) in _navButtons)
        {
            var selected = IsSelectedNav(route);
            button.Background = selected ? theme.Brush("navSelectedBackground") : theme.Brush("transparent");
            button.BorderThickness = new Thickness(0, 0, 0, selected ? Math.Max(2, theme.Tokens.Number("navIndicatorHeight", 2)) : 0);
            if (button.Child is StackPanel stack)
            {
                foreach (var child in stack.Children)
                {
                    if (child is TextBlock text)
                    {
                        text.Foreground = theme.Brush(selected ? "textPrimary" : "textSecondary");
                    }
                    else if (child is IconView icon)
                    {
                        icon.ColorToken = selected ? "accent" : "textSecondary";
                    }
                }
            }
        }
    }

    private void OnNavigated(object? sender, NavigationChangedEventArgs e) => UiDispatch.Run(() => Show(e.Current));

    private void Show(AppRoute route)
    {
        HoverVideoCoordinator.Shared.StopAll();
        var next = ResolveSurface(route);
        var surfaceChanged = !ReferenceEquals(next, _current);
        if (surfaceChanged)
        {
            _current?.Suspend();
            foreach (UIElement child in _content.Children)
            {
                child.Visibility = Visibility.Collapsed;
            }

            if (!_content.Children.Contains(next.View))
            {
                _content.Children.Add(next.View);
            }

            next.View.Visibility = Visibility.Visible;
            AnimateEntrance(next.View);
            _current = next;
        }

        if (surfaceChanged || next.LastRoute != route)
        {
            next.Activate(route);
        }
        UpdateBreadcrumb(route);
        UpdateNavSelection();
    }

    public string? CurrentSurfaceName => _current?.GetType().Name;

    public bool IsCurrentSurfaceRendered => _current is { View: { ActualWidth: > 0, ActualHeight: > 0 } };

    public ScreenStatus? CurrentContentStatus => _current?.Model?.Status;

    private Surface ResolveSurface(AppRoute route)
    {
        try
        {
            return ResolveSurfaceCore(route);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Surface for route {0} could not be created: {1}", route, exception);
            return new RouteFailureSurface(_services, route, () => Show(route));
        }
    }

    private Surface ResolveSurfaceCore(AppRoute route)
    {
        if (route is HomeRoute or GalleryRoute or ImportRoute or SettingsRoute)
        {
            var key = route.GetType();
            if (!_topLevel.TryGetValue(key, out var surface))
            {
                surface = route switch
                {
                    HomeRoute => new HomeSurface(_services, CreateModel<HomeViewModel>(route)),
                    GalleryRoute => new GallerySurface(_services, CreateModel<GalleryViewModel>(route)),
                    ImportRoute => new ImportSurface(_services, CreateModel<ImportViewModel>(route)),
                    _ => new SettingsSurface(_services, CreateModel<SettingsViewModel>(route)),
                };
                _topLevel[key] = surface;
            }

            return surface;
        }

        if (route is TrashRoute or LibraryHealthRoute)
        {
            var settings = ResolveSurface(new SettingsRoute());
            return settings;
        }

        for (var node = _contextual.First; node is not null; node = node.Next)
        {
            if (SameContext(node.Value.Route, route))
            {
                node.Value = (route, node.Value.Surface);
                _contextual.Remove(node);
                _contextual.AddFirst(node);
                return node.Value.Surface;
            }
        }

        Surface created = route switch
        {
            ProfileRoute profile => new ProfileSurface(_services, CreateModel<ProfileDetailViewModel>(profile)),
            FaceReviewRoute face => new PeopleSurface(_services, CreateModel<FaceReviewViewModel>(face)),
            _ => throw new NotSupportedException($"No surface for {route.GetType().Name}."),
        };
        _contextual.AddFirst((route, created));
        while (_contextual.Count > ContextualCapacity)
        {
            var candidate = _contextual.Last;
            while (candidate is not null && ReferenceEquals(candidate.Value.Surface, _current))
            {
                candidate = candidate.Previous;
            }

            if (candidate is null)
            {
                break;
            }

            var evicted = candidate.Value;
            _contextual.Remove(candidate);
            _content.Children.Remove(evicted.Surface.View);
            evicted.Surface.Dispose();
        }

        return created;
    }

    private T CreateModel<T>(AppRoute route) where T : class
    {
        var model = _services.Factory.Create(route);
        return model as T ?? throw new InvalidOperationException(
            model is RouteErrorViewModel error
                ? $"{route.GetType().Name} view model creation failed: {error.Reason}"
                : $"{route.GetType().Name} resolved an incompatible view model.");
    }

    private static bool SameContext(AppRoute a, AppRoute b) => (a, b) switch
    {
        (ProfileRoute x, ProfileRoute y) => x.ProfileId == y.ProfileId,
        (FaceReviewRoute x, FaceReviewRoute y) => x.ProfileId == y.ProfileId,
        _ => false,
    };

    private void AnimateEntrance(FrameworkElement view)
    {
        var theme = ThemeRuntime.Current;
        var duration = theme.Duration("normal");
        if (duration <= 0 || theme.Motion.RouteTransition == "none")
        {
            view.Opacity = 1;
            return;
        }

        var transform = new TranslateTransform();
        view.RenderTransform = transform;
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
        Storyboard.SetTarget(fade, view);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        if (theme.Motion.RouteTransition is "slide-fade" or "depth")
        {
            var slide = new DoubleAnimation
            {
                From = theme.Tokens.Number("motionPageTranslate", 12),
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "Y");
            storyboard.Children.Add(slide);
        }

        storyboard.Begin();
    }

    public void ShowCustomization(string category, PresentationContext context, string? slot, object? subject)
    {
        HoverVideoCoordinator.Shared.StopAll();
        _customization?.Close(apply: false);
        _customization = new CustomizationCenter(_services, category, context, slot, subject, () => HideCustomization());
        _customizationLayer.Children.Clear();
        _customizationLayer.Children.Add(_customization.View);
        _customizationLayer.Visibility = Visibility.Visible;
        _current?.Suspend();
        AnimateEntrance(_customization.View);
    }

    public void HideCustomization()
    {
        if (_customizationLayer.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _customizationLayer.Visibility = Visibility.Collapsed;
        _customizationLayer.Children.Clear();
        var closing = _customization;
        _customization = null;
        closing?.Dispose();
        if (_current is not null)
        {
            _current.Activate(_services.Navigation.CurrentRoute);
        }
    }

    public Neuterradise.App.Gallery.GalleryCardViewModel? SampleCard(Guid? profileId) =>
        _topLevel.Values.OfType<GallerySurface>().FirstOrDefault()?.SampleCard(profileId);

    public Task ImportAsync(IEnumerable<string> paths, Guid? destinationProfileId)
    {
        var import = (ImportSurface)ResolveSurface(new ImportRoute());
        _services.Navigation.ResetToTopLevel(new ImportRoute());
        return import.IntakeAsync(paths, destinationProfileId);
    }

    public void ShowToast(string message, string tone)
    {
        var icon = tone switch
        {
            "success" => "icon.status.success",
            "warning" => "icon.status.warning",
            "error" => "icon.status.error",
            _ => "icon.status.info",
        };
        var toast = UI.Surface(UI.H(10, new IconView(icon, 18, tone switch { "success" => "success", "warning" => "warning", "error" => "danger", _ => "info" }), UI.Text(message, "body", maxLines: 3)), Material.Deep, 10, 12);
        toast.MaxWidth = 420;
        _toasts.Children.Add(toast);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tone == "error" ? 8 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toasts.Children.Remove(toast);
        };
        timer.Start();
    }

    private void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            PresentationBinder.ApplyGlobal(_services.Presentation);
        }

        _services.RaisePresentationChanged(args);
        foreach (var surface in _topLevel.Values.Concat(_contextual.Select(c => c.Surface)))
        {
            surface.OnPresentationChanged(args);
        }
    }

    private void RegisterAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            if (key == VirtualKey.None
                || !Enum.IsDefined(key)
                || modifiers == VirtualKeyModifiers.None)
            {
                return;
            }

            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.Add, VirtualKeyModifiers.Control, () => UiScaleCommands.Execute(UiScaleCommand.Increase, _services.UiScale));
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => UiScaleCommands.Execute(UiScaleCommand.Decrease, _services.UiScale));
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, () => UiScaleCommands.Execute(UiScaleCommand.Reset, _services.UiScale));
        Add(VirtualKey.NumberPad0, VirtualKeyModifiers.Control, () => UiScaleCommands.Execute(UiScaleCommand.Reset, _services.UiScale));
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _services.Navigation.GoBack());
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Keyboard);
        var controlDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (controlDown && (int)e.Key == 187)
        {
            UiScaleCommands.Execute(UiScaleCommand.Increase, _services.UiScale);
            e.Handled = true;
            return;
        }

        if (controlDown && (int)e.Key == 189)
        {
            UiScaleCommands.Execute(UiScaleCommand.Decrease, _services.UiScale);
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        if (Dialogs.TryDismissTop())
        {
            e.Handled = true;
        }
        else if (_services.Overlay.HasOverlay)
        {
            _services.Overlay.RequestEscapeClose();
            e.Handled = true;
        }
        else if (_customization is not null)
        {
            _customization.Close(apply: false);
            e.Handled = true;
        }
    }
}

internal sealed class RouteFailureSurface : Surface
{
    private readonly Grid _view;

    public RouteFailureSurface(AppServices services, AppRoute route, Action retry) : base(services)
    {
        _view = UI.Grid("*", "*");
        _view.Children.Add(UI.V(12,
            UI.Text(UI.T("Route.Error.Title", "This page couldn't be opened."), "section-title"),
            UI.Text(UI.T("Route.Error.Description", "The page failed to initialize. Retry, or return to the previous page."), "body", "textSecondary", 3),
            UI.H(8,
                UI.Button(UI.T("Common.Retry", "Retry"), retry, ButtonKind.Primary),
                UI.Button(UI.T("Common.Back", "Back"), () => services.Navigation.GoBack(), ButtonKind.Secondary)))
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center));
    }

    public override FrameworkElement View => _view;
}
