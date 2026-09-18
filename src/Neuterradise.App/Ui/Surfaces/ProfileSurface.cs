using System.ComponentModel;
using Microsoft.UI.Xaml;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Resources;
using Neuterradise.App.SystemServices.Database.Reads;
using Windows.Media.Core;

namespace Neuterradise.App.Ui;

public sealed class ProfileSurface : Surface
{
    private readonly ProfileDetailViewModel _vm;
    private readonly Grid _root = UI.Grid("*", "*,auto");
    private readonly BackdropView _environment = new();
    private readonly StackPanel _page = UI.V(0);
    private readonly ContentControl _heroHost = new();
    private readonly ContentControl _modulesHost = new();
    private readonly Grid _inspectorHost = new() { Visibility = Visibility.Collapsed, Width = 400 };
    private readonly MediaPlayerElement _bannerVideo = new() { Stretch = Stretch.UniformToFill, AreTransportControlsEnabled = false, AutoPlay = false, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly MediaGridPanel _media;
    private ProfileLayoutDefinition _layout = null!;
    private EnvironmentPlan _env = null!;
    private Disposables _inspectorBag = new();
    private bool _modulesRefreshQueued;

    public ProfileSurface(AppServices services, ProfileDetailViewModel vm) : base(services)
    {
        _vm = vm;
        _media = new MediaGridPanel(services, vm.MediaGrid, () => Context);
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _page.Children.Add(_heroHost);
        _page.Children.Add(_modulesHost);
        var main = new Grid();
        main.Children.Add(_environment);
        var scroll = UI.Scroll(_page);
        scroll.ViewChanged += (_, _) =>
        {
            _vm.MediaGrid.NotifyScrolled();
            HoverVideoCoordinator.Shared.StopAll();
        };
        main.Children.Add(scroll);
        _root.Children.Add(main.At(0, 0));
        _root.Children.Add(_inspectorHost.At(0, 1));
        _root.SizeChanged += (_, _) =>
        {
            _inspectorHost.Width = _root.ActualWidth < 1100 ? Math.Max(320, _root.ActualWidth * 0.45) : 400;
            RefreshVisualLayout();
        };

        Bag.Add(Observe.Props(_vm, Rebuild,
            nameof(ProfileDetailViewModel.Profile),
            nameof(ProfileDetailViewModel.CurrentPresetId),
            nameof(ProfileDetailViewModel.AppearanceOverrides),
            nameof(ProfileDetailViewModel.CoverSource),
            nameof(ProfileDetailViewModel.BannerStillSource),
            nameof(ProfileDetailViewModel.Status),
            nameof(ProfileDetailViewModel.ErrorMessage)));
        Bag.Add(Observe.Props(_vm, RefreshHero,
            nameof(ProfileDetailViewModel.DisplayName),
            nameof(ProfileDetailViewModel.Rating),
            nameof(ProfileDetailViewModel.IsFavorite),
            nameof(ProfileDetailViewModel.CategoryName),
            nameof(ProfileDetailViewModel.Tags),
            nameof(ProfileDetailViewModel.FolderStatusMessage),
            nameof(ProfileDetailViewModel.RenameStatusMessage)));
        Bag.Add(Observe.Props(_vm, QueueModulesRefresh,
            nameof(ProfileDetailViewModel.Overview),
            nameof(ProfileDetailViewModel.Notes)));
        Bag.Add(Observe.Collection(_vm.RelatedProfiles, QueueModulesRefresh));
        Bag.Add(Observe.Props(
            _vm.Related,
            QueueModulesRefresh,
            nameof(Neuterradise.App.RelatedProfiles.RelatedProfilesViewModel.StatusMessage)));
        Bag.Add(Observe.Collection(_vm.Faces, QueueModulesRefresh));
        Bag.Add(Observe.Collection(_vm.RecentMedia, QueueModulesRefresh));
        Bag.Add(Observe.Collection(_vm.AssignmentReviewClusters, QueueModulesRefresh));
        Bag.Add(Observe.Props(_vm, QueueModulesRefresh, nameof(ProfileDetailViewModel.UnknownStatusNotice)));
        Bag.Add(Observe.Props(_vm, UpdateInspectorAndLayout, nameof(ProfileDetailViewModel.Inspector), nameof(ProfileDetailViewModel.IsInspectorOpen)));
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    private ProfilePresentationState State => new(_vm.ProfileId, _vm.RowVersion, _vm.CurrentPresetId, _vm.AppearanceOverrides ?? ProfileAppearanceOverrides.Default);

    private PresentationContext Context => PresentationContext.ForProfile(State);

    protected override void OnActivated(AppRoute route)
    {
        if (route is ProfileRoute profileRoute && profileRoute.ProfileId == _vm.ProfileId)
        {
            if (profileRoute.Origin is not null)
            {
                _vm.MediaGrid.RestoreState(profileRoute.Origin);
            }

            if (profileRoute.InspectAssetId is { } inspectAssetId && inspectAssetId != Guid.Empty)
            {
                _vm.OpenInspector(inspectAssetId);
            }
            else
            {
                _vm.CloseInspector();
            }
        }

        _environment.IsSurfaceActive = true;
        PlayBanner();
    }

    protected override void OnSuspended()
    {
        _environment.IsSurfaceActive = false;
        _bannerVideo.MediaPlayer?.Pause();
        _bannerVideo.Source = null;
        HoverVideoCoordinator.Shared.StopAll();
    }

    public override void OnPresentationChanged(PresentationChangedEventArgs args) => Rebuild();

    private void Rebuild()
    {
        if (_vm.Profile is null)
        {
            var message = _vm.HasError
                ? _vm.ErrorMessage ?? UI.T("Profile.LoadFailed", "This Profile could not be opened.")
                : UI.T("Profile.Loading", "Opening Profile…");
            _heroHost.Content = UI.Text(message, "body-muted", _vm.HasError ? "danger" : null, 4).Margin(32);
            _modulesHost.Content = null;
            return;
        }

        var presentation = Services.Presentation;
        _layout = presentation.ResolvePlan<ProfileLayoutPlan>(PresentationSlots.ProfileLayout, Context).Layout;
        _env = presentation.ResolvePlan<EnvironmentPlan>(PresentationSlots.ProfileEnvironment, Context);
        _environment.Plan = _env.Backdrop;
        _environment.AmbientImagePath = _env.UsesBanner ? _vm.BannerImagePath ?? _vm.CoverImagePath : _vm.CoverImagePath ?? _vm.BannerImagePath;

        RefreshHero();
        RefreshModules();
        PlayBanner();
    }

    private void RefreshVisualLayout()
    {
        if (_vm.Profile is null || _layout is null)
        {
            return;
        }

        RefreshHero();
        QueueModulesRefresh();
    }

    private void RefreshHero()
    {
        if (_vm.Profile is null || _layout is null)
        {
            return;
        }

        _heroHost.Content = Hero();
        PlayBanner();
    }

    private void QueueModulesRefresh()
    {
        if (_modulesRefreshQueued)
        {
            return;
        }

        _modulesRefreshQueued = true;
        var dispatcher = _root.DispatcherQueue;
        if (dispatcher is not null && dispatcher.TryEnqueue(() =>
            {
                _modulesRefreshQueued = false;
                RefreshModules();
            }))
        {
            return;
        }

        _modulesRefreshQueued = false;
        RefreshModules();
    }

    private void RefreshModules()
    {
        if (_vm.Profile is null || _layout is null)
        {
            return;
        }

        _modulesHost.Content = Modules();
    }

    private void UpdateInspectorAndLayout()
    {
        UpdateInspector();
        QueueModulesRefresh();
    }

    private FrameworkElement Hero()
    {
        var theme = ThemeRuntime.Current;
        var hero = _layout.Hero;
        var width = _root.ActualWidth > 0 ? _root.ActualWidth : 1200;
        var height = hero.Height switch { ProfileLayoutHeroHeight.Low => 260.0, ProfileLayoutHeroHeight.Medium => 340.0, _ => 440.0 };
        if (width < 960)
        {
            height *= 0.8;
        }

        var media = Services.Presentation.ResolveMedia(State, null, null);
        var banner = new SkImageView { Source = _vm.BannerStillSource ?? _vm.CoverSource, Transform = _vm.BannerStillSource is null ? media.Cover : media.Banner, PlaceholderToken = "surface1", Blur = hero.Blur == ProfileLayoutBannerBlur.Subtle ? 6 : 0 };
        var art = new Grid();
        art.Children.Add(banner);
        art.Children.Add(_bannerVideo);
        var scrimAlpha = hero.Overlay switch { ProfileLayoutOverlay.Soft => 0.45, ProfileLayoutOverlay.Medium => 0.7, _ => 0.9 } * _env.ScrimStrength / 0.65;
        var scrim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, hero.Fade == ProfileLayoutHeroFade.Full ? 0 : 1), EndPoint = new Windows.Foundation.Point(0.5, 0.2) };
        var scrimColor = theme.Color("heroScrimStrong");
        scrim.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)Math.Clamp(scrimColor.A * scrimAlpha, 0, 255), scrimColor.R, scrimColor.G, scrimColor.B), Offset = 0 });
        scrim.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, scrimColor.R, scrimColor.G, scrimColor.B), Offset = 1 });
        if (hero.Fade != ProfileLayoutHeroFade.None)
        {
            art.Children.Add(new Border { Background = scrim, IsHitTestVisible = false });
        }

        var field = new Grid { Height = height };
        switch (hero.BannerMode)
        {
            case ProfileLayoutBannerMode.None:
                break;
            case ProfileLayoutBannerMode.Contained:
                field.Children.Add(new Border { Child = art, CornerRadius = new CornerRadius(theme.Tokens.Number("radiusHero", 20)), Margin = new Thickness(24, 16, 24, 0) });
                break;
            case ProfileLayoutBannerMode.Split:
                field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                field.Children.Add(art.At(0, width < 960 ? 0 : 1, 1, width < 960 ? 2 : 1));
                break;
            case ProfileLayoutBannerMode.Backdrop:
                banner.Blur = 28;
                banner.Opacity = 0.55;
                field.Children.Add(art);
                break;
            default:
                field.Children.Add(art);
                break;
        }

        var coverSize = hero.CoverSize switch { ProfileLayoutCoverSize.Small => 104.0, ProfileLayoutCoverSize.Medium => 136.0, ProfileLayoutCoverSize.Large => 176.0, _ => 212.0 };
        if (width < 960)
        {
            coverSize *= 0.8;
        }

        var frame = Services.Presentation.ResolvePlan<FramePlan>(PresentationSlots.ProfileFrame, Context);
        var overrides = _vm.AppearanceOverrides ?? ProfileAppearanceOverrides.Default;
        var cover = hero.CoverPlacement == ProfileLayoutCoverPlacement.None ? null : new CoverFrameView
        {
            Source = _vm.CoverSource,
            Transform = media.Cover,
            Plan = frame,
            Appearance = CardDataFactory.AppearanceFor(overrides, frame, ReducedMotionAuthority.IsReduced) with { DetailLevel = hero.CoverDetailLevel },
            FrameMode = hero.CoverDetailLevel == CoverFrameDetailLevel.Hidden ? "none" : hero.CoverDetailLevel == CoverFrameDetailLevel.Compact ? "lite" : "full",
            Width = coverSize,
            Height = coverSize,
        };

        var identity = Identity(hero.IdentityAlignment);
        var overlap = hero.CoverPlacement is ProfileLayoutCoverPlacement.BottomLeftOverlap or ProfileLayoutCoverPlacement.BottomCenterOverlap;
        var center = hero.CoverPlacement is ProfileLayoutCoverPlacement.BottomCenterOverlap or ProfileLayoutCoverPlacement.InlineCenter || hero.IdentityAlignment == ProfileLayoutIdentityAlignment.Center;
        var coverRight = hero.CoverPlacement == ProfileLayoutCoverPlacement.Right;
        FrameworkElement band;
        if (cover is null)
        {
            band = identity;
        }
        else if (center || width < 800)
        {
            band = UI.V(12, cover.Align(HorizontalAlignment.Center), identity);
            identity.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            var grid = UI.Grid("auto", coverRight ? "*,auto" : "auto,*");
            grid.Children.Add(cover.Margin(coverRight ? 20 : 0, 0, coverRight ? 0 : 20, 0).At(0, coverRight ? 1 : 0));
            grid.Children.Add(identity.Align(vertical: VerticalAlignment.Bottom).At(0, coverRight ? 0 : 1));
            band = grid;
        }

        var padding = UI.PagePadding(width);
        band.Margin = new Thickness(padding, overlap ? -coverSize * 0.45 : 16, padding, 8);
        band.MaxWidth = hero.ContentWidth == ProfileLayoutContentWidth.Wide ? 1600 : 1100;
        return UI.V(0, field, band);
    }

    private FrameworkElement Identity(ProfileLayoutIdentityAlignment alignment)
    {
        var name = UI.Text(_vm.DisplayName, "hero", maxLines: 2);
        var meta = UI.Text(string.Join(" · ", new[] { _vm.CategoryName, string.Join(", ", _vm.Tags.Take(5)) }.Where(s => !string.IsNullOrWhiteSpace(s))), "metadata", maxLines: 2);
        var rating = UI.H(2);
        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            var star = UI.IconButton("icon.profile.rating", UI.F("Profile.RateN", "Rate {0} of 5", value), () => Services.RunUserAction(_vm.SetRatingAsync(_vm.Rating == value ? null : value), "ProfileSurface.SetRatingAsync", UI.T("Profile.RatingFailed", "The rating could not be saved.")), 28);
            ((IconView)star.Content).ColorToken = (_vm.Rating ?? 0) >= value ? "warning" : "textMuted";
            rating.Children.Add(star);
        }

        var favorite = UI.IconButton("icon.profile.favorite", _vm.FavoriteTooltip, null, 32, _vm.ToggleFavoriteCommand);
        ((IconView)favorite.Content).ColorToken = _vm.IsFavorite ? "danger" : "textMuted";
        var actions = new VariableWrap(8,
            UI.Button(UI.T("Common.Back", "Back"), null, ButtonKind.Ghost, "icon.action.back", _vm.GoBackCommand),
            UI.Button(UI.T("Profile.Customize", "Customize Profile"), null, ButtonKind.Primary, "icon.navigation.customize", _vm.OpenCustomizeOverlayCommand),
            UI.Button(UI.T("Profile.Edit", "Edit details"), () => Services.RunUserAction(EditDetailsAsync(), "ProfileSurface.EditDetailsAsync", UI.T("Profile.EditFailed", "Profile details could not be saved.")), ButtonKind.Secondary, "icon.action.edit"),
            UI.Button(UI.T("Profile.AddMedia", "Add media"), () => Services.RunUserAction(AddMediaAsync(), "ProfileSurface.AddMediaAsync", UI.T("Profile.AddMediaFailed", "Media could not be added.")), ButtonKind.Secondary, "icon.action.add"),
            UI.Button(UI.T("Profile.AddFolder", "Add folder"), () => Services.RunUserAction(AddFolderAsync(), "ProfileSurface.AddFolderAsync", UI.T("Profile.AddMediaFailed", "Media could not be added.")), ButtonKind.Secondary, "icon.action.add"),
            UI.Button(UI.T("Profile.OpenFolder", "Open folder"), null, ButtonKind.Ghost, "icon.action.open-folder", _vm.OpenProfileFolderCommand));
        var statusText = _vm.HasError
            ? _vm.ErrorMessage
            : _vm.FolderStatusMessage ?? _vm.RenameStatusMessage;
        var status = string.IsNullOrWhiteSpace(statusText)
            ? null
            : UI.Text(statusText, "caption", _vm.HasError ? "danger" : "accent", 3);
        var content = UI.V(8, name, meta, UI.H(10, rating, favorite), actions, status);
        if (alignment == ProfileLayoutIdentityAlignment.Center)
        {
            name.TextAlignment = TextAlignment.Center;
            meta.TextAlignment = TextAlignment.Center;
            content.HorizontalAlignment = HorizontalAlignment.Center;
        }

        return UI.Surface(content, Material.Deep, ThemeRuntime.Current.Tokens.Number("radiusSurface", 16), 18);
    }

    private void PlayBanner()
    {
        _bannerVideo.MediaPlayer?.Pause();
        _bannerVideo.Visibility = Visibility.Collapsed;
        var media = Services.Presentation.ResolveMedia(State, null, null);
        var policy = media.Playback.ReducedMotionPolicy;
        var reduced = ReducedMotionAuthority.IsReduced || ResourceGovernor.Shared.Tier is PresentationTier.Fallback or PresentationTier.Suspended;
        if (!IsActive || !_vm.HasBannerMotion || _vm.BannerMotionPath is not { } path || !File.Exists(path) || (reduced && policy == "poster"))
        {
            return;
        }

        _bannerVideo.Source = MediaSource.CreateFromUri(new Uri(path));
        _bannerVideo.Visibility = Visibility.Visible;
        if (_bannerVideo.MediaPlayer is { } player)
        {
            player.IsMuted = media.Playback.Mute;
            player.IsLoopingEnabled = !reduced && media.Playback.LoopMode == "loop";
            player.PlaybackSession.PlaybackRate = media.Playback.Rate;
            player.PlaybackSession.Position = media.Playback.Start;
            if (reduced && policy == "still-frame")
            {
                player.Pause();
            }
            else
            {
                player.Play();
            }
        }
    }

    private FrameworkElement Modules()
    {
        var width = _root.ActualWidth > 0 ? _root.ActualWidth : 1200;
        var padding = UI.PagePadding(width);
        var wrap = new VariableWrap(20);
        wrap.Margin = new Thickness(padding, 20, padding, 40);
        var available = Math.Max(320, width - (padding * 2) - (_vm.IsInspectorOpen ? _inspectorHost.Width : 0));
        var density = _layout.SectionDensity switch { ProfileLayoutSectionDensity.Compact => 12.0, ProfileLayoutSectionDensity.Spacious => 28.0, _ => 20.0 };
        if (_vm.IsUnknownProfile && _vm.AssignmentReviewClusters.Count > 0)
        {
            var review = UI.Surface(
                AssignmentReview(),
                Material.Raised,
                ThemeRuntime.Current.Tokens.Number("radiusSurface", 16),
                density);
            review.Width = Math.Floor(available);
            wrap.Children.Add(review);
        }

        foreach (var module in _layout.Modules.Where(m => m.Visible).OrderBy(m => m.Order))
        {
            var content = module.Id switch
            {
                ProfileLayoutModuleId.Overview => string.IsNullOrWhiteSpace(_vm.Overview) ? null : UI.Section(UI.T("Profile.Overview", "Overview"), null, UI.Text(_vm.Overview, "body")),
                ProfileLayoutModuleId.Notes => _vm.HasNotes ? UI.Section(UI.T("Profile.Notes", "Private notes"), null, UI.Text(_vm.Notes, "body-muted")) : null,
                ProfileLayoutModuleId.Related => Related(),
                ProfileLayoutModuleId.Faces => _vm.HasFaces ? Faces() : null,
                ProfileLayoutModuleId.RecentMedia => _vm.RecentMedia.Count > 0 ? RecentMedia() : null,
                ProfileLayoutModuleId.Media => UI.Section(UI.T("Profile.Media", "Media"), null, _media.View),
                _ => null,
            };
            if (content is null)
            {
                continue;
            }

            var fraction = width < 960 ? 1 : module.Width switch { ProfileLayoutModuleWidth.Half => 0.5, ProfileLayoutModuleWidth.Third => 0.333, _ => 1.0 };
            var host = UI.Surface(content, module.Id == ProfileLayoutModuleId.Media ? Material.Grounded : Material.Raised, ThemeRuntime.Current.Tokens.Number("radiusSurface", 16), density);
            host.Width = Math.Floor((available * fraction) - (fraction < 1 ? 20 : 0));
            wrap.Children.Add(host);
        }

        return wrap;
    }

    private FrameworkElement AssignmentReview()
    {
        var rows = UI.V(10);
        foreach (var cluster in _vm.AssignmentReviewClusters)
        {
            var location = string.IsNullOrWhiteSpace(cluster.SourceDirectory)
                ? UI.T("Assignment.Cluster.UnknownSource", "same import context")
                : Path.GetFileName(cluster.SourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var hint = cluster.HasCandidate
                ? UI.F("Assignment.Cluster.Candidate", "{0} items from {1}; suggested Profile: {2}", cluster.MemberCount, location, cluster.CandidateProfileName!)
                : UI.F("Assignment.Cluster.Unresolved", "{0} items from {1} need one assignment decision", cluster.MemberCount, location);
            var actions = UI.H(8);
            if (cluster.CandidateProfileId is { } candidateId && cluster.HasCandidate)
            {
                actions.Children.Add(UI.Button(
                    UI.F("Assignment.Cluster.UseCandidate", "Assign all to {0}", cluster.CandidateProfileName!),
                    () => Services.RunUserAction(
                        _vm.AcceptAssignmentClusterAsync(cluster, candidateId),
                        "ProfileSurface.AcceptAssignmentClusterAsync",
                        UI.T("Assignment.Cluster.Failed", "The grouped assignment could not be applied.")),
                    ButtonKind.Primary));
            }

            actions.Children.Add(UI.Button(
                UI.T("Assignment.Cluster.ChooseOther", "Choose another Profile…"),
                () => Services.RunUserAction(
                    _vm.OpenAssignmentClusterPickerAsync(cluster),
                    "ProfileSurface.OpenAssignmentClusterPickerAsync",
                    UI.T("Assignment.Cluster.PickerFailed", "Profiles could not be loaded.")),
                cluster.HasCandidate ? ButtonKind.Ghost : ButtonKind.Primary));
            actions.Children.Add(UI.Button(
                UI.T("Assignment.Cluster.KeepUnknown", "Keep unassigned"),
                () => Services.RunUserAction(
                    _vm.KeepAssignmentClusterUnknownAsync(cluster),
                    "ProfileSurface.KeepAssignmentClusterUnknownAsync",
                    UI.T("Assignment.Cluster.Failed", "The grouped assignment could not be applied.")),
                ButtonKind.Ghost));
            rows.Children.Add(UI.Surface(UI.V(6, UI.Text(hint, "body"), actions), Material.Grounded));
        }

        return UI.Section(
            UI.T("Assignment.Cluster.Title", "Grouped assignment review"),
            UI.T("Assignment.Cluster.Description", "Each row contains media with the same import evidence. Suggestions are hints, never certainty."),
            rows,
            string.IsNullOrWhiteSpace(_vm.UnknownStatusNotice) ? null : UI.Text(_vm.UnknownStatusNotice, "caption"));
    }

    private FrameworkElement Related()
    {
        var row = new VariableWrap(8);
        foreach (var related in _vm.RelatedProfiles.Take(24))
        {
            var detail = related.SharedAssetCount > 0 ? UI.F("Profile.SharedMedia", "{0} shared", related.SharedAssetCount) : related.ManualRelation ? UI.T("Profile.Linked", "linked") : UI.F("Profile.Faces", "{0} faces", related.ConfirmedFaceCount);
            var open = UI.Chip($"{related.RelatedDisplayName} · {detail}", onClick: () => Services.Navigation.Navigate(new ProfileRoute(related.RelatedProfileId)));
            if (related.ManualRelation)
            {
                row.Children.Add(UI.H(4, open,
                    UI.IconButton("icon.action.delete", UI.T("Profile.Related.Remove", "Remove relation"), () => _vm.Related.RemoveManualRelationCommand.Execute(related), 24)));
            }
            else
            {
                row.Children.Add(open);
            }
        }

        if (_vm.RelatedProfiles.Count == 0)
        {
            row.Children.Add(UI.Text(UI.T("Profile.Related.Empty", "No related Profiles yet."), "body-muted"));
        }

        var status = _vm.Related.HasStatusMessage
            ? UI.Text(_vm.Related.StatusMessage, "caption", "danger")
            : null;
        var add = UI.Button(UI.T("Profile.Related.Add", "Add related Profile"), null, ButtonKind.Ghost, "icon.action.add", _vm.Related.OpenAddRelationPickerCommand);
        return UI.Section(UI.T("Profile.Related", "Related"), null,
            row,
            status,
            add);
    }

    private FrameworkElement RecentMedia()
    {
        var row = new VariableWrap(8);
        foreach (var item in _vm.RecentMedia.Take(12))
        {
            var icon = item.MediaType switch
            {
                MediaType.Video => "icon.media.video",
                MediaType.Model => "icon.media.model",
                _ => "icon.media.image",
            };
            var label = string.IsNullOrWhiteSpace(item.ManagedFileName)
                ? MediaSurfaceText.Type(item.MediaType)
                : item.ManagedFileName;
            row.Children.Add(UI.Button(label, () => _vm.OpenMediaDetail(item.AssetId), ButtonKind.Ghost, icon));
        }

        return UI.Section(
            UI.T("Profile.RecentMedia", "Recent media"),
            UI.F("Profile.RecentMediaCount", "{0} recent items", _vm.RecentMedia.Count),
            row);
    }

    private FrameworkElement Faces()
    {
        var row = UI.H(10);
        foreach (var face in _vm.Faces.Where(f => f.IsUnresolved).Take(12))
        {
            FrameworkElement crop;
            if (face.HasFaceCrop)
            {
                crop = new SkImageView { Source = ImageRef.FromPath(face.FaceCropPath, 160), Width = 72, Height = 72, Circle = true };
            }
            else
            {
                var fallback = UI.Surface(new IconView("icon.profile.face", 24), Material.Grounded, 36, 0);
                fallback.Width = 72;
                fallback.Height = 72;
                fallback.Tip(face.FaceCropFallbackText);
                crop = fallback;
            }

            // J08: Four explicit face actions.
            var actions = UI.H(4,
                // J08.1: Assign to this Profile (ConfirmFaceAsync already targets this ProfileId).
                UI.IconButton("icon.status.success", UI.T("Faces.AssignThis", "Assign to this Profile"),
                    () => Services.RunUserAction(_vm.ConfirmFaceAsync(face), "ProfileSurface.ConfirmFaceAsync",
                        UI.T("Faces.ConfirmFailed", "The face could not be confirmed.")), 26),
                // J08.2: Assign to another Profile.
                UI.IconButton("icon.profile.assign", UI.T("Faces.AssignOther", "Assign to another Profile…"),
                    () => Services.RunUserAction(_vm.ChangeFaceProfileCommand.ExecuteAsync(face), "ProfileSurface.ChangeFaceProfile",
                        UI.T("Faces.AssignFailed", "The face could not be assigned.")), 26),
                // J08.3: Ignore / Not a person.
                UI.IconButton("icon.status.error", UI.T("Faces.Reject", "Ignore / Not a person"),
                    () => Services.RunUserAction(_vm.RejectFaceAsync(face), "ProfileSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected.")), 26),
                // J08.4: Open media.
                UI.IconButton("icon.media.open", UI.T("Faces.OpenMedia", "Open media"),
                    () => _vm.OpenMediaDetailCommand.Execute(face.AssetId), 26));

            row.Children.Add(UI.V(4, crop, UI.Text(face.StatusBadgeText, "caption"), actions));
        }

        return UI.Section(UI.T("Profile.FacesTitle", "People in this media"), UI.F("Profile.FacesPending", "{0} waiting for review", _vm.UnresolvedFaceCount),
            UI.Scroll(row, horizontal: true),
            UI.Button(UI.T("Profile.ReviewAll", "Review all faces"), null, ButtonKind.Ghost, command: _vm.OpenFaceReviewWorkspaceCommand));
    }

    private async Task AddMediaAsync()
    {
        var files = await Services.PickFilesAsync(true).ConfigureAwait(true);
        if (files.Count > 0)
        {
            await Services.ImportAsync(files, _vm.ProfileId).ConfigureAwait(true);
        }
    }

    private async Task AddFolderAsync()
    {
        var folder = await Services.PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await Services.ImportAsync([folder], _vm.ProfileId).ConfigureAwait(true);
        }
    }

    private async Task EditDetailsAsync()
    {
        _vm.BeginEditMetadata();
        var name = UI.Input(UI.T("Profile.Name", "Name"), _vm.EditDisplayName, text => _vm.EditDisplayName = text);
        var category = new ComboBox { Header = UI.T("Profile.Category", "Category"), ItemsSource = _vm.AvailableCategories.Select(c => c.DisplayName).ToList(), MinWidth = 240 };
        category.SelectedIndex = _vm.AvailableCategories.ToList().FindIndex(c => c.CategoryId == _vm.EditCategoryId);
        category.SelectionChanged += (_, _) => _vm.EditCategoryId = category.SelectedIndex < 0 ? null : _vm.AvailableCategories[category.SelectedIndex].CategoryId;
        var tags = new VariableWrap(6);
        void FillTags()
        {
            tags.Children.Clear();
            foreach (var tag in _vm.AvailableTags)
            {
                var assigned = _vm.EditTags.Any(t => t.TagId == tag.TagId);
                tags.Children.Add(UI.Chip(tag.DisplayName, assigned, () =>
                {
                    if (assigned) _vm.RemoveEditTag(_vm.EditTags.First(t => t.TagId == tag.TagId));
                    else _vm.AddEditTag(tag);
                    FillTags();
                }));
            }
        }

        FillTags();

        // K03: Unified tag input field with comma/Enter commit.
        var tagInput = new TextBox
        {
            Header = UI.T("Profile.TagLabel", "Tags"),
            PlaceholderText = UI.T("Profile.TagPlaceholder", "Type a tag. Press Enter or comma to add."),
            Text = _vm.TagSearchText,
        };
        var synchronizingTagInput = false;
        tagInput.TextChanged += (_, _) =>
        {
            if (synchronizingTagInput) return;
            _vm.TagSearchText = tagInput.Text;
        };
        tagInput.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                TaskObserver.Observe(
                    _vm.OnTagInputEnterAsync().ContinueWith(_ =>
                    {
                        UiDispatch.Run(() =>
                        {
                            synchronizingTagInput = true;
                            tagInput.Text = _vm.TagSearchText;
                            synchronizingTagInput = false;
                            FillTags();
                        });
                    }),
                    "ProfileSurface.OnTagInputEnterAsync");
            }
        };
        var tagSuggestions = UI.H(6);
        void FillTagSuggestions()
        {
            tagSuggestions.Children.Clear();
            foreach (var suggestion in _vm.TagSearchResults.Take(8))
            {
                tagSuggestions.Children.Add(UI.Chip(suggestion.DisplayName, onClick: () =>
                {
                    _vm.AddEditTag(suggestion);
                    synchronizingTagInput = true;
                    tagInput.Text = _vm.TagSearchText;
                    synchronizingTagInput = false;
                    FillTags();
                }));
            }
        }
        FillTagSuggestions();

        // R05.2: Rating editor — 1-5 selectable stars plus clear.
        var ratingRow = UI.H(4);
        var ratingIcons = new List<IconView>();
        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            ratingRow.Children.Add(UI.IconButton("icon.profile.rating", $"{value}", () =>
            {
                _vm.EditRating = _vm.EditRating == value ? null : value;
                UpdateRatingIcons();
            }, 28));
            if (ratingRow.Children[^1] is Button rb && rb.Content is IconView ri)
            {
                ratingIcons.Add(ri);
            }
        }
        void UpdateRatingIcons()
        {
            for (var idx = 0; idx < ratingIcons.Count; idx++)
            {
                ratingIcons[idx].ColorToken = _vm.EditRating is { } r && r >= idx + 1 ? "warning" : "textMuted";
            }
        }
        UpdateRatingIcons();

        // R05.3: Favorite toggle in edit draft.
        var favoriteToggle = new ToggleSwitch { Header = UI.T("Profile.Favorite", "Favorite"), IsOn = _vm.EditIsFavorite };
        var syncingFav = false;
        favoriteToggle.Toggled += (_, _) =>
        {
            if (!syncingFav) _vm.EditIsFavorite = favoriteToggle.IsOn;
        };

        var overview = new TextBox { Header = UI.T("Profile.Overview", "Overview"), PlaceholderText = UI.T("Profile.Overview.Placeholder", "Optional profile description"), Text = _vm.EditOverview ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        overview.TextChanged += (_, _) => _vm.EditOverview = overview.Text;
        var notes = new TextBox { Header = UI.T("Profile.Notes", "Private notes"), Text = _vm.EditNotes ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        notes.TextChanged += (_, _) => _vm.EditNotes = notes.Text;

        // R05.4: Appearance shortcuts to the existing Customization Center.
        var appearanceSection = UI.V(6,
            UI.Text(UI.T("Profile.Appearance", "Appearance"), "body-strong"),
            UI.H(8,
                UI.Button(UI.T("Profile.EditCoverBanner", "Edit Cover & Banner"), () =>
                {
                    Services.Root!.Dialogs.Close(dialog, () => { });
                    TaskObserver.Observe(_vm.OpenCustomizeOverlayAsync(), "ProfileSurface.OpenCustomizeOverlayAsync");
                }, ButtonKind.Secondary),
                UI.Button(UI.T("Profile.EditFrame", "Edit Frame"), () =>
                {
                    Services.Root!.Dialogs.Close(dialog, () => { });
                    TaskObserver.Observe(_vm.OpenCustomizeOverlayAsync(), "ProfileSurface.OpenCustomizeOverlayAsync");
                }, ButtonKind.Secondary),
                UI.Button(UI.T("Profile.CustomizeLayoutCard", "Customize Layout & Card"), () =>
                {
                    Services.Root!.Dialogs.Close(dialog, () => { });
                    TaskObserver.Observe(_vm.OpenCustomizeOverlayAsync(), "ProfileSurface.OpenCustomizeOverlayAsync");
                }, ButtonKind.Secondary)));

        var status = UI.Text(string.Empty, "caption", "danger");
        status.Visibility = Visibility.Collapsed;
        var save = UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Primary);
        var cancel = UI.Button(UI.T("Overlay.Cancel", "Cancel"));
        var dialog = Services.Root!.Dialogs.Show(UI.V(12, UI.Text(UI.T("Profile.Edit", "Edit details"), "section-title"), name, category,
            tagInput, tagSuggestions, tags,
            UI.H(12, UI.Text(UI.T("Profile.Rating", "Rating"), "control"), ratingRow),
            favoriteToggle,
            overview, notes,
            appearanceSection,
            status, UI.H(8, cancel, save).Align(HorizontalAlignment.Right)), 600);
        var completion = new TaskCompletionSource();
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            cancel.IsEnabled = false;
            status.Visibility = Visibility.Collapsed;
            await _vm.SaveMetadataAsync().ConfigureAwait(true);
            if (!_vm.IsEditingMetadata)
            {
                Services.Root!.Dialogs.Close(dialog, () => completion.TrySetResult());
                return;
            }

            status.Text = UI.T("Profile.Metadata.Failed", "Profile metadata could not be updated.");
            status.Visibility = Visibility.Visible;
            save.IsEnabled = true;
            cancel.IsEnabled = true;
        };
        cancel.Click += (_, _) =>
        {
            _vm.CancelEditMetadata();
            Services.Root!.Dialogs.Close(dialog, () => completion.TrySetResult());
        };
        await completion.Task.ConfigureAwait(true);
    }

    private void UpdateInspector()
    {
        _inspectorBag.Dispose();
        _inspectorBag = new Disposables();
        _inspectorHost.Children.Clear();
        if (!_vm.IsInspectorOpen || _vm.Inspector is not { } inspector)
        {
            _inspectorHost.Visibility = Visibility.Collapsed;
            return;
        }

        _inspectorHost.Visibility = Visibility.Visible;
        var panel = new InspectorPanel(Services, inspector, () => _vm.CloseInspector(), Context);
        _inspectorBag.Add(panel);
        _inspectorHost.Children.Add(panel.View);
    }

    public override void Dispose()
    {
        OnSuspended();
        _inspectorBag.Dispose();
        _media.Dispose();
        var bannerPlayer = _bannerVideo.MediaPlayer;
        bannerPlayer?.Pause();
        _bannerVideo.Source = null;
        bannerPlayer?.Dispose();
        base.Dispose();
    }
}

