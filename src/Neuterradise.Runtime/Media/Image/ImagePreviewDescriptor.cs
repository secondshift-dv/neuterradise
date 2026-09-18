namespace Neuterradise.App.Media.Image;

public sealed record ImagePreviewDescriptor(
    Guid AssetId,
    string? ThumbnailPath = null,
    int? PixelWidth = null,
    int? PixelHeight = null,
    bool IsReady = true,
    string? ErrorDetail = null);
