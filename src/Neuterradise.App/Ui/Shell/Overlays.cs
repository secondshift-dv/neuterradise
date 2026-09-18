using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Windows.Storage.Pickers;

namespace Neuterradise.App.Ui;

/// <summary>Compact focused dialogs (R2 §20): fit 800 × 600, never substitute pages.</summary>
public sealed class DialogService(ProductRoot root)
{
    private readonly List<DialogEntry> _openDialogs = [];

    private sealed record DialogEntry(FrameworkElement View, Action? OnDismiss);

    public bool HasOpenDialog => _openDialogs.Count > 0;

    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool destructive)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmButton = UI.Button(confirm, null, destructive ? ButtonKind.Destructive : ButtonKind.Primary);
        var cancelButton = UI.Button(UI.T("Overlay.Cancel", "Cancel"));
        var body = UI.V(12,
            UI.Text(title, "section-title"),
            UI.Text(message, "body", maxLines: 12),
            destructive ? UI.Text(UI.T("Dialog.CannotUndo", "This cannot be undone."), "caption", "danger") : null,
            UI.H(8, cancelButton, confirmButton).Align(HorizontalAlignment.Right));
        var dialog = Show(body, 480, () => completion.TrySetResult(false));
        confirmButton.Click += (_, _) => Close(dialog, () => completion.TrySetResult(true));
        cancelButton.Click += (_, _) => Close(dialog, () => completion.TrySetResult(false));
        return completion.Task;
    }

    public Task<string?> PromptAsync(string title, string label, string initial, string confirm)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = UI.Input(label, initial);
        var ok = UI.Button(confirm, null, ButtonKind.Primary);
        var cancel = UI.Button(UI.T("Overlay.Cancel", "Cancel"));
        var dialog = Show(
            UI.V(12, UI.Text(title, "section-title"), input, UI.H(8, cancel, ok).Align(HorizontalAlignment.Right)),
            440,
            () => completion.TrySetResult(null));
        ok.Click += (_, _) => Close(dialog, () => completion.TrySetResult(input.Text));
        cancel.Click += (_, _) => Close(dialog, () => completion.TrySetResult(null));
        return completion.Task;
    }

    public FrameworkElement Show(UIElement content, double width, Action? onDismiss = null)
    {
        var theme = ThemeRuntime.Current;
        var scrim = new Grid { Background = theme.Brush("scrimMedium") };
        var card = UI.Surface(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Material.Deep, theme.Tokens.Number("radiusSurface", 16), 20);
        card.MaxWidth = width;
        card.MaxHeight = 560;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Margin = new Thickness(16);
        scrim.Children.Add(card);
        root.OverlayLayer.Children.Add(scrim);
        root.OverlayLayer.Visibility = Visibility.Visible;
        _openDialogs.Add(new DialogEntry(scrim, onDismiss));
        return scrim;
    }

    public void Close(FrameworkElement dialog, Action? after = null)
    {
        Remove(dialog);
        after?.Invoke();
    }

    /// <summary>
    /// Handles shell Escape before overlays/customization. A modal without a structural-dismiss callback
    /// intentionally consumes Escape but remains open; callers that can be cancelled supply the callback.
    /// </summary>
    public bool TryDismissTop()
    {
        if (_openDialogs.Count == 0)
        {
            return false;
        }

        var entry = _openDialogs[^1];
        if (entry.OnDismiss is null)
        {
            return true;
        }

        Remove(entry.View);
        entry.OnDismiss();
        return true;
    }

    private void Remove(FrameworkElement dialog)
    {
        for (var index = _openDialogs.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_openDialogs[index].View, dialog))
            {
                _openDialogs.RemoveAt(index);
                break;
            }
        }

        root.OverlayLayer.Children.Remove(dialog);
        if (root.OverlayLayer.Children.Count == 0)
        {
            root.OverlayLayer.Visibility = Visibility.Collapsed;
        }
    }
}

public sealed class OverlayPresenter(AppServices services, ProductRoot root)
{
    private OverlayRequest? _shown;
    private FrameworkElement? _dialog;

