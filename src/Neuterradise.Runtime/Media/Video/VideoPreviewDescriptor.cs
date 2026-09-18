namespace Neuterradise.App.Media.Video;

public sealed record VideoPreviewDescriptor(
    Guid AssetId,
    string? StaticThumbnailPath = null,
    string? PreviewVideoPath = null,
    int? DurationMs = null,
    bool IsMuted = true,
    bool HasPreview = true,
    string? ErrorDetail = null);
