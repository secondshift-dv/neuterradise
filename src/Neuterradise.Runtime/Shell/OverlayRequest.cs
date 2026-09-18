using System.ComponentModel;
using System.Globalization;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Cache;
using System.IO;

namespace Neuterradise.App.Shell;

public enum OverlayOutcome
{
    Confirmed,
    Dismissed,
}

public abstract record OverlayRequest
{
    private protected OverlayRequest(
        string title,
        bool blocksBackgroundInput = true,
        bool isDismissableByEscape = true)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("An overlay request needs a title.", nameof(title));
        }

        Title = title;
        BlocksBackgroundInput = blocksBackgroundInput;
        IsDismissableByEscape = isDismissableByEscape;
    }

    public string Title { get; }
    public bool BlocksBackgroundInput { get; }
    public bool IsDismissableByEscape { get; }
}

public sealed record ConfirmationOverlayRequest : OverlayRequest
{
    public ConfirmationOverlayRequest(
        string title,
        string message,
        string confirmLabel,
        string? cancelLabel = null,
        bool isDestructive = false,
        string? destructiveActionName = null,
        bool isDismissableByEscape = true)
        : base(title, blocksBackgroundInput: true, isDismissableByEscape: isDismissableByEscape)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A confirmation needs a message.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(confirmLabel))
        {
            throw new ArgumentException("A confirmation needs a confirm label.", nameof(confirmLabel));
        }

        if (isDestructive && string.IsNullOrWhiteSpace(destructiveActionName))
        {
            throw new ArgumentException(
                "A destructive confirmation must name the irreversible action.",
                nameof(destructiveActionName));
        }

        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = string.IsNullOrWhiteSpace(cancelLabel) ? SurfaceText.Get("Overlay.Cancel", "Cancel") : cancelLabel;
        IsDestructive = isDestructive;
        DestructiveActionName = isDestructive ? destructiveActionName : null;
    }

    public string Message { get; }
    public string ConfirmLabel { get; }
    public string CancelLabel { get; }
    public bool IsDestructive { get; }
    public string? DestructiveActionName { get; }

    public string? FormattedDestructiveWarning => IsDestructive && !string.IsNullOrWhiteSpace(DestructiveActionName)
        ? SurfaceText.Format("Overlay.DestructiveWarning", "This will {0}. It cannot be undone.", DestructiveActionName)
        : null;
}

public sealed record ErrorDetailOverlayRequest : OverlayRequest
{
    public ErrorDetailOverlayRequest(
        string message,
        string? detail = null,
        string? title = null)
        : base(string.IsNullOrWhiteSpace(title) ? SurfaceText.Get("Overlay.SomethingWentWrong", "Something went wrong") : title, blocksBackgroundInput: true, isDismissableByEscape: true)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error overlay needs a message.", nameof(message));
        }

        Message = message;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    public string Message { get; }
    public string? Detail { get; }
}

public sealed record AppearanceCoverCandidate(
    string CandidateId,
    Guid AssetId,
    MediaType MediaType,
    CoverVisualSourceKind SourceKind,
    long? TimestampMilliseconds,
    double SuggestedCropX,
    double SuggestedCropY,
    string FileName,
    string? PreviewPath,
    int Rank,
    bool IsRecommended,
    string? Reason)
{
    public string? TimestampText => TimestampMilliseconds is { } milliseconds
        ? TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss\.fff", CultureInfo.CurrentCulture)
        : null;

    public string DisplayTitle => TimestampText is { } timestamp
        ? string.Create(CultureInfo.CurrentCulture, $"{FileName} @ {timestamp}")
        : FileName;

    public string RankText => string.Create(CultureInfo.CurrentCulture, $"#{Rank}");
}

