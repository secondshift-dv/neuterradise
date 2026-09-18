using System.IO;
using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateThumbnailJobOperation : IAuthorizedJobOperation;

public sealed class GenerateThumbnailJobHandler : AuthorizedJobHandler
{
    public GenerateThumbnailJobHandler(IGenerateThumbnailJobOperation operation)
        : base("GenerateThumbnail", [JobLane.Cpu], "Asset", operation, runOffCallingThread: true)
    {
    }
}

public sealed class ImageThumbnailJobOperation : IGenerateThumbnailJobOperation
{

    public const string SizeKey = "512";

    public const int MaxEdgePixels = 512;

    private const int _checkpointSchemaVersion = 1;
    private const int _jpegQuality = 85;

    private readonly MediaReads _reads;
    private readonly ThumbnailCache _cache;
    private readonly VaultPaths _paths;
    private readonly CacheVersionSet _versions;

    public ImageThumbnailJobOperation(
        MediaReads reads,
        ThumbnailCache cache,
        VaultPaths paths,
        CacheVersionSet? versions = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _versions = versions ?? CacheVersionSet.Default;
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = await _reads.GetAssetSourceAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "THUMBNAIL_OWNER_MISSING",
                "The persisted job owner does not identify an Asset.");
        }

        if (source.IsRetired)
        {
            // Skipped, cancelled or de-duplicated media: the thumbnail no longer applies. That is a
            // cancellation, not a failure, and must not inflate the failed-job count.
            return JobExecutionResult.Cancelled("The media left the import before its thumbnail was generated.");
        }

        if (source.MediaType != MediaType.Image)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "THUMBNAIL_MEDIA_TYPE_UNSUPPORTED",
                "GenerateThumbnail is the IMAGE derivation; other media types have their own kinds.");
        }

        if (string.IsNullOrWhiteSpace(source.Sha256))
        {

            return JobExecutionResult.Failed(
                JobFailureClassification.AmbiguousPhysicalState,
                "THUMBNAIL_FINGERPRINT_MISSING",
                "The Asset has no authoritative content fingerprint to key the derivation by.");
        }

        var key = new ThumbnailCacheKey(
            source.AssetId,
            source.Sha256,
            _versions.ThumbnailVersion,
            SizeKey);

        // Join the same single-flight authority as StillExtractionCoordinator and all other
        // thumbnail consumers. For the same ThumbnailCacheKey, at most one decode runs at a time.
        IDisposable? scope;
        while (!_cache.TryBeginGeneration(key, out scope))
        {
            await _cache.WaitForGenerationAsync(key, cancellationToken).ConfigureAwait(false);
            var joined = _cache.Lookup(key);
            if (joined.Status == CacheLookupStatus.Hit)
            {
                await WriteCheckpointAsync(context, key, published: true, cancellationToken).ConfigureAwait(false);
                return JobExecutionResult.Succeeded;
            }
            if (joined.Status == CacheLookupStatus.InvalidOrCorrupt)
            {
                _cache.Invalidate(key);
            }
        }

        using (scope)
        {
            // Re-check after acquiring generation lock.
            if (_cache.Lookup(key).Status == CacheLookupStatus.Hit)
            {
                await WriteCheckpointAsync(context, key, published: true, cancellationToken).ConfigureAwait(false);
                return JobExecutionResult.Succeeded;
            }

            var readablePath = source.ResolveManagedPath(_paths);
            if (readablePath is null)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.ContentMismatch,
                    "THUMBNAIL_SOURCE_MISSING",
                    "The canonical managed media file is not available.");
            }

            try
            {
                await context.Progress.ReportProgressAsync(0, 1, "Rendering thumbnail", cancellationToken)
                    .ConfigureAwait(false);

                var encoded = Encode(readablePath, cancellationToken);
                if (encoded.Length == 0)
                {
                    return JobExecutionResult.Failed(
                        JobFailureClassification.DeterministicInvalidInput,
                        "THUMBNAIL_ENCODE_EMPTY",
                        "The decoder produced no thumbnail bytes for this image.");
                }

                var published = await _cache
                    .StoreAsync(key, encoded, validator: static path => new FileInfo(path).Length > 0, ct: cancellationToken)
                    .ConfigureAwait(false);
                if (!published)
                {

                    return JobExecutionResult.Failed(
                        JobFailureClassification.TransientIo,
                        "THUMBNAIL_PUBLISH_REFUSED",
                        "The persistent cache did not accept the generated thumbnail.");
                }

                await WriteCheckpointAsync(context, key, published: true, cancellationToken).ConfigureAwait(false);
                await context.Progress.ReportProgressAsync(1, 1, "Published", cancellationToken)
                    .ConfigureAwait(false);
                return JobExecutionResult.Succeeded;
            }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("Thumbnail generation reached a safe cancellation boundary.");
        }
        catch (FileNotFoundException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ContentMismatch,
                "THUMBNAIL_SOURCE_MISSING",
                "The image the thumbnail derives from no longer exists.");
        }
        catch (UnauthorizedAccessException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "THUMBNAIL_SOURCE_ACCESS_DENIED",
                "The image the thumbnail derives from cannot currently be read.");
        }
        catch (System.Security.SecurityException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "THUMBNAIL_SOURCE_SECURITY_DENIED",
                "The image the thumbnail derives from cannot be read due to security permissions.");
        }
        catch (IOException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "THUMBNAIL_SOURCE_IO_FAILED",
                "The image the thumbnail derives from could not be read.");
        }
        catch (NotSupportedException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "THUMBNAIL_FORMAT_UNSUPPORTED",
                "No installed image decoder can read this file.");
        }
        catch (ArgumentException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "THUMBNAIL_DECODE_FAILED",
                "The image could not be decoded into a thumbnail.");
        }
        catch (OverflowException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "THUMBNAIL_DECODE_FAILED",
                "The image reports dimensions a thumbnail cannot be derived from.");
        }
    }

    private static ReadOnlyMemory<byte> Encode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var bitmap = TargetSizeDecoder.Decode(path, MaxEdgePixels, MaxEdgePixels)
            ?? throw new NotSupportedException("No installed image decoder can read this file.");

        cancellationToken.ThrowIfCancellationRequested();

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, _jpegQuality)
            ?? throw new InvalidDataException("The image could not be encoded into a thumbnail.");
        return encoded.ToArray();
    }

    private static Task WriteCheckpointAsync(
        JobExecutionContext context,
        ThumbnailCacheKey key,
        bool published,
        CancellationToken cancellationToken)
    {
        var checkpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = _checkpointSchemaVersion,
            assetId = key.AssetId,
            derivationKey = key.LogicalKey,
            sizeKey = key.SizeKey,
            version = key.Version,
            published,
        });

        return context.Checkpoints.UpdateCheckpointAsync(checkpoint, cancellationToken);
    }
}
