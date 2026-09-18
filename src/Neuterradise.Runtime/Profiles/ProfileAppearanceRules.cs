using System.Globalization;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Media;

namespace Neuterradise.App.Profiles;

public enum CoverVisualSourceKind
{

    Image,

    VideoFrame,
}

public enum BannerVisualSourceKind
{
    Image,
    VideoFrame,
    VideoClip,
}

public static class ProfileAppearanceRules
{

    public static bool CanReferenceAsset(Guid profileId, Guid assetId)
    {
        return profileId != Guid.Empty && assetId != Guid.Empty;
    }

    public static bool ValidateEligibility(
        ProfileKind profileKind,
        bool isProfileActive,
        AssetState assetState,
        bool isAssetActive,
        bool hasActiveRelation)
    {
        if (profileKind != ProfileKind.Normal)
        {
            return false;
        }

        if (!isProfileActive)
        {
            return false;
        }

        if (assetState != AssetState.Active || !isAssetActive)
        {
            return false;
        }

        return hasActiveRelation;
    }

    public static bool IsCoverMediaTypeEligible(MediaType mediaType) =>
        mediaType is MediaType.Image or MediaType.Video;

    public static bool IsCoverVisualSourceValid(
        MediaType mediaType,
        CoverVisualSourceKind kind,
        long? timestampMilliseconds) => (mediaType, kind) switch
        {
            (MediaType.Image, CoverVisualSourceKind.Image) => timestampMilliseconds is null,
            (MediaType.Video, CoverVisualSourceKind.VideoFrame) => timestampMilliseconds is >= 0,
            _ => false,
        };

    public static CoverVisualSourceKind ResolveCoverSourceKind(MediaType mediaType) =>
        mediaType == MediaType.Video ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image;

    /// <summary>
    /// Media a Banner may be set from.
    ///
    /// Eligible sources are an image, a still frame from a video, or a video clip.
    /// </summary>
    public static bool IsBannerMediaTypeEligible(MediaType mediaType) =>
        mediaType is MediaType.Image or MediaType.Video;

    public static BannerVisualSourceKind ResolveBannerSourceKind(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => BannerVisualSourceKind.Image,
        MediaType.Video => BannerVisualSourceKind.VideoClip,
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported banner media type."),
    };

    public static BannerVisualSourceKind ResolveBannerSourceKind(
        ProfileAppearanceOverrides overrides,
        MediaType? bannerMediaType)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        if (Enum.TryParse<BannerVisualSourceKind>(
                overrides.BannerSourceKind,
                ignoreCase: true,
                out var explicitKind)
            && Enum.IsDefined(explicitKind))
        {
            return explicitKind;
        }