public sealed record AppearanceBannerCandidate(
    string CandidateId,
    Guid AssetId,
    MediaType MediaType,
    BannerVisualSourceKind SourceKind,
    long? FrameTimestampMilliseconds,
    double StartPointSeconds,
    double DurationSeconds,
    double SuggestedFocusX,
    double SuggestedFocusY,
    string FileName,
    string? PreviewPath,
    int Rank,
    bool IsRecommended,
    string? Reason)
{
    public string? WindowText => SourceKind switch
    {
        BannerVisualSourceKind.VideoFrame when FrameTimestampMilliseconds is { } milliseconds =>
            TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss\.fff", CultureInfo.CurrentCulture),
        BannerVisualSourceKind.VideoClip => string.Create(
            CultureInfo.CurrentCulture,
            $"{TimeSpan.FromSeconds(StartPointSeconds):mm\\:ss} for {DurationSeconds:0.#}s"),
        _ => null,
    };

    public string DisplayTitle => WindowText is { } window
        ? string.Create(CultureInfo.CurrentCulture, $"{FileName} — {window}")
        : FileName;

    public string RankText => string.Create(CultureInfo.CurrentCulture, $"#{Rank}");
}

public sealed record AppearanceLayoutOption(string Id, string DisplayName, ProfileLayoutDefinition Definition);

public sealed record AppearanceCardOption(
    string Id,
    string DisplayName,
    double AspectRatio,
    bool UsesBanner,
    bool ShowsCover);

public sealed record AppearanceFrameOption(
    string Id,
    string DisplayName,
    bool SupportsTint,
    bool SupportsAnimation,
    double DefaultScale,
    IReadOnlyList<CoverShape> SupportedShapes,
    CoverFrameCategory Category = CoverFrameCategory.Plain,
    int SortOrder = 0)
{
    public bool Supports(CoverShape shape) => SupportedShapes.Contains(shape);

    public string CategoryName => CoverFrameCatalog.CategoryDisplayName(Category);

    public CoverAppearance PreviewAppearance { get; } = CoverFrameCatalog.Resolve(
        new CoverAppearanceRequest(
            (SupportedShapes.Contains(CoverFrameCatalog.FallbackShape)
                ? CoverFrameCatalog.FallbackShape
                : SupportedShapes[0]).ToString(),
            Id),
        reduceMotion: false).Appearance;
}

public sealed record AppearanceFrameGroup(
    CoverFrameCategory Category,
    string DisplayName,
    IReadOnlyList<AppearanceFrameOption> Options);

public sealed class AppearancePreviewState : INotifyPropertyChanged
{
    private CoverAppearance? _coverAppearance;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CoverAppearance? CoverAppearance
    {
        get => _coverAppearance;
        internal set
        {
            if (Equals(_coverAppearance, value))
            {
                return;
            }

            _coverAppearance = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverAppearance)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShapeName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameName)));
        }
    }

    private ImageRef? _coverSource;

    public ImageRef? CoverSource
    {
        get => _coverSource;
        internal set
        {
            if (ReferenceEquals(_coverSource, value))
            {
                return;
            }

            _coverSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverSource)));
        }
    }

    public string ShapeName => _coverAppearance?.Shape.ToString() ?? string.Empty;
    public string FrameName => _coverAppearance?.Frame.DisplayName ?? string.Empty;
}

public sealed record AppearanceShapeOption(CoverShape Shape, string DisplayName)
{
    public string Id => Shape.ToString();

    public CoverAppearance PreviewAppearance { get; } = CoverFrameCatalog.Resolve(
        new CoverAppearanceRequest(Shape.ToString(), "minimal"),
        reduceMotion: true).Appearance;
}

