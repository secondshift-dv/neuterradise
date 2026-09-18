using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Activity;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Presentation;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Ui;

/// <summary>
/// Settings: the quietest surface. Visible prose is resolved at the presentation boundary so a warm
/// language switch never exposes renderer-neutral diagnostic strings from the backing view models.
/// </summary>
public sealed class SettingsSurface : Surface
{
    private readonly SettingsViewModel _vm;
    private readonly ExplorerLocationProvider _explorer;
    private readonly Grid _root = UI.Grid("*", "240,*");
    private readonly StackPanel _rail = UI.V(2);
    private readonly ScrollViewer _content;
    private readonly StackPanel _page = UI.V(20);
    private Disposables _sectionBag = new();

    public SettingsSurface(AppServices services, SettingsViewModel vm) : base(services)
    {
        _vm = vm;
        _explorer = new ExplorerLocationProvider(services.Paths);
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        var railScroll = UI.Scroll(_rail.Margin(12, 16, 12, 16));
        var railSurface = new Border
        {
            Child = railScroll,
            Background = ThemeRuntime.Current.Brush("surface1"),
            BorderBrush = ThemeRuntime.Current.Brush("borderSubtle"),
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        _root.Children.Add(railSurface.At(0, 0));
        _page.MaxWidth = 820;
        _content = UI.Scroll(_page.Margin(28, 20, 28, 40));
        _root.Children.Add(_content.At(0, 1));
        _root.SizeChanged += (_, _) =>
            _root.ColumnDefinitions[0].Width = new GridLength(_root.ActualWidth < 960 ? 190 : 240);

        Bag.Add(Observe.Props(_vm, Render,
            nameof(SettingsViewModel.ActiveSection),
            nameof(SettingsViewModel.ActiveSubsection),
            nameof(SettingsViewModel.Theme),
            nameof(SettingsViewModel.Density),
            nameof(SettingsViewModel.ReduceMotion),
            nameof(SettingsViewModel.VideoPreviewDurationSeconds),
            nameof(SettingsViewModel.AutoplayVideo),
            nameof(SettingsViewModel.MuteAudioOnPreview),
            nameof(SettingsViewModel.AutoAnalyzeAfterImport),
            nameof(SettingsViewModel.PreserveSourceTimestamps),
            nameof(SettingsViewModel.CurrentLanguage),
            nameof(SettingsViewModel.CurrentWindowSizeText),
            nameof(SettingsViewModel.IsWindowSizeCustom),
            nameof(SettingsViewModel.ManagedStorageFormatted),
            nameof(SettingsViewModel.TotalProfilesCount),
            nameof(SettingsViewModel.ActiveAssetsCount),
            nameof(SettingsViewModel.UnresolvedFaceCount),
            nameof(SettingsViewModel.UpdateStatusText),
            nameof(SettingsViewModel.UpdateErrorText),
            nameof(SettingsViewModel.UpdateCandidateVersion),
            nameof(SettingsViewModel.CanInstallUpdate),
            nameof(SettingsViewModel.UpdateFeedUrl),
            nameof(SettingsViewModel.Status),
            nameof(SettingsViewModel.ErrorMessage)));
        Bag.Add(Observe.Props(
            _vm,
            ApplyMediaPreviewPreferences,
            nameof(SettingsViewModel.VideoPreviewDurationSeconds),
            nameof(SettingsViewModel.AutoplayVideo),
            nameof(SettingsViewModel.MuteAudioOnPreview)));
        ApplyMediaPreviewPreferences();
        Render();
    }

    public override FrameworkElement View => _root;

    public override ScreenStateViewModel Model => _vm;

    private void ApplyMediaPreviewPreferences() => HoverVideoCoordinator.Shared.ApplyPreferences(new MediaPreferences(
        VideoPreviewDurationSeconds: _vm.VideoPreviewDurationSeconds,
        AutoplayVideo: _vm.AutoplayVideo,
        MuteAudioOnPreview: _vm.MuteAudioOnPreview));

    protected override void OnActivated(AppRoute route)
    {
        var target = route switch
        {
            TrashRoute => new SettingsRoute(SettingsSection.Trash),
            LibraryHealthRoute => new SettingsRoute(SettingsSection.System, SettingsSubsection.LibraryHealth),
            SettingsRoute settings => settings,
            _ => new SettingsRoute(),
        };
        _vm.ApplyRoute(target);
    }

    private void RenderRail()
    {
        _rail.Children.Clear();
        var theme = ThemeRuntime.Current;
        foreach (var item in _vm.RailItems)
        {
            var selected = item.Key == _vm.ActiveSection;
            var entry = new Border
            {
                Child = UI.V(1,
                    UI.Text(item.Title, "control"),
                    UI.Text(item.Description, "caption", maxLines: 2)),
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(8),
                Background = selected ? theme.Brush("surfaceSelected") : theme.Brush("transparent"),
                Tag = item.Key,
            };
            entry.Tapped += (_, _) => Services.Navigation.Navigate(new SettingsRoute(item.Key));
            _rail.Children.Add(entry);
        }
    }

    private void Render()
    {
        RenderRail();
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _sectionBag.Dispose();
        _sectionBag = new Disposables();
        _page.Children.Clear();

        var title = _vm.RailItems.FirstOrDefault(r => r.Key == _vm.ActiveSection)?.Title
            ?? UI.T("Nav.Settings", "Settings");
        _page.Children.Add(UI.Text(title, "page-title"));

        if (_vm.HasError && !string.IsNullOrWhiteSpace(_vm.ErrorMessage))
        {
            _page.Children.Add(UI.Surface(
                UI.Text(_vm.ErrorMessage, "body", "danger", maxLines: 5),
                Material.Deep,
                10,
                12));
        }

        UIElement section = (_vm.ActiveSection, _vm.ActiveSubsection) switch
        {
            (SettingsSection.General, _) => Appearance(),
            (SettingsSection.Display, _) => Display(),
            (SettingsSection.Language, _) => Language(),
            (SettingsSection.Library, SettingsSubsection.MediaPreferences) => MediaPreferences(),
            (SettingsSection.Library, SettingsSubsection.ImportPreferences) => ImportPreferences(),
            (SettingsSection.Library, _) => Library(),
            (SettingsSection.Organization, SettingsSubsection.Categories) => OrganizationCategories(),
            (SettingsSection.Organization, SettingsSubsection.Tags) => OrganizationTags(),
            (SettingsSection.Organization, _) => Organization(),
            (SettingsSection.System, SettingsSubsection.LibraryHealth) => Health(_vm.Health),
            (SettingsSection.System, _) => System(),
            (SettingsSection.People, _) => People(),
            (SettingsSection.Activity, _) => Activity(_vm.Activity),
            (SettingsSection.Trash, _) => Trash(_vm.Trash),
            (SettingsSection.About, _) => About(),
            _ => Appearance(),
        };
        _page.Children.Add(section);
        _content.ChangeView(null, 0, null, true);
    }

    private UIElement Appearance()
    {
        var swatches = new VariableWrap(10);
        foreach (var option in _vm.ThemeOptions)
        {
            var theme = ThemeRuntime.Current;
            var preview = UI.Grid("*,*", "*",
                new Border { Background = UI.Solid(ThemeColorText.Parse(option.CanvasColor)) }.At(0, 0, 2),
                new Border
                {
                    Background = UI.Solid(ThemeColorText.Parse(option.SurfaceColor)),
                    Margin = new Thickness(10, 10, 10, 0),
                    CornerRadius = new CornerRadius(6)
                }.At(0),
                new Border
                {
                    Background = UI.Solid(ThemeColorText.Parse(option.AccentColor)),
                    Height = 8,
                    Width = 40,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(12, 0, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom
                }.At(1));
            preview.Width = 132;
            preview.Height = 80;
            var card = UI.Surface(
                UI.V(6, new Border { Child = preview, CornerRadius = new CornerRadius(8) }, UI.Text(option.Name, "control")),
                Material.Raised,
                12,
                8);
            card.BorderBrush = theme.Brush(option.IsSelected ? "borderSelected" : "borderSubtle");
            card.BorderThickness = new Thickness(option.IsSelected ? 2 : 1);
            card.Tapped += (_, _) => _vm.SelectThemeCommand.Execute(option.Id);
            swatches.Children.Add(card);
        }

        var density = UI.H(6);
        foreach (var mode in Enum.GetValues<DensityMode>())
        {
            density.Children.Add(UI.Chip(
                DensityLabel(mode),
                _vm.Density == mode,
                () => _vm.SelectDensityCommand.Execute(mode.ToString())));
        }

        var reduce = new ToggleSwitch
        {
            IsOn = _vm.ReduceMotion,
            Header = UI.T("Settings.ReduceMotion", "Reduce motion")
        };
        reduce.Toggled += (_, _) => _vm.ReduceMotion = reduce.IsOn;

        return UI.V(20,
            UI.Section(
                UI.T("Customize.Theme", "Theme"),
                UI.T("Settings.Theme.Desc", "Colour, material and atmosphere. More options in the Customization Center."),
                swatches),
            UI.Section(
                UI.T("Settings.Density", "Density"),
                UI.T("Settings.Density.Desc", "Spacing and information packing. Text size stays readable."),
                density),
            UI.Section(
                UI.T("Settings.Motion", "Motion"),
                UI.T("Settings.Motion.Desc", "Reduce motion stops ambient loops and parallax everywhere, but keeps rich static materials."),
                reduce),
            UI.Button(
                UI.T("Settings.OpenCustomization", "Open the Customization Center"),
                () => Services.OpenCustomization(CustomizationCategories.Appearance),
                ButtonKind.Primary,
                "icon.navigation.customize"));
    }

    private static string DensityLabel(DensityMode mode) => mode switch
    {
        DensityMode.Compact => UI.T("Settings.Density.Compact", "Compact"),
        DensityMode.Comfortable => UI.T("Settings.Density.Comfortable", "Comfortable"),
        DensityMode.Spacious => UI.T("Settings.Density.Spacious", "Spacious"),
        _ => mode.ToString(),
    };

    private UIElement Display()
    {
        var presets = UI.V(6);
        foreach (var option in _vm.WindowSizeOptions)
        {
            presets.Children.Add(UI.Button(
                $"{option.Label} — {option.Detail}{(option.IsCurrent ? "  ✓" : string.Empty)}",
                null,
                option.IsCurrent ? ButtonKind.Primary : ButtonKind.Secondary,
                command: _vm.ApplyWindowSizePresetCommand,
                parameter: option));
        }

        var scale = UI.Text(_vm.UiScaleText, "body-strong");
        _sectionBag.Add(Observe.Props(
            _vm,
            () => scale.Text = _vm.UiScaleText,
            nameof(SettingsViewModel.UiScaleText),
            nameof(SettingsViewModel.UiScale)));

        return UI.V(20,
            UI.Section(
                UI.T("Settings.WindowSize", "Window size"),
                UI.T("Settings.WindowSize.Desc", "Choose a window size. The selected preset is marked below."),
                presets),
            UI.Section(
                UI.T("Settings.UiScale", "Interface scale"),
                UI.T("Settings.UiScale.Desc", "Ctrl + Plus / Minus / 0. Layout reflows at every scale."),
                UI.H(8,
                    UI.Button("−", null, command: _vm.DecreaseUiScaleCommand),
                    scale.Align(vertical: VerticalAlignment.Center),
                    UI.Button("+", null, command: _vm.IncreaseUiScaleCommand),
                    UI.Button(UI.T("Common.Reset", "Reset"), null, ButtonKind.Ghost, command: _vm.ResetUiScaleCommand))));
    }

    private UIElement Language() => UI.Section(
        UI.T("Settings.Language", "Language"),
        UI.T("Settings.Language.Desc", "Interface language."),
        UI.H(8,
            UI.Chip(UI.T("Settings.Language.English", "English"), _vm.IsEnglishSelected, () => _vm.SelectLanguageCommand.Execute("en")),
            UI.Chip(UI.T("Settings.Language.Indonesian", "Bahasa Indonesia"), _vm.IsIndonesianSelected, () => _vm.SelectLanguageCommand.Execute("id"))));

    private UIElement MediaPreferences()
    {
        var autoplay = new ToggleSwitch
        {
            Header = UI.T("Settings.Autoplay", "Play video previews on hover"),
            IsOn = _vm.AutoplayVideo
        };
        autoplay.Toggled += (_, _) => _vm.AutoplayVideo = autoplay.IsOn;

        var mute = new ToggleSwitch
        {
            Header = UI.T("Settings.Mute", "Mute previews"),
            IsOn = _vm.MuteAudioOnPreview
        };
        mute.Toggled += (_, _) => _vm.MuteAudioOnPreview = mute.IsOn;

        var duration = new Slider
        {
            Header = UI.T("Settings.PreviewSeconds", "Preview length (seconds)"),
            Minimum = 1,
            Maximum = 60,
            Value = _vm.VideoPreviewDurationSeconds,
            StepFrequency = 1
        };
        duration.ValueChanged += (_, e) => _vm.VideoPreviewDurationSeconds = (int)e.NewValue;

        return UI.V(14,
            autoplay,
            mute,
            duration,
            UI.Button(
                UI.T("Settings.CustomizeMedia", "Customize media tiles"),
                () => Services.OpenCustomization(CustomizationCategories.Media),
                ButtonKind.Ghost));
    }

    private UIElement ImportPreferences()
    {
        var analyze = new ToggleSwitch
        {
            Header = UI.T("Settings.AutoAnalyze", "Look for people after import"),
            IsOn = _vm.AutoAnalyzeAfterImport
        };
        analyze.Toggled += (_, _) => _vm.AutoAnalyzeAfterImport = analyze.IsOn;

        var timestamps = new ToggleSwitch
        {
            Header = UI.T("Settings.Timestamps", "Keep original file dates"),
            IsOn = _vm.PreserveSourceTimestamps
        };
        timestamps.Toggled += (_, _) => _vm.PreserveSourceTimestamps = timestamps.IsOn;

        return UI.V(14,
            analyze,
            timestamps);
    }

    private UIElement Organization() => UI.V(14,
        UI.Text(
            UI.T("Settings.Organization.Desc", "Create, rename and safely delete categories and tags. Deleting organization data never removes Profiles or media."),
            "body-muted"),
        UI.H(8,
            UI.Button(
                UI.T("Settings.Categories.Title", "Categories"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Organization, SettingsSubsection.Categories)),
                ButtonKind.Primary),
            UI.Button(
                UI.T("Settings.Tags.Title", "Tags"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Organization, SettingsSubsection.Tags)),
                ButtonKind.Primary)),
        UI.Button(
            UI.T("Settings.OpenLibrary", "Open Library Customization"),
            () => Services.OpenCustomization(CustomizationCategories.Library),
            ButtonKind.Ghost));

