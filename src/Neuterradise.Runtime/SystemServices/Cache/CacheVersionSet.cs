namespace Neuterradise.App.SystemServices.Cache;

public sealed record CacheVersionSet(
    int ThumbnailVersion,
    int VideoPreviewVersion,
    int BannerPreviewVersion,
    int FaceCropVersion,
    int ModelPreviewVersion)
{
    public static CacheVersionSet Default { get; } = new(1, 1, 1, 1, 1);

    public int GetVersionForFamily(CacheFamily family) => family switch
    {
        CacheFamily.Thumbnails => ThumbnailVersion,
        CacheFamily.VideoPreviews => VideoPreviewVersion,
        CacheFamily.BannerPreviews => BannerPreviewVersion,
        CacheFamily.FaceCrops => FaceCropVersion,
        CacheFamily.ModelPreviews => ModelPreviewVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    public CacheVersionSet WithIncrementedVersion(CacheFamily family) => family switch
    {
        CacheFamily.Thumbnails => this with { ThumbnailVersion = ThumbnailVersion + 1 },
        CacheFamily.VideoPreviews => this with { VideoPreviewVersion = VideoPreviewVersion + 1 },
        CacheFamily.BannerPreviews => this with { BannerPreviewVersion = BannerPreviewVersion + 1 },
        CacheFamily.FaceCrops => this with { FaceCropVersion = FaceCropVersion + 1 },
        CacheFamily.ModelPreviews => this with { ModelPreviewVersion = ModelPreviewVersion + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };
}