public sealed record AppearanceCustomizationResult(
    string? PresetId,
    string? CardVariantId,
    Guid? CoverAssetId,
    CoverVisualSourceKind CoverSourceKind,
    long? CoverVideoTimestampMilliseconds,
    CoverShape CoverShape,
    string? CoverFrameId,
    double? CoverFrameScale,
    string? CoverFrameTint,
    double? CoverFrameIntensity,
    CoverFrameAnimation CoverFrameAnimation,
    bool CoverShadow,
    double CoverZoom,
    double CoverCropX,
    double CoverCropY,
    Guid? BannerAssetId,
    BannerVisualSourceKind? BannerSourceKind,
    long? BannerVideoFrameTimestampMilliseconds,
    double BannerStartPointSeconds,
    double BannerDurationSeconds,
    double BannerFocusX,
    double BannerFocusY,
    double BannerZoom,
    bool BannerLoop);

public sealed record AppearanceCustomizationOverlayRequest : OverlayRequest
{
    private AppearanceCoverCandidate? _selectedCoverCandidate;
    private AppearanceBannerCandidate? _selectedBannerCandidate;

    public AppearanceCustomizationOverlayRequest(
        Guid profileId,
        string profileDisplayName,
        string? currentPresetId,
        string? currentCardVariantId,
        string? currentShape,
        string? currentFrameId,
        Action<string?, string?, string?, string?>? onSave = null,
        IReadOnlyList<AppearanceCoverCandidate>? coverCandidates = null,
        IReadOnlyList<AppearanceBannerCandidate>? bannerCandidates = null,
        Guid? currentCoverAssetId = null,
        CoverVisualSourceKind currentCoverSourceKind = CoverVisualSourceKind.Image,
        long? currentCoverTimestampMilliseconds = null,
        double currentCoverZoom = 1.0,
        double currentCoverCropX = 0.5,
        double currentCoverCropY = 0.5,
        double? currentFrameScale = null,
        string? currentFrameTint = null,
        double? currentFrameIntensity = null,
        string? currentFrameAnimation = null,
        bool currentCoverShadow = true,
        Guid? currentBannerAssetId = null,
        BannerVisualSourceKind currentBannerSourceKind = BannerVisualSourceKind.VideoClip,
        long? currentBannerVideoFrameTimestampMilliseconds = null,
        double currentBannerStartSeconds = 0.0,
        double currentBannerDurationSeconds = 0.0,
        double currentBannerFocusX = 0.5,
        double currentBannerFocusY = 0.5,
        double currentBannerZoom = 1.0,
        bool currentBannerLoop = true,
        Action<AppearanceCustomizationResult>? onApplyResult = null,
        bool reduceMotion = false,
        int initialSectionIndex = 0,
        string? title = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("Profile.Customize", "Customize Profile")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        ProfileId = profileId;
        ProfileDisplayName = profileDisplayName;
        OnSave = onSave;
        OnApplyResult = onApplyResult;
        ReduceMotion = reduceMotion;

        CoverCandidates = coverCandidates ?? [];
        BannerCandidates = bannerCandidates ?? [];

        SelectedPresetId = currentPresetId;
        SelectedCardVariantId = currentCardVariantId;
        SelectedShape = ParseShape(currentShape);
        SelectedFrameId = NormalizeFrameId(currentFrameId);
        FrameScale = currentFrameScale;
        FrameTint = currentFrameTint;
        FrameIntensity = currentFrameIntensity;
        FrameAnimation = ParseAnimation(currentFrameAnimation);
        CoverShadow = currentCoverShadow;

        CoverZoom = currentCoverZoom;
        CoverCropX = currentCoverCropX;
        CoverCropY = currentCoverCropY;

        BannerStartPointSeconds = currentBannerStartSeconds;
        BannerDurationSeconds = currentBannerDurationSeconds;
        BannerFocusX = currentBannerFocusX;
        BannerFocusY = currentBannerFocusY;
        BannerZoom = currentBannerZoom;
        BannerLoop = currentBannerLoop;

        var currentCoverId = currentCoverAssetId is { } coverAssetId
            ? ProfileAppearanceRules.CoverCandidateId(
                coverAssetId,
                currentCoverSourceKind == CoverVisualSourceKind.VideoFrame
                    ? currentCoverTimestampMilliseconds
                    : null)
            : null;
        _selectedCoverCandidate = currentCoverId is null
            ? null
            : CoverCandidates.FirstOrDefault(
                candidate => string.Equals(candidate.CandidateId, currentCoverId, StringComparison.Ordinal));

        _selectedBannerCandidate = currentBannerAssetId is { } bannerAssetId
            ? BannerCandidates.FirstOrDefault(candidate =>
                candidate.AssetId == bannerAssetId
                && candidate.SourceKind == currentBannerSourceKind
                && candidate.FrameTimestampMilliseconds == currentBannerVideoFrameTimestampMilliseconds
                && (candidate.SourceKind != BannerVisualSourceKind.VideoClip
                    || (candidate.StartPointSeconds == currentBannerStartSeconds
                        && candidate.DurationSeconds == currentBannerDurationSeconds)))
            : null;

        InitialSectionIndex = initialSectionIndex;
        PersistedCoverAssetId = currentCoverAssetId;
        PersistedCoverSourceKind = currentCoverSourceKind;
        PersistedCoverTimestampMilliseconds = currentCoverTimestampMilliseconds;
        PersistedBannerAssetId = currentBannerAssetId;
        PersistedBannerSourceKind = currentBannerSourceKind;
        PersistedBannerVideoFrameTimestampMilliseconds = currentBannerVideoFrameTimestampMilliseconds;
    }

