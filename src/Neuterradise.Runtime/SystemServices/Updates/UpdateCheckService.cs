using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdateCheckService
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly UpdateTrustPolicy _trust;

    public UpdateCheckService(HttpClient http, UpdateTrustPolicy? trust = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _trust = trust ?? new UpdateTrustPolicy();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Uri feed,
        UpdateTrustConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.MatchesConfiguredFeed(feed))
            return UpdateCheckResult.Unavailable("Update feed does not match the configured HTTPS feed.");

        try
        {
            using var response = await _http
                .GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Unavailable("Update feed is unavailable.");

            var effectiveFeed = response.RequestMessage?.RequestUri;
            if (effectiveFeed is null || !UpdateUriPolicy.IsTrustedEffectiveUri(feed, effectiveFeed))
                return UpdateCheckResult.Unavailable("Update feed redirected outside the trusted update authority.");

            if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
                return UpdateCheckResult.Unavailable("Update metadata exceeds the safe limit.");

            var bytes = await ReadBoundedAsync(response.Content, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return UpdateCheckResult.Unavailable("Update metadata exceeds the safe limit.");

            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            var manifest = UpdateManifest.Parse(json);
            var decision = _trust.Evaluate(manifest, configuration, isLocalPackage: false, userConfirmedLocalPackage: false);

            if (decision.Accepted)
            {
                return new UpdateCheckResult(true, manifest, decision, null, false);
            }

            var isCurrent = string.Equals(
                    manifest.RuntimeIdentifier,
                    ProductIdentity.RuntimeId,
                    StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(manifest.ProductVersion, out var candidateVersion)
                && Version.TryParse(ProductIdentity.Version, out var installedVersion)
                && candidateVersion <= installedVersion;

            return isCurrent
                ? UpdateCheckResult.Current()
                : UpdateCheckResult.Unavailable(decision.Reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Cancelled();
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Unavailable("Update feed could not be reached; the offline application remains available.");
        }
        catch (DecoderFallbackException)
        {
            return UpdateCheckResult.Unavailable("Update metadata is not valid UTF-8.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Unavailable("Update metadata is invalid.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return UpdateCheckResult.Unavailable(exception.Message);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            if (total > maximumBytes)
                return null;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}

public sealed record UpdateCheckResult(
    bool IsAvailable,
    UpdateManifest? Manifest,
    UpdateTrustDecision? Decision,
    string? SafeError,
    bool IsCurrent)
{
    public static UpdateCheckResult Unavailable(string error) => new(false, null, null, error, false);
    public static UpdateCheckResult Current() => new(false, null, null, null, true);
    public static UpdateCheckResult Cancelled() => new(false, null, null, "Update check cancelled.", false);
}
