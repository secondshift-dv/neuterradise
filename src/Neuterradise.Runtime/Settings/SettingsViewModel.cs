using Neuterradise.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Neuterradise.App.Activity;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Maintenance;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.Updates;
using Neuterradise.App.SystemServices;
using Neuterradise.App.Trash;

using Neuterradise.App.SystemServices.Database.Reads;

using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Settings;

public sealed class CategoryItemViewModel : ObservableObject
{
    private string _name;
    private int _usageCount;
    private bool _isEditing;
    private string _editName = string.Empty;

    public CategoryItemViewModel(string categoryId, string name, int usageCount, long rowVersion)
    {
        CategoryId = categoryId;
        _name = name;
        _usageCount = usageCount;
        RowVersion = rowVersion;
    }

    public string CategoryId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int UsageCount
    {
        get => _usageCount;
        set => SetProperty(ref _usageCount, value);
    }

    public long RowVersion { get; set; }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
    }
}

public sealed class TagItemViewModel : ObservableObject
{
    private string _name;
    private int _usageCount;
    private bool _isEditing;
    private string _editName = string.Empty;

    public TagItemViewModel(string tagId, string name, int usageCount, long rowVersion)
    {
        TagId = tagId;
        _name = name;
        _usageCount = usageCount;
        RowVersion = rowVersion;
    }

    public string TagId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int UsageCount
    {
        get => _usageCount;
        set => SetProperty(ref _usageCount, value);
    }

    public long RowVersion { get; set; }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
    }
}

public sealed class SettingsRailItem : ObservableObject
{
    private readonly string _titleKey;
    private readonly string _defaultTitle;
    private readonly string _descriptionKey;
    private readonly string _defaultDescription;
    private int? _attentionBadgeCount;

    public SettingsRailItem(
        SettingsSection key,
        string titleKey,
        string defaultTitle,
        string descriptionKey,
        string defaultDescription,
        int? attentionBadgeCount = null)
    {
        Key = key;
        _titleKey = titleKey;
        _defaultTitle = defaultTitle;
        _descriptionKey = descriptionKey;
        _defaultDescription = defaultDescription;
        _attentionBadgeCount = attentionBadgeCount;

        SurfaceText.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Description));
    }

    /// <summary>
    /// Releases the static language subscription. Settings view models are created per visit; without
    /// this every visit left its rail items (and through them the page) reachable from a static event.
    /// </summary>
    public void Detach() => SurfaceText.LanguageChanged -= OnLanguageChanged;

    public SettingsRailItem(SettingsSection key, string title, string description, int? attentionBadgeCount = null)
        : this(key, "Settings." + key + ".Title", title, "Settings." + key + ".Desc", description, attentionBadgeCount)
    {
    }

    public SettingsSection Key { get; }
    public string Title => SurfaceText.Get(_titleKey, _defaultTitle);
    public string Description => SurfaceText.Get(_descriptionKey, _defaultDescription);

    public int? AttentionBadgeCount
    {
        get => _attentionBadgeCount;
        set => SetProperty(ref _attentionBadgeCount, value);
    }
}

public sealed class SettingsViewModel : ScreenStateViewModel, IDisposable
{
    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly SettingsOperations? _settingsOperations;
    private readonly SettingsReads? _reads;
    private readonly StorageMetricsProvider? _storageMetrics;
    private readonly CacheMaintenanceOperations? _cacheMaintenance;
    private readonly IPresentationPreferenceApplier? _presentationPreferenceApplier;
    private readonly AppConfigurationStore? _configurationStore;
    private readonly UpdateCoordinator? _updateCoordinator;
    private readonly LatestValueAction<string> _themePersistence;
    private readonly LatestValueAction<DensityMode> _densityPersistence;
    private readonly LatestValueAction<bool> _reduceMotionPersistence;
    private readonly LatestValueAction<MediaPreferences> _mediaPreferencesPersistence;
    private readonly LatestValueAction<ImportPreferences> _importPreferencesPersistence;
    private readonly SynchronizationContext? _uiContext;
    private MediaPreferences _durableMediaPreferences = new();
    private ImportPreferences _durableImportPreferences = new();
    private DiagnosticVersionReport? _versionReport;

    private SettingsSection _activeSection;
    private SettingsSubsection? _activeSubsection;
    private string _theme = ThemeLoader.FallbackThemeId;
    private DensityMode _density = DensityMode.Comfortable;
    private bool _reduceMotion;
    private int _videoPreviewDurationSeconds = 6;
    private bool _autoplayVideo = true;
    private bool _muteAudioOnPreview = true;
    private bool _autoAnalyzeAfterImport = true;
    private bool _preserveSourceTimestamps = true;
    private string _defaultProfileLayout = ProfileLayoutResolver.FallbackPresetId;
    private string _defaultGalleryCardVariant = GalleryCardCatalog.FallbackVariantId;
    private string _updateFeedUrl = string.Empty;
    private UpdatePresentationState _updateState = UpdatePresentationState.Idle;

    private string _newCategoryName = string.Empty;
    private string _newTagName = string.Empty;
    private CategoryItemViewModel? _selectedCategory;
    private TagItemViewModel? _selectedTag;
    private string? _organizationMessage;
    private string? _organizationWarningMessage;

    private StorageMetricsSnapshot? _systemMetrics;
    private long _unresolvedFaceCount;