    public Guid ProfileId { get; }
    public string ProfileDisplayName { get; }
    public Action<string?, string?, string?, string?>? OnSave { get; }
    public Action<AppearanceCustomizationResult>? OnApplyResult { get; }
    public bool ReduceMotion { get; }
    public int InitialSectionIndex { get; }

    public static IReadOnlyList<AppearanceLayoutOption> LayoutOptions { get; } =
        [.. ProfileLayoutResolver.BuiltInPresets.Select(
            static preset => new AppearanceLayoutOption(preset.Id, preset.Name, preset))];

    public static IReadOnlyList<AppearanceCardOption> CardOptions { get; } =
        [.. GalleryCardCatalog.Variants.Select(
            static variant => new AppearanceCardOption(
                variant.Id,
                variant.DisplayName,
                variant.AspectRatio,
                variant.UsesBanner,
                variant.ShowsCover))];

    public static IReadOnlyList<AppearanceFrameOption> FrameOptions { get; } =
        [.. CoverFrameCatalog.Frames.Select(
            static frame => new AppearanceFrameOption(
                frame.Id,
                frame.DisplayName,
                frame.SupportsTint,
                frame.SupportsAnimation,
                frame.DefaultScale,
                frame.SupportedShapes,
                frame.Category,
                frame.SortOrder))];

    public static IReadOnlyList<AppearanceFrameGroup> FrameGroups { get; } =
        [.. FrameOptions
            .GroupBy(static option => option.Category)
            .OrderBy(static group => group.Min(option => option.SortOrder))
            .Select(static group => new AppearanceFrameGroup(
                group.Key,
                CoverFrameCatalog.CategoryDisplayName(group.Key),
                [.. group.OrderBy(static option => option.SortOrder)]))];

    public static IReadOnlyList<AppearanceShapeOption> ShapeOptions { get; } =
        [.. CoverFrameCatalog.SelectableShapes.Select(
            static shape => new AppearanceShapeOption(shape, SplitPascalCase(shape.ToString())))];

    public static IReadOnlyList<CoverFrameAnimation> AnimationOptions { get; } =
        [.. Enum.GetValues<CoverFrameAnimation>()];

    private CoverShape _selectedShape;
    private string? _selectedFrameId;
    private double? _frameScale;
    private string? _frameTint;
    private double? _frameIntensity;
    private CoverFrameAnimation _frameAnimation;
    private bool _coverShadow;

    public AppearancePreviewState Preview { get; } = new();

