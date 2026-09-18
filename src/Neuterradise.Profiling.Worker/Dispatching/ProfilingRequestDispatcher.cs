using System.Collections.Concurrent;
using Neuterradise.Profiling.Protocol;
using Neuterradise.Profiling.Worker.FaceAnalysis;
using Neuterradise.Profiling.Worker.IdentityMatching;
using Neuterradise.Profiling.Worker.StillExtraction;
using Neuterradise.Profiling.Worker.Transport;

namespace Neuterradise.Profiling.Worker.Dispatching;

public sealed class ProfilingRequestDispatcher : IDisposable
{

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequests = new();
    private readonly FaceAnalyzer _faceAnalysis;
    private readonly IdentityIndexCache _indexCache;
    private readonly IdentityMatcher _candidateMatcher;
    private readonly StillExtractor _stillFrames;

    public ProfilingRequestDispatcher(
        FaceAnalyzer? faceAnalysis = null,
        IdentityIndexCache? indexCache = null,
        StillExtractor? stillFrames = null)
    {
        _indexCache = indexCache ?? new IdentityIndexCache();
        _faceAnalysis = faceAnalysis ?? CreateDefaultFaceAnalysis();
        _candidateMatcher = new IdentityMatcher(_indexCache);
        _stillFrames = stillFrames ?? new StillExtractor();
    }

    public int LoadedIndexCount => _indexCache.LoadedIndexCount;

    public void ReleaseRuntimeResources()
    {
        foreach (var inFlight in _inFlightRequests.Values)
        {
            try
            {
                inFlight.Cancel();
            }
            catch (ObjectDisposedException)
            {

            }
        }

        _inFlightRequests.Clear();
        _indexCache.InvalidateAll();
    }

    public void Dispose()
    {
        ReleaseRuntimeResources();
        _indexCache.Dispose();
    }