public sealed class MediaGridPanel : IDisposable
{
    private readonly AppServices _services;
    private readonly MediaGridViewModel _vm;
    private readonly Func<PresentationContext> _context;
    private readonly Grid _root = UI.Grid("auto,*,auto", "*");
    private readonly ItemsRepeater _repeater;
    private readonly PooledElementFactory _factory;
    private readonly TextBlock _state = UI.Text(string.Empty, "body-muted");
    private readonly Disposables _bag = new();
    private readonly Dictionary<FrameworkElement, PropertyChangedEventHandler> _cardObservers = new();
    private MediaTilePlan _tile = null!;
    private MediaBorderPlan _border = null!;
    private MediaInfoPlan _info = null!;

    public MediaGridPanel(AppServices services, MediaGridViewModel vm, Func<PresentationContext> context)
    {
        _services = services;
        _vm = vm;
        _context = context;
        _factory = new PooledElementFactory(KindOf, Create, Bind, Recycle);
        var (scroll, repeater) = Repeaters.Virtualized(_factory, Repeaters.Grid(180, 180, 10));
        _repeater = repeater;
        scroll.MaxHeight = 900;
        scroll.ViewChanged += (_, _) =>
        {
            _vm.NotifyScrolled();
            HoverVideoCoordinator.Shared.StopAll();
        };
        _root.Children.Add(Toolbar().At(0));
        _root.Children.Add(new Grid { Children = { scroll, _state } }.At(1));
        var pager = new PaginationBar(
            _vm,
            () => MediaSurfaceText.Range(_vm),
            () => _vm.CurrentPage,
            () => _vm.TotalPages,
            MediaGridViewModel.PageSizeOptions,
            () => _vm.PageSize,
            size => _vm.PageSize = size,
            _vm.GoToPage);
        _bag.Add(pager);
        _root.Children.Add(pager.View.Margin(0, 8, 0, 0).At(2));
        ResolvePlans();
        _repeater.ItemsSource = _vm.Cards;
        _bag.Add(Observe.Props(_vm, UpdateState, nameof(MediaGridViewModel.State), nameof(MediaGridViewModel.RangeText), nameof(MediaGridViewModel.SelectionCount)));
        services.PresentationChanged += OnPresentationChanged;
        _bag.Add(() => services.PresentationChanged -= OnPresentationChanged);
    }