    public void Sync()
    {
        var current = services.Overlay.Current;
        if (ReferenceEquals(current, _shown))
        {
            return;
        }

        if (_dialog is not null)
        {
            root.Dialogs.Close(_dialog);
            _dialog = null;
        }

        _shown = current;
        if (current is null)
        {
            return;
        }

        if (current is AppearanceCustomizationOverlayRequest appearance)
        {
            services.Overlay.CloseTop();
            _shown = null;
            TaskObserver.Observe(
                OpenShowroomAsync(appearance),
                "OverlayPresenter.OpenShowroomAsync",
                _ => services.Toast(UI.T("Profile.Error.Open", "This Profile could not be opened."), "warning"));
            return;
        }

        _dialog = root.Dialogs.Show(
            Build(current),
            current is ProfilePickerOverlayRequest ? 520 : 480,
            current.IsDismissableByEscape ? () => services.Overlay.RequestEscapeClose() : null);
    }

    private async Task OpenShowroomAsync(AppearanceCustomizationOverlayRequest request)
    {
        var state = await services.Presentation.LoadProfileStateAsync(request.ProfileId).ConfigureAwait(true);
        if (state is null)
        {
            services.Toast(UI.T("Profile.Error.NotFound", "Profile not found."), "warning");
            return;
        }

        var section = request.InitialSectionIndex switch
        {
            1 => PresentationSlots.ProfileCover,
            2 => PresentationSlots.ProfileBanner,
            3 => PresentationSlots.ProfileFrame,
            _ => PresentationSlots.ProfileLayout,
        };
        services.OpenCustomization(CustomizationCategories.Profile, PresentationContext.ForProfile(state), section, request);
    }

    private static string LocalizedPickerTitle(string title) => title switch
    {
        "Reassign Face to Profile" => UI.T("Faces.Picker.ReassignTitle", "Reassign Face to Profile"),
        "Assign Other Profile" => UI.T("Faces.Picker.AssignTitle", "Assign Other Profile"),
        "Filter by Related Profile" => UI.T("Gallery.RelatedPicker.Title", "Filter by Related Profile"),
        "Select Profile" => UI.T("SurfaceText.Select.Existing.Profile.7B23D2D3", "Select Profile"),
        _ => title,
    };

    private static string LocalizedPickerPrompt(string prompt) => prompt switch
    {
        "Select a Profile to assign this face detection to:" => UI.T("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"),
        "Choose a Profile to assign this face detection to:" => UI.T("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"),
        "Select a Profile to filter related Profiles:" => UI.T("Gallery.RelatedPicker.Prompt", "Select a Profile to filter related Profiles:"),
        "Choose a Profile to link:" => UI.T("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a Profile"),
        _ => prompt,
    };