    public async Task<int> RunAsync(ProfilingClientTransport client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            return await RunCoreAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseRuntimeResources();
        }
    }

    private async Task<int> RunCoreAsync(ProfilingClientTransport client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ProfilingEnvelope? envelope;
            try
            {
                envelope = await client.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {

                break;
            }

            if (envelope is null)
            {

                break;
            }

            if (envelope.ProtocolVersion != ProfilingProtocolVersion.Current)
            {
                var versionError = new ErrorPayload(
                    "PROTOCOL_VERSION_MISMATCH",
                    $"Protocol version {envelope.ProtocolVersion} is incompatible with Profiling Worker protocol version {ProfilingProtocolVersion.Current}.",
                    true);
                var versionErrorEnvelope = ProfilingEnvelope.Create(
                    ProfilingMessageType.Error,
                    versionError,
                    envelope.RequestId);
                await client.WriteEnvelopeAsync(versionErrorEnvelope, cancellationToken).ConfigureAwait(false);
                return 1;
            }

            if (envelope.MessageType == ProfilingMessageType.Shutdown)
            {

                return 0;
            }

            if (envelope.MessageType == ProfilingMessageType.Ping)
            {
                var ping = envelope.DeserializePayload<PingPayload>();
                var pong = new PongPayload(ping?.TimestampUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var response = ProfilingEnvelope.Create(ProfilingMessageType.Pong, pong, envelope.RequestId);
                await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.CancelRequest)
            {
                var cancel = envelope.DeserializePayload<CancelRequestPayload>();
                if (cancel is not null && _inFlightRequests.TryGetValue(cancel.TargetRequestId, out var cts))
                {
                    cts.Cancel();
                }
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.Hello)
            {
                await WriteErrorAsync(client, envelope, "UNEXPECTED_HELLO", "Hello is only valid during the initial handshake.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.AnalyzeFaces)
            {
                await HandleAnalyzeFacesAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.ExtractStills)
            {
                await HandleExtractStillsAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.BuildIdentityIndex)
            {
                await HandleBuildIdentityIndexAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.MatchIdentityCandidates)
            {
                await HandleMatchIdentityCandidatesAsync(client, envelope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (envelope.MessageType == ProfilingMessageType.ReleaseIndex)
            {
                var release = envelope.DeserializePayload<ReleaseIdentityIndexRequest>();
                if (release is not null)
                {
                    _indexCache.Invalidate(release.EmbeddingSpaceKey);
                }
                continue;
            }

            var error = new ErrorPayload("UNKNOWN_MESSAGE_TYPE", $"Message type {envelope.MessageType} is not supported in this build.", false);
            var errorEnv = ProfilingEnvelope.Create(ProfilingMessageType.Error, error, envelope.RequestId);
            await client.WriteEnvelopeAsync(errorEnv, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task HandleAnalyzeFacesAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        FaceAnalysisRequest? request;
        try
        {
            request = envelope.DeserializePayload<FaceAnalysisRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"AnalyzeFaces payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "AnalyzeFaces payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        FaceAnalysisResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inFlightRequests[envelope.RequestId] = cts;
            try
            {
                result = _faceAnalysis.Analyze(request, envelope.RequestId, cts.Token);
            }
            finally
            {
                _inFlightRequests.TryRemove(envelope.RequestId, out _);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(client, envelope, "CANCELLED", "AnalyzeFaces request was cancelled.", cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "FACE_ANALYSIS_FAILED", $"Face analysis failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = ProfilingEnvelope.Create(ProfilingMessageType.AnalyzeFacesResult, result, envelope.RequestId);
        await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleExtractStillsAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        ExtractStillsRequest? request;
        try
        {
            request = envelope.DeserializePayload<ExtractStillsRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"ExtractStills payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "ExtractStills payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        ExtractStillsResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inFlightRequests[envelope.RequestId] = cts;
            try
            {
                result = _stillFrames.Extract(request, cts.Token);
            }
            finally
            {
                _inFlightRequests.TryRemove(envelope.RequestId, out _);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(client, envelope, "CANCELLED", "ExtractStills request was cancelled.", cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "STILL_EXTRACTION_FAILED", $"Still extraction failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = ProfilingEnvelope.Create(ProfilingMessageType.ExtractStillsResult, result, envelope.RequestId);
        await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBuildIdentityIndexAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        BuildIdentityIndexRequest? request;
        try
        {
            request = envelope.DeserializePayload<BuildIdentityIndexRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"BuildIdentityIndex payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "BuildIdentityIndex payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var index = _indexCache.GetOrBuild(
            request.EmbeddingSpaceKey,
            request.ModelId,
            request.ModelVersion,
            request.Samples);

        var result = new BuildIdentityIndexResult
        {
            EmbeddingSpaceKey = index.EmbeddingSpaceKey,
            IdentityCount = index.IdentityCount,
            SampleCount = index.SampleCount,
            ExcludedSampleCount = request.Samples.Count - index.SampleCount,
            Diagnostics = index.Diagnostics
        };

        var response = ProfilingEnvelope.Create(ProfilingMessageType.BuildIdentityIndexResult, result, envelope.RequestId);
        await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMatchIdentityCandidatesAsync(ProfilingClientTransport client, ProfilingEnvelope envelope, CancellationToken cancellationToken)
    {
        MatchIdentityCandidatesRequest? request;
        try
        {
            request = envelope.DeserializePayload<MatchIdentityCandidatesRequest>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", $"MatchIdentityCandidates payload is missing or malformed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(client, envelope, "INVALID_PAYLOAD", "MatchIdentityCandidates payload is missing or malformed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = _candidateMatcher.Match(request);
            var response = ProfilingEnvelope.Create(ProfilingMessageType.MatchIdentityCandidatesResult, result, envelope.RequestId);
            await client.WriteEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (IdentitySpaceMismatchException ex)
        {
            await WriteErrorAsync(client, envelope, "EMBEDDING_SPACE_MISMATCH", ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(
        ProfilingClientTransport client,
        ProfilingEnvelope envelope,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var error = new ErrorPayload(errorCode, errorMessage, false);
        var errorEnv = ProfilingEnvelope.Create(ProfilingMessageType.Error, error, envelope.RequestId);
        await client.WriteEnvelopeAsync(errorEnv, cancellationToken).ConfigureAwait(false);
    }

    private static FaceAnalyzer CreateDefaultFaceAnalysis() =>
        new(
            new OpenCvFaceImageSource(),
            CreateYuNetAdapter,
            CreateSFaceAdapter);

    private static IFaceDetectorAdapter CreateYuNetAdapter(FaceAnalysisRequest request)
    {
        var path = ResolveModelPath(
            request.YuNetModelId,
            request.YuNetModelVersion,
            YuNetFaceDetector.ModelId,
            YuNetFaceDetector.ModelVersion,
            YuNetFaceDetector.ArtifactFileName);
        try
        {
            return new YuNetFaceDetector(path);
        }
        catch (FaceModelsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FaceModelsUnavailableException($"YuNet model could not be loaded: {ex.Message}");
        }
    }

    private static IFaceEmbeddingAdapter CreateSFaceAdapter(FaceAnalysisRequest request)
    {
        var path = ResolveModelPath(
            request.SFaceModelId,
            request.SFaceModelVersion,
            SFaceEmbeddingExtractor.ModelId,
            SFaceEmbeddingExtractor.ModelVersion,
            SFaceEmbeddingExtractor.ArtifactFileName);
        try
        {
            return new SFaceEmbeddingExtractor(path);
        }
        catch (FaceModelsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FaceModelsUnavailableException($"SFace model could not be loaded: {ex.Message}");
        }
    }

    private static string ResolveModelPath(
        string modelId,
        string modelVersion,
        string expectedModelId,
        string expectedModelVersion,
        string artifactFileName)
    {
        if (!string.Equals(modelId, expectedModelId, StringComparison.Ordinal)
            || !string.Equals(modelVersion, expectedModelVersion, StringComparison.Ordinal))
        {
            throw new FaceModelsUnavailableException(
                $"Model {modelId}/{modelVersion} is not provisioned in this Profiling Worker build; expected {expectedModelId}/{expectedModelVersion}.");
        }

        return WorkerRuntimeEnvironment.Current.ResolveModelPath(modelId, artifactFileName);
    }
}
