using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.App.SystemServices.Cache;

public enum CacheLookupStatus
{
    Hit,
    Miss,
    InvalidOrCorrupt,
    GenerationPending,
}

public sealed record CacheLookupResult(
    CacheLookupStatus Status,
    string? PhysicalPath,
    IDisposable? ReadLease)
{
    public static CacheLookupResult Miss() => new(CacheLookupStatus.Miss, null, null);

    public static CacheLookupResult InvalidOrCorrupt() => new(CacheLookupStatus.InvalidOrCorrupt, null, null);

    public static CacheLookupResult GenerationPending() => new(CacheLookupStatus.GenerationPending, null, null);

    public static CacheLookupResult Hit(string physicalPath, IDisposable? readLease = null) =>
        new(CacheLookupStatus.Hit, physicalPath, readLease);
}

public interface ICacheKey
{
    CacheFamily Family { get; }

    string LogicalKey { get; }

    string GetRelativePath(string extension = ".bin");
}

public sealed record ThumbnailCacheKey : ICacheKey
{
    public ThumbnailCacheKey(Guid assetId, string contentFingerprint, int version, string sizeKey)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(sizeKey);

        AssetId = assetId;
        ContentFingerprint = contentFingerprint.Trim();
        Version = version;
        SizeKey = sizeKey.Trim();
    }

    public Guid AssetId { get; }

    public string ContentFingerprint { get; }

    public int Version { get; }

    public string SizeKey { get; }

    public CacheFamily Family => CacheFamily.Thumbnails;

    public string LogicalKey => $"{AssetId:D}/{ContentFingerprint}/{Version}/{SizeKey}";

    public string GetRelativePath(string extension = ".bin")
    {
        var ext = NormalizeExtension(extension);
        var sanitizedFingerprint = CacheKey.SanitizeSegment(ContentFingerprint);
        var sanitizedSize = CacheKey.SanitizeSegment(SizeKey);
        return Path.Combine(AssetId.ToString("N"), $"{sanitizedFingerprint}_v{Version}_{sanitizedSize}{ext}");
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? ".bin" : (extension.StartsWith('.') ? extension : "." + extension);
}

public sealed record VideoPreviewCacheKey : ICacheKey
{
    public VideoPreviewCacheKey(Guid assetId, string contentFingerprint, int version, string variantKey)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKey);

        AssetId = assetId;
        ContentFingerprint = contentFingerprint.Trim();
        Version = version;
        VariantKey = variantKey.Trim();
    }

    public Guid AssetId { get; }

    public string ContentFingerprint { get; }

    public int Version { get; }

    public string VariantKey { get; }

    public CacheFamily Family => CacheFamily.VideoPreviews;

    public string LogicalKey => $"{AssetId:D}/{ContentFingerprint}/{Version}/{VariantKey}";

    public string GetRelativePath(string extension = ".bin")
    {
        var ext = NormalizeExtension(extension);
        var sanitizedFingerprint = CacheKey.SanitizeSegment(ContentFingerprint);
        var sanitizedVariant = CacheKey.SanitizeSegment(VariantKey);
        return Path.Combine(AssetId.ToString("N"), $"{sanitizedFingerprint}_v{Version}_{sanitizedVariant}{ext}");
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? ".bin" : (extension.StartsWith('.') ? extension : "." + extension);
}

public sealed record BannerPreviewCacheKey : ICacheKey
{
    public BannerPreviewCacheKey(Guid assetId, string contentFingerprint, int version, string parameterHash)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterHash);

        AssetId = assetId;
        ContentFingerprint = contentFingerprint.Trim();
        Version = version;
        ParameterHash = parameterHash.Trim();
    }

    public Guid AssetId { get; }

    public string ContentFingerprint { get; }

    public int Version { get; }

    public string ParameterHash { get; }

    public CacheFamily Family => CacheFamily.BannerPreviews;

    public string LogicalKey => $"{AssetId:D}/{ContentFingerprint}/{Version}/{ParameterHash}";

    public string GetRelativePath(string extension = ".bin")
    {
        var ext = NormalizeExtension(extension);
        var sanitizedFingerprint = CacheKey.SanitizeSegment(ContentFingerprint);
        var sanitizedParam = CacheKey.SanitizeSegment(ParameterHash);
        return Path.Combine(AssetId.ToString("N"), $"{sanitizedFingerprint}_v{Version}_{sanitizedParam}{ext}");
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? ".bin" : (extension.StartsWith('.') ? extension : "." + extension);
}

public sealed record FaceCropCacheKey : ICacheKey
{
    public FaceCropCacheKey(Guid faceId, string detectionFingerprint, int version, string sizeKey)
    {
        if (faceId == Guid.Empty)
        {
            throw new ArgumentException("FaceId cannot be empty.", nameof(faceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detectionFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(sizeKey);

        FaceId = faceId;
        DetectionFingerprint = detectionFingerprint.Trim();
        Version = version;
        SizeKey = sizeKey.Trim();
    }

    public Guid FaceId { get; }

    public string DetectionFingerprint { get; }

    public int Version { get; }

    public string SizeKey { get; }

    public CacheFamily Family => CacheFamily.FaceCrops;

    public string LogicalKey => $"{FaceId:D}/{DetectionFingerprint}/{Version}/{SizeKey}";

    public string GetRelativePath(string extension = ".bin")
    {
        var ext = NormalizeExtension(extension);
        var sanitizedDetection = CacheKey.SanitizeSegment(DetectionFingerprint);
        var sanitizedSize = CacheKey.SanitizeSegment(SizeKey);
        return Path.Combine(FaceId.ToString("N"), $"{sanitizedDetection}_v{Version}_{sanitizedSize}{ext}");
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? ".bin" : (extension.StartsWith('.') ? extension : "." + extension);
}

public sealed record ModelPreviewCacheKey : ICacheKey
{
    public ModelPreviewCacheKey(Guid assetId, string contentFingerprint, int version, string presetKey)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetKey);

        AssetId = assetId;
        ContentFingerprint = contentFingerprint.Trim();
        Version = version;
        PresetKey = presetKey.Trim();
    }

    public Guid AssetId { get; }

    public string ContentFingerprint { get; }

    public int Version { get; }

    public string PresetKey { get; }

    public CacheFamily Family => CacheFamily.ModelPreviews;

    public string LogicalKey => $"{AssetId:D}/{ContentFingerprint}/{Version}/{PresetKey}";

    public string GetRelativePath(string extension = ".bin")
    {
        var ext = NormalizeExtension(extension);
        var sanitizedFingerprint = CacheKey.SanitizeSegment(ContentFingerprint);
        var sanitizedPreset = CacheKey.SanitizeSegment(PresetKey);
        return Path.Combine(AssetId.ToString("N"), $"{sanitizedFingerprint}_v{Version}_{sanitizedPreset}{ext}");
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? ".bin" : (extension.StartsWith('.') ? extension : "." + extension);
}

public static class CacheKey
{

    public static string ComputeDetectionFingerprint(
        string assetContentFingerprint,
        double x,
        double y,
        double width,
        double height,
        string detectorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetContentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(detectorVersion);

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{assetContentFingerprint.Trim()}:{x:F4}:{y:F4}:{width:F4}:{height:F4}:{detectorVersion.Trim()}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string ComputeBannerParameterHash(
        double startSeconds,
        double durationSeconds,
        double focusX,
        double focusY,
        double zoom)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{startSeconds:F3}:{durationSeconds:F3}:{focusX:F4}:{focusY:F4}:{zoom:F4}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string SanitizeSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            sb.Append(invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch);
        }
        return sb.ToString();
    }
}