        return bannerMediaType == MediaType.Video
            ? BannerVisualSourceKind.VideoClip
            : BannerVisualSourceKind.Image;
    }

    public static ProfileAppearanceOverrides NormalizeBannerSourceKind(
        ProfileAppearanceOverrides overrides,
        MediaType? bannerMediaType)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var resolved = ResolveBannerSourceKind(overrides, bannerMediaType);
        return string.Equals(
                overrides.BannerSourceKind,
                resolved.ToString(),
                StringComparison.Ordinal)
            ? overrides
            : overrides with { BannerSourceKind = resolved.ToString() };
    }

    public static bool IsBannerVisualSourceValid(
        MediaType mediaType,
        BannerVisualSourceKind sourceKind,
        long? frameTimestampMilliseconds,
        double? startPointSeconds,
        double? durationSeconds) => sourceKind switch
    {
        BannerVisualSourceKind.Image => mediaType == MediaType.Image
            && frameTimestampMilliseconds is null
            && startPointSeconds is null
            && durationSeconds is null,
        BannerVisualSourceKind.VideoFrame => mediaType == MediaType.Video
            && frameTimestampMilliseconds is >= 0
            && startPointSeconds is null
            && durationSeconds is null,
        BannerVisualSourceKind.VideoClip => mediaType == MediaType.Video
            && frameTimestampMilliseconds is null
            && startPointSeconds is { } start
            && double.IsFinite(start)
            && start >= 0
            && durationSeconds is { } duration
            && double.IsFinite(duration)
            && duration > 0,
        _ => false,
    };

    public const double MinimumCoverCrop = 0;

    public const double MaximumCoverCrop = 1;

    public const double DefaultCoverCrop = 0.5;

    public const double MinimumCoverZoom = 1;

    public const double MaximumCoverZoom = 4;

    public static bool IsCoverCropValid(double value) =>
        double.IsFinite(value) && value >= MinimumCoverCrop && value <= MaximumCoverCrop;

    public static bool IsCoverZoomValid(double value) =>
        double.IsFinite(value) && value >= MinimumCoverZoom && value <= MaximumCoverZoom;

    public static AppearanceVisualCandidates EvaluateVisualCandidates(
        IReadOnlyList<AppearanceCandidateEvaluationInput> inputs)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return AppearanceVisualCandidates.Empty;
        }

        var covers = new List<CoverVisualCandidate>();
        var banners = new List<BannerVisualCandidate>();

        foreach (var item in inputs)
        {
            covers.AddRange(BuildCoverCandidates(item));
            banners.AddRange(BuildBannerCandidates(item));
        }

        return new AppearanceVisualCandidates(RankCovers(covers), RankBanners(banners));
    }

    private static IEnumerable<CoverVisualCandidate> BuildCoverCandidates(
        AppearanceCandidateEvaluationInput item)
    {
        if (!IsCoverMediaTypeEligible(item.MediaType))
        {
            yield break;
        }

        if (item.MediaType == MediaType.Image)
        {
            yield return ScoreCover(item, timestamp: null, face: OrderedFaceEvidence(item).FirstOrDefault());
            yield break;
        }

        var emitted = new HashSet<long>();
        foreach (var face in OrderedFaceEvidence(item).Take(MaximumFaceCoverCandidatesPerAsset))
        {
            var timestamp = face.SampledTimestampMilliseconds ?? 0;
            if (emitted.Add(timestamp))
            {
                yield return ScoreCover(item, timestamp, face);
            }
        }

        foreach (var timestamp in DeterministicVideoPositions(item.DurationMs))
        {
            if (emitted.Count >= MaximumCoverCandidatesPerAsset)
            {
                yield break;
            }

            if (emitted.Add(timestamp))
            {
                yield return ScoreCover(item, timestamp, face: null);
            }
        }
    }

    private static CoverVisualCandidate ScoreCover(
        AppearanceCandidateEvaluationInput item,
        long? timestamp,
        AppearanceFaceEvidence? face)
    {
        var kind = timestamp is null ? CoverVisualSourceKind.Image : CoverVisualSourceKind.VideoFrame;
        var score = 50.0;
        string reason;

        if (face is { IsConfirmedForTargetProfile: true })
        {
            score += 100;
            reason = "Confirmed face for this Profile";
        }
        else if (face is { TargetIdentitySimilarity: { } similarity })
        {

            score += 40 + (similarity * 60);
            reason = "Face resembling this Profile";
        }
        else if (face is not null || item.HasConfirmedFaceForProfile)
        {
            score += 50;
            reason = "Detected face with suitable framing";
        }
        else if (item.HasFaceDetection)
        {
            score += 25;
            reason = "Frame from media containing a face";
        }
        else
        {
            reason = kind == CoverVisualSourceKind.Image
                ? "Eligible image candidate"
                : "Sampled frame";
        }

        if (face is not null)
        {

            score += 30 * Math.Clamp(face.RelativeArea, 0, 1);
            score += 20 * Math.Clamp(face.DetectionConfidence, 0, 1);
        }

        score += ShapeAndResolutionScore(item, ref reason);

        if (item.HasDerivedPreview)
        {
            score += 10;
        }

        if (item.IsExistingCover && timestamp is null)
        {
            score += 5;
        }

        return new CoverVisualCandidate(
            CandidateId: CoverCandidateId(item.AssetId, timestamp),
            SourceAssetId: item.AssetId,
            SourceMediaType: item.MediaType,
            SourceKind: kind,
            TimestampMilliseconds: timestamp,
            SuggestedCropX: SuggestedFocal(face).X,
            SuggestedCropY: SuggestedFocal(face).Y,
            FaceId: face?.FaceId,
            DetectionConfidence: face?.DetectionConfidence,
            TargetIdentitySimilarity: face?.TargetIdentitySimilarity,
            PixelWidth: item.PixelWidth,
            PixelHeight: item.PixelHeight,
            DisplayName: item.DisplayName,
            Score: score,
            Rank: 0,
            IsRecommended: false,
            Reason: reason);
    }

    private static double ShapeAndResolutionScore(
        AppearanceCandidateEvaluationInput item,
        ref string reason)
    {
        if (item.PixelWidth is not > 0 || item.PixelHeight is not > 0)
        {
            return 0;
        }

        var score = 0.0;
        var ratio = (double)item.PixelWidth.Value / item.PixelHeight.Value;
        if (ratio is >= 0.65 and <= 1.35)
        {
            score += 30;
            if (!item.HasConfirmedFaceForProfile && !item.HasFaceDetection)
            {
                reason = "Balanced framing with high resolution";
            }
        }
        else if (ratio is >= 0.5 and <= 1.6)
        {
            score += 15;
        }
        else if (ratio is > 2.0 or < 0.4)
        {
            score -= 25;
        }

        if (item.PixelWidth >= 800 && item.PixelHeight >= 800)
        {
            score += 20;
        }
        else if (item.PixelWidth >= 400 && item.PixelHeight >= 400)
        {
            score += 10;
        }
        else if (item.PixelWidth < 150 || item.PixelHeight < 150)
        {
            score -= 20;
        }

        return score;
    }

    private static IEnumerable<BannerVisualCandidate> BuildBannerCandidates(
        AppearanceCandidateEvaluationInput item)
    {
        if (!IsBannerMediaTypeEligible(item.MediaType))
        {
            yield break;
        }

        if (item.MediaType == MediaType.Image)
        {
            yield return ScoreBanner(item, timestampMilliseconds: null, kind: BannerVisualSourceKind.Image, face: null);
            yield break;
        }

        var emittedClips = new HashSet<long>();
        var emittedFrames = new HashSet<long>();

        foreach (var face in OrderedFaceEvidence(item).Take(MaximumFaceBannerCandidatesPerAsset))
        {
            var start = ClampWindowStart(face.SampledTimestampMilliseconds ?? 0, item.DurationMs);
            if (emittedClips.Add(start))
            {
                yield return ScoreBanner(item, start, kind: BannerVisualSourceKind.VideoClip, face);
            }
        }

        foreach (var position in DeterministicVideoPositions(item.DurationMs))
        {
            if (emittedClips.Count >= MaximumBannerCandidatesPerAsset)
            {
                break;
            }

            var start = ClampWindowStart(position + BannerLeadInMilliseconds, item.DurationMs);
            if (emittedClips.Add(start))
            {
                yield return ScoreBanner(item, start, kind: BannerVisualSourceKind.VideoClip, face: null);
            }
        }

        foreach (var face in OrderedFaceEvidence(item).Take(MaximumFaceBannerCandidatesPerAsset))
        {
            var timestamp = face.SampledTimestampMilliseconds ?? 0;
            if (emittedFrames.Add(timestamp))
            {
                yield return ScoreBanner(item, timestamp, kind: BannerVisualSourceKind.VideoFrame, face);
            }
        }

        foreach (var position in DeterministicVideoPositions(item.DurationMs))
        {
            if (emittedFrames.Count >= MaximumBannerCandidatesPerAsset)
            {
                break;
            }

            if (emittedFrames.Add(position))
            {
                yield return ScoreBanner(item, position, kind: BannerVisualSourceKind.VideoFrame, face: null);
            }
        }
    }

    private static BannerVisualCandidate ScoreBanner(
        AppearanceCandidateEvaluationInput item,
        long? timestampMilliseconds,
        BannerVisualSourceKind kind,
        AppearanceFaceEvidence? face)
    {
        var score = 50.0;
        string reason = "Eligible media candidate";

        switch (kind)
        {
            case BannerVisualSourceKind.VideoClip:
                score += 30;
                break;
            case BannerVisualSourceKind.VideoFrame:
                score += 10;
                break;
            case BannerVisualSourceKind.Image:
                break;
        }

        if (item.PixelWidth is > 0 && item.PixelHeight is > 0)
        {
            var ratio = (double)item.PixelWidth.Value / item.PixelHeight.Value;
            if (ratio >= 1.7)
            {
                score += 70;
                reason = "Wide landscape composition";
            }
            else if (ratio >= 1.3)
            {
                score += 40;
                reason = "Landscape composition";
            }
            else if (ratio >= 0.9)
            {
                score += 10;
            }
            else
            {
                score -= 40;
                reason = "Portrait aspect ratio is less suitable for a banner";
            }

            score += item.PixelWidth switch
            {
                >= 1600 => 25,
                >= 1200 => 15,
                >= 800 => 5,
                _ => 0,
            };
        }

        if (kind == BannerVisualSourceKind.VideoClip)
        {
            score += item.DurationMs switch
            {
                >= 2_000 and <= 60_000 => 25,
                > 60_000 and <= 300_000 => 15,
                > 300_000 => 5,
                _ => 0,
            };
        }

        if (face is not null)
        {
            score += 25 + (20 * Math.Clamp(face.DetectionConfidence, 0, 1));
            reason = face.IsConfirmedForTargetProfile
                ? $"Frame/clip around a confirmed face for this Profile ({kind})"
                : $"Frame/clip around a detected face ({kind})";
        }

        if (item.HasDerivedPreview)
        {
            score += 10;
        }

        if (item.IsExistingBanner)
        {
            score += 5;
        }

        var duration = ResolveWindowDuration(item.DurationMs);
        var candidateId = kind switch
        {
            BannerVisualSourceKind.Image => item.AssetId.ToString("N"),
            BannerVisualSourceKind.VideoFrame => string.Create(CultureInfo.InvariantCulture, $"{item.AssetId:N}@{timestampMilliseconds}"),
            BannerVisualSourceKind.VideoClip => BannerCandidateId(item.AssetId, timestampMilliseconds, duration),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return new BannerVisualCandidate(
            CandidateId: candidateId,
            SourceAssetId: item.AssetId,
            SourceMediaType: item.MediaType,
            SourceKind: kind,
            FrameTimestampMilliseconds: kind == BannerVisualSourceKind.VideoFrame ? timestampMilliseconds : null,
            StartPointSeconds: kind == BannerVisualSourceKind.VideoClip && timestampMilliseconds is { } start ? start / 1000d : 0,
            DurationSeconds: kind == BannerVisualSourceKind.VideoClip ? duration.TotalSeconds : 0,
            FocusX: SuggestedFocal(face).X,
            FocusY: SuggestedFocal(face).Y,
            FaceId: face?.FaceId,
            DisplayName: item.DisplayName,
            Score: score,
            Rank: 0,
            IsRecommended: false,
            Reason: reason);
    }

    private static IReadOnlyList<CoverVisualCandidate> RankCovers(List<CoverVisualCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

        return [.. ordered.Select((candidate, index) => candidate with
        {
            Rank = index + 1,
            IsRecommended = index < MaximumRecommendedCandidates && candidate.Score >= RecommendationFloor,
        })];
    }

    private static IReadOnlyList<BannerVisualCandidate> RankBanners(List<BannerVisualCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

        return [.. ordered.Select((candidate, index) => candidate with
        {
            Rank = index + 1,
            IsRecommended = index < MaximumRecommendedCandidates && candidate.Score >= RecommendationFloor,
        })];
    }

    private static IEnumerable<AppearanceFaceEvidence> OrderedFaceEvidence(
        AppearanceCandidateEvaluationInput item) =>
        (item.FaceEvidence ?? [])
            .OrderByDescending(static face => face.IsConfirmedForTargetProfile)
            .ThenByDescending(static face => face.TargetIdentitySimilarity ?? -1)
            .ThenByDescending(static face => face.DetectionConfidence)
            .ThenBy(static face => face.SampledTimestampMilliseconds ?? 0);

    private static (double X, double Y) SuggestedFocal(AppearanceFaceEvidence? face)
    {
        if (face?.NormalizedCenterX is not { } centerX || face.NormalizedCenterY is not { } centerY)
        {
            return (DefaultCoverCrop, DefaultCoverCrop);
        }

        // A small upward bias keeps eyes away from the vertical centre; safe clamps prevent a tiny
        // edge face from forcing an extreme crop.
        var upwardBias = Math.Min(0.08, (face.NormalizedHeight ?? 0) * 0.12);
        return (Math.Clamp(centerX, 0.12, 0.88), Math.Clamp(centerY - upwardBias, 0.12, 0.88));
    }

    private static IReadOnlyList<long> DeterministicVideoPositions(int? durationMilliseconds)
    {
        if (durationMilliseconds is not { } duration || duration <= 0)
        {
            return [0];
        }

        return [duration / 4, duration / 2, duration * 3 / 4];
    }

    private static long ClampWindowStart(long aroundMilliseconds, int? durationMilliseconds)
    {
        var start = Math.Max(0, aroundMilliseconds - BannerLeadInMilliseconds);
        if (durationMilliseconds is { } duration && duration > 0)
        {
            var windowMilliseconds = (long)ResolveWindowDuration(duration).TotalMilliseconds;
            start = Math.Clamp(start, 0, Math.Max(0, duration - windowMilliseconds));
        }

        return start;
    }

    private static TimeSpan ResolveWindowDuration(int? durationMilliseconds)
    {
        if (durationMilliseconds is not { } duration || duration <= 0)
        {
            return BannerPresentationPolicy.DefaultDuration;
        }

        var media = TimeSpan.FromMilliseconds(duration);
        if (media >= BannerPresentationPolicy.DefaultDuration)
        {
            return BannerPresentationPolicy.DefaultDuration;
        }

        return media >= BannerPresentationPolicy.MinimumDuration
            ? media
            : BannerPresentationPolicy.MinimumDuration;
    }

    public static string CoverCandidateId(Guid assetId, long? timestampMilliseconds) =>
        timestampMilliseconds is { } timestamp
            ? string.Create(CultureInfo.InvariantCulture, $"{assetId:N}@{timestamp}")
            : assetId.ToString("N");

    public static string BannerCandidateId(Guid assetId, long? startMilliseconds, TimeSpan duration) =>
        startMilliseconds is { } start
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{assetId:N}#{start}+{(long)duration.TotalMilliseconds}")
            : assetId.ToString("N");

    public const int MaximumRecommendedCandidates = 5;

    public const double RecommendationFloor = 50.0;

    public const int MaximumFaceCoverCandidatesPerAsset = 4;

    public const int MaximumCoverCandidatesPerAsset = 6;

    public const int MaximumFaceBannerCandidatesPerAsset = 3;

    public const int MaximumBannerCandidatesPerAsset = 5;

    public const long BannerLeadInMilliseconds = 3_000;
}