    public SettingsViewModel(
        SettingsSection section = SettingsSection.General,
        SettingsSubsection? subsection = null,
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        SettingsOperations? settingsOperations = null,
        StorageMetricsProvider? storageMetrics = null,
        CacheMaintenanceOperations? cacheMaintenance = null,
        IPresentationPreferenceApplier? presentationPreferenceApplier = null,
        AppConfigurationStore? configurationStore = null,
        IWindowPlacement? windowPlacement = null,
        UiScaleService? uiScale = null,
        UpdateCoordinator? updateCoordinator = null)
    {
        Section = section;
        Subsection = subsection;

        // Interface scale: the same app-level authority the Ctrl+/Ctrl-/Ctrl+0 shortcuts drive.
        _uiScale = uiScale;
        IncreaseUiScaleCommand = new RelayCommand(_ => _uiScale?.Increase(), _ => _uiScale?.CanIncrease == true);
        DecreaseUiScaleCommand = new RelayCommand(_ => _uiScale?.Decrease(), _ => _uiScale?.CanDecrease == true);
        ResetUiScaleCommand = new RelayCommand(_ => _uiScale?.Reset(), _ => _uiScale is { IsDefault: false });
        if (_uiScale is not null)
        {
            _uiScale.ScaleChanged += OnUiScaleChanged;
        }
        _activeSection = section;
        _activeSubsection = subsection;
        _catalog = catalog;
        _navigation = navigation;
        _configurationStore = configurationStore;
        _updateCoordinator = updateCoordinator;
        _updateState = updateCoordinator?.State ?? UpdatePresentationState.Idle;
        _uiContext = SynchronizationContext.Current;

        // Display: the window controller is the only authority for window geometry. This surface
        // just asks it to apply a preset and reflects whatever size the window actually has.
        _windowPlacement = windowPlacement;
        WindowSizeOptions = [.. WindowGeometry.SelectablePresets.Select(preset => new WindowSizePresetOption(preset))];
        ApplyWindowSizePresetCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is WindowSizePresetOption option)
                {
                    _windowPlacement?.ApplyPreset(option.Preset);
                    RefreshWindowDisplay();
                }
            },
            _ => _windowPlacement is { IsAttached: true });
        if (_windowPlacement is not null)
        {
            _windowPlacement.PlacementChanged += OnWindowPlacementChanged;
        }

        RefreshWindowDisplay();

        if (_catalog is not null)
        {
            _settingsOperations = settingsOperations ?? new SettingsOperations(_catalog);
            _reads = _catalog.SettingsReads;
            _storageMetrics = storageMetrics ?? new StorageMetricsProvider(_catalog);

            var cachePaths = new CachePaths(_catalog.Paths);
            var cacheBudget = new PersistentCacheBudget(cachePaths);
            _cacheMaintenance = cacheMaintenance ?? new CacheMaintenanceOperations(cachePaths, cacheBudget);
        }
        else
        {
            _settingsOperations = settingsOperations;
            _storageMetrics = storageMetrics;
            _cacheMaintenance = cacheMaintenance;
        }

        _presentationPreferenceApplier = presentationPreferenceApplier;
        _themePersistence = new(
            SetThemePreferenceAsync,
            exception => ShowRecoverableError(SurfaceText.Format(
                "Settings.Theme.SaveFailed",
                "The theme preference could not be saved: {0}",
                OperationExecution.SafeMessage(exception))));
        _densityPersistence = new(
            SetDensityPreferenceAsync,
            exception => ShowRecoverableError(SurfaceText.Format(
                "Settings.Density.SaveFailed",
                "The density preference could not be saved: {0}",
                OperationExecution.SafeMessage(exception))));
        _reduceMotionPersistence = new(
            SetReduceMotionPreferenceAsync,
            exception => ShowRecoverableError(SurfaceText.Format(
                "Settings.Motion.SaveFailed",
                "The motion preference could not be saved: {0}",
                OperationExecution.SafeMessage(exception))));
        _mediaPreferencesPersistence = new(
            PersistMediaPreferencesAsync,
            value =>
            {
                _durableMediaPreferences = value;
                RunOnUi(ShowReady);
            },
            (_, exception) => RunOnUi(() =>
            {
                RollbackMediaPreferences();
                ShowRecoverableError(SurfaceText.Format(
                    "Settings.MediaPreferences.SaveFailed",
                    "Media preferences could not be saved and were restored. Try again. {0}",
                    OperationExecution.SafeMessage(exception)));
            }));
        _importPreferencesPersistence = new(
            PersistImportPreferencesAsync,
            value =>
            {
                _durableImportPreferences = value;
                RunOnUi(ShowReady);
            },
            (_, exception) => RunOnUi(() =>
            {
                RollbackImportPreferences();
                ShowRecoverableError(SurfaceText.Format(
                    "Settings.ImportPreferences.SaveFailed",
                    "Import preferences could not be saved and were restored. Try again. {0}",
                    OperationExecution.SafeMessage(exception)));
            }));

        Activity = new ActivityViewModel(_catalog, _navigation);
        Trash = new TrashViewModel(_catalog, _navigation);
        Health = new LibraryHealthViewModel(_catalog, _navigation, _storageMetrics, _cacheMaintenance);

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }

        LoadThemeOptions();

        SelectSectionCommand = new RelayCommand(param =>
        {
            if (param is SettingsSection sec)
            {
                ApplyRoute(new SettingsRoute(sec));
            }
        });

        SelectThemeCommand = new RelayCommand(param =>
        {
            if (param is string themeId && !string.IsNullOrWhiteSpace(themeId))
            {
                Theme = themeId;
            }
        });

        SelectDensityCommand = new RelayCommand(param =>
        {
            if (param is DensityMode mode)
            {
                Density = mode;
            }
            else if (param is string s && Enum.TryParse<DensityMode>(s, ignoreCase: true, out var parsed))
            {
                Density = parsed;
            }
        });

        SelectLanguageCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string lang && !string.IsNullOrWhiteSpace(lang))
            {
                await ApplyLanguageAsync(lang);
            }
        });

        SurfaceText.LanguageChanged += OnSurfaceLanguageChanged;

        SetDefaultProfileLayoutCommand = new AsyncRelayCommand(async () =>
        {
            if (_settingsOperations is not null && !string.IsNullOrWhiteSpace(DefaultProfileLayout))
            {
                await _settingsOperations.SetDefaultProfileLayoutAsync(DefaultProfileLayout);
            }
        });

        SetDefaultGalleryCardVariantCommand = new AsyncRelayCommand(async () =>
        {
            if (_settingsOperations is not null && !string.IsNullOrWhiteSpace(DefaultGalleryCardVariant))
            {
                var current = await _settingsOperations.GetGalleryPresentationPreferencesAsync();
                if (current.CardVariantId != DefaultGalleryCardVariant)
                {
                    await _settingsOperations.SetGalleryPresentationPreferencesAsync(current with { CardVariantId = DefaultGalleryCardVariant });
                }
            }
        });

        CreateCategoryCommand = new AsyncRelayCommand(() => CreateCategoryAsync());
        RenameCategoryCommand = new RelayCommand(param =>
        {
            if (param is (CategoryItemViewModel item, string newName))
            {
                TaskObserver.Observe(RenameCategoryAsync(item, newName), "SettingsViewModel.RenameCategoryAsync");
            }
        });
        BeginEditCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item)
            {
                item.BeginEdit();
            }
        });
        CommitRenameCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item && !string.IsNullOrWhiteSpace(item.EditName))
            {
                TaskObserver.Observe(RenameCategoryAsync(item, item.EditName), "SettingsViewModel.RenameCategoryAsync");
            }
        });
        CancelEditCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item)
            {
                item.CancelEdit();
            }
        });
        DeleteCategoryCommand = new RelayCommand(param =>
        {
            if (param is CategoryItemViewModel item)
            {
                TaskObserver.Observe(DeleteCategoryAsync(item), "SettingsViewModel.DeleteCategoryAsync");
            }
        });

        CreateTagCommand = new AsyncRelayCommand(() => CreateTagAsync());
        RenameTagCommand = new RelayCommand(param =>
        {
            if (param is (TagItemViewModel item, string newName))
            {
                TaskObserver.Observe(RenameTagAsync(item, newName), "SettingsViewModel.RenameTagAsync");
            }
        });
        BeginEditTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item)
            {
                item.BeginEdit();
            }
        });
        CommitRenameTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item && !string.IsNullOrWhiteSpace(item.EditName))
            {
                TaskObserver.Observe(RenameTagAsync(item, item.EditName), "SettingsViewModel.RenameTagAsync");
            }
        });
        CancelEditTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item)
            {
                item.CancelEdit();
            }
        });
        DeleteTagCommand = new RelayCommand(param =>
        {
            if (param is TagItemViewModel item)
            {
                TaskObserver.Observe(DeleteTagAsync(item), "SettingsViewModel.DeleteTagAsync");
            }
        });

        SaveUpdateFeedCommand = new AsyncRelayCommand(
            SaveUpdateFeedAsync,
            () => _updateCoordinator is not null);
        CheckForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            () => _updateCoordinator is not null);
        InstallUpdateCommand = new AsyncRelayCommand(
            InstallUpdateAsync,
            () => CanInstallUpdate);

        if (_updateCoordinator is not null)
        {
            _updateCoordinator.StateChanged += OnUpdateStateChanged;
        }

        NavigateToFaceReviewCommand = new RelayCommand(_ => _navigation?.Navigate(new FaceReviewRoute()));
        NavigateToTrashCommand = new RelayCommand(_ => _navigation?.Navigate(new TrashRoute()));

        StartRouteTask(InitializeAsync, SurfaceText.Get("Settings.LoadFailed", "Settings could not be loaded."));
        StartRouteTask(LoadThirdPartyNoticesAsync, SurfaceText.Get("Settings.ThirdPartyNotices.LoadFailed", "Third-party notices could not be loaded."));
    }

    public SettingsSection Section { get; }

    public SettingsSubsection? Subsection { get; }

    public SettingsSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                if (_activeSubsection is not null)
                {
                    _activeSubsection = null;
                    RaisePropertyChanged(nameof(ActiveSubsection));
                }

                LoadActiveSection();
            }
        }
    }

    public SettingsSubsection? ActiveSubsection
    {
        get => _activeSubsection;
        private set => SetProperty(ref _activeSubsection, value);
    }

    public void ApplyRoute(SettingsRoute route)
    {
        var sectionChanged = _activeSection != route.Section;
        var subsectionChanged = _activeSubsection != route.Subsection;
        if (!sectionChanged && !subsectionChanged)
        {
            LoadActiveSection();
            return;
        }

        _activeSection = route.Section;
        _activeSubsection = route.Subsection;
        if (sectionChanged)
        {
            RaisePropertyChanged(nameof(ActiveSection));
        }

        if (subsectionChanged)
        {
            RaisePropertyChanged(nameof(ActiveSubsection));
        }

        LoadActiveSection();
    }

    public IReadOnlyList<SettingsRailItem> RailItems { get; } =
    [
        new(SettingsSection.General, "Settings.General.Title", "General", "Settings.General.Desc", "Theme, density, and media preferences"),
        new(SettingsSection.Display, "Settings.Display.Title", "Display", "Settings.Display.Desc", "Window size and placement"),
        new(SettingsSection.Language, "Settings.Language.Title", "Language", "Settings.Language.Desc", "English and Bahasa Indonesia"),
        new(SettingsSection.Library, "Settings.Library.Title", "Library", "Settings.Library.Desc", "Vault root, storage, and import"),
        new(SettingsSection.Organization, "Settings.Organization.Title", "Organization", "Settings.Organization.Desc", "Categories and tags"),
        new(SettingsSection.System, "Settings.System.Title", "System", "Settings.System.Desc", "Diagnostics, cache, and background work"),
        new(SettingsSection.People, "Settings.FaceReview.Title", "People", "Settings.FaceReview.Desc", "Confirm who appears in your media"),
        new(SettingsSection.Activity, "Settings.Activity.Title", "Activity", "Settings.Activity.Desc", "What has happened in your library"),
        new(SettingsSection.Trash, "Settings.Trash.Title", "Trash", "Settings.Trash.Desc", "Reversible staging and permanent purge"),
        new(SettingsSection.About, "Settings.About.Title", "About", "Settings.About.Desc", "Application build, runtime, and architecture"),
    ];

    private readonly IWindowPlacement? _windowPlacement;
    private string _currentWindowSizeText = string.Empty;
    private bool _isWindowSizeCustom;

    /// <summary>The selectable window-size presets. Custom is shown as state, never as a choice.</summary>
    public IReadOnlyList<WindowSizePresetOption> WindowSizeOptions { get; }

    public ICommand ApplyWindowSizePresetCommand { get; }

    private readonly UiScaleService? _uiScale;

    public bool IsUiScaleAvailable => _uiScale is not null;

    /// <summary>The current interface scale (1.0 = 100%), read from the shared authority.</summary>
    public double UiScale => _uiScale?.Scale ?? UiScaleRules.Default;

    public string UiScaleText => string.Create(
        CultureInfo.InvariantCulture,
        $"{UiScaleRules.ToPercent(UiScale)}%");

    public RelayCommand IncreaseUiScaleCommand { get; }

    public RelayCommand DecreaseUiScaleCommand { get; }

    public RelayCommand ResetUiScaleCommand { get; }

    private void OnUiScaleChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(UiScale));
        RaisePropertyChanged(nameof(UiScaleText));
        IncreaseUiScaleCommand.RaiseCanExecuteChanged();
        DecreaseUiScaleCommand.RaiseCanExecuteChanged();
        ResetUiScaleCommand.RaiseCanExecuteChanged();
    }

    public bool IsWindowDisplayAvailable => _windowPlacement is { IsAttached: true };

    public string CurrentWindowSizeText
    {
        get => _currentWindowSizeText;
        private set => SetProperty(ref _currentWindowSizeText, value);
    }

    /// <summary>True when the window was resized by hand to a size that matches no preset.</summary>
    public bool IsWindowSizeCustom
    {
        get => _isWindowSizeCustom;
        private set => SetProperty(ref _isWindowSizeCustom, value);
    }

    private void OnWindowPlacementChanged(object? sender, EventArgs e) => RefreshWindowDisplay();

    private void RefreshWindowDisplay()
    {
        var current = _windowPlacement?.CurrentPreset ?? WindowSizePreset.FitToScreen;
        foreach (var option in WindowSizeOptions)
        {
            option.IsCurrent = _windowPlacement?.RestoreSize is not null && option.Preset == current;
        }

        IsWindowSizeCustom = _windowPlacement?.RestoreSize is not null && current == WindowSizePreset.Custom;

        if (_windowPlacement?.RestoreSize is { } size)
        {
            var label = current == WindowSizePreset.Custom
                ? "Custom"
                : WindowSizePresetOption.LabelFor(current);
            var state = _windowPlacement.IsMaximized ? " — maximized; this is the size it restores to" : string.Empty;
            CurrentWindowSizeText = string.Create(
                CultureInfo.InvariantCulture,
                $"{size.Width:0} × {size.Height:0} ({label}){state}");
        }
        else
        {
            CurrentWindowSizeText = "Window size is not available.";
        }

        RaisePropertyChanged(nameof(IsWindowDisplayAvailable));
    }

    private void OnSurfaceLanguageChanged(object? sender, EventArgs e)
    {
        CurrentLanguage = SurfaceText.CurrentLanguage;
        RaisePropertyChanged(nameof(UpdateStatusText));
    }

    public void Dispose()
    {
        if (_windowPlacement is not null)
        {
            _windowPlacement.PlacementChanged -= OnWindowPlacementChanged;
        }

        if (_uiScale is not null)
        {
            _uiScale.ScaleChanged -= OnUiScaleChanged;
        }

        // Settings is recreated on every visit; a static-event subscription left behind kept each old
        // page (with its Activity, Trash and Health view models) alive for the life of the process.
        SurfaceText.LanguageChanged -= OnSurfaceLanguageChanged;
        if (_updateCoordinator is not null)
        {
            _updateCoordinator.StateChanged -= OnUpdateStateChanged;
        }

        foreach (var railItem in RailItems)
        {
            railItem.Detach();
        }

        _themePersistence.Dispose();
        _densityPersistence.Dispose();
        _reduceMotionPersistence.Dispose();
        _mediaPreferencesPersistence.Dispose();
        _importPreferencesPersistence.Dispose();

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }

        _organizationCoalescer?.Dispose();
        Activity.Dispose();
        Trash.Dispose();
        Health.Dispose();
    }

    public ActivityViewModel Activity { get; }

    public TrashViewModel Trash { get; }

    public LibraryHealthViewModel Health { get; }

    public string Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                RefreshThemeSelection();
                _themePersistence.Submit(value);
            }
        }
    }

    /// <summary>
    /// Every theme this build can apply, each carrying its own swatch colours.
    ///
    /// This is read from the theme catalogue rather than hardcoded: the previous hand-written list
    /// exposed four of the eight shipped themes, which meant half the Design System was unreachable
    /// from the product. Custom themes dropped into the themes folder now appear here too.
    /// </summary>
    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; } = [];

    private void LoadThemeOptions()
    {
        var loader = new ThemeLoader();
        var catalog = loader.LoadBuiltInThemes();

        var available = new List<ThemeDefinition>(catalog.Themes);

        try
        {
            var customDirectory = InstallPaths.CreateProduction().ThemesPath;
            available.AddRange(loader.LoadCustomThemes(customDirectory).Themes);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Custom themes could not be listed: {0}", exception.GetType().Name);
        }

        ThemeOptions.Clear();

        foreach (var theme in available)
        {
            ThemeOptions.Add(new ThemeOptionViewModel(theme));
        }

        RefreshThemeSelection();
    }

    private void RefreshThemeSelection()
    {
        foreach (var option in ThemeOptions)
        {
            option.IsSelected = string.Equals(option.Id, _theme, StringComparison.OrdinalIgnoreCase);
        }
    }

    public DensityMode Density
    {
        get => _density;
        set
        {
            if (SetProperty(ref _density, value))
            {
                _densityPersistence.Submit(value);
            }
        }
    }

    private string _currentLanguage = SurfaceText.CurrentLanguage;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (SetProperty(ref _currentLanguage, value))
            {
                RaisePropertyChanged(nameof(IsEnglishSelected));
                RaisePropertyChanged(nameof(IsIndonesianSelected));
            }
        }
    }

    public bool IsEnglishSelected => string.Equals(CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);
    public bool IsIndonesianSelected => string.Equals(CurrentLanguage, "id", StringComparison.OrdinalIgnoreCase);

    public ICommand SelectLanguageCommand { get; }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (SetProperty(ref _reduceMotion, value))
            {
                _reduceMotionPersistence.Submit(value);
            }
        }
    }

    public int VideoPreviewDurationSeconds
    {
        get => _videoPreviewDurationSeconds;
        set
        {
            if (SetProperty(ref _videoPreviewDurationSeconds, value))
            {
                SubmitMediaPreferences();
            }
        }
    }

    public bool AutoplayVideo
    {
        get => _autoplayVideo;
        set
        {
            if (SetProperty(ref _autoplayVideo, value))
            {
                SubmitMediaPreferences();
            }
        }
    }

    public bool MuteAudioOnPreview
    {
        get => _muteAudioOnPreview;
        set
        {
            if (SetProperty(ref _muteAudioOnPreview, value))
            {
                SubmitMediaPreferences();
            }
        }
    }

    public bool AutoAnalyzeAfterImport
    {
        get => _autoAnalyzeAfterImport;
        set
        {
            if (SetProperty(ref _autoAnalyzeAfterImport, value))
            {
                SubmitImportPreferences();
            }
        }
    }

    public bool PreserveSourceTimestamps
    {
        get => _preserveSourceTimestamps;
        set
        {
            if (SetProperty(ref _preserveSourceTimestamps, value))
            {
                SubmitImportPreferences();
            }
        }
    }

    public string DefaultProfileLayout
    {
        get => _defaultProfileLayout;
        set
        {
            if (SetProperty(ref _defaultProfileLayout, value))
            {
                TaskObserver.Observe(SetDefaultProfileLayoutCommand.ExecuteAsync(null), "SettingsViewModel.ExecuteAsync");
            }
        }
    }

    public string DefaultGalleryCardVariant
    {
        get => _defaultGalleryCardVariant;
        set
        {
            if (SetProperty(ref _defaultGalleryCardVariant, value))
            {
                TaskObserver.Observe(SetDefaultGalleryCardVariantCommand.ExecuteAsync(null), "SettingsViewModel.ExecuteAsync");
            }
        }
    }

    public IReadOnlyList<string> AvailableProfileLayoutPresets { get; } =
        [.. ProfileLayoutResolver.BuiltInPresets.Select(static preset => preset.Id)];

    public IReadOnlyList<string> AvailableGalleryCardVariants { get; } =
        [.. GalleryCardCatalog.Variants.Select(static variant => variant.Id)];

    public IReadOnlyList<ProfileLayoutDefinition> ProfileLayoutPresetDefinitions { get; } =
        ProfileLayoutResolver.BuiltInPresets;

    public IReadOnlyList<GalleryCardDefinition> GalleryCardVariantDefinitions { get; } =
        GalleryCardCatalog.Variants;

    public ProfilePresentationModel PreviewModel { get; } = new(
        Guid.Empty,
        "Sample Profile",
        CategoryName: "People",
        Tags: ["portrait", "studio"],
        Rating: 4,
        IsFavorite: true,
        Overview: "How this preset lays a Profile out.",
        MediaCount: 24,
        HasRelatedIndicator: true,
        CardVariantId: null);

    public CoverAppearance PreviewCoverAppearance { get; } =
        CoverFrameCatalog.Resolve(new CoverAppearanceRequest(null, null), reduceMotion: true).Appearance;

    public BannerPresentation PreviewBannerPresentation { get; } = BannerPresentationPolicy.Default;

    /// <summary>
    /// The real vault root of the open session. When no writable catalog is open there is no vault, so
    /// this reports that truthfully instead of showing an invented example path.
    /// </summary>
    public string VaultRoot => _catalog?.Paths.Root ?? "No vault is open.";

    public string ManagedStorageFormatted => LibraryHealthViewModel.FormatBytes(_systemMetrics?.ManagedMediaBytes ?? 0);

    public long TotalProfilesCount => _systemMetrics?.ProfileCount ?? 0;

    public long ActiveAssetsCount => _systemMetrics?.ActiveAssetCount ?? 0;

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];

    public ObservableCollection<TagItemViewModel> Tags { get; } = [];

    public string NewCategoryName
    {
        get => _newCategoryName;
        set => SetProperty(ref _newCategoryName, value);
    }

    public string NewTagName
    {
        get => _newTagName;
        set => SetProperty(ref _newTagName, value);
    }

    public CategoryItemViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    public TagItemViewModel? SelectedTag
    {
        get => _selectedTag;
        set => SetProperty(ref _selectedTag, value);
    }

    public string? OrganizationMessage
    {
        get => _organizationMessage;
        set => SetProperty(ref _organizationMessage, value);
    }

    public string? OrganizationWarningMessage
    {
        get => _organizationWarningMessage;
        set => SetProperty(ref _organizationWarningMessage, value);
    }

    public StorageMetricsSnapshot? SystemMetrics
    {
        get => _systemMetrics;
        private set => SetProperty(ref _systemMetrics, value);
    }

    public long UnresolvedFaceCount
    {
        get => _unresolvedFaceCount;
        private set => SetProperty(ref _unresolvedFaceCount, value);
    }

    public string UpdateFeedUrl
    {
        get => _updateFeedUrl;
        set => _updateFeedUrl = value ?? string.Empty;
    }

    public string UpdateStatusText => _updateState.Status switch
    {
        UpdateCoordinator.StatusNotChecked => SurfaceText.Get("Settings.Update.Status.NotChecked", "Not checked"),
        UpdateCoordinator.StatusFeedSaved => SurfaceText.Get("Settings.Update.Status.FeedSaved", "Update feed saved"),
        UpdateCoordinator.StatusChecking => SurfaceText.Get("Settings.Update.Status.Checking", "Checking for updates…"),
        UpdateCoordinator.StatusAvailable => SurfaceText.Get("Settings.Update.Status.Available", "Update available"),
        UpdateCoordinator.StatusUnavailable => SurfaceText.Get("Settings.Update.Status.Unavailable", "No update available"),
        UpdateCoordinator.StatusDownloading => SurfaceText.Get("Settings.Update.Status.Downloading", "Downloading update…"),
        UpdateCoordinator.StatusStaging => SurfaceText.Get("Settings.Update.Status.Staging", "Validating update package…"),
        UpdateCoordinator.StatusPreparing => SurfaceText.Get("Settings.Update.Status.Preparing", "Starting updater…"),
        UpdateCoordinator.StatusRestarting => SurfaceText.Get("Settings.Update.Status.Restarting", "Updater started; Neu Terradise is closing…"),
        _ => _updateState.Status,
    };

    public string? UpdateErrorText => _updateState.Error;

    public string? UpdateCandidateVersion => _updateState.CandidateVersion;

    public bool CanInstallUpdate => _updateCoordinator?.HasAcceptedCandidate == true;

    private ThirdPartyNoticesReadModel _thirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices are unavailable in this deployment.");

    public ThirdPartyNoticesReadModel ThirdPartyNotices
    {
        get => _thirdPartyNotices;
        private set => SetProperty(ref _thirdPartyNotices, value);
    }

    public string AppTitle => ProductIdentity.DisplayName;

    public string AppSubtitle => "High-Performance Personal Media Vault";

    public DiagnosticVersionReport VersionReport => _versionReport ??= CreateVersionReport();

    public string AppVersion => VersionReport.AppVersion;

    public string RuntimeInfo => string.Create(
        CultureInfo.InvariantCulture,
        $"{VersionReport.RuntimeDescription} ({RuntimeInformation.OSArchitecture}), {VersionReport.TargetFramework}");

    public string CatalogSchemaVersion => VersionReport.SchemaVersion;

    public string ProfilingWorkerProtocolVersion => VersionReport.ProtocolVersion;

    public string EmbeddingSpaceKeyInfo => VersionReport.EmbeddingSpaceKey ?? "unavailable";

    public string FaceModelInfo => string.Create(
        CultureInfo.InvariantCulture,
        $"YuNet {VersionReport.DetectionModelVersion ?? "not declared"}, "
            + $"SFace {VersionReport.RecognitionModelVersion ?? "not declared"}");

    public string CacheDerivationInfo => VersionReport.CacheDerivationVersion ?? "unavailable";

    public string ProfileManifestSchemaVersionInfo =>
        VersionReport.ProfileManifestSchemaVersion ?? "unavailable";

    private async Task LoadThirdPartyNoticesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var install = InstallPaths.CreateProduction();
            var path = install.ThirdPartyNoticesFilePath;
            if (!install.IsWithinInstallRoot(path) || !File.Exists(path))
            {
                ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices are unavailable in this deployment.");
                return;
            }
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
            const int maxNoticeBytes = 2 * 1024 * 1024;
            ThirdPartyNotices = new ThirdPartyNoticesReadModel(true, content.Length > maxNoticeBytes ? content[..maxNoticeBytes] : content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException)
        {
            ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            ThirdPartyNotices = ThirdPartyNoticesReadModel.Missing("Third-party notices could not be accessed.");
        }
    }

    private DiagnosticVersionReport CreateVersionReport()
    {

        var artifacts = DeploymentArtifactCatalog.TryLoad(InstallPaths.CreateProduction());
        if (artifacts is null)
        {
            Trace.TraceWarning(
                "This deployment has no readable artifact manifest; About reports declared versions as unavailable.");
        }

        return DiagnosticVersionReport.CreateCurrent(_catalog?.SchemaVersion, artifacts);
    }

    public ICommand SelectSectionCommand { get; }
    public ICommand SelectThemeCommand { get; }
    public ICommand SelectDensityCommand { get; }
    public AsyncRelayCommand SetDefaultProfileLayoutCommand { get; }
    public AsyncRelayCommand SetDefaultGalleryCardVariantCommand { get; }
    public ICommand CreateCategoryCommand { get; }
    public ICommand RenameCategoryCommand { get; }
    public ICommand BeginEditCategoryCommand { get; }
    public ICommand CommitRenameCategoryCommand { get; }
    public ICommand CancelEditCategoryCommand { get; }
    public ICommand DeleteCategoryCommand { get; }
    public ICommand CreateTagCommand { get; }
    public ICommand RenameTagCommand { get; }
    public ICommand BeginEditTagCommand { get; }
    public ICommand CommitRenameTagCommand { get; }
    public ICommand CancelEditTagCommand { get; }
    public ICommand DeleteTagCommand { get; }
    public ICommand NavigateToFaceReviewCommand { get; }
    public ICommand NavigateToTrashCommand { get; }
    public AsyncRelayCommand SaveUpdateFeedCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ShowLoading();
        try
        {
            if (_settingsOperations is not null)
            {
                _theme = await _settingsOperations.GetThemeAsync(cancellationToken);
                RefreshThemeSelection();
                _density = await _settingsOperations.GetDensityAsync(cancellationToken);
                _reduceMotion = await _settingsOperations.GetReduceMotionAsync(cancellationToken);

                var mediaPrefs = await _settingsOperations.GetMediaPreferencesAsync(cancellationToken);
                _durableMediaPreferences = mediaPrefs;
                _videoPreviewDurationSeconds = mediaPrefs.VideoPreviewDurationSeconds;
                _autoplayVideo = mediaPrefs.AutoplayVideo;
                _muteAudioOnPreview = mediaPrefs.MuteAudioOnPreview;

                var importPrefs = await _settingsOperations.GetImportPreferencesAsync(cancellationToken);
                _durableImportPreferences = importPrefs;
                _autoAnalyzeAfterImport = importPrefs.AutoAnalyzeAfterImport;
                _preserveSourceTimestamps = importPrefs.PreserveSourceTimestamps;

                _defaultProfileLayout = await _settingsOperations.GetDefaultProfileLayoutAsync(cancellationToken);
                var galleryPrefs = await _settingsOperations.GetGalleryPresentationPreferencesAsync(cancellationToken);
                _defaultGalleryCardVariant = galleryPrefs.CardVariantId;

                RaisePropertyChanged(nameof(Theme));
                RaisePropertyChanged(nameof(Density));
                RaisePropertyChanged(nameof(ReduceMotion));
                RaisePropertyChanged(nameof(VideoPreviewDurationSeconds));
                RaisePropertyChanged(nameof(AutoplayVideo));
                RaisePropertyChanged(nameof(MuteAudioOnPreview));
                RaisePropertyChanged(nameof(AutoAnalyzeAfterImport));
                RaisePropertyChanged(nameof(PreserveSourceTimestamps));
                RaisePropertyChanged(nameof(DefaultProfileLayout));
                RaisePropertyChanged(nameof(DefaultGalleryCardVariant));
            }

            if (_configurationStore is not null)
            {
                var appConfig = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(appConfig.Configuration.UiLanguage))
                {
                    _currentLanguage = appConfig.Configuration.UiLanguage;
                    RaisePropertyChanged(nameof(CurrentLanguage));
                    RaisePropertyChanged(nameof(IsEnglishSelected));
                    RaisePropertyChanged(nameof(IsIndonesianSelected));
                }

                _updateFeedUrl = appConfig.Configuration.Updates.FeedUrl ?? string.Empty;
                RaisePropertyChanged(nameof(UpdateFeedUrl));
            }

            await LoadOrganizationAsync(cancellationToken);
            await LoadSystemMetricsAsync(cancellationToken);

            ShowReady();
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Failed to load settings: {OperationExecution.SafeMessage(ex)}");
        }
    }

    private async Task SaveUpdateFeedAsync()
    {
        if (_updateCoordinator is null)
        {
            return;
        }

        var result = await _updateCoordinator.SaveFeedAsync(UpdateFeedUrl).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _updateFeedUrl = UpdateFeedUrl.Trim();
            RunOnUi(() => RaisePropertyChanged(nameof(UpdateFeedUrl)));
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCoordinator is not null)
        {
            await _updateCoordinator.CheckAsync().ConfigureAwait(false);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updateCoordinator is not null)
        {
            await _updateCoordinator.DownloadAndInstallAsync().ConfigureAwait(false);
        }
    }

    public Task<LocalUpdateInspectionResult> InspectLocalUpdateAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        return _updateCoordinator is null
            ? Task.FromResult(LocalUpdateInspectionResult.Reject("The update service is unavailable."))
            : _updateCoordinator.InspectLocalPackageAsync(archivePath, cancellationToken);
    }

    public Task<UpdateCommandResult> InstallLocalUpdateAsync(
        string archivePath,
        string expectedPayloadSha256,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        return _updateCoordinator is null
            ? Task.FromResult(UpdateCommandResult.Failed("The update service is unavailable."))
            : _updateCoordinator.InstallFromLocalPackageAsync(
                archivePath,
                expectedPayloadSha256,
                userConfirmed,
                cancellationToken);
    }

    private void OnUpdateStateChanged(UpdatePresentationState state)
    {
        RunOnUi(() =>
        {
            _updateState = state;
            RaisePropertyChanged(nameof(UpdateStatusText));
            RaisePropertyChanged(nameof(UpdateErrorText));
            RaisePropertyChanged(nameof(UpdateCandidateVersion));
            RaisePropertyChanged(nameof(CanInstallUpdate));
            SaveUpdateFeedCommand.RaiseCanExecuteChanged();
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
            InstallUpdateCommand.RaiseCanExecuteChanged();
        });
    }

    public async Task ApplyLanguageAsync(string languageCode, CancellationToken cancellationToken = default)
    {
        var normalized = string.Equals(languageCode, "id", StringComparison.OrdinalIgnoreCase) ? "id" : "en";
        CurrentLanguage = normalized;
        SurfaceText.ApplyLanguage(normalized);

        if (_configurationStore is not null)
        {
            try
            {
                var save = await _configurationStore.UpdateAsync(
                    configuration => configuration with { UiLanguage = normalized },
                    cancellationToken);
                if (!save.IsSaved)
                {
                    ShowRecoverableError(save.SafeErrorDetail ?? "The language preference could not be saved.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ShowRecoverableError($"The language preference could not be saved: {OperationExecution.SafeMessage(exception)}");
            }
        }
    }

    private void SubmitMediaPreferences() => _mediaPreferencesPersistence.Submit(new MediaPreferences(
        SchemaVersion: 1,
        VideoPreviewDurationSeconds: VideoPreviewDurationSeconds,
        AutoplayVideo: AutoplayVideo,
        MuteAudioOnPreview: MuteAudioOnPreview));

    private void SubmitImportPreferences() => _importPreferencesPersistence.Submit(new ImportPreferences(
        SchemaVersion: 1,
        AutoAnalyzeAfterImport: AutoAnalyzeAfterImport,
        PreserveSourceTimestamps: PreserveSourceTimestamps));

    private async Task PersistMediaPreferencesAsync(MediaPreferences preferences, CancellationToken cancellationToken)
    {
        if (_settingsOperations is null)
        {
            _durableMediaPreferences = preferences;
            return;
        }

        var result = await _settingsOperations.SetMediaPreferencesAsync(preferences, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.UserMessage ?? "Media preferences were not persisted.");
        }
    }

    private async Task PersistImportPreferencesAsync(ImportPreferences preferences, CancellationToken cancellationToken)
    {
        if (_settingsOperations is null)
        {
            _durableImportPreferences = preferences;
            return;
        }

        var result = await _settingsOperations.SetImportPreferencesAsync(preferences, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.UserMessage ?? "Import preferences were not persisted.");
        }
    }

    private void RollbackMediaPreferences()
    {
        _videoPreviewDurationSeconds = _durableMediaPreferences.VideoPreviewDurationSeconds;
        _autoplayVideo = _durableMediaPreferences.AutoplayVideo;
        _muteAudioOnPreview = _durableMediaPreferences.MuteAudioOnPreview;
        RaisePropertyChanged(nameof(VideoPreviewDurationSeconds));
        RaisePropertyChanged(nameof(AutoplayVideo));
        RaisePropertyChanged(nameof(MuteAudioOnPreview));
    }

    private void RollbackImportPreferences()
    {
        _autoAnalyzeAfterImport = _durableImportPreferences.AutoAnalyzeAfterImport;
        _preserveSourceTimestamps = _durableImportPreferences.PreserveSourceTimestamps;
        RaisePropertyChanged(nameof(AutoAnalyzeAfterImport));
        RaisePropertyChanged(nameof(PreserveSourceTimestamps));
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
            return;
        }

        _uiContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    public async Task SetThemePreferenceAsync(
        string themeId,
        CancellationToken cancellationToken = default)
    {
        if (_presentationPreferenceApplier is null)
        {
            return;
        }

        var result = await _presentationPreferenceApplier.ApplyThemeAsync(themeId, cancellationToken);
        if (!result.Succeeded)
        {
            if (_settingsOperations is not null)
            {
                _theme = await _settingsOperations.GetThemeAsync(cancellationToken);
                RaisePropertyChanged(nameof(Theme));
                RefreshThemeSelection();
            }

            ShowRecoverableError(result.Message ?? "The theme could not be saved.");
        }
    }

    public async Task SetDensityPreferenceAsync(
        DensityMode density,
        CancellationToken cancellationToken = default)
    {
        if (_settingsOperations is null)
        {
            return;
        }

        var result = await _settingsOperations.SetDensityAsync(density, cancellationToken);
        if (result.IsSuccess && result.Value is { } appliedDensity)
        {
            _presentationPreferenceApplier?.ApplyDensity(appliedDensity);
        }
    }

    public async Task SetReduceMotionPreferenceAsync(
        bool reduceMotion,
        CancellationToken cancellationToken = default)
    {
        if (_settingsOperations is null)
        {
            return;
        }

        var result = await _settingsOperations.SetReduceMotionAsync(reduceMotion, cancellationToken);
        if (result.IsSuccess)
        {
            _presentationPreferenceApplier?.ApplyReduceMotion(reduceMotion);
        }
    }

    public async Task LoadOrganizationAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is null)
        {
            return;
        }

        Categories.Clear();
        var cats = await _catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken);
        foreach (var cat in cats)
        {
            var usage = _reads is not null
                ? await _reads.GetCategoryUsageCountAsync(cat.CategoryId, cancellationToken)
                : 0;
            Categories.Add(new CategoryItemViewModel(cat.CategoryId, cat.Name, usage, cat.RowVersion));
        }

        Tags.Clear();
        var tags = await _catalog.SettingsReads.GetAllTagsAsync(cancellationToken);
        foreach (var tag in tags)
        {
            var usage = _reads is not null
                ? await _reads.GetTagUsageCountAsync(tag.TagId, cancellationToken)
                : 0;
            Tags.Add(new TagItemViewModel(tag.TagId, tag.Name, usage, tag.RowVersion));
        }
    }

    public async Task CreateCategoryAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewCategoryName) || _catalog is null)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var id = Guid.NewGuid().ToString("N")[..12];
            var writes = new SettingsWrites(_catalog);
            await writes.CreateCategoryAsync(id, NewCategoryName.Trim(), cancellationToken);

            Categories.Add(new CategoryItemViewModel(id, NewCategoryName.Trim(), 0, 0));
            OrganizationMessage = $"Category '{NewCategoryName.Trim()}' created.";
            NewCategoryName = string.Empty;
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not create category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task RenameCategoryAsync(CategoryItemViewModel item, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(newName) || _catalog is null)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            var nextVer = await writes.UpdateCategoryAsync(item.CategoryId, newName.Trim(), item.RowVersion, cancellationToken)
                ;

            item.Name = newName.Trim();
            item.RowVersion = nextVer;
            item.CancelEdit();
            OrganizationMessage = $"Category renamed to '{item.Name}'.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not rename category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task DeleteCategoryAsync(CategoryItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_catalog is null)
        {
            Categories.Remove(item);
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            await writes.DeleteCategoryAsync(item.CategoryId, item.RowVersion, cancellationToken);

            Categories.Remove(item);
            OrganizationMessage = $"Category '{item.Name}' deleted.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not delete category: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task CreateTagAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewTagName) || _catalog is null)
        {
            return;
        }

        // K04.1: Support batch comma/Enter creation using TaxonomyNamePolicy.
        var tokens = TaxonomyNamePolicy.ParseTagTokens(NewTagName);
        if (tokens.Count == 0)
        {
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;
            var writes = new SettingsWrites(_catalog);
            var created = 0;

            foreach (var token in tokens)
            {
                // K04.4: Check for duplicate canonical names.
                if (Tags.Any(t => TaxonomyNamePolicy.AreSameName(t.Name, token.CanonicalName)))
                {
                    continue;
                }

                var id = Guid.NewGuid().ToString("N")[..12];
                await writes.CreateTagAsync(id, token.DisplayName, cancellationToken);
                Tags.Add(new TagItemViewModel(id, token.DisplayName, 0, 0));
                created++;
            }

            if (created > 0)
            {
                OrganizationMessage = created == 1
                    ? $"Tag '{tokens[0].DisplayName}' created."
                    : $"{created} tags created.";
            }
            NewTagName = string.Empty;
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not create tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task RenameTagAsync(TagItemViewModel item, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(newName) || _catalog is null)
        {
            return;
        }

        // K04.3: Reject multi-token rename input.
        var tokens = TaxonomyNamePolicy.ParseTagTokens(newName);
        if (tokens.Count > 1)
        {
            OrganizationWarningMessage = "To add multiple tags, use the New Tag field. Rename only works for one tag at a time.";
            return;
        }

        if (tokens.Count == 0)
        {
            return;
        }

        var displayName = tokens[0].DisplayName;

        // K04.4: Check duplicate canonical name.
        if (Tags.Any(t => t.TagId != item.TagId && TaxonomyNamePolicy.AreSameName(t.Name, displayName)))
        {
            OrganizationWarningMessage = $"A tag with that name already exists.";
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            var nextVer = await writes.UpdateTagAsync(item.TagId, displayName, item.RowVersion, cancellationToken);

            item.Name = displayName;
            item.RowVersion = nextVer;
            item.CancelEdit();
            OrganizationMessage = $"Tag renamed to '{item.Name}'.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not rename tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task DeleteTagAsync(TagItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_catalog is null)
        {
            Tags.Remove(item);
            return;
        }

        try
        {
            OrganizationMessage = null;
            OrganizationWarningMessage = null;

            var writes = new SettingsWrites(_catalog);
            await writes.DeleteTagAsync(item.TagId, item.RowVersion, cancellationToken);

            Tags.Remove(item);
            OrganizationMessage = $"Tag '{item.Name}' deleted.";
        }
        catch (Exception ex)
        {
            OrganizationWarningMessage = $"Could not delete tag: {OperationExecution.SafeMessage(ex)}";
        }
    }

    public async Task LoadSystemMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (_storageMetrics is null && _catalog is null)
        {
            return;
        }

        try
        {
            if (_storageMetrics is not null)
            {
                SystemMetrics = await _storageMetrics.GetSnapshotAsync(cancellationToken);
            }

            if (_catalog is not null)
            {
                var summary = await _catalog.HealthReads.GetHealthSummaryAsync(cancellationToken);
                UnresolvedFaceCount = summary.UnresolvedFaceDetections;
            }

            RaisePropertyChanged(nameof(ManagedStorageFormatted));
            RaisePropertyChanged(nameof(TotalProfilesCount));
            RaisePropertyChanged(nameof(ActiveAssetsCount));
        }
        catch (Exception ex)
        {
            ShowRecoverableError($"Could not evaluate system metrics: {OperationExecution.SafeMessage(ex)}");
        }
    }

    private void LoadActiveSection()
    {
        if (ActiveSection == SettingsSection.Activity)
        {
            TaskObserver.Observe(Activity.LoadAsync(reset: true), "SettingsViewModel.Activity.LoadAsync");
        }
        else if (ActiveSection == SettingsSection.Trash)
        {
            TaskObserver.Observe(Trash.LoadAsync(), "SettingsViewModel.Trash.LoadAsync");
        }
        else if (ActiveSection == SettingsSection.System)
        {
            TaskObserver.Observe(LoadSystemMetricsAsync(), "SettingsViewModel.LoadSystemMetricsAsync");
            if (ActiveSubsection == SettingsSubsection.LibraryHealth)
            {
                TaskObserver.Observe(Health.LoadMetricsAsync(), "SettingsViewModel.Health.LoadMetricsAsync");
            }
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Category:
            case CatalogInvalidationDomain.Tag:
            case CatalogInvalidationDomain.TaxonomyUsage:
                SignalOrganizationCoalescer();
                break;
        }
    }

    private RefreshCoalescer? _organizationCoalescer;

    private void SignalOrganizationCoalescer()
    {
        _organizationCoalescer ??= new RefreshCoalescer(
            ct => LoadOrganizationAsync(ct),
            TimeSpan.FromMilliseconds(600));
        UiDispatch.Run(() => _organizationCoalescer.Signal());
    }
}

/// <summary>
/// One selectable theme, carrying the swatch colours needed to show what it actually looks like.
///
/// The brushes are resolved once, here, from the theme's own definition rather than from the live
/// resource dictionary, so every option renders in its own palette instead of all of them rendering
/// in whichever theme happens to be active.
/// </summary>
public sealed class ThemeOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public ThemeOptionViewModel(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        Id = theme.Id;
        Name = theme.Name;
        IsDark = theme.IsDark;

        CanvasColor = theme.Colors.Canvas;
        SurfaceColor = theme.Colors.Surface2;
        AccentColor = theme.Colors.Accent;
        TextColor = theme.Colors.TextPrimary;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsDark { get; }

    /// <summary>Swatch colours as validated #AARRGGBB text; the renderer turns them into brushes.</summary>
    public string CanvasColor { get; }

    public string SurfaceColor { get; }

    public string AccentColor { get; }

    public string TextColor { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
