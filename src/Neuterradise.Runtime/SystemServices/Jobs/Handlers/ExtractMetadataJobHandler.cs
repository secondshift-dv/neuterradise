using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.MediaTools;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IExtractMetadataJobOperation : IAuthorizedJobOperation;

public sealed class ExtractMetadataJobHandler : AuthorizedJobHandler
{
    public ExtractMetadataJobHandler(IExtractMetadataJobOperation operation)
        : base(
            "ExtractMetadata",
            [JobLane.Cpu, JobLane.Media],
            "Asset",
            operation,
            runOffCallingThread: true)
    {
    }
}

/// <summary>
/// Immutable execution identity for a video-preview generation.  Created in
/// <see cref="ProductionMediaToolPlanSource.ResolveGenerateVideoPreviewAsync"/> and carried
/// through the plan so that <c>PublishAsync</c> never reconstructs the cache key from mutable
/// database state.  The temp output path is deterministic and uniquely keyed by the generation
/// identity so that concurrent different-key generations for the same asset cannot collide.
/// </summary>
public sealed record VideoPublicationContext(
    VideoPreviewCacheKey CacheKey,
    string ExpectedFingerprint,
    string TempOutputPath);

public sealed record AuthorizedMediaToolPlan(
    bool AlreadyCompleted,
    ProcessRunRequest? Request,
    JobFailureClassification NonZeroExitClassification =
        JobFailureClassification.DeterministicInvalidInput,
    /// <summary>
    /// Execution-scoped disposable that the caller (<see cref="ExternalMediaToolJobOperation"/>)
    /// owns and guarantees to release in a finally block. Used by video-preview single-flight to
    /// hold the <c>VideoPreviewCache.TryBeginGeneration</c> scope across the full
    /// Resolve → external-tool → Publish pipeline. <c>null</c> when the plan carries no lease.
    /// </summary>
    IDisposable? GenerationLease = null,
    /// <summary>
    /// Immutable video publication identity — set only for <c>GenerateVideoPreview</c> plans.
    /// <c>null</c> for metadata extraction and model-preview plans.
    /// </summary>
    VideoPublicationContext? VideoContext = null);

public interface IAuthorizedMediaToolPlanSource
{
    Task<AuthorizedMediaToolPlan> ResolveAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken);

    Task<JobExecutionResult> PublishAsync(
        JobExecutionContext context,
        AuthorizedMediaToolPlan plan,
        ProcessRunResult processResult,
        CancellationToken cancellationToken);
}

public sealed class ExternalMediaToolJobOperation :
    IExtractMetadataJobOperation,
    IGenerateVideoPreviewJobOperation,
    IGenerateModelPreviewJobOperation
{
    private readonly IProcessLauncher _launcher;
    private readonly IAuthorizedMediaToolPlanSource _plans;

    public ExternalMediaToolJobOperation(
        IProcessLauncher launcher,
        IAuthorizedMediaToolPlanSource plans)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        // The plan may carry an execution-scoped generation lease (video-preview single-flight).
        // Declare outside the try so the finally can guarantee disposal on every exit path.
        AuthorizedMediaToolPlan? plan = null;
        try
        {
            plan = await _plans.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
            if (plan.AlreadyCompleted)
            {
                return JobExecutionResult.Succeeded;
            }

            if (plan.Request is null)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_PLAN_INVALID",
                    "Persisted authority did not resolve an external-tool request.");
            }

            if (!Enum.IsDefined(plan.NonZeroExitClassification)
                || plan.NonZeroExitClassification == JobFailureClassification.Cancelled)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_PLAN_INVALID",
                    "The external-tool plan supplied an invalid exit classification.");
            }

            await context.Progress.ReportProgressAsync(0, 1, "External media tool", cancellationToken)
                .ConfigureAwait(false);
            var processResult = await _launcher.RunAsync(plan.Request, cancellationToken)
                .ConfigureAwait(false);
            if (processResult.TimedOut)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.ToolLaunchTransient,
                    "MEDIA_TOOL_TIMEOUT",
                    "The external media tool exceeded its bounded timeout.");
            }

            if (processResult.ExitCode != 0)
            {
                return JobExecutionResult.Failed(
                    plan.NonZeroExitClassification,
                    "MEDIA_TOOL_EXIT_FAILED",
                    $"The external media tool exited with code {processResult.ExitCode}.");
            }

            var publishResult = await _plans.PublishAsync(context, plan, processResult, cancellationToken)
                .ConfigureAwait(false);
            if (publishResult.IsSucceeded)
            {
                await context.Progress.ReportProgressAsync(1, 1, "Published", cancellationToken)
                    .ConfigureAwait(false);
            }

            return publishResult;
        }
        catch (MediaToolPlanRefusedException refusal)
        {
            return JobExecutionResult.Failed(
                refusal.Classification,
                refusal.ErrorCode,
                refusal.SafeDetail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobExecutionResult.Cancelled("The external media tool was stopped at a safe boundary.");
        }
        catch (FileNotFoundException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "MEDIA_TOOL_MISSING",
                "The approved external media tool is not present.");
        }
        catch (UnauthorizedAccessException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "MEDIA_TOOL_ACCESS_DENIED",
                "The media source cannot currently be read.");
        }
        catch (System.Security.SecurityException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.AccessTemporarilyDenied,
                "MEDIA_TOOL_SECURITY_DENIED",
                "The media source cannot be read due to security permissions.");
        }
        catch (IOException)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "MEDIA_TOOL_IO_FAILED",
                "An I/O error occurred while reading the media source.");
        }
        catch (JsonException exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_CORRUPT",
                $"The media tool output was not valid JSON: {exception.Message}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.MissingRequiredToolOrModel,
                "MEDIA_TOOL_MISSING",
                "The approved external media tool is not present.");
        }
        catch (Win32Exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.ToolLaunchTransient,
                "MEDIA_TOOL_LAUNCH_FAILED",
                "The approved external media tool could not be launched.");
        }
        finally
        {
            // The generation lease (e.g. video-preview single-flight scope) is owned by this
            // execution, not by the shared plan source.  Guarantee release on every exit path:
            // success, timeout, non-zero exit, cancellation, exception.
            plan?.GenerationLease?.Dispose();
        }
    }
}