    private UIElement Build(OverlayRequest request)
    {
        switch (request)
        {
            case ConfirmationOverlayRequest confirmation:
            {
                var confirm = UI.Button(confirmation.ConfirmLabel, () => services.Overlay.ConfirmTop(), confirmation.IsDestructive ? ButtonKind.Destructive : ButtonKind.Primary);
                var cancel = UI.Button(confirmation.CancelLabel, () => services.Overlay.CloseTop());
                return UI.V(12,
                    UI.Text(confirmation.Title, "section-title"),
                    UI.Text(confirmation.Message, "body", maxLines: 12),
                    confirmation.FormattedDestructiveWarning is { } warning ? UI.Text(warning, "caption", "danger") : null,
                    UI.H(8, cancel, confirm).Align(HorizontalAlignment.Right));
            }

            case ErrorDetailOverlayRequest error:
                return UI.V(12,
                    UI.Text(error.Title, "section-title"),
                    UI.Text(error.Message, "body", maxLines: 10),
                    error.Detail is { } detail ? UI.Text(detail, "caption", maxLines: 8) : null,
                    UI.Button(UI.T("Overlay.Dismiss", "Dismiss"), () => services.Overlay.CloseTop(), ButtonKind.Primary).Align(HorizontalAlignment.Right));

            case ProfilePickerOverlayRequest picker:
            {
                var list = new StackPanel { Spacing = 4 };
                var search = UI.Input(UI.T("Picker.Search", "Search Profiles"));
                void Fill()
                {
                    list.Children.Clear();
                    foreach (var candidate in picker.Candidates.Where(c => string.IsNullOrWhiteSpace(search.Text) || c.DisplayName.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).Take(200))
                    {
                        var row = UI.Button(candidate.CategoryName is null ? candidate.DisplayName : $"{candidate.DisplayName} · {candidate.CategoryName}", () =>
                        {
                            services.Overlay.CloseTop();
                            picker.OnProfileSelected(candidate);
                        }, ButtonKind.Ghost);
                        row.HorizontalAlignment = HorizontalAlignment.Stretch;
                        row.HorizontalContentAlignment = HorizontalAlignment.Left;
                        list.Children.Add(row);
                    }
                }

                search.TextChanged += (_, _) => Fill();
                Fill();
                return UI.V(10,
                    UI.Text(LocalizedPickerTitle(picker.Title), "section-title"),
                    UI.Text(LocalizedPickerPrompt(picker.Prompt), "body-muted"),
                    search,
                    new ScrollViewer { Content = list, MaxHeight = 320 },
                    UI.Button(UI.T("Overlay.Cancel", "Cancel"), () => services.Overlay.CloseTop()).Align(HorizontalAlignment.Right));
            }

            case AssociationEditorOverlayRequest associations:
            {
                var list = UI.V(6);
                foreach (var association in associations.Associations)
                {
                    var relation = association.RelationKind switch
                    {
                        "Owner" => UI.T("Profile.Relation.Owner", "Owner"),
                        "Appears In" => UI.T("Profile.Relation.Appears", "Appears In"),
                        "Manual" => UI.T("Profile.Relation.Manual", "Manual"),
                        _ => association.RelationKind,
                    };
                    list.Children.Add(UI.Grid("auto", "*,auto",
                        UI.Text($"{association.DisplayName} · {relation}", "body").At(0, 0),
                        associations.OnRemoveAssociation is null ? null : UI.Button(UI.T("Common.Remove", "Remove"), () =>
                        {
                            services.Overlay.CloseTop();
                            associations.OnRemoveAssociation(association.TargetProfileId);
                        }, ButtonKind.Ghost).At(0, 1)));
                }

                return UI.V(12,
                    UI.Text(associations.Title, "section-title"),
                    UI.Text(associations.SourceProfileDisplayName, "body-muted"),
                    list,
                    UI.H(8,
                        associations.OnAddAssociationRequested is null ? null : UI.Button(UI.T("Common.Add", "Add"), () =>
                        {
                            services.Overlay.CloseTop();
                            associations.OnAddAssociationRequested();
                        }),
                        UI.Button(UI.T("Overlay.Dismiss", "Done"), () => services.Overlay.CloseTop(), ButtonKind.Primary)).Align(HorizontalAlignment.Right));
            }

            default:
                return UI.V(12, UI.Text(request.Title, "section-title"), UI.Button(UI.T("Overlay.Dismiss", "Dismiss"), () => services.Overlay.CloseTop(), ButtonKind.Primary));
        }
    }
}

