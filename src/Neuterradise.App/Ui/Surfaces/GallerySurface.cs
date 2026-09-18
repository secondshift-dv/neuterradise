using Microsoft.UI.Xaml;
using Layout = Microsoft.UI.Xaml.Controls.Layout;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Gallery;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Ui;

/// <summary>
/// Gallery: calm premium browsing over a virtualized repeater (no ScrollViewer + WrapPanel). Only the
/// viewport plus a small buffer is realized; cards are pooled per compiled card plan and rebound on
/// reuse. Layout, card and density come from the Presentation Contract.
/// </summary>
public sealed class GallerySurface : Surface
{
    private readonly GalleryViewModel _vm;
    private readonly Grid _root = UI.Grid("auto,auto,*,auto", "*");
    private readonly PooledElementFactory _factory;
    private readonly ItemsRepeater _repeater;
    private readonly ScrollViewer _scroll;
    private readonly Grid _field = new();
    private readonly TextBlock _count = UI.Text(string.Empty, "metadata");
    private readonly TextBlock _notice = UI.Text(string.Empty, "caption", "danger");
    private readonly StackPanel _chips = UI.H(6);
    private readonly Grid _empty = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<Guid, CompiledDefinition> _cardByProfile = [];
    private GalleryLayoutPlan _layout = null!;
    private GalleryDensityState _density = GalleryDensityState.Default;
    private CompiledDefinition _globalCard = null!;
    private bool _initialized;