    public FrameworkElement View => _root;

    private void OnPresentationChanged(PresentationChangedEventArgs args)
    {
        if (args.PacksChanged || args.Slots.Any(s => s.StartsWith("media.", StringComparison.Ordinal) || s.StartsWith("appearance.", StringComparison.Ordinal)))
        {
            ResolvePlans();
        }
    }

    private void ResolvePlans()
    {
        var context = _context() with { Surface = "media" };
        _tile = _services.Presentation.ResolvePlan<MediaTilePlan>(PresentationSlots.MediaTile, context);
        _border = _services.Presentation.ResolvePlan<MediaBorderPlan>(PresentationSlots.MediaBorder, context);
        _info = _services.Presentation.ResolvePlan<MediaInfoPlan>(PresentationSlots.MediaInfo, context);
        var width = _tile.LegacyVariant == "Compact" ? 150 : 190;
        _repeater.Layout = Repeaters.Grid(width, (width / _tile.Aspect) + (_info.Placement == "below" ? 40 : 0), 10);
        _factory.Clear();
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = _vm.Cards;
    }

    private FrameworkElement Toolbar()
    {
        var typeValues = Enum.GetValues<MediaTypeFilter>();
        var type = new ComboBox { ItemsSource = typeValues.Select(MediaSurfaceText.TypeFilter).ToList(), SelectedIndex = (int)_vm.TypeFilter };
        var synchronizingType = false;
        type.SelectionChanged += (_, _) =>
        {
            if (!synchronizingType && type.SelectedIndex >= 0)
            {
                _vm.TypeFilter = (MediaTypeFilter)type.SelectedIndex;
            }
        };
        _bag.Add(Observe.Props(_vm, () =>
        {
            synchronizingType = true;
            type.SelectedIndex = (int)_vm.TypeFilter;
            synchronizingType = false;
        }, nameof(MediaGridViewModel.TypeFilter)));

        var relationValues = Enum.GetValues<MediaRelationFilter>();
        var relation = new ComboBox { ItemsSource = relationValues.Select(MediaSurfaceText.RelationFilter).ToList(), SelectedIndex = (int)_vm.RelationFilter };
        var synchronizingRelation = false;
        relation.SelectionChanged += (_, _) =>
        {
            if (!synchronizingRelation && relation.SelectedIndex >= 0)
            {
                _vm.RelationFilter = (MediaRelationFilter)relation.SelectedIndex;
            }
        };
        _bag.Add(Observe.Props(_vm, () =>
        {
            synchronizingRelation = true;
            relation.SelectedIndex = (int)_vm.RelationFilter;
            synchronizingRelation = false;
        }, nameof(MediaGridViewModel.RelationFilter)));

        var sortValues = Enum.GetValues<MediaGridSort>();
        var sort = new ComboBox { ItemsSource = sortValues.Select(MediaSurfaceText.Sort).ToList(), SelectedIndex = (int)_vm.Sort };
        var synchronizingSort = false;
        sort.SelectionChanged += (_, _) =>
        {
            if (!synchronizingSort && sort.SelectedIndex >= 0)
            {
                _vm.Sort = (MediaGridSort)sort.SelectedIndex;
            }
        };
        _bag.Add(Observe.Props(_vm, () =>
        {
            synchronizingSort = true;
            sort.SelectedIndex = (int)_vm.Sort;
            synchronizingSort = false;
        }, nameof(MediaGridViewModel.Sort)));

        var favorites = new ToggleButton { Content = UI.T("Gallery.Favorites", "Favorites"), IsChecked = _vm.IsFavoriteOnly };
        favorites.Click += (_, _) => _vm.IsFavoriteOnly = favorites.IsChecked == true;
        _bag.Add(Observe.Props(_vm, () => favorites.IsChecked = _vm.IsFavoriteOnly, nameof(MediaGridViewModel.IsFavoriteOnly)));
        return new VariableWrap(8, type, relation, sort, favorites,
            UI.Button(UI.T("Media.Customize", "Customize media"), () => _services.OpenCustomization(CustomizationCategories.Media, _context() with { Surface = "media" }), ButtonKind.Ghost, "icon.navigation.customize")).Margin(0, 0, 0, 10);
    }