    public string? SelectedPresetId { get; set; }
    public string? SelectedCardVariantId { get; set; }

    public CoverShape SelectedShape
    {
        get => _selectedShape;
        set
        {
            _selectedShape = value;
            RefreshPreview();
        }
    }

    public string? SelectedFrameId
    {
        get => _selectedFrameId;
        set
        {
            _selectedFrameId = value;
            var frame = SelectedFrame;
            _selectedShape = CoverFrameCatalog.TryGetFrame(frame.Id, out var definition)
                ? CoverFrameCatalog.ClosestSupportedShape(definition, _selectedShape)
                : _selectedShape;
            RefreshPreview();
        }
    }

    public double? FrameScale
    {
        get => _frameScale;
        set
        {
            _frameScale = value;
            RefreshPreview();
        }
    }

    public string? FrameTint
    {
        get => _frameTint;
        set
        {
            _frameTint = value;
            RefreshPreview();
        }
    }

    public double? FrameIntensity
    {
        get => _frameIntensity;
        set
        {
            _frameIntensity = value;
            RefreshPreview();
        }
    }

    public CoverFrameAnimation FrameAnimation
    {
        get => _frameAnimation;
        set
        {
            _frameAnimation = value;
            RefreshPreview();
        }
    }

    public bool CoverShadow
    {
        get => _coverShadow;
        set
        {
            _coverShadow = value;
            RefreshPreview();
        }
    }

    public double CoverZoom { get; set; } = 1.0;
    public double CoverCropX { get; set; } = 0.5;
    public double CoverCropY { get; set; } = 0.5;
    public double BannerStartPointSeconds { get; set; }
    public double BannerDurationSeconds { get; set; }
    public double BannerFocusX { get; set; } = 0.5;
    public double BannerFocusY { get; set; } = 0.5;
    public double BannerZoom { get; set; } = 1.0;
    public bool BannerLoop { get; set; } = true;

    public IReadOnlyList<AppearanceCoverCandidate> CoverCandidates { get; }
    public IReadOnlyList<AppearanceBannerCandidate> BannerCandidates { get; }

    public IReadOnlyList<AppearanceCoverCandidate> RecommendedCoverCandidates =>
        [.. CoverCandidates.Where(static candidate => candidate.IsRecommended)];

    public IReadOnlyList<AppearanceCoverCandidate> OtherCoverCandidates =>
        [.. CoverCandidates.Where(static candidate => !candidate.IsRecommended)];

    public IReadOnlyList<AppearanceBannerCandidate> RecommendedBannerCandidates =>
        [.. BannerCandidates.Where(static candidate => candidate.IsRecommended)];

    public IReadOnlyList<AppearanceBannerCandidate> OtherBannerCandidates =>
        [.. BannerCandidates.Where(static candidate => !candidate.IsRecommended)];

    public AppearanceCoverCandidate? SelectedCoverCandidate
    {
        get => _selectedCoverCandidate;
        set
        {
            _selectedCoverCandidate = value;
            ShowCandidateCover(value);
        }
    }

    public const int PreviewCoverDecodeWidth = 256;

    private void ShowCandidateCover(AppearanceCoverCandidate? candidate)
    {
        if (candidate?.PreviewPath is not { Length: > 0 } path)
        {
            if (candidate is null)
            {
                Preview.CoverSource = _previewCoverSource;
            }

            return;
        }

        Preview.CoverSource = new ImageRef(path, PreviewCoverDecodeWidth);
    }

    public AppearanceBannerCandidate? SelectedBannerCandidate
    {
        get => _selectedBannerCandidate;
        set
        {
            _selectedBannerCandidate = value;
            if (value is { SourceKind: BannerVisualSourceKind.VideoClip })
            {
                BannerStartPointSeconds = value.StartPointSeconds;
                BannerDurationSeconds = value.DurationSeconds;
            }
            else
            {
                BannerStartPointSeconds = ProfileAppearanceOverrides.Default.BannerStartPointSeconds;
                BannerDurationSeconds = ProfileAppearanceOverrides.Default.BannerDurationSeconds;
            }
        }
    }

