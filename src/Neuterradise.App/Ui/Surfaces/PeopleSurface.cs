using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Faces;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Lifecycle;

namespace Neuterradise.App.Ui;

/// <summary>
/// People & Face Review: optional post-import enrichment with explicit human confirmation. Suggestions
/// never control media ownership; this surface only records face-identity decisions through the existing
/// face decision operations.
/// </summary>
public sealed class PeopleSurface : Surface
{
    private readonly FaceReviewViewModel _vm;
    private readonly Grid _root = new();
    private readonly StackPanel _list = UI.V(12);
    private readonly TextBlock _state = UI.Text(string.Empty, "body-muted");
    private readonly Border _profilingNotice = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<Guid, ReviewRow> _rows = new();

    public PeopleSurface(AppServices services, FaceReviewViewModel vm) : base(services)
    {
        _vm = vm;
        _root.Background = ThemeRuntime.Current.Brush("canvas");
        _state.Visibility = Visibility.Collapsed;
        var bulkAssign = UI.Button(UI.T("Faces.AssignAll", "Assign all to this profile"), null, ButtonKind.Secondary, command: _vm.AssignAllToThisProfileCommand);
        var header = UI.V(4,
            UI.H(10, UI.IconButton("icon.action.back", UI.T("Common.Back", "Back"), () => Services.Navigation.GoBack()),
                UI.V(2, UI.Text(UI.T("Faces.Title", "People in your media"), "page-title"), UI.Text(UI.T("Faces.Desc", "Confirm who appears in your photos and videos. Suggestions are hints, never certainty."), "body-muted"))),
            _vm.IsScoped ? bulkAssign : null);
        var page = UI.V(18,
            header,
            _profilingNotice,
            _state,
            _list);
        page.MaxWidth = 1100;
        _root.Children.Add(UI.Scroll(page.Margin(28, 20, 28, 40)));
        Bag.Add(Observe.Collection(_vm.Reviews, SyncRows));
        Bag.Add(Observe.Props(_vm, SyncRows,
            nameof(FaceReviewViewModel.StatusMessage),
            nameof(FaceReviewViewModel.Status),
            nameof(FaceReviewViewModel.ErrorMessage)));
        Bag.Add(Observe.Props(Services.Status, UpdateProfilingNotice, nameof(ShellStatusViewModel.ProfilingState)));
        SyncRows();
        UpdateProfilingNotice();
    }

    public override FrameworkElement View => _root;

    public override Neuterradise.App.Shell.ScreenStateViewModel Model => _vm;

    protected override void OnActivated(AppRoute route) =>
        Services.RunUserAction(_vm.LoadReviewsAsync(), "PeopleSurface.LoadReviewsAsync", UI.T("Faces.LoadFailed", "Face reviews could not be loaded."));