    private UIElement OrganizationCategories()
    {
        var list = UI.V(8);
        var message = UI.Text(string.Empty, "caption");
        var warning = UI.Text(string.Empty, "caption", "danger");
        var addName = new TextBox
        {
            PlaceholderText = UI.T("Settings.Categories.New", "New category name"),
            Text = _vm.NewCategoryName,
            MinWidth = 260
        };
        addName.TextChanged += (_, _) => _vm.NewCategoryName = addName.Text;

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in _vm.Categories)
            {
                var edit = new TextBox { Text = item.Name, MinWidth = 220 };
                item.EditName = item.Name;
                edit.TextChanged += (_, _) => item.EditName = edit.Text;
                var row = UI.Grid("auto", "*,auto",
                    UI.V(2,
                        edit,
                        UI.Text(UI.F("Settings.Organization.Usage", "Used by {0} Profiles", item.UsageCount), "caption")).At(0, 0),
                    UI.H(6,
                        UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Ghost, command: _vm.CommitRenameCategoryCommand, parameter: item),
                        UI.Button(UI.T("Common.Delete", "Delete"), null, ButtonKind.Destructive, command: _vm.DeleteCategoryCommand, parameter: item)).At(0, 1));
                row.Tapped += (_, _) => _vm.SelectedCategory = item;
                list.Children.Add(UI.Surface(row, Material.Grounded, 10, 10));
            }
        }

        void FillStatus()
        {
            message.Text = _vm.OrganizationMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Saved", "Organization changes saved.");
            message.Visibility = string.IsNullOrWhiteSpace(message.Text) ? Visibility.Collapsed : Visibility.Visible;

            warning.Text = _vm.OrganizationWarningMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Failed", "The organization change could not be saved. Resolve the conflict and try again.");
            warning.Visibility = string.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        Fill();
        FillStatus();
        _sectionBag.Add(Observe.Collection(_vm.Categories, Fill));
        _sectionBag.Add(Observe.Props(
            _vm,
            FillStatus,
            nameof(SettingsViewModel.OrganizationMessage),
            nameof(SettingsViewModel.OrganizationWarningMessage)));

        return UI.V(12,
            UI.H(8,
                addName,
                UI.Button(UI.T("Common.Add", "Add"), null, ButtonKind.Primary, command: _vm.CreateCategoryCommand)),
            message,
            warning,
            list,
            UI.Button(
                UI.T("Common.Back", "Back"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Organization)),
                ButtonKind.Ghost));
    }

    private UIElement OrganizationTags()
    {
        var list = UI.V(8);
        var message = UI.Text(string.Empty, "caption");
        var warning = UI.Text(string.Empty, "caption", "danger");
        var addName = new TextBox
        {
            PlaceholderText = UI.T("Settings.Tags.New", "New tag name (comma to add multiple)"),
            Text = _vm.NewTagName,
            MinWidth = 260
        };
        addName.TextChanged += (_, _) => _vm.NewTagName = addName.Text;
        // K04.2: Enter commits the tag(s).
        addName.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                TaskObserver.Observe(_vm.CreateTagAsync(), "SettingsSurface.CreateTagAsync");
            }
        };

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in _vm.Tags)
            {
                var edit = new TextBox { Text = item.Name, MinWidth = 220 };
                item.EditName = item.Name;
                edit.TextChanged += (_, _) => item.EditName = edit.Text;
                var row = UI.Grid("auto", "*,auto",
                    UI.V(2,
                        edit,
                        UI.Text(UI.F("Settings.Organization.Usage", "Used by {0} Profiles", item.UsageCount), "caption")).At(0, 0),
                    UI.H(6,
                        UI.Button(UI.T("Common.Save", "Save"), null, ButtonKind.Ghost, command: _vm.CommitRenameTagCommand, parameter: item),
                        UI.Button(UI.T("Common.Delete", "Delete"), null, ButtonKind.Destructive, command: _vm.DeleteTagCommand, parameter: item)).At(0, 1));
                row.Tapped += (_, _) => _vm.SelectedTag = item;
                list.Children.Add(UI.Surface(row, Material.Grounded, 10, 10));
            }
        }

        void FillStatus()
        {
            message.Text = _vm.OrganizationMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Saved", "Organization changes saved.");
            message.Visibility = string.IsNullOrWhiteSpace(message.Text) ? Visibility.Collapsed : Visibility.Visible;

            warning.Text = _vm.OrganizationWarningMessage is null
                ? string.Empty
                : UI.T("Settings.Organization.Failed", "The organization change could not be saved. Resolve the conflict and try again.");
            warning.Visibility = string.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        Fill();
        FillStatus();
        _sectionBag.Add(Observe.Collection(_vm.Tags, Fill));
        _sectionBag.Add(Observe.Props(
            _vm,
            FillStatus,
            nameof(SettingsViewModel.OrganizationMessage),
            nameof(SettingsViewModel.OrganizationWarningMessage)));

        return UI.V(12,
            UI.H(8,
                addName,
                UI.Button(UI.T("Common.Add", "Add"), null, ButtonKind.Primary, command: _vm.CreateTagCommand)),
            message,
            warning,
            list,
            UI.Button(
                UI.T("Common.Back", "Back"),
                () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Organization)),
                ButtonKind.Ghost));
    }

    private UIElement Library()
    {
        var vaultRoot = Path.IsPathFullyQualified(_vm.VaultRoot)
            ? _vm.VaultRoot
            : UI.T("Settings.Library.NoVault", "No Vault is open.");

        return UI.V(12,
            UI.Text(vaultRoot, "mono"),
            UI.Text(
                UI.F("Settings.Library.Counts", "{0} Profiles · {1} media · {2}", _vm.TotalProfilesCount, _vm.ActiveAssetsCount, _vm.ManagedStorageFormatted),
                "body"),
            UI.H(8,
                UI.Button(
                    UI.T("Settings.Library.OpenVault", "Open Vault"),
                    () => Services.RunUserAction(OpenVaultAsync(), "Settings.OpenVault", UI.T("Settings.Library.OpenVaultFailed", "The Vault could not be opened.")),
                    ButtonKind.Primary),
                UI.Button(
                    UI.T("Settings.Library.ShowDatabase", "Show catalog.db"),
                    () => Services.RunUserAction(ShowCatalogDatabaseAsync(), "Settings.ShowCatalogDatabase", UI.T("Settings.Library.ShowDatabaseFailed", "The catalog database could not be shown.")),
                    ButtonKind.Secondary)),
            UI.H(8,
                UI.Button(
                    UI.T("Settings.MediaPreferences.Title", "Media Preferences"),
                    () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Library, SettingsSubsection.MediaPreferences)),
                    ButtonKind.Secondary),
                UI.Button(
                    UI.T("Settings.ImportPreferences.Title", "Import Preferences"),
                    () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.Library, SettingsSubsection.ImportPreferences)),
                    ButtonKind.Secondary)));
    }

    private async Task OpenVaultAsync()
    {
        var result = await _explorer.OpenVaultAsync().ConfigureAwait(true);
        if (result.Status != StorageOperationStatus.Success && result.Status != StorageOperationStatus.Cancelled)
        {
            throw new InvalidOperationException(result.SafeErrorDetail ?? "The Vault could not be opened.");
        }
    }

    private async Task ShowCatalogDatabaseAsync()
    {
        var result = await _explorer.ShowCatalogDatabaseAsync().ConfigureAwait(true);
        if (result.Status != StorageOperationStatus.Success && result.Status != StorageOperationStatus.Cancelled)
        {
            throw new InvalidOperationException(result.SafeErrorDetail ?? "The catalog database could not be shown.");
        }
    }

    private UIElement System() => UI.V(12,
        UI.Text(UI.T("Settings.System.Desc", "Advanced diagnostics are available when recovery requires them."), "body", "textSecondary"),
        UI.Button(
            UI.T("Settings.LibraryHealth.Title", "Library Health"),
            () => Services.Navigation.Navigate(new SettingsRoute(SettingsSection.System, SettingsSubsection.LibraryHealth)),
            ButtonKind.Primary,
            "icon.status.activity"));

    private UIElement People() => UI.Section(
        UI.T("Settings.People", "People & faces"),
        UI.F("Settings.People.Pending", "{0} faces are waiting for review.", _vm.UnresolvedFaceCount),
        UI.Button(
            UI.T("Settings.OpenFaceReview", "Review faces"),
            null,
            ButtonKind.Primary,
            "icon.profile.face",
            _vm.NavigateToFaceReviewCommand));

    private UIElement About()
    {
        var notices = _vm.ThirdPartyNotices;
        var noticeText = notices.IsAvailable
            ? notices.Content
            : UI.T("Settings.About.NoticesUnavailable", "Third-party notices are unavailable in this deployment.");

        var feed = new TextBox
        {
            Text = _vm.UpdateFeedUrl,
            PlaceholderText = UI.T(
                "Settings.Update.FeedPlaceholder",
                "https://example.com/updates/update-manifest.json"),
            MinWidth = 420,
        };
        feed.TextChanged += (_, _) => _vm.UpdateFeedUrl = feed.Text;

        var updateError = string.IsNullOrWhiteSpace(_vm.UpdateErrorText)
            ? null
            : UI.Text(
                UI.F(
                    "Settings.Update.Error",
                    "Update error: {0}",
                    _vm.UpdateErrorText),
                "caption",
                "danger",
                maxLines: 5);

        var updateInfo = UI.V(
            8,
            UI.Text(UI.T("Settings.Update.SectionTitle", "Updates"), "body-strong"),
            UI.Text(UI.T("Settings.Update.FeedLabel", "Update feed"), "caption", "textSecondary"),
            feed,
            UI.H(
                8,
                UI.Button(
                    UI.T("Settings.Update.SaveFeed", "Save feed"),
                    null,
                    ButtonKind.Ghost,
                    command: _vm.SaveUpdateFeedCommand),
                UI.Button(
                    UI.T("Settings.Update.Check", "Check for updates"),
                    null,
                    ButtonKind.Secondary,
                    command: _vm.CheckForUpdatesCommand),
                UI.Button(
                    UI.T("Settings.Update.DownloadInstall", "Download & Install"),
                    null,
                    ButtonKind.Primary,
                    command: _vm.InstallUpdateCommand),
                UI.Button(
                    UI.T("Settings.Update.InstallZip", "Install from ZIP…"),
                    () => Services.RunUserAction(
                        InstallFromZipAsync(),
                        "Settings.InstallFromZip",
                        UI.T("Settings.Update.LocalFailed", "The local update could not be installed.")),
                    ButtonKind.Secondary)),
            UI.Text(
                UI.F(
                    "Settings.Update.Status",
                    "Status: {0}",
                    _vm.UpdateStatusText),
                "caption"),
            string.IsNullOrWhiteSpace(_vm.UpdateCandidateVersion)
                ? null
                : UI.Text(
                    UI.F(
                        "Settings.Update.Candidate",
                        "Available version: {0}",
                        _vm.UpdateCandidateVersion),
                    "caption"),
            updateError);

        return UI.V(8,
            UI.Text($"{_vm.AppTitle} {_vm.AppVersion}", "section-title"),
            UI.Text(_vm.RuntimeInfo, "mono"),
            UI.Text(
                UI.F("Settings.About.Catalog", "Catalog schema {0} · Presentation Contract v{1}", _vm.CatalogSchemaVersion, PresentationContract.Version),
                "mono"),
            UI.Text(
                UI.F("Settings.About.Profiling", "Profiling protocol {0} · {1}", _vm.ProfilingWorkerProtocolVersion, _vm.FaceModelInfo),
                "mono"),
            UI.Text(UI.T("Settings.About.Renderer", "Renderer: Uno Platform Skia Desktop (Win32)"), "mono"),
            updateInfo,
            UI.Text(UI.T("Settings.Notices", "Third-party notices"), "body-strong"),
            UI.Text(noticeText, "caption", maxLines: 60));
    }

    private async Task InstallFromZipAsync()
    {
        var selected = await Services.PickFilesAsync(false, ".zip").ConfigureAwait(true);
        if (selected.Count == 0)
        {
            return;
        }

        var archivePath = selected[0];
        var inspection = await _vm.InspectLocalUpdateAsync(archivePath).ConfigureAwait(true);
        if (!inspection.IsAccepted)
        {
            Services.Toast(
                inspection.SafeError ?? UI.T("Settings.Update.LocalInvalid", "The selected ZIP is not an installable Neu Terradise update."),
                "warning");
            return;
        }

        var confirmed = await Services.ConfirmAsync(
            UI.T("Settings.Update.LocalConfirmTitle", "Install local update?"),
            UI.F(
                "Settings.Update.LocalConfirmBody",
                "Install Neu Terradise {0} ({1}) from the selected ZIP? The application will close and restart. Your Vault will not be modified.",
                inspection.ProductVersion ?? "?",
                inspection.RuntimeIdentifier ?? "?"),
            UI.T("Settings.Update.LocalConfirm", "Install update")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(inspection.PayloadSha256))
        {
            Services.Toast(
                UI.T("Settings.Update.LocalInvalid", "The selected ZIP is not an installable Neu Terradise update."),
                "warning");
            return;
        }

        var result = await _vm.InstallLocalUpdateAsync(
            archivePath,
            inspection.PayloadSha256,
            userConfirmed: true).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.SafeError ?? UI.T("Settings.Update.LocalFailed", "The local update could not be installed."));
        }
    }

    private UIElement Activity(ActivityViewModel activity)
    {
        var list = UI.V(6);
        var categories = UI.H(6);
        var feedback = UI.Text(string.Empty, "caption", "danger", 3);

        void Fill()
        {
            categories.Children.Clear();
            foreach (var category in activity.Categories)
            {
                categories.Children.Add(UI.Chip(
                    category,
                    category == activity.SelectedCategory,
                    () => activity.FilterCategoryCommand.Execute(category)));
            }

            list.Children.Clear();
            foreach (var entry in activity.DisplayEntries.Take(300))
            {
                list.Children.Add(UI.Grid("auto", "*,auto",
                    UI.Text(entry.Description, "body", maxLines: 2).At(0, 0),
                    UI.Text(entry.OccurredAtUtc.ToLocalTime().ToString("g"), "caption").At(0, 1)));
            }

            feedback.Text = activity.HasError ? activity.ErrorMessage ?? string.Empty : string.Empty;
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        _sectionBag.Add(Observe.Collection(activity.DisplayEntries, Fill));
        _sectionBag.Add(Observe.Props(activity, Fill,
            nameof(ActivityViewModel.SelectedCategory),
            nameof(ActivityViewModel.Status),
            nameof(ActivityViewModel.ErrorMessage)));
        Fill();

        return UI.V(12,
            categories,
            feedback,
            list,
            UI.Button(UI.T("Activity.LoadMore", "Show more"), null, ButtonKind.Ghost, command: activity.LoadMoreCommand));
    }

    private UIElement Trash(TrashViewModel trash)
    {
        var list = UI.V(6);
        var confirm = UI.V(8);
        var feedback = UI.Text(string.Empty, "caption", "accent", 3);
        var loadMore = UI.Button(
            UI.T("Common.LoadMore", "Load more"),
            null,
            ButtonKind.Ghost,
            command: trash.LoadMoreCommand);

        void Fill()
        {
            list.Children.Clear();
            foreach (var item in trash.Items)
            {
                var typeLabel = string.Equals(item.EntityType, "Profile", StringComparison.OrdinalIgnoreCase)
                    ? UI.T("Settings.Trash.Profile", "Profile")
                    : UI.T("Settings.Trash.Media", "Media");

                list.Children.Add(UI.Surface(
                    UI.Grid("auto", "*,auto",
                        UI.V(2,
                            UI.Text(item.DisplayName, "body-strong", maxLines: 1),
                            UI.Text($"{typeLabel} · {item.TrashedAtUtc.ToLocalTime():g}", "caption")).At(0, 0),
                        UI.H(6,
                            UI.Button(UI.T("Trash.Restore", "Restore"), null, command: trash.RestoreItemCommand, parameter: item),
                            UI.Button(UI.T("Trash.Delete", "Delete permanently"), null, ButtonKind.Destructive, command: trash.PurgeItemCommand, parameter: item)).At(0, 1)),
                    Material.Grounded,
                    10,
                    12));
            }

            confirm.Children.Clear();
            if (trash.IsPurgeConfirmationActive)
            {
                confirm.Children.Add(UI.Surface(
                    UI.V(8,
                        UI.Text(trash.PurgeConfirmationTitle ?? string.Empty, "body-strong"),
                        UI.Text(trash.PurgeConfirmationMessage ?? string.Empty, "body"),
                        UI.H(8,
                            UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, command: trash.CancelPurgeCommand),
                            UI.Button(trash.PurgeConfirmationConfirmLabel, null, ButtonKind.Destructive, command: trash.ConfirmPurgeCommand))),
                    Material.Deep,
                    12,
                    14));
            }

            feedback.Text = trash.HasError
                ? trash.ErrorMessage ?? string.Empty
                : trash.ActionMessage ?? string.Empty;
            feedback.Foreground = ThemeRuntime.Current.Brush(trash.HasError ? "danger" : "accent");
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
            loadMore.Visibility = trash.HasMoreItems ? Visibility.Visible : Visibility.Collapsed;
        }

        _sectionBag.Add(Observe.Collection(trash.Items, Fill));
        _sectionBag.Add(Observe.Props(trash, Fill));
        Fill();

        return UI.V(12,
            UI.Text(UI.T("Trash.Desc", "Items here can be restored. Permanent deletion asks first."), "body-muted"),
            feedback,
            confirm,
            list,
            loadMore,
            UI.Button(UI.T("Trash.Empty", "Empty Trash"), null, ButtonKind.Destructive, command: trash.EmptyTrashCommand));
    }

    private UIElement Health(LibraryHealthViewModel health)
    {
        var findings = UI.V(8);
        var status = UI.Text(string.Empty, "section-title");
        var metrics = UI.Text(string.Empty, "body");
        var feedback = UI.Text(string.Empty, "caption", "accent", 3);
        var repair = UI.V(8);

        void Fill()
        {
            status.Text = health.HealthFindingCount == 0
                ? UI.T("Health.Status.Healthy", "Healthy")
                : UI.T("Health.Status.Attention", "Needs attention");
            metrics.Text = UI.F(
                "Health.Metrics",
                "{0} media · {1} catalog · {2} / {3} cache · {4} trash",
                health.ManagedMediaBytesFormatted,
                health.DatabaseBytesFormatted,
                health.CacheTotalBytesFormatted,
                health.CacheQuotaBytesFormatted,
                health.TrashBytesFormatted);
            feedback.Text = health.HasError
                ? health.ErrorMessage ?? string.Empty
                : health.ActionFeedbackMessage ?? string.Empty;
            feedback.Foreground = ThemeRuntime.Current.Brush(health.HasError ? "danger" : "accent");
            feedback.Visibility = string.IsNullOrWhiteSpace(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;

            findings.Children.Clear();
            foreach (var group in health.FindingGroups)
            {
                foreach (var finding in group.Findings)
                {
                    findings.Children.Add(UI.Grid("auto", "*,auto",
                        UI.Text(
                            UI.F("Health.Finding.Generic", "Library health issue ({0}). Review it before applying a repair.", finding.Code),
                            "body",
                            maxLines: 3).At(0, 0),
                        finding.RepairAvailable
                            ? UI.Button(UI.T("Health.Repair", "Repair…"), () =>
                            {
                                health.SelectFindingCommand.Execute(finding);
                                health.PrepareRepairCommand.Execute(null);
                            }).At(0, 1)
                            : null));
                }
            }

            repair.Children.Clear();
            if (health.HasPendingRepair)
            {
                repair.Children.Add(UI.Surface(
                    UI.V(8,
                        UI.Text(
                            UI.T("Health.RepairConfirmation", "Review this repair before continuing. The plan will be revalidated before it changes anything."),
                            "body"),
                        UI.H(8,
                            UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, command: health.CancelRepairCommand),
                            UI.Button(UI.T("Health.Confirm", "Repair"), null, ButtonKind.Primary, command: health.ConfirmRepairCommand))),
                    Material.Deep,
                    12,
                    14));
            }
        }

        _sectionBag.Add(Observe.Props(health, Fill));
        _sectionBag.Add(Observe.Collection(health.FindingGroups, Fill));
        Fill();

        return UI.V(12,
            status,
            metrics,
            feedback,
            UI.H(8,
                UI.Button(UI.T("Health.Check", "Check library"), null, ButtonKind.Primary, command: health.RunHealthCheckCommand),
                UI.Button(UI.T("Health.Deep", "Deep check"), null, command: health.RunDeepHealthCheckCommand),
                UI.Button(UI.T("Health.ClearCache", "Clear cache"), null, ButtonKind.Ghost, command: health.ClearCacheCommand)),
            UI.Text(UI.T("Health.CacheNote", "The cache only holds regenerable previews. Clearing it never touches your media."), "caption"),
            repair,
            findings);
    }

    public override void Dispose()
    {
        _sectionBag.Dispose();
        base.Dispose();
    }
}