    public ProfilePresentationModel? PreviewModel { get; init; }

    private ImageRef? _previewCoverSource;

    public ImageRef? PreviewCoverSource
    {
        get => _previewCoverSource;
        init
        {
            _previewCoverSource = value;
            if (_selectedCoverCandidate is null || Preview.CoverSource is null)
            {
                Preview.CoverSource = value;
            }
        }
    }

    public ImageRef? PreviewBannerStillSource { get; init; }

    public CoverAppearance PreviewCoverAppearance =>
        CoverFrameCatalog.Resolve(
            new CoverAppearanceRequest(
                SelectedShape.ToString(),
                SelectedFrameId,
                FrameScale,
                IsTintAvailable ? FrameTint : null,
                FrameIntensity,
                (IsAnimationAvailable ? FrameAnimation : CoverFrameAnimation.None).ToString(),
                CoverShadow),
            reduceMotion: ReduceMotion).Appearance;

    public BannerPresentation PreviewBannerPresentation =>
        BannerPresentationPolicy.Resolve(
            new BannerPresentationRequest(
                BannerStartPointSeconds,
                BannerDurationSeconds,
                BannerFocusX,
                BannerFocusY,
                BannerZoom,
                BannerLoop)).Presentation;

    public Guid? PersistedCoverAssetId { get; }
    public CoverVisualSourceKind PersistedCoverSourceKind { get; }
    public long? PersistedCoverTimestampMilliseconds { get; }
    public Guid? PersistedBannerAssetId { get; }

    public BannerVisualSourceKind PersistedBannerSourceKind { get; }

    public long? PersistedBannerVideoFrameTimestampMilliseconds { get; }

    public AppearanceFrameOption SelectedFrame =>
        FrameOptions.FirstOrDefault(
            option => string.Equals(option.Id, SelectedFrameId, StringComparison.OrdinalIgnoreCase))
        ?? FrameOptions[0];

    public IReadOnlyList<AppearanceShapeOption> CompatibleShapeOptions =>
        [.. ShapeOptions.Where(option => SelectedFrame.Supports(option.Shape))];

    public bool IsTintAvailable => SelectedFrame.SupportsTint;
    public bool IsAnimationAvailable => SelectedFrame.SupportsAnimation && !ReduceMotion;
    public bool IsFrameShapeCompatible => SelectedFrame.Supports(SelectedShape);

    public AppearanceCustomizationResult BuildResult() => new(
        SelectedPresetId,
        SelectedCardVariantId,
        SelectedCoverCandidate is { } cover ? cover.AssetId : PersistedCoverAssetId,
        SelectedCoverCandidate is { } kind ? kind.SourceKind : PersistedCoverSourceKind,
        SelectedCoverCandidate is { } stamp ? stamp.TimestampMilliseconds : PersistedCoverTimestampMilliseconds,
        SelectedShape,
        SelectedFrameId,
        FrameScale,
        IsTintAvailable ? FrameTint : null,
        FrameIntensity,
        IsAnimationAvailable ? FrameAnimation : CoverFrameAnimation.None,
        CoverShadow,
        CoverZoom,
        CoverCropX,
        CoverCropY,
        SelectedBannerCandidate?.AssetId ?? PersistedBannerAssetId,
        SelectedBannerCandidate?.SourceKind ?? (PersistedBannerAssetId is null ? null : PersistedBannerSourceKind),
        (SelectedBannerCandidate?.SourceKind ?? PersistedBannerSourceKind) == BannerVisualSourceKind.VideoFrame
            ? SelectedBannerCandidate?.FrameTimestampMilliseconds ?? PersistedBannerVideoFrameTimestampMilliseconds
            : null,
        (SelectedBannerCandidate?.SourceKind ?? PersistedBannerSourceKind) == BannerVisualSourceKind.VideoClip
            ? BannerStartPointSeconds
            : ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
        (SelectedBannerCandidate?.SourceKind ?? PersistedBannerSourceKind) == BannerVisualSourceKind.VideoClip
            ? BannerDurationSeconds
            : ProfileAppearanceOverrides.Default.BannerDurationSeconds,
        BannerFocusX,
        BannerFocusY,
        BannerZoom,
        BannerLoop);

