using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Intake;
using Neuterradise.App.Shell;
using Windows.ApplicationModel.DataTransfer;

namespace Neuterradise.App.Ui;

/// <summary>
/// Import: a quiet, human workflow. Drop/Pick → Reading → Choose Profile → Checking → Verify → Save → Done,
/// with truthful live activity controls: Pause/Prioritize/Cancel while running or Start/Resume/Cancel when paused.
/// Durable work keeps going when the person navigates away; there is no manual Refresh step.
/// </summary>
public sealed class ImportSurface : Surface
{
    private readonly ImportViewModel _vm;
    private readonly Grid _root = new();
    private readonly StackPanel _list = UI.V(10);
    private readonly Grid _wizardHost = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _intake = UI.Text(string.Empty, "body-muted", maxLines: 2);
    private readonly StackPanel _filters = UI.H(6);
    private readonly Dictionary<Guid, ImportUnitRow> _rows = [];
    private FrameworkElement? _emptyActivity;
    private ImportWizardViewModel? _wizard;
    private Disposables? _wizardBag;
    private Disposables? _wizardRenderBag;

    public ImportSurface(AppServices services, ImportViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        var page = UI.V(20,
            UI.V(2, UI.Text(UI.T("Nav.Import", "Import"), "page-title"), UI.Text(_vm.MoveSummary, "body-muted", maxLines: 3)),
            DropZone(),
            _intake,
            UI.Grid("auto", "*,auto", UI.Text(UI.T("Import.Activity", "Your imports"), "section-title").At(0, 0), _filters.At(0, 1)),
            _list);
        page.MaxWidth = 1000;
        var scroll = UI.Scroll(page);
        _root.SizeChanged += (_, _) => page.Margin = new Thickness(UI.PagePadding(_root.ActualWidth), 20, UI.PagePadding(_root.ActualWidth), 40);
        _root.Children.Add(scroll);
        _root.Children.Add(_wizardHost);

        Bag.Add(Observe.Collection(_vm.FilteredUnits, ReconcileRows));
        Bag.Add(Observe.Props(_vm, UpdateHeader, nameof(ImportViewModel.IntakeStatusMessage), nameof(ImportViewModel.HasIntakeStatus), nameof(ImportViewModel.IsActiveFilter), nameof(ImportViewModel.IsAttentionFilter), nameof(ImportViewModel.IsHistoryFilter), nameof(ImportViewModel.HasAttention), nameof(ImportViewModel.HasClearableHistory)));
        Bag.Add(Observe.Props(_vm, UpdateWizard, nameof(ImportViewModel.Wizard)));
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    protected override void OnActivated(AppRoute route)
    {
        // ImportActivityService owns the live polling loop. Entering the page only wakes that loop;
        // it must not turn a transient database read into a page-level "could not refresh" notice.
        Services.ImportActivity.RequestRefresh();
    }

    /// <summary>Starts intake for paths picked elsewhere (e.g. "Add media" on a Profile).</summary>
    public Task IntakeAsync(IEnumerable<string> paths, Guid? destinationProfileId) =>
        _vm.IntakeSourcesAsync(paths, IntakeOrigin.Picker, destinationProfileId);

    private FrameworkElement DropZone()
    {
        var theme = ThemeRuntime.Current;
        var zone = new Border
        {
            AllowDrop = true,
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusSurface", 16)),
            BorderBrush = theme.Brush("borderDefault"),
            BorderThickness = new Thickness(1.5),
            Background = theme.Brush("surface1"),
            Padding = new Thickness(24),
            Child = UI.V(12,
                new IconView("icon.navigation.import", 32, "accent").Align(HorizontalAlignment.Center),
                UI.Text(UI.T("Import.Drop", "Drop photos, videos, 3D models or folders here"), "section-title").Align(HorizontalAlignment.Center),
                UI.H(8,
                    UI.Button(UI.T("Import.ChooseFiles", "Choose files"), () => Services.RunUserAction(PickFilesAsync(), "ImportSurface.PickFilesAsync", UI.T("Import.PickerFailed", "Files could not be selected.")), ButtonKind.Primary),
                    UI.Button(UI.T("Import.ChooseFolder", "Choose folder"), () => Services.RunUserAction(PickFolderAsync(), "ImportSurface.PickFolderAsync", UI.T("Import.PickerFailed", "A folder could not be selected."))).Align(HorizontalAlignment.Center))),
        };
        zone.DragOver += (_, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            zone.BorderBrush = theme.Brush("accent");
        };
        zone.DragLeave += (_, _) => zone.BorderBrush = theme.Brush("borderDefault");
        zone.Drop += (_, e) => Services.RunUserAction(
            HandleDropAsync(zone, theme, e),
            "ImportSurface.DropAsync",
            UI.T("Import.DropFailed", "Those items could not be imported."));
        return zone;
    }

    private async Task HandleDropAsync(Border zone, ThemeRuntime theme, DragEventArgs args)
    {
        zone.BorderBrush = theme.Brush("borderDefault");
        if (!args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await args.DataView.GetStorageItemsAsync();
        var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (paths.Count > 0)
        {
            await _vm.IntakeSourcesAsync(paths, IntakeOrigin.DragDrop).ConfigureAwait(true);
        }
    }

    private async Task PickFilesAsync()
    {
        var files = await Services.PickFilesAsync(true).ConfigureAwait(true);
        if (files.Count > 0)
        {
            await _vm.IntakeSourcesAsync(files).ConfigureAwait(true);
        }
    }

    private async Task PickFolderAsync()
    {
        var folder = await Services.PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await _vm.IntakeSourcesAsync([folder]).ConfigureAwait(true);
        }
    }

    private void UpdateHeader()
    {
        _intake.Text = _vm.IntakeStatusMessage ?? string.Empty;
        _intake.Visibility = _vm.HasIntakeStatus ? Visibility.Visible : Visibility.Collapsed;
        _filters.Children.Clear();
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.Active", "In progress"), _vm.IsActiveFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.Active)));
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.Attention", "Needs attention") + (_vm.HasAttention ? " •" : string.Empty), _vm.IsAttentionFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.NeedsAttention)));
        _filters.Children.Add(UI.Chip(UI.T("Import.Filter.History", "Finished"), _vm.IsHistoryFilter, () => _vm.SelectFilterCommand.Execute(ImportActivityFilter.History)));
        if (_vm.IsHistoryFilter && _vm.HasClearableHistory)
        {
            _filters.Children.Add(UI.Button(
                    UI.T("Import.ClearAll", "Clear All"),
                    null,
                    ButtonKind.Ghost,
                    command: _vm.ClearHistoryCommand)
                .Tip(UI.T("Import.ClearAll.Tooltip", "Remove all finished imports from history")));
        }
        ReconcileRows();
    }

    private void ReconcileRows()
    {
        if (_vm.ShowEmptyActivity)
        {
            RetireAllRows();
            if (_emptyActivity is not null)
            {
                _list.Children.Remove(_emptyActivity);
            }
            _emptyActivity = UI.Surface(UI.V(4, UI.Text(_vm.EmptyActivityTitle, "body-strong"), UI.Text(_vm.EmptyActivityMessage, "body-muted")), Material.Grounded, 12, 16);
            _list.Children.Add(_emptyActivity);
            return;
        }

        if (_emptyActivity is not null)
        {
            _list.Children.Remove(_emptyActivity);
            _emptyActivity = null;
        }

        var wanted = _vm.FilteredUnits.Select(unit => unit.UnitId).ToHashSet();
        foreach (var retired in _rows.Where(pair => !wanted.Contains(pair.Key)).ToList())
        {
            _list.Children.Remove(retired.Value.View);
            retired.Value.Dispose();
            _rows.Remove(retired.Key);
        }

        for (var index = 0; index < _vm.FilteredUnits.Count; index++)
        {
            var unit = _vm.FilteredUnits[index];
            if (!_rows.TryGetValue(unit.UnitId, out var row))
            {
                row = new ImportUnitRow(_vm, unit);
                _rows.Add(unit.UnitId, row);
            }

            var currentIndex = _list.Children.IndexOf(row.View);
            if (currentIndex == index)
            {
                continue;
            }
            if (currentIndex >= 0)
            {
                _list.Children.RemoveAt(currentIndex);
            }
            _list.Children.Insert(index, row.View);
        }
    }

    private void RetireAllRows()
    {
        foreach (var row in _rows.Values)
        {
            _list.Children.Remove(row.View);
            row.Dispose();
        }
        _rows.Clear();
    }

    private sealed class ImportUnitRow : IDisposable
    {
        private readonly ImportViewModel _owner;
        private readonly ImportUnitItemViewModel _unit;
        private readonly TextBlock _title;
        private readonly TextBlock _stage;
        private readonly Border _priority;
        private readonly TextBlock _detail;
        private readonly ProgressBar _progress;
        private readonly TextBlock _progressText;
        private readonly StackPanel _actions;
        private readonly IDisposable _subscription;

        public ImportUnitRow(ImportViewModel owner, ImportUnitItemViewModel unit)
        {
            _owner = owner;
            _unit = unit;
            _title = UI.Text(string.Empty, "body-strong", maxLines: 1);
            _stage = UI.Text(string.Empty, "metadata");
            _priority = UI.Badge(UI.T("Import.Priority", "Priority"), "accent");
            _detail = UI.Text(string.Empty, "caption", maxLines: 2);
            _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 6, 0, 0) };
            _progressText = UI.Text(string.Empty, "caption");
            _actions = UI.H(6);
            View = UI.Surface(UI.V(4,
                UI.Grid("auto", "*,auto", _title.At(0, 0), UI.H(6, _priority, _stage).At(0, 1)),
                _detail,
                _progress,
                _progressText,
                _actions.Margin(0, 6, 0, 0)), Material.Grounded, 12, 16);
            _subscription = Observe.Props(unit, Apply);
        }

