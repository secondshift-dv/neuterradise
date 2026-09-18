using Neuterradise.App.Media;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// The independent capability tracks one derivative readiness dimension for one asset.
/// Terminal states are <see cref="Ready"/> and <see cref="NotApplicable"/>.
/// A required capability that is not <see cref="NotApplicable"/> must reach <see cref="Ready"/>
/// before the asset is considered fully prepared.
/// </summary>
public enum AssetCapabilityState
{
    Queued,
    Processing,
    Ready,
    NotApplicable,
    Failed,
}

/// <summary>
/// The independent capability dimensions that Stage 2 tracks per asset.
/// Applicability is deterministic per <see cref="MediaType"/> and never hides a failure behind NOT_APPLICABLE.
/// </summary>
public enum AssetCapability
{
    CanonicalMedia,
    Metadata,
    Thumbnail,
    PresentationStill,
    VideoPreview,
    FaceDetection,
    FaceEmbedding,
    SearchProjection,
    SimilarityRelated,
    PresentationInput,
}

/// <summary>
/// Deterministic applicability: which capabilities are required or applicable for a given media type.
/// </summary>
public static class CapabilityApplicability
{
    /// <summary>
    /// Returns all capabilities (required + optional applicable) with their required flag.
    /// Used by capability seeding to create rows for all applicable capabilities.
    /// </summary>
    public static IReadOnlyList<(AssetCapability Capability, bool Required)> GetAll(MediaType mediaType) => mediaType switch
    {
        MediaType.Image =>
        [
            (AssetCapability.CanonicalMedia, true),
            (AssetCapability.Metadata, true),
            (AssetCapability.Thumbnail, true),
            (AssetCapability.PresentationStill, true),
            (AssetCapability.FaceDetection, false),
            (AssetCapability.FaceEmbedding, false),
            (AssetCapability.SearchProjection, false),
            (AssetCapability.SimilarityRelated, false),
            (AssetCapability.PresentationInput, false),
        ],
        MediaType.Video =>
        [
            (AssetCapability.CanonicalMedia, true),
            (AssetCapability.Metadata, true),
            (AssetCapability.Thumbnail, true),
            (AssetCapability.PresentationStill, true),
            (AssetCapability.VideoPreview, true),
            (AssetCapability.FaceDetection, false),
            (AssetCapability.FaceEmbedding, false),
            (AssetCapability.SearchProjection, false),
            (AssetCapability.SimilarityRelated, false),
            (AssetCapability.PresentationInput, false),
        ],
        MediaType.Model =>
        [
            (AssetCapability.CanonicalMedia, true),
            (AssetCapability.Metadata, true),
            (AssetCapability.Thumbnail, true),
            (AssetCapability.PresentationInput, false),
        ],
        _ =>
        [
            (AssetCapability.CanonicalMedia, true),
            (AssetCapability.Metadata, true),
        ],
    };

    /// <summary>
    /// Returns only the required capabilities for the given media type.
    /// Used by legacy seeding that only creates rows for required capabilities.
    /// </summary>
    public static IReadOnlyList<AssetCapability> GetRequired(MediaType mediaType) =>
        GetAll(mediaType).Where(x => x.Required).Select(x => x.Capability).ToList();

    /// <summary>
    /// Whether FaceEmbedding is applicable. It is only applicable when FaceDetection produces
    /// at least one face candidate. This is evaluated dynamically after FaceDetection completes.
    /// </summary>
    public static bool IsFaceEmbeddingApplicable(MediaType mediaType) =>
        mediaType is MediaType.Image or MediaType.Video;
}