    public void Apply()
    {
        OnApplyResult?.Invoke(BuildResult());
        OnSave?.Invoke(SelectedPresetId, SelectedCardVariantId, SelectedShape.ToString(), SelectedFrameId);
    }

    public void Cancel()
    {
    }

    private void RefreshPreview() => Preview.CoverAppearance = PreviewCoverAppearance;

    private static CoverShape ParseShape(string? value) =>
        Enum.TryParse<CoverShape>(value?.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed)
            ? parsed
            : CoverFrameCatalog.FallbackShape;

    private static CoverFrameAnimation ParseAnimation(string? value) =>
        Enum.TryParse<CoverFrameAnimation>(value, ignoreCase: true, out var parsed)
            ? parsed
            : CoverFrameAnimation.None;

    private static string NormalizeFrameId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CoverFrameCatalog.TryGetFrame(value.Trim(), out var frame)
            ? frame.Id
            : CoverFrameCatalog.NoneFrameId;

    private static string SplitPascalCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]))
            {
                builder.Append(' ');
            }

            builder.Append(value[index]);
        }

        return builder.ToString();
    }
}

public sealed record ProfilePickerItem(Guid ProfileId, string DisplayName, string? CategoryName);

public sealed record ProfilePickerOverlayRequest : OverlayRequest
{
    public ProfilePickerOverlayRequest(
        IReadOnlyList<ProfilePickerItem> candidates,
        Action<ProfilePickerItem> onProfileSelected,
        string? title = null,
        string? prompt = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        Candidates = candidates ?? [];
        OnProfileSelected = onProfileSelected ?? throw new ArgumentNullException(nameof(onProfileSelected));
        Prompt = string.IsNullOrWhiteSpace(prompt)
            ? SurfaceText.Get("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile")
            : prompt;
    }

    public string Prompt { get; }
    public IReadOnlyList<ProfilePickerItem> Candidates { get; }
    public Action<ProfilePickerItem> OnProfileSelected { get; }
    public ProfilePickerItem? SelectedCandidate { get; set; }
}

public sealed record ProfileAssociationEntry(Guid TargetProfileId, string DisplayName, string RelationKind);

public sealed record AssociationEditorOverlayRequest : OverlayRequest
{
    public AssociationEditorOverlayRequest(
        Guid sourceProfileId,
        string sourceProfileDisplayName,
        IReadOnlyList<ProfileAssociationEntry> associations,
        Action<Guid>? onRemoveAssociation = null,
        Action? onAddAssociationRequested = null,
        string? title = null)
        : base(
            string.IsNullOrWhiteSpace(title)
                ? SurfaceText.Get("Profile.Related", "Related")
                : title,
            blocksBackgroundInput: true,
            isDismissableByEscape: true)
    {
        SourceProfileId = sourceProfileId;
        SourceProfileDisplayName = sourceProfileDisplayName;
        Associations = associations ?? [];
        OnRemoveAssociation = onRemoveAssociation;
        OnAddAssociationRequested = onAddAssociationRequested;
    }

    public Guid SourceProfileId { get; }
    public string SourceProfileDisplayName { get; }
    public IReadOnlyList<ProfileAssociationEntry> Associations { get; }
    public Action<Guid>? OnRemoveAssociation { get; }
    public Action? OnAddAssociationRequested { get; }
}
