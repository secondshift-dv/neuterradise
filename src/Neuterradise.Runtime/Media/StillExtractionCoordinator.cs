using Neuterradise.Profiling.Protocol;
using System.Globalization;
using System.IO;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Media;

public sealed record DerivedStillRequest(
    Guid AssetId,
    long? TimestampMilliseconds = null,
    int MaxEdgePixels = StillExtractionCoordinator.DefaultMaxEdgePixels,
    Guid? FaceId = null,
    string? FaceDetectionKey = null,
    int? CropX = null,
    int? CropY = null,
    int? CropWidth = null,
    int? CropHeight = null)
{
    public bool IsFaceCrop =>
        FaceId is { } faceId
        && faceId != Guid.Empty
        && !string.IsNullOrWhiteSpace(FaceDetectionKey)
        && CropWidth is > 0
        && CropHeight is > 0;
}

public sealed class StillExtractionCoordinator
{
    public const int DefaultMaxEdgePixels = 512;
    public const int FaceCropMaxEdgePixels = 256;

    private readonly MediaReads _reads;
    private readonly VaultPaths _paths;
    private readonly ThumbnailCache _stills;
    private readonly FaceCropCache _faceCrops;
    private readonly Func<ProfilingEnvelope, CancellationToken, Task<ProfilingEnvelope>> _sendAsync;
    private readonly CacheVersionSet _versions;
    private readonly TimeSpan _timeout;

    public StillExtractionCoordinator(
        MediaReads reads,
        VaultPaths paths,
        ThumbnailCache stills,
        FaceCropCache faceCrops,
        Func<ProfilingEnvelope, CancellationToken, Task<ProfilingEnvelope>> sendAsync,
        CacheVersionSet? versions = null,
        TimeSpan? timeout = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _stills = stills ?? throw new ArgumentNullException(nameof(stills));
        _faceCrops = faceCrops ?? throw new ArgumentNullException(nameof(faceCrops));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        _versions = versions ?? CacheVersionSet.Default;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static string ResolveSizeKey(long? timestampMilliseconds, int maxEdgePixels) =>
        timestampMilliseconds is { } timestamp
            ? string.Create(CultureInfo.InvariantCulture, $"f{timestamp}-{maxEdgePixels}")
            : maxEdgePixels.ToString(CultureInfo.InvariantCulture);

    public string? TryResolvePublishedPath(DerivedStillRequest request, string? contentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(contentFingerprint))
        {
            return null;
        }

        var key = CreateStillKey(request, contentFingerprint);
        var lookup = _stills.Lookup(key);
        return lookup.Status == CacheLookupStatus.Hit ? lookup.PhysicalPath : null;
    }

    public async Task<string?> GetOrCreateAsync(
        DerivedStillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await _reads.GetAssetSourceAsync(request.AssetId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || source.IsRetired || string.IsNullOrWhiteSpace(source.Sha256))
        {
            return null;
        }

        if (source.MediaType is not (MediaType.Image or MediaType.Video))
        {
            return null;
        }

        var timestamp = source.MediaType == MediaType.Video ? request.TimestampMilliseconds : null;
        var normalized = request with { TimestampMilliseconds = timestamp };

        if (normalized.IsFaceCrop)
        {
            return await GetOrCreateFaceCropAsync(normalized, source, cancellationToken).ConfigureAwait(false);
        }

        var key = CreateStillKey(normalized, source.Sha256!);
        var lookup = _stills.Lookup(key);
        if (lookup.Status == CacheLookupStatus.Hit)
        {
            return lookup.PhysicalPath;
        }
        if (lookup.Status == CacheLookupStatus.InvalidOrCorrupt)
        {
            _stills.Invalidate(key);
        }

        var readablePath = source.ResolveReadablePath(_paths);
        if (readablePath is null)
        {
            return null;
        }

        IDisposable? scope;
        while (!_stills.TryBeginGeneration(key, out scope))
        {
            await _stills.WaitForGenerationAsync(key, cancellationToken).ConfigureAwait(false);
            var joined = _stills.Lookup(key);
            if (joined.Status == CacheLookupStatus.Hit)
            {
                return joined.PhysicalPath;
            }
            if (joined.Status == CacheLookupStatus.InvalidOrCorrupt)
            {
                _stills.Invalidate(key);
            }
        }

        using (scope)
        {
            var still = await RequestStillAsync(
                    source,
                    readablePath,
                    new StillExtractionRequestItem(
                        RequestKey: "still",
                        TimestampMilliseconds: timestamp,
                        Crop: null,
                        MaxEdgePixels: normalized.MaxEdgePixels),
                    cancellationToken)
                .ConfigureAwait(false);
            if (still is null)
            {
                return null;
            }

            var published = await _stills
                .StoreAsync(key, still, validator: static path => new FileInfo(path).Length > 0, ct: cancellationToken)
                .ConfigureAwait(false);
            if (!published)
            {
                return null;
            }
        }

        var republished = _stills.Lookup(key);
        return republished.Status == CacheLookupStatus.Hit ? republished.PhysicalPath : null;
    }