        public FrameworkElement View { get; }

        private void Apply()
        {
            _title.Text = WebUtility.HtmlDecode(_unit.SourceDisplayName);
            _stage.Text = _unit.StageText;
            _stage.Foreground = ThemeRuntime.Current.Brush(_unit.NeedsAttention ? "warning" : "textSecondary");
            _priority.Visibility = _unit.IsPriority ? Visibility.Visible : Visibility.Collapsed;
            _detail.Text = _unit.DetailText;
            _progress.Visibility = _unit.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsIndeterminate = _unit.IsIndeterminate;
            _progress.Value = _unit.ProgressValue;
            _progressText.Text = _unit.ProgressText ?? string.Empty;
            _actions.Children.Clear();
            if (_unit.CanContinue)
            {
                var actionText = _unit.Stage == ImportActivityStage.ReadyToVerify
                    ? UI.T("Import.Verify", "Verify")
                    : UI.T("Import.ChooseProfile", "Choose Profile");
                _actions.Children.Add(UI.Button(actionText, null, ButtonKind.Primary, command: _owner.ContinueImportCommand, parameter: _unit));
            }
            if (_unit.ShowTransportControls)
            {
                if (_unit.CanStart)
                {
                    _actions.Children.Add(UI.Button(
                        UI.T("Import.Start", "Start"),
                        null,
                        ButtonKind.Primary,
                        "icon.action.play",
                        _owner.StartUnitCommand,
                        _unit).Tip(_unit.TransportTooltip));
                }
                else
                {
                    var pause = UI.Button(UI.T("Import.Pause", "Pause"), null, icon: "icon.action.pause", command: _owner.PauseUnitCommand, parameter: _unit)
                        .Tip(UI.T("Import.Pause.Tooltip", "Pause import"));
                    pause.IsEnabled = _unit.CanPause;
                    _actions.Children.Add(pause);

                    if (_unit.CanPrioritize)
                    {
                        _actions.Children.Add(UI.Button(
                            UI.T("Import.Prioritize", "Prioritize"),
                            null,
                            ButtonKind.Primary,
                            "icon.action.play",
                            _owner.StartUnitCommand,
                            _unit).Tip(_unit.TransportTooltip));
                    }
                }
            }
            if (_unit.CanRetry) _actions.Children.Add(UI.Button(UI.T("Import.Retry", "Retry"), null, command: _owner.RetryUnitCommand, parameter: _unit));
            if (_unit.CanOpenProfile) _actions.Children.Add(UI.Button(UI.T("Import.OpenProfile", "Open Profile"), null, ButtonKind.Ghost, command: _owner.OpenProfileCommand, parameter: _unit));
            if (_unit.CanClearHistory) _actions.Children.Add(UI.Button(UI.T("Import.Clear", "Clear"), null, ButtonKind.Ghost, command: _owner.ClearHistoryItemCommand, parameter: _unit)
                .Tip(UI.T("Import.Clear.Tooltip", "Remove this import from history")));
            if (_unit.CanCancel)
            {
                _actions.Children.Add(UI.Button(
                    UI.T("Import.Cancel", "Cancel"),
                    () =>
                    {
                        _owner.IntakeStatusMessage = UI.T(
                            "Import.CancelRequested",
                            "Cancellation requested. Running work will stop at the next safe interruption point.");
                        _owner.CancelUnitCommand.Execute(_unit);
                    },
                    ButtonKind.Ghost));
            }
        }

