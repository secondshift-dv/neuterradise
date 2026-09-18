using System.IO;
using System.Text.Json;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Captures stable source filesystem identity where supported.
/// Uses available BCL APIs (full path, last write time, attributes).
/// Does not fabricate identity when unavailable — returns null instead.
/// The identity is used at cleanup time to verify the source object
/// being deleted is the same object whose identity was captured.
/// </summary>
public static class SourceIdentityHelper
{
    /// <summary>
    /// Captures source file identity as a JSON string.
    /// Returns null if the file does not exist or identity cannot be determined.
    /// </summary>
    public static string? CaptureIdentity(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var info = new FileInfo(filePath);
            info.Refresh();

            if (!info.Exists)
            {
                return null;
            }

            var identity = new SourceFileIdentity(
                info.FullName,
                info.LastWriteTimeUtc.Ticks,
                (long)info.Attributes,
                info.Length);

            return JsonSerializer.Serialize(identity, SourceFileIdentityContext.Default.SourceFileIdentity);
        }
        catch
        {
            // Identity unavailable — caller should preserve source rather than delete.
            return null;
        }
    }

    /// <summary>
    /// Compares a previously captured identity against the current file state.
    /// Returns true only when the file still represents the same logical object
    /// (same path, same last write time, same attributes).
    /// </summary>
    public static bool VerifyIdentity(string? capturedJson, string currentFilePath)
    {
        if (capturedJson is null)
        {
            return false;
        }

        try
        {
            var captured = JsonSerializer.Deserialize(capturedJson, SourceFileIdentityContext.Default.SourceFileIdentity);
            if (captured is null)
            {
                return false;
            }

            if (!File.Exists(currentFilePath))
            {
                return false;
            }

            var info = new FileInfo(currentFilePath);
            info.Refresh();

            if (!info.Exists)
            {
                return false;
            }

            return string.Equals(captured.FullPath, info.FullName, StringComparison.OrdinalIgnoreCase)
                && captured.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && captured.Attributes == (long)info.Attributes
                && captured.ByteLength == info.Length;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed record SourceFileIdentity(
    string FullPath,
    long LastWriteTicks,
    long Attributes,
    long ByteLength);

[System.Text.Json.Serialization.JsonSerializable(typeof(SourceFileIdentity))]
internal sealed partial class SourceFileIdentityContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