public static class Pickers
{
    private static void InitializeDesktopPicker(Window window, object picker)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(picker);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The desktop picker requires an attached application window.");
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window window, bool multiple, params string[] extensions)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeDesktopPicker(window, picker);
        foreach (var extension in extensions.Length == 0 ? ["*"] : extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files is null ? [] : [.. files.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p))];
        }

        var file = await picker.PickSingleFileAsync();
        return file is null || string.IsNullOrWhiteSpace(file.Path) ? [] : [file.Path];
    }

    public static async Task<string?> PickFolderAsync(Window window)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeDesktopPicker(window, picker);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(Window window, string suggestedName, string extension, string label)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitializeDesktopPicker(window, picker);
        picker.FileTypeChoices.Add(label, [extension]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

public static class StartupScreens
{
    public static FrameworkElement Splash(string status)
    {
        var theme = ThemeRuntime.Current;
        var root = new Grid { Background = theme.Brush("canvas") };
        root.Children.Add(new BackdropView { Plan = BackdropPlan.Still(), IsSurfaceActive = false });
        root.Children.Add(UI.V(14,
            new IconView("icon.brand", 56, "accent").Align(HorizontalAlignment.Center),
            UI.Text("Neu Terradise", "hero").Align(HorizontalAlignment.Center),
            UI.Text(status, "body-muted").Align(HorizontalAlignment.Center),
            new ProgressBar { IsIndeterminate = true, Width = 180 }).Align(HorizontalAlignment.Center, VerticalAlignment.Center));
        return root;
    }

    public static FrameworkElement Onboarding(string suggestion, Func<string, Task<string?>> choose, Action<string> accept, Action exit)
    {
        var theme = ThemeRuntime.Current;
        var path = UI.Input(UI.T("Onboarding.ChooseFolder", "Choose Folder"), suggestion);
        var error = UI.Text(string.Empty, "caption", "danger");

        async Task BrowseAsync()
        {
            var picked = await choose(path.Text).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                path.Text = picked;
            }
        }

        var browse = UI.Button(
            UI.T("Onboarding.ChooseFolder", "Choose Folder"),
            () => TaskObserver.Observe(
                BrowseAsync(),
                "StartupScreens.Onboarding.BrowseAsync",
                _ => error.Text = UI.T("Import.PickerFailed", "The requested item could not be selected.")));
        var use = UI.Button(UI.T("Onboarding.UseLocation", "Use This Location"), () =>
        {
            if (!Path.IsPathFullyQualified(path.Text))
            {
                error.Text = UI.T("Onboarding.Validation", "Choose a fully qualified local folder.");
                return;
            }

            accept(path.Text);
        }, ButtonKind.Primary);
        var panel = UI.Surface(UI.V(14,
            UI.Text(UI.T("Onboarding.Title", "Set up Neu Terradise"), "page-title"),
            UI.Text(UI.T("Onboarding.Heading", "Choose where Neu Terradise should keep your catalog and managed media."), "body"),
            UI.Text(UI.T("Onboarding.Subheading", "The suggested folder is not created until you select Use This Location."), "body-muted"),
            UI.Grid("auto", "*,auto", path.At(0, 0), browse.Margin(8, 0, 0, 0).At(0, 1)),
            error,
            UI.H(8, UI.Button(UI.T("Onboarding.Exit", "Exit"), exit), use).Align(HorizontalAlignment.Right)), Material.Deep, 16, 28);
        panel.MaxWidth = 560;
        panel.Margin = new Thickness(24);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid { Background = theme.Brush("canvas") };
        root.Children.Add(new BackdropView { Plan = BackdropPlan.Still() });
        root.Children.Add(panel);
        return root;
    }

    public static FrameworkElement Failure(string title, string message, string nextStep, string code, Action? retry, Action openDiagnostics, Action exit, Action? primary = null, string? primaryLabel = null)
    {
        var theme = ThemeRuntime.Current;
        var panel = UI.Surface(UI.V(12,
            UI.H(10, new IconView("icon.status.warning", 24, "warning"), UI.Text(title, "page-title")),
            UI.Text(message, "body"),
            UI.Text(nextStep, "body-muted"),
            UI.Text(code, "caption"),
            UI.H(8,
                UI.Button(UI.T("Startup.OpenDiagnostics", "Open diagnostics"), openDiagnostics),
                UI.Button(UI.T("Onboarding.Exit", "Exit"), exit),
                retry is null ? null : UI.Button(UI.T("Startup.Retry", "Try again"), retry, primary is null ? ButtonKind.Primary : ButtonKind.Secondary),
                primary is null ? null : UI.Button(primaryLabel ?? UI.T("Common.OK", "OK"), primary, ButtonKind.Primary)).Align(HorizontalAlignment.Right)), Material.Deep, 16, 28);
        panel.MaxWidth = 560;
        panel.Margin = new Thickness(24);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid { Background = theme.Brush("canvas") };
        root.Children.Add(new BackdropView { Plan = BackdropPlan.Still() });
        root.Children.Add(panel);
        return root;
    }
}