public sealed class ProductionMediaToolPlanSource : IAuthorizedMediaToolPlanSource
{
    private const int _checkpointSchemaVersion = 1;

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    // A 60-second user-selectable derivative is intentionally allowed more than realtime on slow
    // machines while remaining bounded and cancellable.
    public static readonly TimeSpan VideoPreviewTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The derivative provides the complete user-selectable preview window. Playback duration is
    /// still decided by the persisted 1-60 second preference in HoverVideoCoordinator; generating
    /// the maximum window prevents the cache asset from silently truncating that visible contract.
    /// </summary>
    public const string VideoPreviewDurationSeconds = "60";

    public const int VideoPreviewMaxEdgePixels = 480;

    // Variant change intentionally invalidates the historical 5-second silent derivative.
    public const string VideoPreviewVariantKey = "hover-60s-480-av";

    private readonly MediaReads _reads;
    private readonly AssetWrites _writes;
    private readonly VaultPaths _paths;
    private readonly ExternalToolResolver _tools;
    private readonly ModelPreviewAdapterRegistry _modelAdapters;
    private readonly VideoPreviewCache _videoPreviews;
    private readonly CacheVersionSet _versions;
    private readonly ImageMetadataAdapter _imageMetadata = new();

    public ProductionMediaToolPlanSource(
        MediaReads reads,
        AssetWrites writes,
        VaultPaths paths,
        ExternalToolResolver tools,
        ModelPreviewAdapterRegistry modelAdapters,
        VideoPreviewCache videoPreviews,
        CacheVersionSet? versions = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _modelAdapters = modelAdapters ?? throw new ArgumentNullException(nameof(modelAdapters));
        _videoPreviews = videoPreviews ?? throw new ArgumentNullException(nameof(videoPreviews));
        _versions = versions ?? CacheVersionSet.Default;
    }