    private void UpdateState()
    {
        _state.Text = _vm.State == MediaGridState.Ready ? string.Empty : MediaSurfaceText.State(_vm.State);
        var range = MediaSurfaceText.Range(_vm);
        _state.Text = _vm.HasSelection ? UI.F("Media.Selected", "{0} selected · {1}", _vm.SelectionCount, range) : _state.Text;
        _state.Visibility = string.IsNullOrEmpty(_state.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string KindOf(object? data) => data is MediaGridCardViewModel card ? card.MediaType.ToString() : "empty";

    private FrameworkElement Create(string kind, object? data)
    {
        var theme = ThemeRuntime.Current;
        var host = new Grid();
        var frame = new Border
        {
            CornerRadius = new CornerRadius(_border.Radius),
            BorderThickness = new Thickness(_border.Thickness),
            BorderBrush = theme.Brush(_border.Color.Replace("token:", string.Empty, StringComparison.Ordinal)),
            Background = theme.Brush("surface2"),
        };
        var content = new Grid();
        var image = new SkImageView { CornerRadiusValue = Math.Max(0, _border.Radius - _border.Thickness) };
        content.Children.Add(image);
        var info = UI.V(0, UI.Text(null, "caption", _info.Placement == "below" ? "textSecondary" : "onMediaPrimary", 1), UI.Text(null, "micro", _info.Placement == "below" ? "textMuted" : "onMediaSecondary", 1));
        if (_info.Placement is "overlay-bottom" or "hover")
        {
            var strip = new Border
            {
                Child = info,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = theme.Brush(_tile.Overlay == "glass-strip" ? "glassStrip" : "mediaOverlay"),
                Opacity = _info.Placement == "hover" ? 0 : 1,
            };
            content.Children.Add(strip);
        }

        var badges = UI.H(4);
        badges.HorizontalAlignment = HorizontalAlignment.Right;
        badges.VerticalAlignment = VerticalAlignment.Top;
        badges.Margin = new Thickness(6);
        content.Children.Add(badges);
        var favorite = UI.IconButton("icon.action.favorite", UI.T("Card.Favorite", "Favorite"), null, 26);
        favorite.HorizontalAlignment = HorizontalAlignment.Left;
        favorite.VerticalAlignment = VerticalAlignment.Top;
        favorite.Margin = new Thickness(4);
        favorite.Click += (_, _) =>
        {
            if (host.DataContext is MediaGridCardViewModel card)
            {
                _services.RunUserAction(_vm.ToggleFavoriteAsync(card), "ProfileSurface.ToggleFavoriteAsync", UI.T("Profile.FavoriteFailed", "The favorite state could not be saved."));
            }
        };
        content.Children.Add(favorite);
        frame.Child = content;
        host.Children.Add(frame);
        if (_info.Placement == "below")
        {
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.Children.Add(info.Margin(2, 6, 2, 0).At(1));
        }

        var scale = new ScaleTransform();
        host.RenderTransform = scale;
        host.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        host.PointerEntered += (_, _) =>
        {
            if (host.DataContext is not MediaGridCardViewModel card)
            {
                return;
            }

            _vm.HandleCardPointerEnter(card);
            if (_tile.Hover == "lift" && !ReducedMotionAuthority.IsReduced)
            {
                scale.ScaleX = scale.ScaleY = 1.03;
            }

            if (_info.Placement == "hover" && content.Children[1] is Border strip)
            {
                strip.Opacity = 1;
            }

        };
        host.PointerExited += (_, _) =>
        {
            scale.ScaleX = scale.ScaleY = 1;
            if (_info.Placement == "hover" && content.Children[1] is Border strip)
            {
                strip.Opacity = 0;
            }

            if (host.DataContext is MediaGridCardViewModel card)
            {
                _vm.HandleCardPointerLeave(card);
            }

            HoverVideoCoordinator.Shared.Release(content);
        };
        host.PointerPressed += (_, e) =>
        {
            if (host.DataContext is not MediaGridCardViewModel card)
            {
                return;
            }

            var point = e.GetCurrentPoint(host);
            var modifiers = SystemServices.ModifierKeys.None;
            var state = e.KeyModifiers;
            if (state.HasFlag(Windows.System.VirtualKeyModifiers.Control)) modifiers |= SystemServices.ModifierKeys.Control;
            if (state.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) modifiers |= SystemServices.ModifierKeys.Shift;
            var button = point.Properties.IsRightButtonPressed ? SystemServices.MouseButton.Right : SystemServices.MouseButton.Left;
            HoverVideoCoordinator.Shared.StopAll();
            _vm.HandleCardPointerDown(card, button, 1, modifiers, new SystemServices.Point(point.Position.X, point.Position.Y));
        };
        return host;
    }

    private void Bind(FrameworkElement element, object? data)
    {
        if (data is not MediaGridCardViewModel card || element is not Grid host || host.Children[0] is not Border frame || frame.Child is not Grid content)
        {
            return;
        }

        RetireCardObserver(host);
        host.DataContext = card;
        ApplyCardVisual(host, card, frame, content);

        PropertyChangedEventHandler observer = (_, _) => UiDispatch.Run(() =>
        {
            if (ReferenceEquals(host.DataContext, card)
                && host.Children[0] is Border currentFrame
                && currentFrame.Child is Grid currentContent)
            {
                ApplyCardVisual(host, card, currentFrame, currentContent);
                if (card.IsVideo && card.IsHovered && card.ActivePreviewPath is { } path)
                {
                    HoverVideoCoordinator.Shared.RequestReady(currentContent, card.AssetId, "ProfileMediaVideo", path);
                }
                else if (!card.IsActivePreview)
                {
                    HoverVideoCoordinator.Shared.Release(currentContent);
                }
            }
        });
        card.PropertyChanged += observer;
        _cardObservers[host] = observer;
    }

    private void ApplyCardVisual(Grid host, MediaGridCardViewModel card, Border frame, Grid content)
    {
        var theme = ThemeRuntime.Current;
        var image = (SkImageView)content.Children[0];
        image.Transform = _tile.Fit == "fit" ? MediaTransformState.Default with { Fit = "fit" } : MediaTransformState.Default;
        image.Source = card.ThumbnailSource ?? ImageRef.FromPath(card.ThumbnailPath, 360);
        frame.BorderBrush = theme.Brush((card.IsSelected ? _border.SelectedColor : _border.Color).Replace("token:", string.Empty, StringComparison.Ordinal));
        frame.BorderThickness = new Thickness(card.IsSelected ? Math.Max(2, _border.Thickness) : _border.Thickness);
        var info = content.Children.OfType<Border>().FirstOrDefault()?.Child as StackPanel ?? host.Children.OfType<StackPanel>().FirstOrDefault();
        if (info is not null && _info.Placement != "hidden")
        {
            var lines = info.Children.OfType<TextBlock>().ToList();
            lines[0].Text = _info.Fields.Contains("name") ? card.CurrentManagedFileName ?? string.Empty : string.Empty;
            var parts = new List<string>();
            if (_info.Fields.Contains("type")) parts.Add(MediaSurfaceText.Type(card.MediaType));
            if (_info.Fields.Contains("duration") && card.IsVideo) parts.Add(card.FormattedDuration);
            if (_info.Fields.Contains("dimensions") && card.PixelWidth is > 0) parts.Add($"{card.PixelWidth}×{card.PixelHeight}");
            lines[1].Text = string.Join(" · ", parts);
        }

        var badges = content.Children.OfType<StackPanel>().First(p => p.HorizontalAlignment == HorizontalAlignment.Right);
        badges.Children.Clear();
        if (card.ThumbnailSource is null && string.IsNullOrWhiteSpace(card.ThumbnailPath))
        {
            badges.Children.Add(UI.Surface(new IconView(card.IsVideo ? "icon.media.video" : card.IsModel ? "icon.media.model" : "icon.media.image", 18, "onMediaPrimary"), Material.Frost, 6, 5));
        }
        if (_tile.ShowTypeBadge && !card.IsImage)
        {
            badges.Children.Add(UI.Surface(new IconView(card.IsVideo ? "icon.media.video" : "icon.media.model", 14, "onMediaPrimary"), Material.Frost, 6, 4));
        }

        if (card.HasAttention)
        {
            badges.Children.Add(UI.Badge("!", "warning"));
        }

        var favorite = content.Children.OfType<Button>().First();
        ((IconView)favorite.Content).ColorToken = card.IsFavorite ? "danger" : "onMediaSecondary";
        favorite.Opacity = card.IsFavorite ? 1 : 0.7;
        ToolTipService.SetToolTip(host, card.CurrentManagedFileName);
    }

    private void RetireCardObserver(FrameworkElement element)
    {
        if (_cardObservers.Remove(element, out var observer)
            && element.DataContext is MediaGridCardViewModel previous)
        {
            previous.PropertyChanged -= observer;
        }
    }

    private void Recycle(FrameworkElement element)
    {
        if (element is Grid host && host.DataContext is MediaGridCardViewModel card)
        {
            RetireCardObserver(host);
            _vm.HandleCardRecycled(card);
            if (host.Children[0] is Border { Child: Grid content })
            {
                HoverVideoCoordinator.Shared.Release(content);
            }

            host.DataContext = null;
        }
    }

    public void Dispose()
    {
        foreach (var element in _cardObservers.Keys.ToList())
        {
            RetireCardObserver(element);
        }

        HoverVideoCoordinator.Shared.StopAll();
        _factory.Clear();
        _bag.Dispose();
    }
}

public sealed class InspectorPanel : IDisposable
{
    private readonly AppServices _services;
    private readonly MediaDetailViewModel _vm;
    private readonly Disposables _bag = new();
    private readonly StackPanel _tab = UI.V(8);
    private readonly Grid _preview = new() { Height = 260 };
    private readonly StackPanel _imageControls = UI.H(6);
    private readonly StackPanel _modelControls = UI.H(6);
    private readonly TextBlock _zoomText = UI.Text("100%", "caption");
    private readonly MediaPlayerElement _player = new() { AreTransportControlsEnabled = true, AutoPlay = false, Stretch = Stretch.Uniform };

    public InspectorPanel(AppServices services, MediaDetailViewModel vm, Action close, PresentationContext context)
    {
        _services = services;
        _vm = vm;

        _imageControls.Children.Add(UI.IconButton("icon.action.chevron", UI.T("Inspector.ZoomOut", "Zoom out"), null, 28, _vm.ZoomOutCommand));
        _imageControls.Children.Add(_zoomText.Align(vertical: VerticalAlignment.Center));
        _imageControls.Children.Add(UI.IconButton("icon.action.add", UI.T("Inspector.ZoomIn", "Zoom in"), null, 28, _vm.ZoomInCommand));
        _imageControls.Children.Add(UI.Button(UI.T("Inspector.Fit", "Fit"), null, ButtonKind.Ghost, command: _vm.FitCommand));
        _imageControls.Children.Add(UI.Button(UI.T("Inspector.ActualSize", "Actual size"), null, ButtonKind.Ghost, command: _vm.ActualSizeCommand));

        _modelControls.Children.Add(UI.Button(UI.T("Inspector.Rotate", "Rotate"), null, ButtonKind.Ghost, command: _vm.RotateCommand));
        _modelControls.Children.Add(UI.Button(UI.T("Inspector.ResetCamera", "Reset camera"), null, ButtonKind.Ghost, command: _vm.ResetCameraCommand));

        var tabs = UI.H(4);
        foreach (var name in new[] { "Info", "Clues", "People", "Connections", "File Details" })
        {
            tabs.Children.Add(UI.Chip(UI.T("Inspector." + name.Replace(" ", string.Empty), name), false, () => _vm.SelectTabCommand.Execute(name)));
        }

        var actions = new VariableWrap(6,
            UI.IconButton("icon.action.open", UI.T("Inspector.Open", "Open with default app"), null, 32, _vm.OpenInDefaultAppCommand),
            UI.IconButton("icon.action.open-folder", UI.T("Inspector.ShowInFolder", "Show in folder"), null, 32, _vm.ShowInFolderCommand),
            UI.IconButton("icon.action.favorite", UI.T("Card.Favorite", "Favorite"), null, 32, _vm.ToggleFavoriteCommand),
            UI.IconButton("icon.action.crop", UI.T("Inspector.SetCover", "Use as Cover"), null, 32, _vm.SetAsCoverCommand),
            UI.IconButton("icon.action.layout", UI.T("Inspector.SetBanner", "Use as Banner"), null, 32, _vm.SetAsBannerCommand),
            UI.IconButton("icon.profile.face", UI.T("Inspector.ChangePrimaryProfile", "Change primary Profile"), () => services.RunUserAction(OpenProfilePickerAsync(changePrimary: true), "Inspector.ChangePrimaryProfile", UI.T("Inspector.ProfilePickerFailed", "Profiles could not be loaded.")), 32),
            UI.IconButton("icon.action.add", UI.T("Inspector.AddAssociation", "Add person association"), () => services.RunUserAction(OpenProfilePickerAsync(changePrimary: false), "Inspector.AddAssociation", UI.T("Inspector.ProfilePickerFailed", "Profiles could not be loaded.")), 32),
            UI.IconButton("icon.action.card", UI.T("Inspector.CopyPath", "Copy path"), null, 32, _vm.CopyPathCommand),
            UI.IconButton("icon.action.card", UI.T("Inspector.CopyFilename", "Copy filename"), null, 32, _vm.CopyFilenameCommand),
            UI.IconButton("icon.action.delete", UI.T("Inspector.Trash", "Move to Trash"), null, 32, _vm.MoveToTrashCommand),
            UI.IconButton("icon.navigation.customize", UI.T("Media.Customize", "Customize media"), () => services.OpenCustomization(CustomizationCategories.Media, context with { Surface = "media", ItemId = vm.AssetId })));
        var header = UI.Grid("auto", "auto,*,auto",
            UI.IconButton("icon.action.back", UI.T("Common.Previous", "Previous"), null, 30, _vm.PreviousCommand).At(0, 0),
            UI.Text(_vm.FileName, "card-title", maxLines: 1).Margin(8, 0, 8, 0).Align(vertical: VerticalAlignment.Center).At(0, 1),
            UI.H(2, UI.IconButton("icon.action.chevron", UI.T("Common.Next", "Next"), null, 30, _vm.NextCommand), UI.IconButton("icon.action.close", UI.T("Common.Close", "Close"), close, 30)).At(0, 2));
        View = UI.Surface(UI.Grid("auto,auto,auto,auto,auto,*", "*",
            header.At(0),
            _preview.Margin(0, 10, 0, 10).At(1),
            UI.V(4, _imageControls, _modelControls).At(2),
            actions.At(3),
            tabs.Margin(0, 10, 0, 6).At(4),
            UI.Scroll(_tab).At(5)), Material.Deep, 0, 14);
        _bag.Add(Observe.Props(_vm, Render));
    }

    public FrameworkElement View { get; }

    private void Render()
    {
        _zoomText.Text = _vm.ZoomText;
        _imageControls.Visibility = _vm.MediaType == MediaType.Image ? Visibility.Visible : Visibility.Collapsed;
        _modelControls.Visibility = _vm.MediaType == MediaType.Model ? Visibility.Visible : Visibility.Collapsed;

        _player.MediaPlayer?.Pause();
        _player.Source = null;
        _preview.Children.Clear();
        if (_vm.MediaType == MediaType.Video && _vm.HasPreviewFile && _vm.PreviewFilePath is { } videoPath)
        {
            if (_vm.PresentationPreviewPath is { } poster)
            {
                _preview.Children.Add(new SkImageView { Source = new ImageRef(poster, 1200), Transform = MediaTransformState.Default with { Fit = "fit" }, CornerRadiusValue = 10 });
            }
            else
            {
                _preview.Children.Add(UI.Surface(new IconView("icon.media.video", 40), Material.Grounded, 10, 0));
            }
            _player.Source = MediaSource.CreateFromUri(new Uri(videoPath));
            _preview.Children.Add(_player);
        }
        else if (_vm.MediaType == MediaType.Image && _vm.PresentationPreviewPath is { } imagePath)
        {
            var image = new SkImageView
            {
                Source = new ImageRef(imagePath, 1200),
                CornerRadiusValue = 10,
                PlaceholderToken = "surface2",
            };
            ApplyImagePreviewTransform(image);
            _preview.Children.Add(image);
        }
        else if (_vm.MediaType == MediaType.Model && _vm.PresentationPreviewPath is { } modelPreview)
        {
            _preview.Children.Add(new SkImageView
            {
                Source = new ImageRef(modelPreview, 1200),
                Transform = MediaTransformState.Default with { Fit = "fit", Rotation = _vm.RotationAngle },
                CornerRadiusValue = 10,
                PlaceholderToken = "surface2",
            });
        }

        if (_preview.Children.Count == 0)
        {
            var icon = _vm.MediaType switch
            {
                MediaType.Video => "icon.media.video",
                MediaType.Model => "icon.media.model",
                _ => "icon.media.image",
            };
            _preview.Children.Add(UI.Surface(UI.V(8,
                new IconView(icon, 38, "textMuted").Align(HorizontalAlignment.Center),
                UI.Text(_vm.FileName, "control", maxLines: 1).Align(HorizontalAlignment.Center),
                UI.Text(UI.T("Inspector.PreviewUnavailable", "Preview unavailable"), "caption", "textMuted").Align(HorizontalAlignment.Center)), Material.Raised, 10, 16));
        }

        _tab.Children.Clear();
        switch (_vm.ActiveTab)
        {
            case "Clues":
                _tab.Children.Add(Row(UI.T("Inspector.Who", "Who"), _vm.Who));
                _tab.Children.Add(Row(UI.T("Inspector.Where", "Where"), _vm.Where));
                _tab.Children.Add(Row(UI.T("Inspector.When", "When"), _vm.When));
                _tab.Children.Add(Row(UI.T("Inspector.How", "How"), _vm.How));
                break;
            case "People":
                foreach (var person in _vm.PeopleInMedia)
                {
                    _tab.Children.Add(UI.Grid("auto", "*,auto",
                        Row(person.DisplayName, MediaSurfaceText.AppearanceBasis(person.AppearanceBasis)).At(0, 0),
                        UI.IconButton(
                            "icon.action.delete",
                            UI.T("Inspector.RemoveAssociation", "Remove association"),
                            null,
                            26,
                            _vm.RemoveAssociationCommand,
                            person.ProfileId).At(0, 1)));
                }
                break;
            case "Connections":
                foreach (var linked in _vm.LinkedProfiles)
                {
                    _tab.Children.Add(UI.Text(linked.DisplayName, "body"));
                }
                break;
            case "File Details":
                foreach (var group in _vm.FileDetailGroups)
                {
                    _tab.Children.Add(UI.Text(MediaSurfaceText.DetailGroup(group.GroupName), "body-strong"));
                    foreach (var row in group.Rows)
                    {
                        _tab.Children.Add(Row(
                            MediaSurfaceText.DetailField(row.Label),
                            MediaSurfaceText.DetailValue(row.Label, row.Value)));
                    }
                }
                break;
            default:
                _tab.Children.Add(Row(UI.T("Inspector.FileName", "Filename"), _vm.FileName));
                _tab.Children.Add(Row(UI.T("Inspector.OriginalName", "Original filename"), _vm.OriginalName));
                _tab.Children.Add(Row(UI.T("Inspector.Type", "Type"), MediaSurfaceText.Type(_vm.MediaType)));
                switch (_vm.Info)
                {
                    case ImageMediaInfo image:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Format"), image.Format));
                        if (image.PixelWidth > 0 && image.PixelHeight > 0) _tab.Children.Add(Row(UI.T("Inspector.Dimensions", "Dimensions"), $"{image.PixelWidth}×{image.PixelHeight}"));
                        break;
                    case VideoMediaInfo video:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Container"), video.Container));
                        if (video.PixelWidth > 0 && video.PixelHeight > 0) _tab.Children.Add(Row(UI.T("Inspector.Dimensions", "Dimensions"), $"{video.PixelWidth}×{video.PixelHeight}"));
                        if (video.Duration > TimeSpan.Zero) _tab.Children.Add(Row(UI.T("Inspector.Duration", "Duration"), video.Duration.ToString(@"h\:mm\:ss")));
                        _tab.Children.Add(Row(UI.T("Inspector.Codec", "Codec"), string.Join(" · ", new[] { video.VideoCodec, video.AudioCodec }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
                        break;
                    case ModelMediaInfo model:
                        _tab.Children.Add(Row(UI.T("Inspector.Format", "Model format"), model.Format));
                        if (model.MeshCount is { } meshes) _tab.Children.Add(Row(UI.T("Inspector.Meshes", "Meshes"), meshes.ToString(System.Globalization.CultureInfo.CurrentCulture)));
                        if (model.MaterialCount is { } materials) _tab.Children.Add(Row(UI.T("Inspector.Materials", "Materials"), materials.ToString(System.Globalization.CultureInfo.CurrentCulture)));
                        break;
                }
                _tab.Children.Add(Row(UI.T("Inspector.Size", "Size"), _vm.ByteLengthText));
                _tab.Children.Add(Row(UI.T("Inspector.Added", "Added"), _vm.AddedToLibraryAtText));
                _tab.Children.Add(Row(UI.T("Inspector.Owner", "Profile"), _vm.PrimaryProfileName));
                _tab.Children.Add(Row(UI.T("Inspector.Location", "Location"), _vm.CurrentLibraryLocation));
                _tab.Children.Add(Row(UI.T("Inspector.Favorite", "Favorite"), _vm.IsFavorite ? UI.T("Common.Yes", "Yes") : UI.T("Common.No", "No")));
                _tab.Children.Add(Row(UI.T("Inspector.Status", "Status"), _vm.StatusText));
                if (_vm.HasActionFeedback)
                {
                    _tab.Children.Add(UI.Text(MediaSurfaceText.InspectorFeedback(_vm.ActionFeedbackMessage), "caption", "accent"));
                }
                break;
        }
    }

    private async Task OpenProfilePickerAsync(bool changePrimary)
    {
        IReadOnlyList<ProfilePickerItem> candidates = await new ProfilePickerReads(_services.Catalog)
            .GetAllCandidatesAsync(excludeProfileId: _vm.OwnerProfile?.ProfileId)
            .ConfigureAwait(true);

        if (!changePrimary && _vm.PeopleInMedia.Count > 0)
        {
            var existing = _vm.PeopleInMedia.Select(person => person.ProfileId).ToHashSet();
            candidates = candidates.Where(candidate => !existing.Contains(candidate.ProfileId)).ToArray();
        }

        var request = new ProfilePickerOverlayRequest(
            candidates,
            picked =>
            {
                if (changePrimary)
                {
                    _vm.ChangePrimaryProfileCommand.Execute(picked.ProfileId);
                }
                else
                {
                    _vm.AddAssociationCommand.Execute((picked.ProfileId, ProfileAssetRelation.Appears));
                }
            },
            title: changePrimary
                ? UI.T("Inspector.ChangePrimaryProfile", "Change primary Profile")
                : UI.T("Inspector.AddAssociation", "Add person association"),
            prompt: changePrimary
                ? UI.T("Inspector.ChangePrimaryProfilePrompt", "Choose the new primary Profile for this media item:")
                : UI.T("Inspector.AddAssociationPrompt", "Choose a Profile that appears in this media item:"));

        _services.Overlay.Push(request);
    }

    private void ApplyImagePreviewTransform(SkImageView image)
    {
        var scale = _vm.IsActualSize ? GetActualImageScale() : _vm.ZoomLevel;
        image.Transform = MediaTransformState.Default with
        {
            Fit = "fit",
            Zoom = Math.Max(1.0, scale),
        };

        if (scale < 1.0)
        {
            image.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
    }

    private double GetActualImageScale()
    {
        if (_vm.Info is not ImageMediaInfo image || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            return 1.0;
        }

        var slotWidth = _preview.ActualWidth > 1 ? _preview.ActualWidth : 360.0;
        var slotHeight = _preview.ActualHeight > 1 ? _preview.ActualHeight : 260.0;
        var fittedScale = Math.Min(slotWidth / image.PixelWidth, slotHeight / image.PixelHeight);
        return fittedScale > 0 ? 1.0 / fittedScale : 1.0;
    }

    private static FrameworkElement Row(string label, string? value) =>
        UI.Grid("auto", "120,*", UI.Text(label, "caption").At(0, 0), UI.Text(value ?? "—", "body", maxLines: 4).At(0, 1));

    public void Dispose()
    {
        var player = _player.MediaPlayer;
        player?.Pause();
        _player.Source = null;
        player?.Dispose();
        _bag.Dispose();
    }
}
