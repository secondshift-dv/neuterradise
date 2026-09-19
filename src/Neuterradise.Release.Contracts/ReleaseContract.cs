using System.Reflection;
using System.Text.Json;

namespace Neuterradise.Release.Contracts;

public sealed record ReleaseContractDocument(
    int SchemaVersion,
    string ProductId,
    string RuntimeIdentifier,
    string ModelsRelativeRoot,
    IReadOnlyList<string> RequiredMembers,
    IReadOnlyList<string> RequiredUniqueFileNames,
    string FfmpegMirrorUrl);

public static class ReleaseContract
{
    private const string ResourceName = "Neuterradise.Release.Contracts.release-contract.json";
    private static readonly Lazy<ReleaseContractDocument> Authority =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ReleaseContractDocument Current => Authority.Value;

    public static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Contains('\0'))
        {
            throw new FormatException("Release contract contains an unsafe relative path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."))
        {
            throw new FormatException("Release contract contains an unsafe relative path segment.");
        }

        return string.Join('/', segments);
    }

    private static ReleaseContractDocument Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded release-contract.json is missing.");
        var document = JsonSerializer.Deserialize<ReleaseContractDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new FormatException("Release contract is empty.");

        if (document.SchemaVersion != 1)
            throw new FormatException("Unsupported release contract schema.");
        if (string.IsNullOrWhiteSpace(document.ProductId)
            || string.IsNullOrWhiteSpace(document.RuntimeIdentifier))
        {
            throw new FormatException("Release contract identity is incomplete.");
        }

        var modelsRoot = NormalizeRelativePath(document.ModelsRelativeRoot);
        var required = ValidateUnique(document.RequiredMembers, NormalizeRelativePath, "required member");
        var requiredNames = ValidateUnique(
            document.RequiredUniqueFileNames,
            value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                var trimmed = value.Trim();
                if (trimmed.Contains(Path.DirectorySeparatorChar)
                    || trimmed.Contains(Path.AltDirectorySeparatorChar)
                    || trimmed.Contains(':', StringComparison.Ordinal)
                    || trimmed.Contains('\0'))
                {
                    throw new FormatException("Release contract required file name is unsafe.");
                }

                return trimmed;
            },
            "required file name");

        if (!Uri.TryCreate(document.FfmpegMirrorUrl, UriKind.Absolute, out var mirror)
            || !string.Equals(mirror.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mirror.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Release contract FFmpeg mirror URL is invalid.");
        }

        return document with
        {
            ModelsRelativeRoot = modelsRoot,
            RequiredMembers = required,
            RequiredUniqueFileNames = requiredNames,
            FfmpegMirrorUrl = mirror.AbsoluteUri,
        };
    }

    private static IReadOnlyList<string> ValidateUnique(
        IReadOnlyList<string>? values,
        Func<string, string> normalize,
        string label)
    {
        if (values is null || values.Count == 0)
            throw new FormatException($"Release contract {label} list is empty.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var normalized = normalize(value);
            if (!seen.Add(normalized))
                throw new FormatException($"Release contract contains a duplicate {label}.");
            result.Add(normalized);
        }

        return result;
    }
}