        public void Dispose() => _subscription.Dispose();
    }

    // ---------------------------------------------------------------- wizard

    private void UpdateWizard()
    {
        if (ReferenceEquals(_wizard, _vm.Wizard))
        {
            return;
        }

        _wizardBag?.Dispose();
        _wizardRenderBag?.Dispose();
        _wizardBag = new Disposables();
        _wizard = _vm.Wizard;
        _wizardHost.Children.Clear();
        if (_wizard is null)
        {
            _wizardHost.Visibility = Visibility.Collapsed;
            return;
        }

        var theme = ThemeRuntime.Current;
        var wizard = _wizard;
        var content = new Grid();
        var body = UI.V(14);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var footer = UI.H(8);
        var error = UI.Text(string.Empty, "caption", "danger", 3);
        var stepLabel = UI.Text(string.Empty, "caption");
        var card = UI.Surface(UI.Grid("auto,*,auto,auto", "*",
            UI.V(2, UI.Text(WebUtility.HtmlDecode(wizard.SourceDisplayName), "section-title", maxLines: 1), stepLabel).At(0),
            scroll.Margin(0, 12, 0, 12).At(1),
            error.At(2),
            footer.Align(HorizontalAlignment.Right).At(3)), Material.Grounded, theme.Tokens.Number("radiusSurface", 16), 20);
        card.MaxWidth = 760;
        card.MaxHeight = 640;
        card.Margin = new Thickness(16);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        content.Background = theme.Brush("scrimMedium");
        content.Children.Add(card);
        _wizardHost.Children.Add(content);
        _wizardHost.Visibility = Visibility.Visible;

        void Render()
        {
            _wizardRenderBag?.Dispose();
            _wizardRenderBag = new Disposables();
            body.Children.Clear();
            footer.Children.Clear();
            var preparation = UI.Text(string.Empty, "caption");
            var mediaSummary = UI.Text(string.Empty, "body-muted", maxLines: 2);
            body.Children.Add(preparation);
            body.Children.Add(mediaSummary);
            _wizardRenderBag.Add(Observe.Props(wizard, () =>
            {
                preparation.Text = wizard.PreparationText;
                preparation.Visibility = string.IsNullOrWhiteSpace(preparation.Text) ? Visibility.Collapsed : Visibility.Visible;
                mediaSummary.Text = wizard.MediaSummaryText;
            }, nameof(ImportWizardViewModel.PreparationText), nameof(ImportWizardViewModel.MediaSummaryText)));
            if (wizard.IsChooseProfileMode)
            {
                BuildProfileStep(wizard, body, _wizardRenderBag);
                footer.Children.Add(UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, ButtonKind.Ghost, command: wizard.CloseCommand));
                footer.Children.Add(UI.Button(UI.T("Import.ChooseProfile", "Choose Profile"), null, ButtonKind.Primary, command: wizard.ContinueCommand));
            }
            else
            {
                BuildDetailsStep(wizard, body, _wizardRenderBag);
                footer.Children.Add(UI.Button(UI.T("Overlay.Cancel", "Cancel"), null, ButtonKind.Ghost, command: wizard.CloseCommand));
                footer.Children.Add(UI.Button(UI.T("Import.Save", "Save"), null, ButtonKind.Primary, "icon.navigation.import", command: wizard.ImportCommand));
            }
        }

        _wizardBag.Add(Observe.Props(wizard, () =>
        {
            stepLabel.Text = $"{wizard.StepIndicator} · {wizard.StepTitle}";
            error.Text = wizard.ErrorMessage ?? string.Empty;
            error.Visibility = wizard.HasError ? Visibility.Visible : Visibility.Collapsed;
        }, nameof(ImportWizardViewModel.StepIndicator), nameof(ImportWizardViewModel.StepTitle), nameof(ImportWizardViewModel.ErrorMessage), nameof(ImportWizardViewModel.HasError)));
        _wizardBag.Add(Observe.Props(wizard, Render, nameof(ImportWizardViewModel.Step), nameof(ImportWizardViewModel.Choice)));
    }

    private static void BuildProfileStep(ImportWizardViewModel wizard, StackPanel body, Disposables lifetime)
    {
        var choices = UI.H(8,
            UI.Chip(UI.T("Import.Choice.Existing", "Existing Profile"), wizard.IsExistingChoice, () => wizard.ChooseExistingCommand.Execute(null)),
            UI.Chip(UI.T("Import.Choice.New", "New Profile"), wizard.IsNewChoice, () => wizard.ChooseNewCommand.Execute(null)),
            UI.Chip(UI.T("Import.Choice.DecideLater", "Decide Later"), wizard.IsDecideLaterChoice, () => wizard.ChooseDecideLaterCommand.Execute(null)));
        body.Children.Add(UI.Text(UI.T("Import.Step.Who", "Who is this for?"), "body-strong"));
        body.Children.Add(choices);

        var hints = UI.V(4);
        void FillHints()
        {
            hints.Children.Clear();
            foreach (var hint in wizard.SharedMediaHints.Concat(wizard.FaceHints).Take(4))
            {
                hints.Children.Add(UI.Button($"{hint.DisplayName} — {hint.Evidence}", null, ButtonKind.Ghost, hint.IsFaceMatch ? "icon.profile.face" : "icon.media.image", wizard.UseHintCommand, hint));
            }
        }
        lifetime.Add(Observe.Collection(wizard.SharedMediaHints, FillHints));
        lifetime.Add(Observe.Collection(wizard.FaceHints, FillHints));
        body.Children.Add(hints);

        if (wizard.IsNewChoice)
        {
            var name = UI.Input(UI.T("Import.NewName", "Profile name"), wizard.NewProfileName, text => wizard.NewProfileName = text);
            body.Children.Add(name);
            var validation = UI.V(6);
            void UpdateValidation()
            {
                validation.Children.Clear();
                if (wizard.NameValidationText is { } message)
                {
                    validation.Children.Add(UI.Text(message, "caption", "warning"));
                }

                if (wizard.HasDuplicateName)
                {
                    validation.Children.Add(UI.V(6, UI.Text(wizard.DuplicateNameText, "caption", "warning", 3),
                        UI.Button(UI.T("Import.UseExisting", "Use the existing Profile"), null, ButtonKind.Secondary, command: wizard.UseDuplicateNameProfileCommand)));
                }
            }
            lifetime.Add(Observe.Props(wizard, UpdateValidation,
                nameof(ImportWizardViewModel.NameValidationText),
                nameof(ImportWizardViewModel.HasDuplicateName),
                nameof(ImportWizardViewModel.DuplicateNameText)));
            body.Children.Add(validation);
        }
        else if (wizard.IsExistingChoice)
        {
            var search = UI.Input(UI.T("Import.SearchProfiles", "Search Profiles"), wizard.ExistingSearchText, text => wizard.ExistingSearchText = text);
            var list = UI.V(4);
            void Fill()
            {
                list.Children.Clear();
                foreach (var profile in wizard.ExistingProfiles.Take(40))
                {
                    var selected = ReferenceEquals(wizard.SelectedExistingProfile, profile);
                    list.Children.Add(UI.Chip(profile.DisplayName, selected, () => wizard.SelectedExistingProfile = profile));
                }
            }

            lifetime.Add(Observe.Collection(wizard.ExistingProfiles, Fill));
            lifetime.Add(Observe.Props(wizard, Fill, nameof(ImportWizardViewModel.SelectedExistingProfile)));
            body.Children.Add(search);
            body.Children.Add(list);
        }
        else
        {
            body.Children.Add(UI.Text(wizard.DestinationNote, "body-muted", maxLines: 4));
        }
    }

    private static void BuildDetailsStep(ImportWizardViewModel wizard, StackPanel body, Disposables lifetime)
    {
        var profileSummary = UI.Text(string.Empty, "body-strong", maxLines: 2);
        lifetime.Add(Observe.Props(wizard, () => profileSummary.Text = wizard.ProfileSummaryText, nameof(ImportWizardViewModel.ProfileSummaryText)));
        body.Children.Add(profileSummary);
        if (wizard.ShowNewProfileDetails)
        {
            var category = new ComboBox { MinWidth = 220 };
            var synchronizingCategory = false;
            void UpdateCategory()
            {
                synchronizingCategory = true;
                try
                {
                    category.ItemsSource = new[] { UI.T("Import.NoCategory", "No category") }.Concat(wizard.Categories.Select(c => c.Name)).ToList();
                    var index = wizard.Categories.ToList().FindIndex(c => c.CategoryId == wizard.SelectedCategoryId);
                    category.SelectedIndex = index + 1;
                }
                finally
                {
                    synchronizingCategory = false;
                }
            }
            category.SelectionChanged += (_, _) =>
            {
                if (!synchronizingCategory)
                {
                    wizard.SelectedCategoryId = category.SelectedIndex <= 0 ? null : wizard.Categories[category.SelectedIndex - 1].CategoryId;
                }
            };
            lifetime.Add(Observe.Collection(wizard.Categories, UpdateCategory));
            lifetime.Add(Observe.Props(wizard, UpdateCategory, nameof(ImportWizardViewModel.SelectedCategoryId)));

            var newCategory = UI.Input(UI.T("Import.NewCategory", "New category"), wizard.NewCategoryName, text => wizard.NewCategoryName = text);
            var createCategory = UI.H(8, newCategory, UI.Button(UI.T("Import.CreateCategory", "+ New Category"), null, ButtonKind.Secondary, command: wizard.CreateCategoryCommand));

            var tags = UI.H(6);
            void FillTags()
            {
                tags.Children.Clear();
                foreach (var tag in wizard.SelectedTags)
                {
                    tags.Children.Add(UI.Chip($"{tag.Name}  ✕", true, () => wizard.RemoveTagCommand.Execute(tag)));
                }
            }
            lifetime.Add(Observe.Collection(wizard.SelectedTags, FillTags));

            // K02: ONE tag input field — comma/Enter to commit, suggestions inline.
            var tagInput = new TextBox { Header = UI.T("Import.TagLabel", "Tags"), PlaceholderText = UI.T("Import.TagPlaceholder", "Type a tag. Press Enter or comma to add."), Text = wizard.TagSearchText };
            var synchronizingTagInput = false;
            tagInput.TextChanged += (_, _) =>
            {
                if (synchronizingTagInput) return;
                wizard.TagSearchText = tagInput.Text;
            };
            tagInput.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    TaskObserver.Observe(wizard.OnTagInputEnterAsync(), "ImportSurface.OnTagInputEnterAsync");
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                synchronizingTagInput = true;
                tagInput.Text = wizard.TagSearchText;
                synchronizingTagInput = false;
            }, nameof(ImportWizardViewModel.TagSearchText)));

            var suggestions = UI.H(6);
            void FillSuggestions()
            {
                suggestions.Children.Clear();
                foreach (var suggestion in wizard.TagSuggestions.Take(8))
                {
                    suggestions.Children.Add(UI.Chip(suggestion.Name, onClick: () => wizard.AddTagCommand.Execute(suggestion)));
                }
            }
            lifetime.Add(Observe.Collection(wizard.TagSuggestions, FillSuggestions));

            var rating = UI.H(4);
            var ratingIcons = new List<IconView>();
            for (var i = 1; i <= 5; i++)
            {
                var value = i;
                rating.Children.Add(UI.IconButton("icon.profile.rating", $"{value}", () => wizard.Rating = wizard.Rating == value ? 0 : value, 28).Visible(true));
                if (rating.Children[^1] is Button button && button.Content is IconView icon)
                {
                    ratingIcons.Add(icon);
                }
            }
            lifetime.Add(Observe.Props(wizard, () =>
            {
                for (var index = 0; index < ratingIcons.Count; index++)
                {
                    ratingIcons[index].ColorToken = wizard.Rating >= index + 1 ? "warning" : "textMuted";
                }
            }, nameof(ImportWizardViewModel.Rating)));

            var favorite = new ToggleSwitch { Header = UI.T("Import.Favorite", "Favorite"), IsOn = wizard.IsFavorite };
            var synchronizingFavorite = false;
            favorite.Toggled += (_, _) =>
            {
                if (!synchronizingFavorite)
                {
                    wizard.IsFavorite = favorite.IsOn;
                }
            };
            lifetime.Add(Observe.Props(wizard, () =>
            {
                synchronizingFavorite = true;
                favorite.IsOn = wizard.IsFavorite;
                synchronizingFavorite = false;
            }, nameof(ImportWizardViewModel.IsFavorite)));
            var overview = new TextBox { Header = UI.T("Import.Overview", "Overview"), Text = wizard.Overview, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
            overview.TextChanged += (_, _) => wizard.Overview = overview.Text;
            body.Children.Add(UI.Section(UI.T("Import.Details", "Details"), null, category, createCategory, tags, tagInput, suggestions, UI.H(12, UI.Text(UI.T("Import.Rating", "Rating"), "control"), rating), favorite, overview));
        }

        var problems = UI.V(8);
        void RenderProblems()
        {
            problems.Children.Clear();
            if (!wizard.HasProblems)
            {
                problems.Children.Add(UI.Text(UI.T("Import.ProblemsResolved", "All import decisions are resolved."), "body-muted"));
                return;
            }

            problems.Children.Add(UI.Text(UI.T("Import.Problems", "Problems to resolve"), "body-strong"));
            foreach (var problem in wizard.DuplicateProblems)
            {
                var owner = string.IsNullOrWhiteSpace(problem.ExistingProfileName)
                    ? UI.T("Import.ExistingLibraryMedia", "Existing library media")
                    : problem.ExistingProfileName;
                problems.Children.Add(UI.Surface(UI.V(6,
                    GatePreview(problem.PreviewImagePath),
                    UI.Text(UI.T("Import.AlreadyInLibrary", "Already in library"), "body-strong"),
                    UI.Text($"{problem.SourceFileName} · {owner}", "caption", maxLines: 2),
                    UI.H(8,
                        UI.Button(UI.T("Import.Duplicate.Continue", "Continue in destination"), null, ButtonKind.Primary, command: wizard.ResolveDuplicateReuseCommand, parameter: problem),
                        UI.Button(UI.T("Import.Duplicate.Remove", "Remove from this import"), null, ButtonKind.Ghost, command: wizard.ResolveDuplicateSkipCommand, parameter: problem))),
                    Material.Raised, 12, 12));
            }

            foreach (var problem in wizard.ProfileCollisionProblems)
            {
                problems.Children.Add(UI.Surface(UI.V(6,
                    GatePreview(problem.PreviewImagePath),
                    UI.Text(UI.T("Import.ProfileCollision", "Recognized as another Profile"), "body-strong"),
                    UI.Text($"{problem.SourceFileName} · Strong match: {problem.CandidateProfileName}", "caption", maxLines: 2),
                    UI.H(8,
                        UI.Button($"Continue {wizard.DestinationDisplayName}", null, ButtonKind.Primary, command: wizard.KeepCollisionCommand, parameter: problem),
                        UI.Button($"Move to {problem.CandidateProfileName}", null, ButtonKind.Secondary, command: wizard.MoveCollisionCommand, parameter: problem),
                        UI.Button(UI.T("Import.Collision.Skip", "Cancel this media"), null, ButtonKind.Ghost, command: wizard.SkipCollisionCommand, parameter: problem))),
                    Material.Raised, 12, 12));
            }
        }
        lifetime.Add(Observe.Collection(wizard.DuplicateProblems, RenderProblems));
        lifetime.Add(Observe.Collection(wizard.ProfileCollisionProblems, RenderProblems));
        lifetime.Add(Observe.Props(wizard, RenderProblems, nameof(ImportWizardViewModel.HasProblems)));
        body.Children.Add(problems);

        if (wizard.ShowAppearance)
        {
            body.Children.Add(VisualChoices(UI.T("Import.Cover", "Cover"), wizard, isCover: true, lifetime: lifetime));
            body.Children.Add(VisualChoices(UI.T("Import.Banner", "Banner"), wizard, isCover: false, lifetime: lifetime));
        }

        var transfer = UI.Text(string.Empty, "caption", maxLines: 4);
        lifetime.Add(Observe.Props(wizard, () => transfer.Text = wizard.TransferText, nameof(ImportWizardViewModel.TransferText)));
        body.Children.Add(transfer);
    }

    private static FrameworkElement GatePreview(string? path) => string.IsNullOrWhiteSpace(path)
        ? UI.Text(UI.T("Import.PreviewUnavailable", "Preview unavailable"), "caption")
        : new SkImageView { Source = ImageRef.FromPath(path, ImportWizardViewModel.PreviewDecodeWidth), CornerRadiusValue = 10, Height = 120 };

    private static FrameworkElement VisualChoices(string title, ImportWizardViewModel wizard, bool isCover, Disposables lifetime)
    {
        var content = UI.V(8, UI.Text(title, "body-strong"));
        // R02: Track per-item preview observers so they can be disposed on rerender.
        var previewObservers = new List<(ImportVisualOption Option, PropertyChangedEventHandler Handler)>();

        void RenderChoices()
        {
            // R02.1/R02.4: Dispose old observers before creating new ones.
            foreach (var (option, handler) in previewObservers)
            {
                option.PropertyChanged -= handler;
            }
            previewObservers.Clear();

            while (content.Children.Count > 1)
            {
                content.Children.RemoveAt(content.Children.Count - 1);
            }

            var options = isCover ? wizard.VisibleCoverOptions : wizard.VisibleBannerOptions;
            var select = isCover ? wizard.SelectCoverCommand : wizard.SelectBannerCommand;
            var pending = isCover ? wizard.IsCoverPending : wizard.IsBannerPending;
            var hasNoOptions = isCover ? wizard.HasNoCoverOptions : wizard.HasNoBannerOptions;
            var hasWeakOptions = isCover ? wizard.HasWeakCoverOptions : wizard.HasWeakBannerOptions;
            var row = UI.H(10);
            foreach (var option in options)
            {
                var image = new SkImageView { Source = option.Preview, CornerRadiusValue = 10, Width = 132, Height = 88 };
                // R02.1: Subscribe with tracked disposal.
                PropertyChangedEventHandler previewObserver = (_, args) =>
                {
                    if (args.PropertyName is nameof(ImportVisualOption.Preview) or nameof(ImportVisualOption.HasPreview))
                    {
                        UiDispatch.Run(() => image.Source = option.Preview);
                    }
                };
                option.PropertyChanged += previewObserver;
                previewObservers.Add((option, previewObserver));

                var tile = UI.Surface(UI.V(4, image, UI.Text(option.Title, "caption", maxLines: 1), option.IsRecommended ? UI.Badge(UI.T("Import.Recommended", "Recommended"), "accent") : null), option.IsSelected ? Material.Deep : Material.Raised, 12, 6);
                tile.BorderBrush = ThemeRuntime.Current.Brush(option.IsSelected ? "borderSelected" : "borderSubtle");
                tile.BorderThickness = new Thickness(option.IsSelected ? 2 : 1);
                tile.Tapped += (_, _) => select.Execute(option);
                row.Children.Add(tile);
            }
            if (pending)
            {
                // K06.1: Section remains visible while pending.
                var pendingText = isCover
                    ? UI.T("Import.PreparingCover", "Preparing Cover suggestions…")
                    : UI.T("Import.PreparingBanner", "Preparing Banner suggestions…");
                content.Children.Add(UI.Text(pendingText, "caption"));
            }
            else if (hasNoOptions)
            {
                // K06.2: Explicit no-candidate state.
                var noCandidateText = isCover
                    ? UI.T("Import.NoCoverSuggestion", "No suitable Cover suggestion was prepared.")
                    : UI.T("Import.NoBannerSuggestion", "No suitable Banner suggestion was prepared.");
                content.Children.Add(UI.Text(noCandidateText, "caption"));
            }
            else
            {
                if (hasWeakOptions)
                {
                    content.Children.Add(UI.Text(UI.T("Import.Appearance.NoStrongRecommendation", "No strong recommendation. Choose any eligible media."), "caption"));
                }
                content.Children.Add(UI.Scroll(row, horizontal: true));
            }
            var selected = isCover ? wizard.SelectedCover : wizard.SelectedBanner;
            if (selected is not null)
            {
                content.Children.Add(UI.Button(
                    UI.T("Common.Clear", "Clear"),
                    null,
                    ButtonKind.Ghost,
                    command: isCover ? wizard.ClearCoverCommand : wizard.ClearBannerCommand));
            }
            var hasMore = isCover ? wizard.HasMoreCovers : wizard.HasMoreBanners;
            if (hasMore)
            {
                content.Children.Add(UI.Button(
                    isCover ? wizard.CoverToggleText : wizard.BannerToggleText,
                    null,
                    ButtonKind.Ghost,
                    command: isCover ? wizard.ToggleAllCoversCommand : wizard.ToggleAllBannersCommand));
            }
        }

        lifetime.Add(Observe.Collection(isCover ? wizard.CoverOptions : wizard.BannerOptions, RenderChoices));
        lifetime.Add(Observe.Props(wizard, RenderChoices,
            isCover ? nameof(ImportWizardViewModel.SelectedCover) : nameof(ImportWizardViewModel.SelectedBanner),
            isCover ? nameof(ImportWizardViewModel.ShowAllCovers) : nameof(ImportWizardViewModel.ShowAllBanners),
            isCover ? nameof(ImportWizardViewModel.IsCoverPending) : nameof(ImportWizardViewModel.IsBannerPending),
            isCover ? nameof(ImportWizardViewModel.HasNoCoverOptions) : nameof(ImportWizardViewModel.HasNoBannerOptions),
            isCover ? nameof(ImportWizardViewModel.HasWeakCoverOptions) : nameof(ImportWizardViewModel.HasWeakBannerOptions)));
        // R02.1: Dispose observers when lifetime ends.
        lifetime.Add(() =>
        {
            foreach (var (option, handler) in previewObservers)
            {
                option.PropertyChanged -= handler;
            }
            previewObservers.Clear();
        });
        return content;
    }

    public override void Dispose()
    {
        _wizardRenderBag?.Dispose();
        _wizardBag?.Dispose();
        RetireAllRows();
        base.Dispose();
    }
}