    private async Task<string?> GetOrCreateFaceCropAsync(
        DerivedStillRequest request,
        AssetSource source,
        CancellationToken cancellationToken)
    {
        var key = new FaceCropCacheKey(
            request.FaceId!.Value,
            request.FaceDetectionKey!,
            _versions.FaceCropVersion,
            request.MaxEdgePixels.ToString(CultureInfo.InvariantCulture));

        var lookup = _faceCrops.Lookup(key);
        if (lookup.Status == CacheLookupStatus.Hit)
        {
            return lookup.PhysicalPath;
        }
        if (lookup.Status == CacheLookupStatus.InvalidOrCorrupt)
        {
            _faceCrops.Invalidate(key);
        }

        var readablePath = source.ResolveReadablePath(_paths);
        if (readablePath is null)
        {
            return null;
        }

        IDisposable? scope;
        while (!_faceCrops.TryBeginGeneration(key, out scope))
        {
            await _faceCrops.WaitForGenerationAsync(key, cancellationToken).ConfigureAwait(false);
            var joined = _faceCrops.Lookup(key);
            if (joined.Status == CacheLookupStatus.Hit)
            {
                return joined.PhysicalPath;
            }
            if (joined.Status == CacheLookupStatus.InvalidOrCorrupt)
            {
                _faceCrops.Invalidate(key);
            }
        }

        using (scope)
        {
            var still = await RequestStillAsync(
                    source,
                    readablePath,
                    new StillExtractionRequestItem(
                        RequestKey: "face",
                        TimestampMilliseconds: request.TimestampMilliseconds,
                        Crop: new FaceBounds(
                            request.CropX ?? 0,
                            request.CropY ?? 0,
                            request.CropWidth ?? 0,
                            request.CropHeight ?? 0),
                        MaxEdgePixels: request.MaxEdgePixels),
                    cancellationToken)
                .ConfigureAwait(false);
            if (still is null)
            {
                return null;
            }

            var published = await _faceCrops
                .StoreAsync(key, still, validator: static path => new FileInfo(path).Length > 0, ct: cancellationToken)
                .ConfigureAwait(false);
            if (!published)
            {
                return null;
            }
        }

        var republished = _faceCrops.Lookup(key);
        return republished.Status == CacheLookupStatus.Hit ? republished.PhysicalPath : null;
    }

    private ThumbnailCacheKey CreateStillKey(DerivedStillRequest request, string contentFingerprint) =>
        new(
            request.AssetId,
            contentFingerprint,
            _versions.ThumbnailVersion,
            ResolveSizeKey(request.TimestampMilliseconds, request.MaxEdgePixels));

    private async Task<byte[]?> RequestStillAsync(
        AssetSource source,
        string readablePath,
        StillExtractionRequestItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_timeout);

            var envelope = ProfilingEnvelope.Create(
                ProfilingMessageType.ExtractStills,
                new ExtractStillsRequest(
                    source.AssetId,
                    readablePath,
                    source.Sha256 ?? string.Empty,
                    source.MediaType == MediaType.Video ? "Video" : "Image",
                    [item]));

            var response = await _sendAsync(envelope, bounded.Token).ConfigureAwait(false);
            if (response.MessageType != ProfilingMessageType.ExtractStillsResult)
            {
                return null;
            }

            var result = response.DeserializePayload<ExtractStillsResult>();
            var still = result?.Stills?.FirstOrDefault(
                candidate => string.Equals(candidate.RequestKey, item.RequestKey, StringComparison.Ordinal));
            if (still is null || string.IsNullOrWhiteSpace(still.JpegBase64))
            {
                return null;
            }

            return Convert.FromBase64String(still.JpegBase64);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