    private void UpdateProfilingNotice()
    {
        if (Services.Status.ProfilingState == ProfilingInitializationState.Unavailable)
        {
            _profilingNotice.Visibility = Visibility.Visible;
            _profilingNotice.Background = ThemeRuntime.Current.Brush("surface2");
            _profilingNotice.CornerRadius = new CornerRadius(12);
            _profilingNotice.Padding = new Thickness(16, 12, 16, 12);
            _profilingNotice.Child = UI.V(4,
                new IconView("icon.profile.face", 20, "warning"),
                UI.Text(
                    UI.T("Faces.ProfilingUnavailable.Title", "Face detection is not available right now"),
                    "body-strong"),
                UI.Text(
                    UI.T("Faces.ProfilingUnavailable.Message", "The face analysis engine could not start. People suggestions will not appear for imported media. You can still assign people manually from each Profile. This does not affect your media or library."),
                    "body-muted", maxLines: 4));
        }
        else
        {
            _profilingNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncRows()
    {
        var desiredIds = _vm.Reviews.Select(static review => review.FaceId).ToHashSet();
        foreach (var staleId in _rows.Keys.Where(id => !desiredIds.Contains(id)).ToList())
        {
            _rows[staleId].Dispose();
            _rows.Remove(staleId);
        }

        foreach (var review in _vm.Reviews.Take(200))
        {
            if (!_rows.ContainsKey(review.FaceId))
            {
                _rows.Add(review.FaceId, new ReviewRow(Services, _vm, review));
            }
        }

        _list.Children.Clear();
        foreach (var review in _vm.Reviews.Take(200))
        {
            if (_rows.TryGetValue(review.FaceId, out var row))
            {
                _list.Children.Add(row.View);
            }
        }

        if (_vm.Reviews.Count > 0)
        {
            _state.Text = _vm.StatusMessage ?? string.Empty;
            _state.Visibility = string.IsNullOrWhiteSpace(_state.Text) ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        _state.Text = _vm.HasError
            ? UI.F(
                "Faces.LoadErrorDetail",
                "Face review could not be loaded: {0}",
                _vm.ErrorMessage ?? _vm.StatusMessage ?? UI.T("Faces.LoadFailed", "Face reviews could not be loaded."))
            : !string.IsNullOrWhiteSpace(_vm.StatusMessage)
                ? _vm.StatusMessage
                : UI.T("Faces.Empty", "Nothing to review right now.");
        _state.Visibility = Visibility.Visible;
    }

    public override void Dispose()
    {
        foreach (var row in _rows.Values)
        {
            row.Dispose();
        }
        _rows.Clear();
        base.Dispose();
    }

    private sealed class ReviewRow : IDisposable
    {
        private readonly AppServices _services;
        private readonly FaceReviewViewModel _owner;
        private readonly FaceReviewItemViewModel _review;
        private readonly Border _host = new();
        private readonly PropertyChangedEventHandler _propertyChanged;

        public ReviewRow(AppServices services, FaceReviewViewModel owner, FaceReviewItemViewModel review)
        {
            _services = services;
            _owner = owner;
            _review = review;
            _host.CornerRadius = new CornerRadius(14);
            _propertyChanged = (_, _) => UiDispatch.Run(Render);
            _review.PropertyChanged += _propertyChanged;
            Render();
        }

        public FrameworkElement View => _host;

        private void Render()
        {
            FrameworkElement crop;
            if (_review.HasFaceCrop)
            {
                crop = new SkImageView
                {
                    Source = ImageRef.FromPath(_review.FaceCropPath, 240),
                    Width = 112,
                    Height = 112,
                    CornerRadiusValue = 12
                };
            }
            else
            {
                var fallback = UI.Surface(
                    UI.V(4,
                        new IconView("icon.profile.face", 28),
                        UI.Text(_review.FaceCropFallbackText, "micro", maxLines: 2)),
                    Material.Grounded,
                    12,
                    8);
                fallback.Width = 112;
                fallback.Height = 112;
                crop = fallback;
            }
            var candidates = new VariableWrap(8);
            foreach (var candidate in _review.Candidates.Take(5))
            {
                if (candidate.ProfileId is { } profileId)
                {
                    candidates.Children.Add(UI.Chip(
                        $"{candidate.ProfileDisplayName} {candidate.RankText}",
                        onClick: () => _services.RunUserAction(
                            _owner.AssignOtherFaceAsync(_review, profileId),
                            "PeopleSurface.AssignOtherFaceAsync",
                            UI.T("Faces.AssignFailed", "The face could not be assigned."))));
                }
            }

            var actions = UI.H(8);
            var suggestedName = _review.SelectedCandidate?.ProfileDisplayName
                ?? _review.SuggestedProfileDisplayName;
            var hasSuggestion = _owner.CanConfirmFace(_review)
                && !string.IsNullOrWhiteSpace(suggestedName);

            if (hasSuggestion)
            {
                actions.Children.Add(UI.Button(
                    UI.F("Faces.ConfirmAs", "Confirm as {0}", suggestedName!),
                    () => _services.RunUserAction(
                        _owner.ConfirmFaceAsync(_review),
                        "PeopleSurface.ConfirmFaceAsync",
                        UI.T("Faces.ConfirmFailed", "The face could not be confirmed.")),
                    ButtonKind.Primary));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.ChooseAnother", "Choose another person…"),
                    null,
                    ButtonKind.Ghost,
                    command: _owner.AssignOtherWithPickerCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.F("Faces.NotPerson", "Not {0}", suggestedName!),
                    () => _services.RunUserAction(
                        _owner.RejectFaceAsync(_review),
                        "PeopleSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected."))));
            }
            else
            {
                actions.Children.Add(UI.Button(
                    UI.T("Faces.AssignPerson", "Assign person…"),
                    null,
                    ButtonKind.Primary,
                    command: _owner.AssignOtherWithPickerCommand,
                    parameter: _review));
                actions.Children.Add(UI.Button(
                    UI.T("Faces.IgnoreNotPerson", "Ignore / Not a person"),
                    () => _services.RunUserAction(
                        _owner.RejectFaceAsync(_review),
                        "PeopleSurface.RejectFaceAsync",
                        UI.T("Faces.RejectFailed", "The face could not be rejected."))));
            }

            actions.Children.Add(UI.Button(UI.T("Faces.OpenMedia", "Open media"), null, ButtonKind.Ghost, command: _owner.OpenAssetCommand, parameter: _review.AssetId));
            var body = UI.V(6,
                UI.H(8, UI.Text(_review.TargetProfileDisplayName, "card-title"), UI.Badge(_review.StatusBadgeText)),
                UI.Text(_review.DetectionConfidenceNotice, "caption", maxLines: 2),
                _review.HasCandidates ? UI.Text(_review.CandidateSummaryText, "caption") : null,
                candidates,
                actions);
            _host.Child = UI.Grid("auto", "auto,*", crop.At(0, 0), body.Margin(16, 0, 0, 0).At(0, 1));
            _host.Background = ThemeRuntime.Current.Brush("surface2");
            _host.Padding = new Thickness(14);
        }

        public void Dispose() => _review.PropertyChanged -= _propertyChanged;
    }
}