public sealed record AppearanceFaceEvidence(
    Guid FaceId,
    long? SampledTimestampMilliseconds,
    double DetectionConfidence,
    double? TargetIdentitySimilarity,
    bool IsConfirmedForTargetProfile,
    double RelativeArea,
    double? NormalizedCenterX = null,
    double? NormalizedCenterY = null,
    double? NormalizedX = null,
    double? NormalizedY = null,
    double? NormalizedWidth = null,
    double? NormalizedHeight = null);

public sealed record AppearanceCandidateEvaluationInput(
    Guid AssetId,
    MediaType MediaType,
    string DisplayName,
    int? PixelWidth,
    int? PixelHeight,
    int? DurationMs,
    bool HasDerivedPreview,
    bool HasConfirmedFaceForProfile,
    bool HasFaceDetection,
    long CreatedAtMs,
    bool IsExistingCover = false,
    bool IsExistingBanner = false,
    IReadOnlyList<AppearanceFaceEvidence>? FaceEvidence = null);

public sealed record CoverVisualCandidate(
    string CandidateId,
    Guid SourceAssetId,
    MediaType SourceMediaType,
    CoverVisualSourceKind SourceKind,
    long? TimestampMilliseconds,
    double SuggestedCropX,
    double SuggestedCropY,
    Guid? FaceId,
    double? DetectionConfidence,
    double? TargetIdentitySimilarity,
    int? PixelWidth,
    int? PixelHeight,
    string DisplayName,
    double Score,
    int Rank,
    bool IsRecommended,
    string Reason);

public sealed record BannerVisualCandidate(
    string CandidateId,
    Guid SourceAssetId,
    MediaType SourceMediaType,
    BannerVisualSourceKind SourceKind,
    long? FrameTimestampMilliseconds,
    double StartPointSeconds,
    double DurationSeconds,
    double FocusX,
    double FocusY,
    Guid? FaceId,
    string DisplayName,
    double Score,
    int Rank,
    bool IsRecommended,
    string Reason);

public sealed record AppearanceVisualCandidates(
    IReadOnlyList<CoverVisualCandidate> Covers,
    IReadOnlyList<BannerVisualCandidate> Banners)
{
    public static AppearanceVisualCandidates Empty { get; } = new([], []);

    public IReadOnlyList<CoverVisualCandidate> RecommendedCovers =>
        [.. Covers.Where(static candidate => candidate.IsRecommended)];

    public IReadOnlyList<BannerVisualCandidate> RecommendedBanners =>
        [.. Banners.Where(static candidate => candidate.IsRecommended)];
}
