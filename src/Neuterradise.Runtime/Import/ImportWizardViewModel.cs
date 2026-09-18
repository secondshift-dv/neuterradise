using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Import;

public enum ImportWizardStep
{
    Profile = 1,
    Details = 2,
}

public enum ImportWizardMode
{
    ChooseProfile,
    Verify,
}

public enum ImportDestinationChoice
{
    New,
    Existing,
    DecideLater,
}

/// <summary>What intake found for the files the user picked, before any preparation.</summary>
public sealed record ImportIntakeSummary(int DiscoveredCount, int ReadyCount)
{
    public int UnsupportedCount => Math.Max(0, DiscoveredCount - ReadyCount);
}

public sealed record CategoryOptionViewModel(string CategoryId, string Name);

public sealed record TagOptionViewModel(string TagId, string Name);

public sealed class ProfileLookupItemViewModel(ProfileLookupResult result) : ObservableObject
{
    public Guid ProfileId { get; } = result.ProfileId;

    public string DisplayName { get; } = result.DisplayName;
}

/// <summary>
/// A likely existing profile, with the kind of evidence kept separate: shared media (the library
/// already holds some of these exact files in that profile) is not the same claim as a face match.
/// </summary>
public sealed record ImportProfileHint(Guid ProfileId, string DisplayName, string Evidence, bool IsFaceMatch);

/// <summary>One Cover or Banner choice, with its real preview decoded off the UI thread.</summary>
public sealed class ImportVisualOption : ObservableObject
{
    private bool _isSelected;
    private ImageRef? _preview;

    public ImportVisualOption(VerificationCoverCandidate cover)
    {
        Cover = cover;
        CandidateId = cover.CandidateId;
        Title = cover.DisplayTitle;
        IsRecommended = cover.IsRecommended;
        PreviewPath = cover.PreviewImagePath;
    }

    public ImportVisualOption(VerificationBannerCandidate banner)
    {
        Banner = banner;
        CandidateId = banner.CandidateId;
        Title = banner.DisplayTitle;
        IsRecommended = banner.IsRecommended;
        PreviewPath = banner.PreviewImagePath;
    }

    public VerificationCoverCandidate? Cover { get; }

    public VerificationBannerCandidate? Banner { get; }

    public string CandidateId { get; }

    public string Title { get; }

    public bool IsRecommended { get; }

    public string? PreviewPath { get; }

    public Guid AssetId => Cover?.AssetId ?? Banner!.AssetId;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ImageRef? Preview
    {
        get => _preview;
        set
        {
            if (SetProperty(ref _preview, value))
            {
                RaisePropertyChanged(nameof(HasPreview));
            }
        }
    }

    public bool HasPreview => _preview is not null;
}

/// <summary>
/// The two-step Import overlay: Step 1 "Who is this media for?", Step 2 "Details". It owns only the
/// user's draft. Background intake keeps running while it is open; hints, duplicate facts, face matches
/// and Cover/Banner candidates fill in progressively and never gate typing, choosing or Import.
///
/// Pressing Import hands the draft to <see cref="ImportFinalizer"/> as durable intent and closes the
/// overlay; closing without importing keeps the durable import (and saves the draft) so it can be
/// continued later. Neither action cancels anything.
/// </summary>
public sealed class ImportWizardViewModel : ObservableObject, IDisposable
{
    public const int MaximumNameLength = 100;
    public const int RecommendedVisibleOptions = 4;
    public static readonly TimeSpan HintRefreshInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly CatalogDb? _catalog;
    private readonly VerificationOperations? _operations;
    private readonly ImportFinalizer? _finalizer;
    private readonly ImportPublicationCoordinator? _publication;
    private readonly Action<ImportWizardViewModel>? _close;
    private readonly Action<Guid>? _imported;
    private readonly DerivedImageLoader _images;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ImportIntakeSummary? _intakeSummary;

    private readonly List<TagOptionViewModel> _allTags = [];
    private readonly List<StagedDuplicateDecision> _duplicateDecisions = [];
    private readonly List<StagedProfileCollisionDecision> _collisionDecisions = [];
    private IReadOnlyList<StagedFaceDecision> _faceDecisions = [];
    private IReadOnlyList<Guid> _acknowledgedMissingDependencyItemIds = [];
    private IReadOnlyList<Guid> _attentionItemIds = [];
    private CancellationTokenSource? _nameCheck;
    private CancellationTokenSource? _profileSearch;
    private long _hintGeneration;

    private ImportWizardStep _step = ImportWizardStep.Profile;
    private ImportWizardMode _mode = ImportWizardMode.ChooseProfile;
    private ImportDestinationChoice _choice = ImportDestinationChoice.New;
    private string _newProfileName = string.Empty;
    private ProfileLookupItemViewModel? _duplicateNameProfile;
    private string _existingSearchText = string.Empty;
    private ProfileLookupItemViewModel? _selectedExistingProfile;
    private string? _selectedCategoryId;
    private string _tagSearchText = string.Empty;
    private string _newCategoryName = string.Empty;
    private int _rating;
    private readonly SemaphoreSlim _tagCommitGate = new(1, 1);
    private Task _pendingTagCommit = Task.CompletedTask;
    private bool _isFavorite;
    private string _overview = string.Empty;
    private ImportVisualOption? _selectedCover;
    private ImportVisualOption? _selectedBanner;
    private bool _coverTouched;
    private bool _bannerTouched;
    private VerificationAppearanceIntent _coverIntent = VerificationAppearanceIntent.Unchanged;
    private VerificationAppearanceIntent _bannerIntent = VerificationAppearanceIntent.Unchanged;
    private bool _showAllCovers;
    private bool _showAllBanners;
    private bool _candidatesLoaded;
    private int _totalCount;
    private int _alreadyInLibraryCount;
    private int _preparedCount;
    private int _admittedCount;
    private bool _isMove;
    private bool _isImporting;
    private bool _importRequested;
    private string? _errorMessage;
    private bool _disposed;