    public GallerySurface(AppServices services, GalleryViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _factory = new PooledElementFactory(KindOf, Create, Bind, Recycle);
        (_scroll, _repeater) = Repeaters.Virtualized(_factory, Repeaters.Grid(240, 135, 16));
        _repeater.ItemsSource = _vm.Cards;
        _scroll.ViewChanged += (_, _) => HoverVideoCoordinator.Shared.StopAll();
        _field.Children.Add(_scroll);
        _field.Children.Add(_empty);

        _root.Children.Add(Header().At(0));
        _root.Children.Add(_chips.Margin(24, 0, 24, 8).At(1));
        _root.Children.Add(_field.At(2));
        _root.Children.Add(Pagination().At(3));
        _root.SizeChanged += (_, _) =>
        {
            ApplyPadding();
            if (_layout?.Primitive == CollectionPrimitives.Carousel) ApplyLayout();
        };

        ResolvePlans();
        _notice.TextWrapping = TextWrapping.Wrap;
        Bag.Add(Observe.Props(_vm, UpdateState, nameof(GalleryViewModel.Status), nameof(GalleryViewModel.ErrorMessage), nameof(GalleryViewModel.ResultCountText), nameof(GalleryViewModel.RangeText), nameof(GalleryViewModel.CanGoNext), nameof(GalleryViewModel.CanGoPrevious), nameof(GalleryViewModel.ErrorNotice)));
        Bag.Add(Observe.Collection(_vm.ActiveFilters, RebuildChips));
        Bag.Add(Observe.Collection(_vm.Cards, () => _cardByProfile.Clear()));
        UpdateState();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    public GalleryOriginState CaptureOrigin() => _vm.BuildOrigin(_vm.SelectedProfileId, _scroll.VerticalOffset);

    /// <summary>A real loaded card for Customization previews: the requested Profile, else the first visible one.</summary>
    public Neuterradise.App.Gallery.GalleryCardViewModel? SampleCard(Guid? profileId) =>
        (profileId is { } id ? _vm.Cards.FirstOrDefault(c => c.ProfileId == id) : null) ?? _vm.Cards.FirstOrDefault();

    private PresentationContext Context => new("gallery");

    private void ResolvePlans()
    {
        var presentation = Services.Presentation;
        _layout = presentation.ResolvePlan<GalleryLayoutPlan>(PresentationSlots.GalleryLayout, Context);
        _density = GalleryDensityState.Parse(presentation.Resolve(PresentationSlots.GalleryDensity, Context).StateJson);
        var selection = presentation.Resolve(PresentationSlots.GalleryCard, Context);
        _globalCard = presentation.ResolveCompiled(PresentationSlots.GalleryCard, Context);

        // A layout's preferred card is only a suggestion: it never replaces a card the user chose.
        if (selection.Source == ResolutionSource.BuiltInDefault && _layout.PreferredCardId is { } preferred
            && presentation.Find(DefinitionRef.BuiltIn(preferred)) is { } suggested)
        {
            _globalCard = suggested;
        }

        _cardByProfile.Clear();
        _factory.Clear();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var scale = _density.CardScale;
        var aspect = _layout.ItemAspect ?? _globalCard.PlanAs<CardPlan>().Aspect;
        var preferredWidth = _layout.ItemMinWidth * scale;
        var viewportWidth = Math.Max(320, _field.ActualWidth > 0 ? _field.ActualWidth : _root.ActualWidth);
        var viewportHeight = Math.Max(240, _field.ActualHeight > 0 ? _field.ActualHeight : 540);
        var width = _layout.Primitive == CollectionPrimitives.Carousel
            ? Math.Clamp(preferredWidth, 180, Math.Min(520, viewportWidth * 0.72))
            : preferredWidth;
        if (_layout.Primitive == CollectionPrimitives.Carousel && width / aspect > viewportHeight * 0.68)
        {
            width = viewportHeight * 0.68 * aspect;
        }
        Layout layout = _layout.Primitive switch
        {
            CollectionPrimitives.List => Repeaters.Stack(false, _layout.Spacing),
            CollectionPrimitives.Carousel => Repeaters.Stack(true, _layout.Spacing),
            _ => Repeaters.Grid(width, width / aspect, _layout.Spacing, _layout.MaxColumns),
        };
        _repeater.Layout = layout;
        var horizontal = _layout.Primitive == CollectionPrimitives.Carousel;
        _scroll.HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled;
        _scroll.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled;
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = _vm.Cards;
    }

    private void ApplyPadding()
    {
        var padding = UI.PagePadding(_root.ActualWidth);
        _scroll.Padding = new Thickness(padding, 8, padding, 24);
    }

    protected override void OnActivated(AppRoute route)
    {
        var origin = (route as GalleryRoute)?.Origin;
        if (origin is not null)
        {
            _vm.RestoreOrigin(origin);
        }

        var operation = ActivateAsync(origin);
        _initialized = true;
        Services.RunUserAction(operation, "GallerySurface.ActivateAsync", UI.T("Gallery.RefreshFailed", "Gallery could not be refreshed."));
    }

    private async Task ActivateAsync(GalleryOriginState? origin)
    {
        if (_initialized)
        {
            await _vm.RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            await _vm.InitializeAsync().ConfigureAwait(true);
        }

        if (origin is not null && origin.AnchorOffsetDip > 0)
        {
            _scroll.ChangeView(null, origin.AnchorOffsetDip, null, disableAnimation: true);
        }
    }

    protected override void OnSuspended() => HoverVideoCoordinator.Shared.StopAll();

    public override void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("gallery.", StringComparison.Ordinal) || s.StartsWith("profile.", StringComparison.Ordinal) || s.StartsWith("surface.card", StringComparison.Ordinal) || s.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            ResolvePlans();
        }
    }

    // ---------------------------------------------------------------- toolbar

    private FrameworkElement Header()
    {
        var search = new AutoSuggestBox
        {
            PlaceholderText = UI.T("Gallery.Search", "Search Profiles, categories or tags"),
            Text = _vm.SearchText,
            ItemsSource = _vm.Suggestions,
            TextMemberPath = nameof(GallerySearchSuggestion.Text),
        };
        search.MinWidth = 220;
        search.MaxWidth = 380;
        search.TextChanged += (sender, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _vm.SearchText = sender.Text;
            }
        };
        search.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is GallerySearchSuggestion suggestion)
            {
                _vm.ApplySuggestion(suggestion);
            }
        };
        search.QuerySubmitted += (sender, args) =>
        {
            if (args.ChosenSuggestion is null)
            {
                _vm.SearchText = sender.Text;
                _vm.DismissSuggestions();
            }
        };
        Bag.Add(Observe.Props(_vm, () =>
        {
            if (!string.Equals(search.Text, _vm.SearchText, StringComparison.Ordinal))
            {
                search.Text = _vm.SearchText;
            }
            search.IsSuggestionListOpen = _vm.IsSuggestionFlyoutOpen;
        }, nameof(GalleryViewModel.SearchText), nameof(GalleryViewModel.IsSuggestionFlyoutOpen)));

        var category = new ComboBox { PlaceholderText = UI.T("Gallery.Category", "Category"), MinWidth = 140 };
        var tag = new ComboBox { PlaceholderText = UI.T("Gallery.Tag", "Tag"), MinWidth = 120 };
        var synchronizingFacets = false;
        void SelectFacet(ComboBox combo, IReadOnlyList<GalleryFilterOption> options, string? selectedId)
        {
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                combo.SelectedIndex = 0;
                return;
            }

            var index = -1;
            for (var candidate = 0; candidate < options.Count; candidate++)
            {
                if (string.Equals(options[candidate].Id, selectedId, StringComparison.Ordinal))
                {
                    index = candidate + 1;
                    break;
                }
            }
            combo.SelectedIndex = index;
        }

        void SyncFacetSelection()
        {
            synchronizingFacets = true;
            try
            {
                SelectFacet(category, _vm.Categories, _vm.SelectedCategoryId);
                SelectFacet(tag, _vm.Tags, _vm.SelectedTagId);
            }
            finally
            {
                synchronizingFacets = false;
            }
        }

        void FillFacets()
        {
            synchronizingFacets = true;
            try
            {
                category.ItemsSource = new[] { UI.T("Gallery.AllCategories", "All categories") }.Concat(_vm.Categories.Select(c => $"{c.Name} ({c.ProfileCount})")).ToList();
                tag.ItemsSource = new[] { UI.T("Gallery.AllTags", "All tags") }.Concat(_vm.Tags.Select(t => $"{t.Name} ({t.ProfileCount})")).ToList();
                SelectFacet(category, _vm.Categories, _vm.SelectedCategoryId);
                SelectFacet(tag, _vm.Tags, _vm.SelectedTagId);
            }
            finally
            {
                synchronizingFacets = false;
            }
        }

        Bag.Add(Observe.Collection(_vm.Categories, FillFacets));
        Bag.Add(Observe.Collection(_vm.Tags, FillFacets));
        Bag.Add(Observe.Props(_vm, SyncFacetSelection, nameof(GalleryViewModel.SelectedCategoryId), nameof(GalleryViewModel.SelectedTagId)));
        FillFacets();
        category.SelectionChanged += (_, _) =>
        {
            if (!synchronizingFacets)
            {
                _vm.SelectedCategoryId = category.SelectedIndex <= 0 ? null : _vm.Categories[category.SelectedIndex - 1].Id;
            }
        };
        tag.SelectionChanged += (_, _) =>
        {
            if (!synchronizingFacets)
            {
                _vm.SelectedTagId = tag.SelectedIndex <= 0 ? null : _vm.Tags[tag.SelectedIndex - 1].Id;
            }
        };

        var favorites = new ToggleButton { Content = UI.H(6, new IconView("icon.profile.favorite", 14, "danger"), UI.Text(UI.T("Gallery.Favorites", "Favorites"), "control")), IsChecked = _vm.FavoritesOnly };
        favorites.Click += (_, _) => _vm.FavoritesOnly = favorites.IsChecked == true;
        Bag.Add(Observe.Props(_vm, () => favorites.IsChecked = _vm.FavoritesOnly, nameof(GalleryViewModel.FavoritesOnly)));

        var sort = new ComboBox { ItemsSource = _vm.SortOptions.Select(o => o.DisplayName).ToList(), MinWidth = 150 };
        sort.SelectedIndex = Math.Max(0, _vm.SortOptions.ToList().FindIndex(o => o.Order == _vm.SortOrder));
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex >= 0)
            {
                _vm.SortOrder = _vm.SortOptions[sort.SelectedIndex].Order;
            }
        };
        Bag.Add(Observe.Props(_vm, () =>
        {
            var selected = _vm.SortOptions.ToList().FindIndex(option => option.Order == _vm.SortOrder);
            sort.SelectedIndex = Math.Max(0, selected);
        }, nameof(GalleryViewModel.SortOrder)));

        ComboBox RatingFilter(string header, Func<int?> read, Action<int?> write, string propertyName)
        {
            var combo = new ComboBox
            {
                Header = header,
                ItemsSource = new[] { UI.T("Common.Any", "Any"), "1", "2", "3", "4", "5" },
                SelectedIndex = read() ?? 0,
                MinWidth = 92,
            };
            var synchronizing = false;
            combo.SelectionChanged += (_, _) =>
            {
                if (!synchronizing)
                {
                    write(combo.SelectedIndex <= 0 ? null : combo.SelectedIndex);
                }
            };
            Bag.Add(Observe.Props(_vm, () =>
            {
                synchronizing = true;
                combo.SelectedIndex = read() ?? 0;
                synchronizing = false;
            }, propertyName));
            return combo;
        }

        ToggleButton FilterToggle(string label, Func<bool> read, Action<bool> write, string propertyName)
        {
            var toggle = new ToggleButton { Content = label, IsChecked = read() };
            toggle.Click += (_, _) => write(toggle.IsChecked == true);
            Bag.Add(Observe.Props(_vm, () => toggle.IsChecked = read(), propertyName));
            return toggle;
        }

        var minRating = RatingFilter(UI.T("Gallery.MinRating", "Min rating"), () => _vm.MinRating, value => _vm.MinRating = value, nameof(GalleryViewModel.MinRating));
        var maxRating = RatingFilter(UI.T("Gallery.MaxRating", "Max rating"), () => _vm.MaxRating, value => _vm.MaxRating = value, nameof(GalleryViewModel.MaxRating));
        var images = FilterToggle(UI.T("Gallery.HasImages", "Images"), () => _vm.HasImages, value => _vm.HasImages = value, nameof(GalleryViewModel.HasImages));
        var videos = FilterToggle(UI.T("Gallery.HasVideos", "Videos"), () => _vm.HasVideos, value => _vm.HasVideos = value, nameof(GalleryViewModel.HasVideos));
        var models = FilterToggle(UI.T("Gallery.HasModels", "3D models"), () => _vm.HasModels, value => _vm.HasModels = value, nameof(GalleryViewModel.HasModels));
        var shared = FilterToggle(UI.T("Gallery.HasSharedMedia", "Shared media"), () => _vm.HasSharedMedia, value => _vm.HasSharedMedia = value, nameof(GalleryViewModel.HasSharedMedia));
        var faces = FilterToggle(UI.T("Gallery.HasConfirmedFace", "Confirmed faces"), () => _vm.HasConfirmedFaceRelation, value => _vm.HasConfirmedFaceRelation = value, nameof(GalleryViewModel.HasConfirmedFaceRelation));
        var manual = FilterToggle(UI.T("Gallery.HasManualRelation", "Manual relations"), () => _vm.HasManualRelation, value => _vm.HasManualRelation = value, nameof(GalleryViewModel.HasManualRelation));
        var unresolved = FilterToggle(UI.T("Gallery.ShowUnresolved", "Unresolved"), () => _vm.ShowUnresolvedUnknown, value => _vm.ShowUnresolvedUnknown = value, nameof(GalleryViewModel.ShowUnresolvedUnknown));
        var related = UI.Button(
            UI.T("Gallery.Related.Select", "Related Profile…"),
            null,
            ButtonKind.Ghost,
            "icon.profile.related",
            _vm.PickRelatedToProfileCommand);

        var customize = UI.Button(UI.T("Gallery.Customize", "Customize Gallery"), () => Services.OpenCustomization(CustomizationCategories.Gallery, Context), ButtonKind.Ghost, "icon.navigation.customize");
        var titleRow = UI.Grid("auto", "auto,*,auto",
            UI.V(0, UI.Text(UI.T("Nav.Gallery", "Gallery"), "page-title"), _count).At(0, 0),
            customize.Align(HorizontalAlignment.Right, VerticalAlignment.Center).At(0, 2));
        FrameworkElement Group(string label, params UIElement[] controls) =>
            UI.V(4, UI.Text(label, "micro", "textMuted"), new VariableWrap(8, controls));
        var groups = new VariableWrap(14,
            Group(UI.T("Gallery.Filters.Primary", "Browse"), search, category, tag, sort, favorites),
            Group(UI.T("Gallery.Filters.Rating", "Rating"), minRating, maxRating),
            Group(UI.T("Gallery.Filters.Media", "Media type"), images, videos, models, shared),
            Group(UI.T("Gallery.Filters.Relation", "Relation and status"), faces, manual, unresolved, related));
        var header = UI.Surface(UI.V(10, titleRow, groups, _notice), Material.Frost, 0, 0);
        header.BorderThickness = new Thickness(0, 0, 0, 1);
        header.Padding = new Thickness(24, 16, 24, 12);
        return header;
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        foreach (var filter in _vm.ActiveFilters)
        {
            _chips.Children.Add(UI.Chip($"{filter.DisplayText}  ✕", onClick: () => _vm.RemoveFilterCommand.Execute(filter.Kind)));
        }

        if (_vm.ActiveFilters.Count > 0)
        {
            _chips.Children.Add(UI.Button(UI.T("Gallery.ClearFilters", "Clear filters"), () => _vm.ClearFilters(), ButtonKind.Ghost));
        }
    }

    private FrameworkElement Pagination()
    {
        var pager = new PaginationBar(
            _vm,
            () => _vm.RangeText,
            () => _vm.PageIndex,
            () => _vm.TotalPages,
            GalleryViewModel.PageSizeOptions,
            () => _vm.PageSize,
            size => _vm.SetPageSizeCommand.Execute(size),
            page => _ = _vm.GoToPageAsync(page));
        Bag.Add(pager);
        return pager.View.Margin(24, 6, 24, 10);
    }

    private void UpdateState()
    {
        _count.Text = _vm.ResultCountText;
        var notice = _vm.ErrorNotice ?? (_vm.HasError ? _vm.ErrorMessage : null);
        _notice.Text = notice ?? string.Empty;
        _notice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
        _empty.Children.Clear();
        if (_vm.IsEmptyLibrary || _vm.IsNoResults)
        {
            _empty.Visibility = Visibility.Visible;
            _empty.Children.Add(UI.V(12,
                new IconView("icon.navigation.gallery", 40, "textMuted").Align(HorizontalAlignment.Center),
                UI.Text(_vm.IsEmptyLibrary ? UI.T("Gallery.Empty", "Your collection is empty.") : UI.T("Gallery.NoResults", "No Profiles match these filters."), "section-title").Align(HorizontalAlignment.Center),
                _vm.IsEmptyLibrary
                    ? UI.Button(UI.T("Home.Empty.Import", "Import media"), () => Services.Navigation.ResetToTopLevel(new ImportRoute()), ButtonKind.Primary).Align(HorizontalAlignment.Center)
                    : UI.Button(UI.T("Gallery.ClearFilters", "Clear filters"), () => _vm.ClearFilters()).Align(HorizontalAlignment.Center)).Align(HorizontalAlignment.Center, VerticalAlignment.Center));
        }
        else
        {
            _empty.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- cards

    private CompiledDefinition CardFor(GalleryCardViewModel card)
    {
        if (_cardByProfile.TryGetValue(card.ProfileId, out var cached))
        {
            return cached;
        }

        var state = new ProfilePresentationState(card.ProfileId, 0, null, card.Appearance);
        var context = new PresentationContext("gallery", card.ProfileId, null, state);
        var resolved = Services.Presentation.Resolve(PresentationSlots.GalleryCard, context);
        var definition = resolved.Source == ResolutionSource.Profile ? Services.Presentation.ResolveCompiled(PresentationSlots.GalleryCard, context) : _globalCard;
        _cardByProfile[card.ProfileId] = definition;
        return definition;
    }

    private string KindOf(object? data) => data is GalleryCardViewModel card ? CardFor(card).Ref.ToString() : "empty";

    private FrameworkElement Create(string kind, object? data)
    {
        var definition = data is GalleryCardViewModel card ? CardFor(card) : _globalCard;
        var host = new Grid();
        var visual = new CardVisual(definition);
        host.Children.Add(visual);
        if (_layout.Primitive == CollectionPrimitives.List)
        {
            host.Height = _layout.RowHeight;
        }
        else if (_layout.Primitive == CollectionPrimitives.Carousel)
        {
            var aspect = _layout.ItemAspect ?? definition.PlanAs<CardPlan>().Aspect;
            var viewportWidth = Math.Max(320, _field.ActualWidth > 0 ? _field.ActualWidth : _root.ActualWidth);
            var viewportHeight = Math.Max(240, _field.ActualHeight > 0 ? _field.ActualHeight : 540);
            host.Width = Math.Clamp(_layout.ItemMinWidth * _density.CardScale, 180, Math.Min(520, viewportWidth * 0.72));
            host.Height = host.Width / aspect;
            if (host.Height > viewportHeight * 0.68)
            {
                host.Height = viewportHeight * 0.68;
                host.Width = host.Height * aspect;
            }
        }

        host.Tapped += (_, _) =>
        {
            if (host.DataContext is GalleryCardViewModel item)
            {
                HoverVideoCoordinator.Shared.StopAll();
                _vm.SelectProfile(item.ProfileId);
                _vm.OpenProfile(item.ProfileId, CaptureOrigin());
            }
        };
        host.PointerEntered += (_, _) =>
        {
            if (host.DataContext is GalleryCardViewModel { HasBannerMotion: true } item && _density.BannerMotion == BannerMotionMode.HoverOnly && visual.Plan.UsesBanner)
            {
                var media = Services.Presentation.ResolveMedia(new ProfilePresentationState(item.ProfileId, 0, null, item.Appearance), PresentationSlots.CardCover, PresentationSlots.CardBanner);
                HoverVideoCoordinator.Shared.Request(host, item.ProfileId, "GalleryBanner", item.BannerMotionPath, media.Playback.Start, media.Playback.Duration, media.Playback.LoopMode == "loop");
            }
        };
        host.PointerExited += (_, _) => HoverVideoCoordinator.Shared.Release(host);
        return host;
    }

    private void Bind(FrameworkElement element, object? data)
    {
        if (data is not GalleryCardViewModel card || element is not Grid host || host.Children[0] is not CardVisual visual)
        {
            return;
        }

        (host.Tag as IDisposable)?.Dispose();
        host.DataContext = card;
        void UpdateCard() => ApplyCardData(host, visual, card);
        host.Tag = Observe.Props(
            card,
            UpdateCard,
            nameof(GalleryCardViewModel.IsSelected),
            nameof(GalleryCardViewModel.DisplayName),
            nameof(GalleryCardViewModel.CategoryName),
            nameof(GalleryCardViewModel.Tags),
            nameof(GalleryCardViewModel.Rating),
            nameof(GalleryCardViewModel.IsFavorite),
            nameof(GalleryCardViewModel.MediaCount),
            nameof(GalleryCardViewModel.RelatedProfileCount),
            nameof(GalleryCardViewModel.Appearance),
            nameof(GalleryCardViewModel.CoverAppearance),
            nameof(GalleryCardViewModel.BannerPresentation),
            nameof(GalleryCardViewModel.CoverSource),
            nameof(GalleryCardViewModel.BannerStillSource),
            nameof(GalleryCardViewModel.BannerMotionPath));
        UpdateCard();
    }

    private void ApplyCardData(Grid host, CardVisual visual, GalleryCardViewModel card)
    {
        var plan = visual.Plan;
        var coverWidth = (int)Math.Clamp(_layout.ItemMinWidth * _density.CardScale * 1.5, 160, 720);
        var data2 = CardDataFactory.From(card, Services.Presentation, coverWidth, plan.UsesBanner ? coverWidth * 2 : coverWidth);
        data2 = data2 with
        {
            Tags = _density.ShowTags ? data2.Tags : [],
            Category = _density.ShowCategory ? data2.Category : null,
            Rating = _density.ShowRating ? data2.Rating : null,
            Tier = _density.ShowTier ? data2.Tier : null,
            RelatedCount = _density.ShowRelated ? data2.RelatedCount : 0,
        };
        visual.Bind(data2);
        visual.IsSelected = card.IsSelected;
        ToolTipService.SetToolTip(host, card.DisplayName);
    }

    private static void Recycle(FrameworkElement element)
    {
        if (element is Grid host)
        {
            HoverVideoCoordinator.Shared.Release(host);
            (host.Tag as IDisposable)?.Dispose();
            host.Tag = null;
            host.DataContext = null;
        }
    }
}

/// <summary>A simple wrapping row for toolbars (few, fixed children — not for collections).</summary>
public sealed class VariableWrap : Panel
{
    private readonly double _spacing;

    public VariableWrap(double spacing, params UIElement[] children)
    {
        _spacing = spacing;
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                x = 0;
                y += row + _spacing;
                row = 0;
            }

            x += size.Width + _spacing;
            row = Math.Max(row, size.Height);
            width = Math.Max(width, x);
        }

        return new Windows.Foundation.Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, y + row);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += row + _spacing;
                row = 0;
            }

            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width + _spacing;
            row = Math.Max(row, size.Height);
        }

        return finalSize;
    }
}