    public async Task<AuthorizedMediaToolPlan> ResolveAsync(
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = await _reads.GetAssetSourceAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || source.IsRetired)
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OWNER_UNUSABLE",
                "The persisted job owner is not an Asset a derivation may be published for.");
        }

        var readablePath = source.ResolveManagedPath(_paths);
        if (readablePath is null)
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.ContentMismatch,
                "MEDIA_TOOL_SOURCE_MISSING",
                "The canonical managed media file is not available.");
        }

        return context.Kind switch
        {
            "ExtractMetadata" => await ResolveExtractMetadataAsync(
                context,
                source,
                readablePath,
                cancellationToken).ConfigureAwait(false),
            "GenerateVideoPreview" => await ResolveGenerateVideoPreviewAsync(
                context,
                source,
                readablePath,
                cancellationToken).ConfigureAwait(false),
            "GenerateModelPreview" => await ResolveGenerateModelPreviewAsync(
                context,
                source,
                readablePath,
                cancellationToken).ConfigureAwait(false),
            _ => throw new MediaToolPlanRefusedException(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_KIND_UNSUPPORTED",
                $"'{context.Kind}' is not a kind this plan source serves."),
        };
    }

    public async Task<JobExecutionResult> PublishAsync(
        JobExecutionContext context,
        AuthorizedMediaToolPlan plan,
        ProcessRunResult processResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(processResult);

        if (string.Equals(context.Kind, "GenerateVideoPreview", StringComparison.Ordinal))
        {
            if (plan.VideoContext is null)
            {
                return JobExecutionResult.Failed(
                    JobFailureClassification.DeterministicInvalidInput,
                    "VIDEO_PREVIEW_CONTEXT_MISSING",
                    "The GenerateVideoPreview plan did not carry a video publication context.");
            }

            return await PublishVideoPreviewAsync(context, plan.VideoContext, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(context.Kind, "ExtractMetadata", StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_PUBLISH_UNSUPPORTED",
                $"'{context.Kind}' produced external-tool output that no publisher owns.");
        }

        VideoMetadata metadata;
        try
        {
            metadata = VideoMetadataAdapter.ParseJson(processResult.StandardOutput);
        }
        catch (JsonException exception)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_CORRUPT",
                $"The video probe output was not valid JSON: {exception.Message}");
        }

        if (metadata.Width is null && metadata.Duration is null && metadata.Container is null)
        {
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_OUTPUT_UNUSABLE",
                "The video probe produced no usable typed metadata.");
        }

        await _writes.SaveMetadataAsync(
                context.OwnerId,
                metadata.Width,
                metadata.Height,
                metadata.Duration is { } duration
                    ? (int)Math.Min(int.MaxValue, duration.TotalMilliseconds)
                    : null,
                metadata.CapturedAt,
                VideoMetadataAdapter.Serialize(metadata),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await WriteCheckpointAsync(context, "video-ffprobe", cancellationToken).ConfigureAwait(false);
        return JobExecutionResult.Succeeded;
    }

    private async Task<AuthorizedMediaToolPlan> ResolveExtractMetadataAsync(
        JobExecutionContext context,
        AssetSource source,
        string readablePath,
        CancellationToken cancellationToken)
    {
        switch (source.MediaType)
        {
            case MediaType.Image:
            {
                ImageMetadata metadata;
                try
                {
                    metadata = _imageMetadata.ExtractFromFile(readablePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.DeterministicInvalidInput,
                        "IMAGE_METADATA_UNUSABLE",
                        $"Image metadata extraction failed: {exception.Message}");
                }

                await _writes.SaveMetadataAsync(
                        context.OwnerId,
                        metadata.Width,
                        metadata.Height,
                        durationMs: null,
                        metadata.CapturedAt,
                        ImageMetadataAdapter.Serialize(metadata),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await WriteCheckpointAsync(
                        context,
                        "image-inprocess",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }

            case MediaType.Model:
            {
                ModelMetadata probe;
                try
                {
                    probe = await _modelAdapters
                        .ProbeAsync(
                            new ModelProbeInput(readablePath, FileSizeBytes: source.ByteLength ?? 0),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.DeterministicInvalidInput,
                        "MODEL_PROBE_FAILED",
                        $"Model metadata probe failed: {exception.Message}");
                }

                await _writes.SaveMetadataAsync(context.OwnerId, probe, cancellationToken)
                    .ConfigureAwait(false);
                await WriteCheckpointAsync(
                        context,
                        $"model-{probe.AdapterId}",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }

            case MediaType.Video:
            {
                var resolution = _tools.Resolve(ExternalToolResolver.FfprobeToolId);
                if (!resolution.IsUsable)
                {
                    throw new MediaToolPlanRefusedException(
                        JobFailureClassification.MissingRequiredToolOrModel,
                        "MEDIA_TOOL_UNAVAILABLE",
                        resolution.Reason);
                }

                return new AuthorizedMediaToolPlan(
                    AlreadyCompleted: false,
                    Request: new ProcessRunRequest(
                        ExecutablePath: resolution.ResolvedPath!,
                        Arguments:
                        [
                            "-v", "quiet",
                            "-print_format", "json",
                            "-show_format",
                            "-show_streams",
                            readablePath,
                        ],
                        Timeout: ProbeTimeout),
                    NonZeroExitClassification: JobFailureClassification.DeterministicInvalidInput);
            }

            default:
                throw new MediaToolPlanRefusedException(
                    JobFailureClassification.DeterministicInvalidInput,
                    "MEDIA_TOOL_MEDIA_TYPE_UNSUPPORTED",
                    $"'{source.MediaType}' has no metadata derivation.");
        }
    }

    /// <summary>
    /// Builds a bounded, cancellable ffmpeg derivation of the complete configurable hover-preview
    /// window from authoritative managed video. The output keeps an optional audio stream so the
    /// persisted mute/unmute preference remains meaningful; the shared player decides how much of
    /// the 60-second derivative to play.
    /// </summary>
    private async Task<AuthorizedMediaToolPlan> ResolveGenerateVideoPreviewAsync(
        JobExecutionContext context,
        AssetSource source,
        string readablePath,
        CancellationToken cancellationToken)
    {
        if (source.MediaType != MediaType.Video)
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.DeterministicInvalidInput,
                "MEDIA_TOOL_MEDIA_TYPE_UNSUPPORTED",
                $"'{source.MediaType}' has no video-preview derivation.");
        }

        if (string.IsNullOrWhiteSpace(source.Sha256))
        {
            throw new MediaToolPlanRefusedException(
                JobFailureClassification.AmbiguousPhysicalState,
                "VIDEO_PREVIEW_FINGERPRINT_MISSING",
                "The Asset has no authoritative content fingerprint to key the derivation by.");
        }

        var key = new VideoPreviewCacheKey(
            context.OwnerId,
            source.Sha256,
            _versions.VideoPreviewVersion,
            VideoPreviewVariantKey);

        if (_videoPreviews.Lookup(key).Status == CacheLookupStatus.Hit)
        {
            await WriteCheckpointAsync(context, "video-preview-cached", cancellationToken)
                .ConfigureAwait(false);
            return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
        }

        IDisposable? scope;
        while (!_videoPreviews.TryBeginGeneration(key, out scope))
        {
            await _videoPreviews.WaitForGenerationAsync(key, cancellationToken).ConfigureAwait(false);
            var joined = _videoPreviews.Lookup(key);
            if (joined.Status == CacheLookupStatus.Hit)
            {
                await WriteCheckpointAsync(context, "video-preview-cached", cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }
            if (joined.Status == CacheLookupStatus.InvalidOrCorrupt)
            {
                _videoPreviews.Invalidate(key);
            }
        }

        try
        {
            if (_videoPreviews.Lookup(key).Status == CacheLookupStatus.Hit)
            {
                scope.Dispose();
                await WriteCheckpointAsync(context, "video-preview-cached", cancellationToken)
                    .ConfigureAwait(false);
                return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
            }

            var resolution = _tools.Resolve(ExternalToolResolver.FfmpegToolId);
            if (!resolution.IsUsable)
            {
                throw new MediaToolPlanRefusedException(
                    JobFailureClassification.MissingRequiredToolOrModel,
                    "MEDIA_TOOL_UNAVAILABLE",
                    resolution.Reason);
            }

            var videoContext = new VideoPublicationContext(
                CacheKey: key,
                ExpectedFingerprint: source.Sha256,
                TempOutputPath: ResolveVideoPreviewTempPath(key));

            TryDeleteQuiet(videoContext.TempOutputPath);

            return new AuthorizedMediaToolPlan(
                AlreadyCompleted: false,
                Request: new ProcessRunRequest(
                    ExecutablePath: resolution.ResolvedPath!,
                    Arguments:
                    [
                        "-y",
                        "-i", readablePath,
                        "-t", VideoPreviewDurationSeconds,
                        "-map", "0:v:0",
                        "-map", "0:a?",
                        "-vf", $"scale='min({VideoPreviewMaxEdgePixels},iw)':-2",
                        "-c:v", "libx264",
                        "-preset", "veryfast",
                        "-c:a", "aac",
                        "-b:a", "128k",
                        "-movflags", "+faststart",
                        videoContext.TempOutputPath,
                    ],
                    Timeout: VideoPreviewTimeout),
                NonZeroExitClassification: JobFailureClassification.DeterministicInvalidInput,
                GenerationLease: scope,
                VideoContext: videoContext);
        }
        catch
        {
            scope?.Dispose();
            throw;
        }
    }

    private async Task<JobExecutionResult> PublishVideoPreviewAsync(
        JobExecutionContext context,
        VideoPublicationContext videoContext,
        CancellationToken cancellationToken)
    {
        // NOTE: the generation lease is owned and released by the caller
        // (ExternalMediaToolJobOperation.ExecuteAsync), not by this method.

        var tempOutputPath = videoContext.TempOutputPath;
        if (!File.Exists(tempOutputPath) || new FileInfo(tempOutputPath).Length == 0)
        {
            TryDeleteQuiet(tempOutputPath);
            return JobExecutionResult.Failed(
                JobFailureClassification.DeterministicInvalidInput,
                "VIDEO_PREVIEW_EMPTY",
                "ffmpeg produced no preview bytes for this video.");
        }

        var currentSource = await _reads.GetAssetSourceAsync(context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (currentSource is null || !string.Equals(
                currentSource.Sha256,
                videoContext.ExpectedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteQuiet(tempOutputPath);
            return JobExecutionResult.Failed(
                JobFailureClassification.ContentMismatch,
                "VIDEO_PREVIEW_STALE",
                "The asset's content fingerprint changed while the video preview was being generated.");
        }

        var published = _videoPreviews.PublishFromTempFile(
            videoContext.CacheKey,
            tempOutputPath,
            validator: static path => new FileInfo(path).Length > 0);
        if (!published)
        {
            TryDeleteQuiet(tempOutputPath);
            return JobExecutionResult.Failed(
                JobFailureClassification.TransientIo,
                "VIDEO_PREVIEW_PUBLISH_REFUSED",
                "The persistent cache did not accept the generated video preview.");
        }

        await WriteCheckpointAsync(context, "video-ffmpeg-preview", cancellationToken).ConfigureAwait(false);
        return JobExecutionResult.Succeeded;
    }

    private string ResolveVideoPreviewTempPath(VideoPreviewCacheKey key)
    {
        Directory.CreateDirectory(_videoPreviews.Paths.TempPath);
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.LogicalKey)));
        return Path.Combine(_videoPreviews.Paths.TempPath, $"{key.AssetId:N}-{keyHash[..16]}-preview.mp4");
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<AuthorizedMediaToolPlan> ResolveGenerateModelPreviewAsync(
        JobExecutionContext context,
        AssetSource source,
        string readablePath,
        CancellationToken cancellationToken)
    {
        var hasMetadata = await _reads.HasModelMetadataAsync(
                context.OwnerId, cancellationToken)
            .ConfigureAwait(false);
        if (!hasMetadata)
        {
            var probe = await _modelAdapters
                .ProbeAsync(
                    new ModelProbeInput(readablePath, FileSizeBytes: source.ByteLength ?? 0),
                    cancellationToken)
                .ConfigureAwait(false);
            await _writes.SaveMetadataAsync(context.OwnerId, probe, cancellationToken)
                .ConfigureAwait(false);
        }

        var descriptor = await _modelAdapters
            .GetOrGeneratePreviewAsync(
                new ModelPreviewRequest(context.OwnerId, readablePath),
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(descriptor.StaticThumbnailPath))
        {
            await WriteCheckpointAsync(
                    context,
                    $"model-preview-{descriptor.AdapterId}",
                    cancellationToken)
                .ConfigureAwait(false);
            return new AuthorizedMediaToolPlan(AlreadyCompleted: true, Request: null);
        }

        throw new MediaToolPlanRefusedException(
            JobFailureClassification.MissingRequiredToolOrModel,
            "MEDIA_TOOL_NOT_DECLARED",
            $"Adapter '{descriptor.AdapterId}' probed the model but this deployment has no approved MODEL renderer.");
    }

    private static Task WriteCheckpointAsync(
        JobExecutionContext context,
        string derivation,
        CancellationToken cancellationToken)
    {
        var checkpoint = JsonSerializer.Serialize(new
        {
            schemaVersion = _checkpointSchemaVersion,
            assetId = context.OwnerId,
            kind = context.Kind,
            derivation,
            published = true,
        });

        return context.Checkpoints.UpdateCheckpointAsync(checkpoint, cancellationToken);
    }
}

public sealed class MediaToolPlanRefusedException : InvalidOperationException
{
    public MediaToolPlanRefusedException(
        JobFailureClassification classification,
        string errorCode,
        string safeDetail)
        : base(safeDetail)
    {
        Classification = classification;
        ErrorCode = errorCode;
        SafeDetail = safeDetail;
    }

    public JobFailureClassification Classification { get; }
    public string ErrorCode { get; }
    public string SafeDetail { get; }
}