    public ImportWizardViewModel(
        Guid unitId,
        string sourceDisplayName,
        CatalogDb? catalog = null,
        ImportFinalizer? finalizer = null,
        Action<ImportWizardViewModel>? close = null,
        Action<Guid>? imported = null,
        ImportIntakeSummary? intakeSummary = null,
        ImportWizardMode mode = ImportWizardMode.ChooseProfile,
        DerivedImageLoader? images = null,
        TimeProvider? timeProvider = null)
    {
        UnitId = unitId;
        SourceDisplayName = sourceDisplayName;
        _catalog = catalog;
        _operations = catalog is null ? null : new VerificationOperations(catalog, timeProvider);
        _finalizer = finalizer;
        _publication = catalog is null ? null : new ImportPublicationCoordinator(catalog, timeProvider);
        _close = close;
        _imported = imported;
        _intakeSummary = intakeSummary;
        _mode = mode;
        _step = mode == ImportWizardMode.Verify ? ImportWizardStep.Details : ImportWizardStep.Profile;
        _images = images ?? DerivedImageLoader.Shared;
        _timeProvider = timeProvider ?? TimeProvider.System;

        ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => CanContinue, onError: ReportImportFailure);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => CanImport, onError: ReportImportFailure);
        CloseCommand = new AsyncRelayCommand(CloseAsync, onError: exception => Trace.TraceWarning("Import overlay close: {0}", exception));
        ChooseNewCommand = new RelayCommand(_ => Choice = ImportDestinationChoice.New);
        ChooseExistingCommand = new RelayCommand(_ => Choice = ImportDestinationChoice.Existing);
        ChooseDecideLaterCommand = new RelayCommand(_ => Choice = ImportDestinationChoice.DecideLater);
        UseHintCommand = new RelayCommand(parameter =>
        {
            if (parameter is ImportProfileHint hint)
            {
                UseExistingProfile(hint.ProfileId, hint.DisplayName);
            }
        });
        UseDuplicateNameProfileCommand = new RelayCommand(_ =>
        {
            if (_duplicateNameProfile is { } profile)
            {
                UseExistingProfile(profile.ProfileId, profile.DisplayName);
            }
        });
        AddTagCommand = new RelayCommand(parameter =>
        {
            if (parameter is TagOptionViewModel tag)
            {
                AddTag(tag);
            }
        });
        RemoveTagCommand = new RelayCommand(parameter =>
        {
            if (parameter is TagOptionViewModel tag)
            {
                RemoveTag(tag);
            }
        });
        SelectCoverCommand = new RelayCommand(parameter => SelectCover(parameter as ImportVisualOption, byUser: true));
        SelectBannerCommand = new RelayCommand(parameter => SelectBanner(parameter as ImportVisualOption, byUser: true));
        ClearCoverCommand = new RelayCommand(_ => SelectCover(null, byUser: true));
        ClearBannerCommand = new RelayCommand(_ => SelectBanner(null, byUser: true));
        ToggleAllCoversCommand = new RelayCommand(_ => ShowAllCovers = !ShowAllCovers);
        ToggleAllBannersCommand = new RelayCommand(_ => ShowAllBanners = !ShowAllBanners);
        ResolveDuplicateReuseCommand = new AsyncRelayCommand(
            parameter => parameter is VerificationDuplicateProblem problem ? ResolveDuplicateReuseAsync(problem) : Task.CompletedTask,
            onError: ReportImportFailure);
        ResolveDuplicateSkipCommand = new AsyncRelayCommand(
            parameter => parameter is VerificationDuplicateProblem problem ? ResolveDuplicateSkipAsync(problem) : Task.CompletedTask,
            onError: ReportImportFailure);
        KeepCollisionCommand = new AsyncRelayCommand(
            parameter => parameter is VerificationProfileCollisionProblem problem ? ResolveCollisionAsync(problem, ProfileCollisionAction.KeepDestination) : Task.CompletedTask,
            onError: ReportImportFailure);
        MoveCollisionCommand = new AsyncRelayCommand(
            parameter => parameter is VerificationProfileCollisionProblem problem ? ResolveCollisionAsync(problem, ProfileCollisionAction.MoveToProfile) : Task.CompletedTask,
            onError: ReportImportFailure);
        SkipCollisionCommand = new AsyncRelayCommand(
            parameter => parameter is VerificationProfileCollisionProblem problem ? ResolveCollisionAsync(problem, ProfileCollisionAction.Skip) : Task.CompletedTask,
            onError: ReportImportFailure);
        CreateCategoryCommand = new AsyncRelayCommand(CreateCategoryAsync, onError: ReportImportFailure);
    }

    public Guid UnitId { get; }

    public string SourceDisplayName { get; }

    public ICommand ContinueCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ChooseNewCommand { get; }
    public ICommand ChooseExistingCommand { get; }
    public ICommand ChooseDecideLaterCommand { get; }
    public ICommand UseHintCommand { get; }
    public ICommand UseDuplicateNameProfileCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand SelectCoverCommand { get; }
    public ICommand SelectBannerCommand { get; }
    public ICommand ClearCoverCommand { get; }
    public ICommand ClearBannerCommand { get; }
    public ICommand ToggleAllCoversCommand { get; }
    public ICommand ToggleAllBannersCommand { get; }
    public ICommand ResolveDuplicateReuseCommand { get; }
    public ICommand ResolveDuplicateSkipCommand { get; }
    public ICommand KeepCollisionCommand { get; }
    public ICommand MoveCollisionCommand { get; }
    public ICommand SkipCollisionCommand { get; }
    public ICommand CreateCategoryCommand { get; }

    public ImportWizardMode Mode => _mode;

    public bool IsChooseProfileMode => _mode == ImportWizardMode.ChooseProfile;

    public bool IsVerifyMode => _mode == ImportWizardMode.Verify;

    // ---------------------------------------------------------------- steps

    public ImportWizardStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                RaisePropertyChanged(nameof(IsProfileStep));
                RaisePropertyChanged(nameof(IsDetailsStep));
                RaisePropertyChanged(nameof(StepTitle));
                RaisePropertyChanged(nameof(StepIndicator));
                RaiseSummary();
            }
        }
    }

    public bool IsProfileStep => _step == ImportWizardStep.Profile;

    public bool IsDetailsStep => _step == ImportWizardStep.Details;

    public string StepTitle => IsChooseProfileMode
        ? SurfaceText.Get("ImportWizard.ChooseProfile.Title", "Choose Profile")
        : SurfaceText.Get("ImportWizard.Verify.Title", "Verify");

    public string StepIndicator => IsChooseProfileMode
        ? SurfaceText.Get("ImportWizard.ChooseProfile.Indicator", "Destination")
        : SurfaceText.Get("ImportWizard.Verify.Indicator", "Profile details and import decisions");

    public string MediaSummaryText
    {
        get
        {
            var parts = new List<string>();
            var total = _totalCount > 0 ? _totalCount : _intakeSummary?.ReadyCount ?? 0;
            if (_intakeSummary is { UnsupportedCount: > 0 } intake)
            {
                parts.Add(SurfaceText.Format("ImportWizard.Found", "{0} files found", intake.DiscoveredCount));
                parts.Add(SurfaceText.Format("ImportWizard.Ready", "{0} media ready", total));
                parts.Add(SurfaceText.Format("ImportWizard.Unsupported", "{0} not supported (left untouched)", intake.UnsupportedCount));
            }
            else
            {
                parts.Add(total == 1
                    ? SurfaceText.Get("ImportWizard.OneMedia", "1 media")
                    : SurfaceText.Format("ImportWizard.ManyMedia", "{0} media", total));
            }

            if (_alreadyInLibraryCount > 0)
            {
                parts.Add(SurfaceText.Format("ImportWizard.New", "{0} new", Math.Max(0, total - _alreadyInLibraryCount)));
                parts.Add(SurfaceText.Format("ImportWizard.AlreadyInLibrary", "{0} already in your library", _alreadyInLibraryCount));
            }

            return string.Join(" · ", parts);
        }
    }

    public string PreparationText => _totalCount > 0 && _preparedCount < _totalCount
        ? SurfaceText.Format("ImportWizard.Preparing", "Preparing in the background… {0} of {1} checked. You can carry on.", _preparedCount, _totalCount)
        : string.Empty;

    public bool IsPreparing => _totalCount == 0 || _preparedCount < _totalCount;

    public ImportDestinationChoice Choice
    {
        get => _choice;
        set
        {
            if (SetProperty(ref _choice, value))
            {
                RaisePropertyChanged(nameof(IsNewChoice));
                RaisePropertyChanged(nameof(IsExistingChoice));
                RaisePropertyChanged(nameof(IsDecideLaterChoice));
                RaisePropertyChanged(nameof(ShowNewProfileDetails));
                RaisePropertyChanged(nameof(ShowAppearance));
                RaisePropertyChanged(nameof(DestinationNote));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                RaiseValidity();
            }
        }
    }

    public bool IsNewChoice
    {
        get => _choice == ImportDestinationChoice.New;
        set { if (value) Choice = ImportDestinationChoice.New; }
    }

    public bool IsExistingChoice
    {
        get => _choice == ImportDestinationChoice.Existing;
        set { if (value) Choice = ImportDestinationChoice.Existing; }
    }

    public bool IsDecideLaterChoice
    {
        get => _choice == ImportDestinationChoice.DecideLater;
        set { if (value) Choice = ImportDestinationChoice.DecideLater; }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (SetProperty(ref _newProfileName, value ?? string.Empty))
            {
                if (_choice != ImportDestinationChoice.New && !string.IsNullOrWhiteSpace(value))
                {
                    Choice = ImportDestinationChoice.New;
                }

                RaisePropertyChanged(nameof(IsUnknownNameWarning));
                RaisePropertyChanged(nameof(NameValidationText));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                RaiseValidity();
                ScheduleNameCheck();
            }
        }
    }

    public bool IsUnknownNameWarning =>
        _choice == ImportDestinationChoice.New
        && string.Equals(_newProfileName.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

    public string? NameValidationText => _newProfileName.Trim().Length > MaximumNameLength
        ? SurfaceText.Get("ImportWizard.NameTooLong", "Keep the name under 100 characters.")
        : null;

    public ProfileLookupItemViewModel? DuplicateNameProfile
    {
        get => _duplicateNameProfile;
        private set
        {
            if (SetProperty(ref _duplicateNameProfile, value))
            {
                RaisePropertyChanged(nameof(HasDuplicateName));
                RaisePropertyChanged(nameof(DuplicateNameText));
            }
        }
    }

    public bool HasDuplicateName => _duplicateNameProfile is not null && _choice == ImportDestinationChoice.New;

    public string DuplicateNameText => _duplicateNameProfile is null
        ? string.Empty
        : SurfaceText.Format(
            "ImportWizard.DuplicateName",
            "A profile named \"{0}\" already exists. Add to it, or continue to create a second profile with the same name.",
            _duplicateNameProfile.DisplayName);

    public string ExistingSearchText
    {
        get => _existingSearchText;
        set
        {
            if (SetProperty(ref _existingSearchText, value ?? string.Empty))
            {
                ScheduleProfileSearch();
            }
        }
    }

    public ObservableCollection<ProfileLookupItemViewModel> ExistingProfiles { get; } = [];

    public ProfileLookupItemViewModel? SelectedExistingProfile
    {
        get => _selectedExistingProfile;
        set
        {
            if (SetProperty(ref _selectedExistingProfile, value))
            {
                if (value is not null)
                {
                    Choice = ImportDestinationChoice.Existing;
                }

                RaisePropertyChanged(nameof(DestinationNote));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                RaiseValidity();
                RaiseSummary();
            }
        }
    }

    public ObservableCollection<ImportProfileHint> SharedMediaHints { get; } = [];
    public ObservableCollection<ImportProfileHint> FaceHints { get; } = [];
    public bool HasSharedMediaHints => SharedMediaHints.Count > 0;
    public bool HasFaceHints => FaceHints.Count > 0;

    public string DuplicateMediaText => _alreadyInLibraryCount == 0
        ? string.Empty
        : SurfaceText.Format(
            "ImportWizard.DuplicateMedia",
            "{0} of these files are already in your library. They will be linked, not copied again.",
            _alreadyInLibraryCount);

    public bool HasDuplicateMedia => _alreadyInLibraryCount > 0;
    public bool ShowNewProfileDetails => _choice == ImportDestinationChoice.New;
    public bool ShowAppearance => _choice != ImportDestinationChoice.DecideLater;

    public string DestinationNote => _choice switch
    {
        ImportDestinationChoice.Existing => SurfaceText.Format(
            "ImportWizard.ExistingNote",
            "Adding to {0}. Their name, category, tags, rating and overview stay as they are.",
            _selectedExistingProfile?.DisplayName ?? "the chosen profile"),
        ImportDestinationChoice.DecideLater => SurfaceText.Get(
            "ImportWizard.DecideLaterNote",
            "These media will stay unassigned for now. You can connect them to a profile later."),
        _ => string.Empty,
    };

    public ObservableCollection<CategoryOptionViewModel> Categories { get; } = [];

    public string? SelectedCategoryId
    {
        get => _selectedCategoryId;
        set
        {
            if (SetProperty(ref _selectedCategoryId, value))
            {
                RaiseSummary();
            }
        }
    }

    public ObservableCollection<TagOptionViewModel> SelectedTags { get; } = [];
    public ObservableCollection<TagOptionViewModel> TagSuggestions { get; } = [];
    public ObservableCollection<VerificationDuplicateProblem> DuplicateProblems { get; } = [];
    public ObservableCollection<VerificationProfileCollisionProblem> ProfileCollisionProblems { get; } = [];
    public bool HasProblems => DuplicateProblems.Count > 0 || ProfileCollisionProblems.Count > 0;

    public string NewCategoryName
    {
        get => _newCategoryName;
        set => SetProperty(ref _newCategoryName, value ?? string.Empty);
    }

    public string TagSearchText
    {
        get => _tagSearchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _tagSearchText, next))
            {
                if (next.Contains(',') || next.Contains('\n') || next.Contains('\r'))
                {
                    var task = CommitTagTokensAsync();
                    _pendingTagCommit = task;
                    TaskObserver.Observe(task, "ImportWizardViewModel.CommitTagTokensAsync");
                    return;
                }
                RefreshTagSuggestions();
            }
        }
    }

    public Task CommitTagTokensAsync() => CommitTagTokensCoreAsync(isPartial: true);

    public Task OnTagInputEnterAsync() => CommitTagTokensCoreAsync(isPartial: false);

    private async Task CommitTagTokensCoreAsync(bool isPartial)
    {
        if (_catalog is null)
        {
            return;
        }

        await _tagCommitGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            IReadOnlyList<TaxonomyNamePolicy.TaxonomyTagToken> tokens = [];
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (isPartial)
                {
                    var (committed, remaining) = TaxonomyNamePolicy.TryParsePartialTagInput(_tagSearchText);
                    tokens = committed;
                    _tagSearchText = remaining ?? string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
                else
                {
                    var remaining = _tagSearchText.Trim();
                    if (string.IsNullOrWhiteSpace(remaining))
                    {
                        tokens = [];
                        return;
                    }

                    tokens = TaxonomyNamePolicy.ParseTagTokens(remaining);
                    _tagSearchText = string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
            }).ConfigureAwait(false);

            foreach (var token in tokens)
            {
                await EnsureTagAndSelectAsync(token.CanonicalName, token.DisplayName).ConfigureAwait(false);
            }

            await UiDispatch.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    RefreshTagSuggestions();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _tagCommitGate.Release();
        }
    }

    public async Task CommitPendingTagInputForSubmissionAsync()
    {
        await _pendingTagCommit.ConfigureAwait(false);

        var hasPendingText = false;
        await UiDispatch.InvokeAsync(() =>
        {
            hasPendingText = !_disposed && !string.IsNullOrWhiteSpace(_tagSearchText);
        }).ConfigureAwait(false);

        if (!hasPendingText)
        {
            return;
        }

        var commitTask = CommitTagTokensCoreAsync(isPartial: false);
        _pendingTagCommit = commitTask;
        await commitTask.ConfigureAwait(false);
    }

    private async Task EnsureTagAndSelectAsync(string canonicalName, string displayName)
    {
        var alreadySelected = false;
        TagOptionViewModel? existing = null;
        await UiDispatch.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            alreadySelected = SelectedTags.Any(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
            existing = _allTags.FirstOrDefault(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
        }).ConfigureAwait(false);

        if (_disposed || alreadySelected)
        {
            return;
        }

        if (existing is not null)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    AddTag(existing);
                }
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            await _catalog!.SettingsWrites.CreateTagAsync(id, displayName, _lifetime.Token).ConfigureAwait(false);
            var option = new TagOptionViewModel(id, displayName);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _allTags.Add(option);
                AddTag(option);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            IReadOnlyList<TagRecord> tags;
            try
            {
                tags = await _catalog!.SettingsReads.GetAllTagsAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var resolvedDuplicate = false;
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _allTags.Clear();
                _allTags.AddRange(tags
                    .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(tag => new TagOptionViewModel(tag.TagId, tag.Name)));
                var match = _allTags.FirstOrDefault(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
                if (match is not null)
                {
                    AddTag(match);
                    resolvedDuplicate = true;
                }
                RefreshTagSuggestions();
            }).ConfigureAwait(false);

            if (!resolvedDuplicate && !_disposed)
            {
                throw;
            }
        }
    }

    private async Task LoadTagVocabularyAsync()
    {
        if (_catalog is null) return;
        try
        {
            var tags = await _catalog.SettingsReads.GetAllTagsAsync(_lifetime.Token).ConfigureAwait(true);
            _allTags.Clear();
            _allTags.AddRange(tags
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(tag => new TagOptionViewModel(tag.TagId, tag.Name)));
            RefreshTagSuggestions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    public int Rating
    {
        get => _rating;
        set => SetProperty(ref _rating, Math.Clamp(value, 0, 5));
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    public string Overview
    {
        get => _overview;
        set => SetProperty(ref _overview, value ?? string.Empty);
    }

    public ObservableCollection<ImportVisualOption> CoverOptions { get; } = [];
    public ObservableCollection<ImportVisualOption> BannerOptions { get; } = [];

    public IEnumerable<ImportVisualOption> VisibleCoverOptions => _showAllCovers ? CoverOptions : CoverOptions.Take(RecommendedVisibleOptions);
    public IEnumerable<ImportVisualOption> VisibleBannerOptions => _showAllBanners ? BannerOptions : BannerOptions.Take(RecommendedVisibleOptions);

    public bool ShowAllCovers
    {
        get => _showAllCovers;
        set
        {
            if (SetProperty(ref _showAllCovers, value))
            {
                RaisePropertyChanged(nameof(VisibleCoverOptions));
                RaisePropertyChanged(nameof(CoverToggleText));
            }
        }
    }

    public bool ShowAllBanners
    {
        get => _showAllBanners;
        set
        {
            if (SetProperty(ref _showAllBanners, value))
            {
                RaisePropertyChanged(nameof(VisibleBannerOptions));
                RaisePropertyChanged(nameof(BannerToggleText));
            }
        }
    }

    public bool HasMoreCovers => CoverOptions.Count > RecommendedVisibleOptions;
    public bool HasMoreBanners => BannerOptions.Count > RecommendedVisibleOptions;

    public string CoverToggleText => _showAllCovers
        ? SurfaceText.Get("ImportWizard.ShowFewer", "Show fewer")
        : SurfaceText.Format("ImportWizard.ChooseAnother", "Choose another ({0})", CoverOptions.Count);

    public string BannerToggleText => _showAllBanners
        ? SurfaceText.Get("ImportWizard.ShowFewer", "Show fewer")
        : SurfaceText.Format("ImportWizard.ChooseAnother", "Choose another ({0})", BannerOptions.Count);

    public bool IsCoverPending => !_candidatesLoaded || (CoverOptions.Count == 0 && IsPreparing);
    public bool IsBannerPending => !_candidatesLoaded || (BannerOptions.Count == 0 && IsPreparing);
    public bool HasNoCoverOptions => _candidatesLoaded && CoverOptions.Count == 0 && !IsPreparing;
    public bool HasNoBannerOptions => _candidatesLoaded && BannerOptions.Count == 0 && !IsPreparing;
    public bool HasWeakCoverOptions => CoverOptions.Count > 0 && CoverOptions.All(static option => !option.IsRecommended);
    public bool HasWeakBannerOptions => BannerOptions.Count > 0 && BannerOptions.All(static option => !option.IsRecommended);
    public ImportVisualOption? SelectedCover => _selectedCover;
    public ImportVisualOption? SelectedBanner => _selectedBanner;
    public string SelectedCoverText => _selectedCover?.Title ?? SurfaceText.Get("ImportWizard.None", "None");
    public string SelectedBannerText => _selectedBanner?.Title ?? SurfaceText.Get("ImportWizard.None", "None");

    public string ProfileSummaryText => _choice switch
    {
        ImportDestinationChoice.Existing => SurfaceText.Format("ImportWizard.Summary.Profile", "Profile: {0}", _selectedExistingProfile?.DisplayName ?? "—"),
        ImportDestinationChoice.DecideLater => SurfaceText.Get("ImportWizard.Summary.Unassigned", "Profile: decide later (unassigned)"),
        _ => SurfaceText.Format("ImportWizard.Summary.NewProfile", "New profile: {0}", string.IsNullOrWhiteSpace(_newProfileName) ? "—" : _newProfileName.Trim()),
    };

    public string DestinationDisplayName => _choice switch
    {
        ImportDestinationChoice.Existing => _selectedExistingProfile?.DisplayName ?? SurfaceText.Get("Import.Destination.Current", "current Profile"),
        _ => string.IsNullOrWhiteSpace(_newProfileName) ? SurfaceText.Get("Import.Destination.Current", "current Profile") : _newProfileName.Trim(),
    };

    public string CategorySummaryText
    {
        get
        {
            if (_choice != ImportDestinationChoice.New || _selectedCategoryId is null)
            {
                return string.Empty;
            }

            var name = Categories.FirstOrDefault(category => category.CategoryId == _selectedCategoryId)?.Name;
            return name is null ? string.Empty : SurfaceText.Format("ImportWizard.Summary.Category", "Category: {0}", name);
        }
    }

    public string TransferText => _isMove
        ? SurfaceText.Get(
            "ImportWizard.Transfer.Move",
            "Your files will be moved into your library. The originals are removed only after the library copy is safely in place.")
        : SurfaceText.Get(
            "ImportWizard.Transfer.Copy",
            "Your files will be copied into your library. The originals stay exactly where they are.");

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                RaiseValidity();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);
    public bool CanContinue => IsChooseProfileMode && IsProfileChoiceComplete && !_isImporting && _finalizer is not null;
    public bool CanImport => IsVerifyMode && IsProfileChoiceComplete && !HasProblems && _admittedCount > 0 && !_isImporting && _publication is not null;

    public bool IsProfileChoiceComplete => _choice switch
    {
        ImportDestinationChoice.New => !string.IsNullOrWhiteSpace(_newProfileName) && _newProfileName.Trim().Length <= MaximumNameLength,
        ImportDestinationChoice.Existing => _selectedExistingProfile is not null,
        _ => true,
    };

    public async Task InitializeAsync()
    {
        using var measure = PerfTrace.Measure("import.wizard.open", 150);
        if (_operations is null || _catalog is null)
        {
            _candidatesLoaded = true;
            return;
        }

        var token = _lifetime.Token;
        var model = await _operations.LoadVerificationReadModelAsync(UnitId, token).ConfigureAwait(true);
        if (model is not null)
        {
            RestoreDraft(VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
        }

        await LoadOrganizationAsync(token).ConfigureAwait(true);
        await RefreshHintsAsync(token).ConfigureAwait(true);
        TaskObserver.Observe(KeepHintsFreshAsync(token), "Import overlay background refresh");
    }

    private void RestoreDraft(VerificationDraftV1 draft)
    {
        _importRequested = draft.IsImportRequested;
        switch (draft.Destination.Kind)
        {
            case DestinationKind.ExistingNormal when draft.Destination.ProfileId is { } profileId:
                _choice = ImportDestinationChoice.Existing;
                _pendingExistingProfileId = profileId;
                break;
            case DestinationKind.SystemUnknown:
                _choice = ImportDestinationChoice.DecideLater;
                break;
            case DestinationKind.NewNormal when draft.Destination.NewProfile is { } profile:
                _choice = ImportDestinationChoice.New;
                _newProfileName = profile.DisplayName;
                _selectedCategoryId = profile.CategoryId;
                _rating = profile.Rating ?? 0;
                _isFavorite = profile.Favorite ?? false;
                _overview = profile.Overview ?? string.Empty;
                _pendingTagIds = profile.TagIds;
                break;
        }

        _pendingCoverAssetId = draft.Appearance.CoverAssetId;
        _pendingCoverSourceKind = draft.Appearance.CoverSourceKind;
        _pendingCoverTimestampMilliseconds = draft.Appearance.CoverVideoTimestampMilliseconds;
        _pendingBannerAssetId = draft.Appearance.BannerAssetId;
        _pendingBannerSourceKind = draft.Appearance.BannerSourceKind;
        _pendingBannerFrameTimestampMilliseconds = draft.Appearance.BannerVideoFrameTimestampMilliseconds;
        _pendingBannerStartPointSeconds = draft.Appearance.BannerStartPointSeconds;
        _pendingBannerDurationSeconds = draft.Appearance.BannerDurationSeconds;
        _coverIntent = draft.Appearance.CoverIntent;
        _bannerIntent = draft.Appearance.BannerIntent;
        _coverTouched = draft.Appearance.CoverIntent != VerificationAppearanceIntent.Unchanged;
        _bannerTouched = draft.Appearance.BannerIntent != VerificationAppearanceIntent.Unchanged;
        _duplicateDecisions.Clear();
        _duplicateDecisions.AddRange(draft.DuplicateDecisions ?? []);
        _collisionDecisions.Clear();
        _collisionDecisions.AddRange(draft.ProfileCollisionDecisions ?? []);
        _faceDecisions = draft.FaceDecisions;
        _acknowledgedMissingDependencyItemIds = draft.AcknowledgedMissingDependencyItemIds ?? [];
        _attentionItemIds = draft.AttentionItemIds ?? [];
        RaisePropertyChanged(string.Empty);
    }

    private Guid? _pendingExistingProfileId;
    private IReadOnlyList<string>? _pendingTagIds;
    private Guid? _pendingCoverAssetId;
    private string? _pendingCoverSourceKind;
    private long? _pendingCoverTimestampMilliseconds;
    private Guid? _pendingBannerAssetId;
    private string? _pendingBannerSourceKind;
    private long? _pendingBannerFrameTimestampMilliseconds;
    private double? _pendingBannerStartPointSeconds;
    private double? _pendingBannerDurationSeconds;

    private async Task LoadOrganizationAsync(CancellationToken token)
    {
        try
        {
            var categories = await _catalog!.SettingsReads.GetAllCategoriesAsync(token).ConfigureAwait(true);
            var tags = await _catalog.SettingsReads.GetAllTagsAsync(token).ConfigureAwait(true);

            Categories.Clear();
            foreach (var category in categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(new CategoryOptionViewModel(category.CategoryId, category.Name));
            }

            _allTags.Clear();
            _allTags.AddRange(tags
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(tag => new TagOptionViewModel(tag.TagId, tag.Name)));

            if (_pendingTagIds is { } pending)
            {
                foreach (var tag in _allTags.Where(tag => pending.Contains(tag.TagId, StringComparer.Ordinal)))
                {
                    SelectedTags.Add(tag);
                }

                _pendingTagIds = null;
            }

            RefreshTagSuggestions();
            RaisePropertyChanged(nameof(SelectedCategoryId));
            RaiseSummary();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Import overlay taxonomy could not be read: {0}", exception.GetType().Name);
        }

        if (_pendingExistingProfileId is { } existingId)
        {
            try
            {
                var profile = await _catalog!.ProfileReads.GetDetailAsync(existingId, token).ConfigureAwait(true);
                if (profile is not null && !string.IsNullOrWhiteSpace(profile.DisplayName))
                {
                    UseExistingProfile(existingId, profile.DisplayName);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Import overlay could not restore the chosen profile: {0}", exception.GetType().Name);
            }

            _pendingExistingProfileId = null;
        }
    }

    private async Task KeepHintsFreshAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && (IsPreparing || !_candidatesLoaded))
        {
            await Task.Delay(HintRefreshInterval, token).ConfigureAwait(true);
            await RefreshHintsAsync(token).ConfigureAwait(true);
        }
    }

    public async Task RefreshHintsAsync(CancellationToken cancellationToken = default)
    {
        if (_operations is null || _catalog is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _hintGeneration);
        using var measure = PerfTrace.Measure("import.wizard.hints", 150);

        ImportUnitProgress? progress;
        IReadOnlyList<ImportDuplicateMatch> matches;
        IReadOnlyList<ImportProfileHint> shared;
        IReadOnlyList<VerificationFaceCandidate> faces;
        VerificationAppearanceCandidates candidates;
        VerificationGateProblems problems;
        bool isMove;

        try
        {
            var progressMap = await _catalog.ImportReads.ListUnitProgressAsync([UnitId], cancellationToken).ConfigureAwait(false);
            progress = progressMap.GetValueOrDefault(UnitId);
            matches = await _operations.GetExactDuplicateMatchesAsync(UnitId, cancellationToken).ConfigureAwait(false);
            shared = await ImportWizardQueries.ReadSharedMediaHintsAsync(_catalog, matches, cancellationToken).ConfigureAwait(false);
            faces = await ReadFaceCandidatesSafelyAsync(cancellationToken).ConfigureAwait(false);
            candidates = await _operations.GetAppearanceCandidatesAsync(UnitId, cancellationToken).ConfigureAwait(false);
            problems = await _operations.GetGateProblemsAsync(UnitId, BuildDraft(), cancellationToken).ConfigureAwait(false);
            isMove = await ImportWizardQueries.IsMovePolicyAsync(_catalog, UnitId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Import overlay hints could not be read: {0}", exception.GetType().Name);
            return;
        }

        if (generation != Interlocked.Read(ref _hintGeneration) || _disposed)
        {
            return;
        }

        await DispatchAsync(() => ApplyHints(progress, matches, shared, faces, candidates, problems, isMove)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VerificationFaceCandidate>> ReadFaceCandidatesSafelyAsync(CancellationToken token)
    {
        try
        {
            return await _operations!.GetFaceCandidatesAsync(UnitId, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceInformation("Face hints unavailable for import: {0}", exception.GetType().Name);
            return [];
        }
    }

    private static Task DispatchAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void ApplyHints(
        ImportUnitProgress? progress,
        IReadOnlyList<ImportDuplicateMatch> matches,
        IReadOnlyList<ImportProfileHint> shared,
        IReadOnlyList<VerificationFaceCandidate> faces,
        VerificationAppearanceCandidates candidates,
        VerificationGateProblems problems,
        bool isMove)
    {
        if (_disposed)
        {
            return;
        }

        _totalCount = progress?.TotalItemCount ?? _totalCount;
        _preparedCount = progress?.PreparedItemCount ?? _preparedCount;
        _admittedCount = progress?.AdmittedItemCount ?? _admittedCount;
        _alreadyInLibraryCount = matches
            .Where(match => match.MatchedAssetState == Media.AssetState.Active)
            .Select(match => match.ImportItemId)
            .Distinct()
            .Count();
        _isMove = isMove;

        ReplaceIfChanged(SharedMediaHints, shared);
        ReplaceIfChanged(FaceHints, BuildFaceHints(faces));
        MergeOptions(CoverOptions, candidates.Covers.Select(c => new ImportVisualOption(c)), isCover: true);
        MergeOptions(BannerOptions, candidates.Banners.Select(b => new ImportVisualOption(b)), isCover: false);
        ReplaceProblems(DuplicateProblems, problems.Duplicates, static problem => problem.ImportItemId);
        ReplaceProblems(ProfileCollisionProblems, problems.ProfileCollisions, static problem => problem.ImportItemId);
        _candidatesLoaded = true;

        AutoSelectVisuals();

        RaisePropertyChanged(nameof(MediaSummaryText));
        RaisePropertyChanged(nameof(PreparationText));
        RaisePropertyChanged(nameof(IsPreparing));
        RaisePropertyChanged(nameof(DuplicateMediaText));
        RaisePropertyChanged(nameof(HasDuplicateMedia));
        RaisePropertyChanged(nameof(HasSharedMediaHints));
        RaisePropertyChanged(nameof(HasFaceHints));
        RaisePropertyChanged(nameof(TransferText));
        RaisePropertyChanged(nameof(VisibleCoverOptions));
        RaisePropertyChanged(nameof(VisibleBannerOptions));
        RaisePropertyChanged(nameof(HasMoreCovers));
        RaisePropertyChanged(nameof(HasMoreBanners));
        RaisePropertyChanged(nameof(CoverToggleText));
        RaisePropertyChanged(nameof(BannerToggleText));
        RaisePropertyChanged(nameof(IsCoverPending));
        RaisePropertyChanged(nameof(IsBannerPending));
        RaisePropertyChanged(nameof(HasNoCoverOptions));
        RaisePropertyChanged(nameof(HasNoBannerOptions));
        RaisePropertyChanged(nameof(HasWeakCoverOptions));
        RaisePropertyChanged(nameof(HasWeakBannerOptions));
        RaisePropertyChanged(nameof(HasProblems));
        RaiseValidity();
    }

    private static void ReplaceProblems<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, Guid> id)
    {
        var nextIds = source.Select(id).ToHashSet();
        foreach (var stale in target.Where(item => !nextIds.Contains(id(item))).ToList())
        {
            target.Remove(stale);
        }
        foreach (var item in source)
        {
            if (!target.Any(existing => id(existing) == id(item)))
            {
                target.Add(item);
            }
        }
    }

    private static IReadOnlyList<ImportProfileHint> BuildFaceHints(IReadOnlyList<VerificationFaceCandidate> faces) =>
        [.. faces
            .Where(face => face.SuggestedProfileId.HasValue
                && !string.IsNullOrWhiteSpace(face.SuggestedProfileName)
                && face.Confidence is { } confidence
                && confidence >= ProfilingFaceAnalysisJobOperation.SuggestionThreshold)
            .GroupBy(face => face.SuggestedProfileId!.Value)
            .Select(group => new ImportProfileHint(
                group.Key,
                group.First().SuggestedProfileName!,
                group.Count() == 1
                    ? SurfaceText.Get("ImportWizard.FaceHint.One", "Face match in 1 photo")
                    : SurfaceText.Format("ImportWizard.FaceHint.Many", "Face match in {0} photos", group.Count()),
                IsFaceMatch: true))
            .OrderByDescending(hint => hint.Evidence.Length)
            .Take(3)];

    private static void ReplaceIfChanged(ObservableCollection<ImportProfileHint> target, IReadOnlyList<ImportProfileHint> next)
    {
        if (target.SequenceEqual(next)) return;
        target.Clear();
        foreach (var hint in next) target.Add(hint);
    }

    private void MergeOptions(ObservableCollection<ImportVisualOption> target, IEnumerable<ImportVisualOption> incoming, bool isCover)
    {
        var existing = target.ToDictionary(option => option.CandidateId, StringComparer.Ordinal);
        var ordered = new List<ImportVisualOption>();
        foreach (var option in incoming)
        {
            if (existing.TryGetValue(option.CandidateId, out var kept) && string.Equals(kept.PreviewPath, option.PreviewPath, StringComparison.Ordinal))
            {
                ordered.Add(kept);
            }
            else
            {
                ordered.Add(option);
                LoadPreview(option);
            }
        }

        if (ordered.Select(o => o.CandidateId).SequenceEqual(target.Select(o => o.CandidateId), StringComparer.Ordinal)
            && ordered.Zip(target).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return;
        }

        var selectedId = isCover ? _selectedCover?.CandidateId : _selectedBanner?.CandidateId;
        target.Clear();
        foreach (var option in ordered)
        {
            option.IsSelected = string.Equals(option.CandidateId, selectedId, StringComparison.Ordinal);
            target.Add(option);
        }

        if (isCover && _selectedCover is not null)
        {
            _selectedCover = ordered.FirstOrDefault(option => option.CandidateId == _selectedCover.CandidateId) ?? _selectedCover;
        }
        else if (!isCover && _selectedBanner is not null)
        {
            _selectedBanner = ordered.FirstOrDefault(option => option.CandidateId == _selectedBanner.CandidateId) ?? _selectedBanner;
        }
    }

    private void LoadPreview(ImportVisualOption option)
    {
        if (string.IsNullOrWhiteSpace(option.PreviewPath)) return;
        option.Preview = ImageRef.FromPath(option.PreviewPath, PreviewDecodeWidth);
        TaskObserver.Observe(
            _images.PrefetchAsync(option.PreviewPath, PreviewDecodeWidth, _lifetime.Token),
            "ImportWizardViewModel.PrefetchPreviewAsync");
    }

    public const int PreviewDecodeWidth = 320;

    private void AutoSelectVisuals()
    {
        if (_coverIntent != VerificationAppearanceIntent.Clear
            && ((_choice == ImportDestinationChoice.New && !_coverTouched)
                || (_pendingCoverAssetId is not null && _selectedCover is null)))
        {
            var preferred = _pendingCoverAssetId is { } coverId
                ? CoverOptions.FirstOrDefault(option => MatchesPendingCover(option, coverId))
                : CoverOptions.FirstOrDefault(option => option.IsRecommended);
            if (preferred is not null)
            {
                SelectCover(preferred, byUser: false);
                _pendingCoverAssetId = null;
            }
        }

        if (_bannerIntent != VerificationAppearanceIntent.Clear
            && ((_choice == ImportDestinationChoice.New && !_bannerTouched)
                || (_pendingBannerAssetId is not null && _selectedBanner is null)))
        {
            var preferred = _pendingBannerAssetId is { } bannerId
                ? BannerOptions.FirstOrDefault(option => MatchesPendingBanner(option, bannerId))
                : BannerOptions.FirstOrDefault(option => option.IsRecommended);
            if (preferred is not null)
            {
                SelectBanner(preferred, byUser: false);
                _pendingBannerAssetId = null;
            }
        }
    }

    private bool MatchesPendingCover(ImportVisualOption option, Guid assetId)
    {
        var cover = option.Cover;
        return cover is not null
            && cover.AssetId == assetId
            && (string.IsNullOrWhiteSpace(_pendingCoverSourceKind)
                || string.Equals(cover.SourceKind.ToString(), _pendingCoverSourceKind, StringComparison.OrdinalIgnoreCase))
            && cover.TimestampMilliseconds == _pendingCoverTimestampMilliseconds;
    }

    private bool MatchesPendingBanner(ImportVisualOption option, Guid assetId)
    {
        var banner = option.Banner;
        if (banner is null || banner.AssetId != assetId) return false;

        var sourceKind = Enum.TryParse<BannerVisualSourceKind>(_pendingBannerSourceKind, ignoreCase: true, out var parsed)
            ? parsed
            : banner.MediaType == MediaType.Video ? BannerVisualSourceKind.VideoClip : BannerVisualSourceKind.Image;
        return banner.SourceKind == sourceKind
            && banner.FrameTimestampMilliseconds == _pendingBannerFrameTimestampMilliseconds
            && (sourceKind != BannerVisualSourceKind.VideoClip
                || (banner.StartPointSeconds == _pendingBannerStartPointSeconds
                    && banner.DurationSeconds == _pendingBannerDurationSeconds));
    }

    public void SelectCover(ImportVisualOption? option, bool byUser)
    {
        if (byUser)
        {
            _coverTouched = true;
            _coverIntent = option is null ? VerificationAppearanceIntent.Clear : VerificationAppearanceIntent.Set;
            _pendingCoverAssetId = null;
            _pendingCoverSourceKind = null;
            _pendingCoverTimestampMilliseconds = null;
        }

        _selectedCover = option;
        foreach (var candidate in CoverOptions) candidate.IsSelected = ReferenceEquals(candidate, option);
        RaisePropertyChanged(nameof(SelectedCover));
        RaisePropertyChanged(nameof(SelectedCoverText));
    }

    public void SelectBanner(ImportVisualOption? option, bool byUser)
    {
        if (byUser)
        {
            _bannerTouched = true;
            _bannerIntent = option is null ? VerificationAppearanceIntent.Clear : VerificationAppearanceIntent.Set;
            _pendingBannerAssetId = null;
            _pendingBannerSourceKind = null;
            _pendingBannerFrameTimestampMilliseconds = null;
            _pendingBannerStartPointSeconds = null;
            _pendingBannerDurationSeconds = null;
        }

        _selectedBanner = option;
        foreach (var candidate in BannerOptions) candidate.IsSelected = ReferenceEquals(candidate, option);
        RaisePropertyChanged(nameof(SelectedBanner));
        RaisePropertyChanged(nameof(SelectedBannerText));
    }

    private void ScheduleNameCheck()
    {
        _nameCheck?.Cancel();
        _nameCheck?.Dispose();
        _nameCheck = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TaskObserver.Observe(CheckNameAsync(_newProfileName, _nameCheck.Token), "Import overlay name check");
    }

    private async Task CheckNameAsync(string name, CancellationToken token)
    {
        await Task.Delay(SearchDebounce, token).ConfigureAwait(true);
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || _operations is null)
        {
            DuplicateNameProfile = null;
            return;
        }

        var matches = await _operations.SearchProfilesAsync(trimmed, 10, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !string.Equals(_newProfileName.Trim(), trimmed, StringComparison.Ordinal)) return;
        var exact = matches.FirstOrDefault(profile => string.Equals(profile.DisplayName.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase));
        DuplicateNameProfile = exact is null ? null : new ProfileLookupItemViewModel(exact);
    }

    private void ScheduleProfileSearch()
    {
        _profileSearch?.Cancel();
        _profileSearch?.Dispose();
        _profileSearch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TaskObserver.Observe(SearchProfilesAsync(_existingSearchText, _profileSearch.Token), "Import overlay profile search");
    }

    private async Task SearchProfilesAsync(string query, CancellationToken token)
    {
        await Task.Delay(SearchDebounce, token).ConfigureAwait(true);
        if (_operations is null) return;
        var results = await _operations.SearchProfilesAsync(query, 20, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !string.Equals(_existingSearchText, query, StringComparison.Ordinal)) return;
        ExistingProfiles.Clear();
        foreach (var profile in results) ExistingProfiles.Add(new ProfileLookupItemViewModel(profile));
    }

    private void UseExistingProfile(Guid profileId, string displayName)
    {
        var existing = ExistingProfiles.FirstOrDefault(profile => profile.ProfileId == profileId);
        if (existing is null)
        {
            existing = new ProfileLookupItemViewModel(new ProfileLookupResult(profileId, displayName, string.Empty));
            ExistingProfiles.Insert(0, existing);
        }

        SelectedExistingProfile = existing;
        Choice = ImportDestinationChoice.Existing;
    }

    private void RefreshTagSuggestions()
    {
        TagSuggestions.Clear();
        var attached = SelectedTags.Select(tag => tag.TagId).ToHashSet(StringComparer.Ordinal);
        var query = _tagSearchText.Trim();
        var normalizedQuery = TaxonomyNamePolicy.TryNormalize(query);

        var candidates = _allTags
            .Where(tag => !attached.Contains(tag.TagId))
            .Where(tag => normalizedQuery is null || TaxonomyNamePolicy.TryNormalize(tag.Name) is { } nk && nk.Contains(normalizedQuery, StringComparison.Ordinal))
            .OrderBy(tag =>
            {
                var nk = TaxonomyNamePolicy.TryNormalize(tag.Name);
                return normalizedQuery is not null && nk is not null && nk.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1;
            })
            .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12);

        foreach (var tag in candidates) TagSuggestions.Add(tag);
    }

    public void AddTag(TagOptionViewModel tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (SelectedTags.Any(existing => existing.TagId == tag.TagId)) return;
        SelectedTags.Add(tag);
        TagSearchText = string.Empty;
        RefreshTagSuggestions();
    }

    public void RemoveTag(TagOptionViewModel tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var match = SelectedTags.FirstOrDefault(existing => existing.TagId == tag.TagId);
        if (match is not null)
        {
            SelectedTags.Remove(match);
            RefreshTagSuggestions();
        }
    }

    public async Task CreateCategoryAsync()
    {
        if (_catalog is null || TaxonomyNamePolicy.TryNormalizeDisplayName(_newCategoryName) is not { } displayName) return;
        var id = Guid.NewGuid().ToString("N")[..12];
        await _catalog.SettingsWrites.CreateCategoryAsync(id, displayName, _lifetime.Token).ConfigureAwait(true);
        var option = new CategoryOptionViewModel(id, displayName);
        Categories.Add(option);
        SelectedCategoryId = id;
        NewCategoryName = string.Empty;
    }

    private async Task ResolveDuplicateReuseAsync(VerificationDuplicateProblem problem)
    {
        if (_operations is null) return;
        await _operations.ApplyDuplicateDecisionAsync(problem.ImportItemId, DuplicateDecision.Reuse, problem.MatchedAssetId, _lifetime.Token).ConfigureAwait(true);
        ReplaceDuplicateDecision(new StagedDuplicateDecision(problem.ImportItemId, DuplicateDecisionAction.Reuse));
        DuplicateProblems.Remove(problem);
        await PersistCurrentDraftAsync().ConfigureAwait(true);
        RaiseProblemsChanged();
    }

    private async Task ResolveDuplicateSkipAsync(VerificationDuplicateProblem problem)
    {
        if (_operations is null) return;
        await _operations.ApplyDuplicateDecisionAsync(problem.ImportItemId, DuplicateDecision.Skip, cancellationToken: _lifetime.Token).ConfigureAwait(true);
        _admittedCount = Math.Max(0, _admittedCount - 1);
        ReplaceDuplicateDecision(new StagedDuplicateDecision(problem.ImportItemId, DuplicateDecisionAction.Skip));
        DuplicateProblems.Remove(problem);
        var collision = ProfileCollisionProblems.FirstOrDefault(existing => existing.ImportItemId == problem.ImportItemId);
        if (collision is not null)
        {
            ReplaceCollisionDecision(new StagedProfileCollisionDecision(problem.ImportItemId, ProfileCollisionAction.Skip, null));
            ProfileCollisionProblems.Remove(collision);
        }
        await PersistCurrentDraftAsync().ConfigureAwait(true);
        RaiseProblemsChanged();
    }

    private async Task ResolveCollisionAsync(VerificationProfileCollisionProblem problem, ProfileCollisionAction action)
    {
        if (_operations is null) return;
        var targetProfileId = action == ProfileCollisionAction.MoveToProfile ? problem.CandidateProfileId : (Guid?)null;
        ReplaceCollisionDecision(new StagedProfileCollisionDecision(problem.ImportItemId, action, targetProfileId));
        if (action == ProfileCollisionAction.Skip)
        {
            var duplicate = DuplicateProblems.FirstOrDefault(existing => existing.ImportItemId == problem.ImportItemId);
            if (duplicate is not null)
            {
                await _operations.ApplyDuplicateDecisionAsync(problem.ImportItemId, DuplicateDecision.Skip, cancellationToken: _lifetime.Token).ConfigureAwait(true);
                ReplaceDuplicateDecision(new StagedDuplicateDecision(problem.ImportItemId, DuplicateDecisionAction.Skip));
                DuplicateProblems.Remove(duplicate);
            }
            else
            {
                await _operations.SetItemDispositionAsync(problem.ImportItemId, ItemDisposition.Skipped, cancellationToken: _lifetime.Token).ConfigureAwait(true);
            }
            _admittedCount = Math.Max(0, _admittedCount - 1);
        }

        ProfileCollisionProblems.Remove(problem);
        await PersistCurrentDraftAsync().ConfigureAwait(true);
        RaiseProblemsChanged();
    }

    private void ReplaceDuplicateDecision(StagedDuplicateDecision decision)
    {
        _duplicateDecisions.RemoveAll(existing => existing.ImportItemId == decision.ImportItemId);
        _duplicateDecisions.Add(decision);
    }

    private void ReplaceCollisionDecision(StagedProfileCollisionDecision decision)
    {
        _collisionDecisions.RemoveAll(existing => existing.ImportItemId == decision.ImportItemId);
        _collisionDecisions.Add(decision);
    }

    private void RaiseProblemsChanged()
    {
        RaisePropertyChanged(nameof(HasProblems));
        RaiseValidity();
    }

    private async Task PersistCurrentDraftAsync()
    {
        if (_operations is null) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await _operations.LoadVerificationReadModelAsync(UnitId, _lifetime.Token).ConfigureAwait(true);
            if (model is null) return;
            try
            {
                await _operations.UpdateDraftAsync(UnitId, BuildDraft(), model.RowVersion, _lifetime.Token).ConfigureAwait(true);
                return;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }
    }

    public async Task ContinueAsync()
    {
        if (!CanContinue || _finalizer is null) return;
        IsImporting = true;
        ErrorMessage = null;
        var previousStep = _step;
        _step = ImportWizardStep.Details;
        try
        {
            var accepted = await _finalizer.RequestImportAsync(UnitId, BuildDraft(), _lifetime.Token).ConfigureAwait(true);
            if (!accepted)
            {
                _step = previousStep;
                ErrorMessage = SurfaceText.Get("ImportWizard.Gone", "This import is no longer available.");
                return;
            }
            _importRequested = true;
            _imported?.Invoke(UnitId);
            _close?.Invoke(this);
        }
        finally
        {
            IsImporting = false;
        }
    }

    public VerificationDraftV1 BuildDraft()
    {
        NewProfileDraft? newProfile = null;
        VerificationDestinationDraft destination;
        switch (_choice)
        {
            case ImportDestinationChoice.Existing:
                destination = new VerificationDestinationDraft(DestinationKind.ExistingNormal, _selectedExistingProfile?.ProfileId, null);
                break;
            case ImportDestinationChoice.DecideLater:
                destination = new VerificationDestinationDraft(DestinationKind.SystemUnknown, null, null);
                break;
            default:
                newProfile = new NewProfileDraft(
                    DisplayName: _newProfileName.Trim(),
                    CategoryId: string.IsNullOrWhiteSpace(_selectedCategoryId) ? null : _selectedCategoryId,
                    TagIds: SelectedTags.Count == 0 ? null : [.. SelectedTags.Select(tag => tag.TagId)],
                    Rating: _rating > 0 ? _rating : null,
                    Favorite: _isFavorite,
                    Overview: string.IsNullOrWhiteSpace(_overview) ? null : _overview.Trim());
                destination = new VerificationDestinationDraft(DestinationKind.NewNormal, null, newProfile);
                break;
        }

        var cover = ShowAppearance ? _selectedCover?.Cover : null;
        var banner = ShowAppearance ? _selectedBanner?.Banner : null;
        var appearance = new VerificationAppearanceDraft(
            CoverAssetId: cover?.AssetId,
            BannerAssetId: banner?.AssetId,
            BannerPresentation: null,
            CoverSourceKind: cover?.SourceKind.ToString(),
            CoverImportItemId: cover?.ImportItemId,
            BannerImportItemId: banner?.ImportItemId,
            CoverVideoTimestampMilliseconds: cover?.TimestampMilliseconds,
            BannerStartPointSeconds: banner?.SourceKind == BannerVisualSourceKind.VideoClip ? banner.StartPointSeconds : null,
            BannerDurationSeconds: banner?.SourceKind == BannerVisualSourceKind.VideoClip ? banner.DurationSeconds : null,
            BannerSourceKind: banner?.SourceKind.ToString(),
            BannerVideoFrameTimestampMilliseconds: banner?.SourceKind == BannerVisualSourceKind.VideoFrame ? banner.FrameTimestampMilliseconds : null,
            CoverIntent: _coverIntent,
            BannerIntent: _bannerIntent);

        return new VerificationDraftV1(
            SchemaVersion: VerificationDraftV1.CurrentSchemaVersion,
            CurrentStep: (int)_step,
            Destination: destination,
            Appearance: appearance,
            FaceDecisions: _faceDecisions,
            UpdatedAtMs: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            AcknowledgedMissingDependencyItemIds: _acknowledgedMissingDependencyItemIds,
            AttentionItemIds: _attentionItemIds,
            DuplicateDecisions: [.. _duplicateDecisions],
            ProfileCollisionDecisions: [.. _collisionDecisions],
            ImportRequested: _importRequested);
    }

    public async Task ImportAsync()
    {
        if (!CanImport || _publication is null) return;
        IsImporting = true;
        ErrorMessage = null;
        try
        {
            await CommitPendingTagInputForSubmissionAsync().ConfigureAwait(true);
            var result = await _publication.PublishAsync(UnitId, BuildDraft(), _lifetime.Token).ConfigureAwait(true);
            if (result.Status == ImportPublicationStatus.Blocked)
            {
                ErrorMessage = result.UserMessage ?? SurfaceText.Get("ImportWizard.PublishBlocked", "Verify still has unresolved choices.");
                return;
            }
            _imported?.Invoke(UnitId);
            _close?.Invoke(this);
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void ReportImportFailure(Exception exception)
    {
        IsImporting = false;
        ErrorMessage = SurfaceText.Format(
            "ImportWizard.ImportFailed",
            "Your choices could not be saved just now. Nothing was lost — try again. {0}",
            OperationExecution.SafeMessage(exception));
    }

    public async Task CloseAsync()
    {
        if (_operations is not null && !_isImporting)
        {
            try
            {
                var model = await _operations.LoadVerificationReadModelAsync(UnitId, _lifetime.Token).ConfigureAwait(true);
                if (model is not null
                    && !model.State.IsUnitCommitted()
                    && model.State is not (ImportUnitState.Cancelled or ImportUnitState.Committing))
                {
                    await _operations.UpdateDraftAsync(UnitId, BuildDraft(), model.RowVersion, _lifetime.Token).ConfigureAwait(true);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Import draft could not be saved on close: {0}", exception.GetType().Name);
            }
        }
        _close?.Invoke(this);
    }

    private void RaiseValidity()
    {
        RaisePropertyChanged(nameof(IsProfileChoiceComplete));
        RaisePropertyChanged(nameof(CanContinue));
        RaisePropertyChanged(nameof(CanImport));
        RaisePropertyChanged(nameof(HasDuplicateName));
        RaisePropertyChanged(nameof(IsUnknownNameWarning));
        (ContinueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        RaisePropertyChanged(nameof(ProfileSummaryText));
        RaisePropertyChanged(nameof(CategorySummaryText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _nameCheck?.Dispose();
        _profileSearch?.Dispose();
        _lifetime.Dispose();
    }
}

public static class ImportWizardQueries
{
    public static async Task<IReadOnlyList<ImportProfileHint>> ReadSharedMediaHintsAsync(
        CatalogDb catalog,
        IReadOnlyList<ImportDuplicateMatch> matches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var assetIds = matches
            .Where(match => match.MatchedAssetState == Media.AssetState.Active)
            .Select(match => match.MatchedAssetId)
            .Distinct()
            .Take(400)
            .ToList();
        if (assetIds.Count == 0) return [];

        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var index = 0; index < assetIds.Count; index++)
        {
            var name = "$a" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, DbGuid.Format(assetIds[index]));
        }

        command.CommandText =
            $"""
            SELECT p.profile_id, p.display_name, COUNT(DISTINCT pa.asset_id)
            FROM profile_assets pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE pa.relation_type = 'OWNER'
              AND pa.publication_import_unit_id IS NULL
              AND p.kind = 'NORMAL'
              AND p.trashed_at_ms IS NULL
              AND pa.asset_id IN ({string.Join(",", names)})
            GROUP BY p.profile_id, p.display_name
            ORDER BY COUNT(DISTINCT pa.asset_id) DESC
            LIMIT 3;
            """;

        var hints = new List<ImportProfileHint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = reader.GetInt32(2);
            hints.Add(new ImportProfileHint(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                count == 1
                    ? SurfaceText.Get("ImportWizard.SharedHint.One", "1 of these files is already in this profile")
                    : SurfaceText.Format("ImportWizard.SharedHint.Many", "{0} of these files are already in this profile", count),
                IsFaceMatch: false));
        }

        return hints;
    }

    public static async Task<bool> IsMovePolicyAsync(CatalogDb catalog, Guid unitId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SUM(CASE WHEN cleanup_policy = 'MOVE' THEN 1 ELSE 0 END), COUNT(*)
            FROM import_items
            WHERE import_unit_id = $unitId;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0)) return false;
        var moves = reader.GetInt32(0);
        var total = reader.GetInt32(1);
        return total > 0 && moves == total;
    }
}
