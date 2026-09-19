using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Operations;

using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Settings;

public static class TaxonomyNamePolicy
{

    public const int MaximumLength = 64;

    public static string? TryNormalize(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = CollapseWhitespace(name);
        if (collapsed.Length == 0)
        {
            return null;
        }

        var canonical = collapsed.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return canonical.Length is 0 or > MaximumLength ? null : canonical;
    }

    public static string? TryNormalizeDisplayName(string? name)
    {
        var collapsed = name is null ? string.Empty : CollapseWhitespace(name);
        if (collapsed.Length == 0)
        {
            return null;
        }

        var display = collapsed.Normalize(NormalizationForm.FormKC);
        return display.Length > MaximumLength ? null : display;
    }

    public static bool AreSameName(string? left, string? right)
    {
        var leftKey = TryNormalize(left);
        return leftKey is not null && string.Equals(leftKey, TryNormalize(right), StringComparison.Ordinal);
    }

    public static string Normalize(string? name) =>
        TryNormalize(name)
        ?? throw new ArgumentException(
            $"'{name}' is not a usable Category or Tag name: it must contain at least one visible character and at most {MaximumLength} after normalization.",
            nameof(name));

    public static string NormalizeDisplayName(string? name) =>
        TryNormalizeDisplayName(name)
        ?? throw new ArgumentException(
            $"'{name}' is not a usable Category or Tag name.",
            nameof(name));

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    // ── K01: Tag input tokenization ───────────────────────────────────────────

    public sealed record TaxonomyTagToken(string CanonicalName, string DisplayName);

    /// <summary>
    /// Parses tag input text by splitting on comma, CR, and LF.
    /// Returns normalized tokens suitable for Import, Profile, and Settings.
    /// Deduplicates by canonical key, preserving first display spelling.
    /// </summary>
    public static IReadOnlyList<TaxonomyTagToken> ParseTagTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<TaxonomyTagToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in SplitTagInput(text))
        {
            var canonical = TryNormalize(raw);
            if (canonical is null)
            {
                continue;
            }

            if (!seen.Add(canonical))
            {
                continue;
            }

            var display = TryNormalizeDisplayName(raw) ?? canonical;
            result.Add(new TaxonomyTagToken(canonical, display));
        }

        return result;
    }

    /// <summary>
    /// Parses partial tag input for live TextBox usage.
    /// Returns completed tokens and the remaining partial search text.
    /// </summary>
    public static (IReadOnlyList<TaxonomyTagToken> Committed, string? Remaining) TryParsePartialTagInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ([], null);
        }

        var committed = new List<TaxonomyTagToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var segments = SplitTagInputSegments(text);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;
            var endsWithDelimiter = !isLast || text.EndsWith(",", StringComparison.Ordinal)
                || text.EndsWith("\n", StringComparison.Ordinal)
                || text.EndsWith("\r", StringComparison.Ordinal);

            if (isLast && !endsWithDelimiter)
            {
                // Last segment without trailing delimiter is partial.
                var remaining = string.IsNullOrWhiteSpace(segment) ? null : segment.TrimStart();
                return (committed, remaining);
            }

            var canonical = TryNormalize(segment);
            if (canonical is null)
            {
                continue;
            }

            if (!seen.Add(canonical))
            {
                continue;
            }

            var display = TryNormalizeDisplayName(segment) ?? canonical;
            committed.Add(new TaxonomyTagToken(canonical, display));
        }

        return (committed, null);
    }

    private static List<string> SplitTagInputSegments(string text)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch is ',' or '\n' or '\r')
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        segments.Add(current.ToString());
        return segments;
    }

    private static IEnumerable<string> SplitTagInput(string text)
    {
        foreach (var segment in SplitTagInputSegments(text))
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                yield return segment;
            }
        }
    }
}

public sealed record MediaPreferences(
    int SchemaVersion = 1,
    int VideoPreviewDurationSeconds = 6,
    bool AutoplayVideo = true,
    bool MuteAudioOnPreview = true);

public sealed record ImportPreferences(
    int SchemaVersion = 1,
    bool AutoAnalyzeAfterImport = true,
    bool PreserveSourceTimestamps = true);

public sealed class SettingsOperations
{

    public const string DefaultProfileLayoutPresetKey = "profile.defaultLayoutPresetId";

    public const string ThemeKey = "app.theme";

    public const string DensityKey = "app.density";

    public const string ReduceMotionKey = "app.reduceMotion";

    public const string GalleryPresentationKey = "gallery.presentation.v1";

    public const string MediaPreferencesKey = "media.preferences.v1";

    public const string ImportPreferencesKey = "import.preferences.v1";

    private readonly CatalogDb _catalog;
    private readonly TimeProvider _timeProvider;

    public SettingsOperations(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationResult<string>> SetDefaultProfileLayoutAsync(
        string layoutPresetId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetDefaultProfileLayoutAsync));
        var presetId = layoutPresetId?.Trim();
        if (string.IsNullOrEmpty(presetId) || !ProfileLayoutResolver.IsBuiltInPresetId(presetId))
        {
            return OperationResult<string>.Validation(
                OperationErrorCode.ProfileLayoutInvalid,
                "That Profile layout is not one of the available layouts.");
        }

        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(DefaultProfileLayoutPresetKey, JsonSerializer.Serialize(presetId), cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<string>.Success(presetId);
    }

    public async Task<string> GetDefaultProfileLayoutAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(DefaultProfileLayoutPresetKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return ProfileLayoutResolver.FallbackPresetId;
        }

        try
        {
            var presetId = JsonSerializer.Deserialize<string>(stored);
            return ProfileLayoutResolver.IsBuiltInPresetId(presetId)
                ? presetId!
                : ProfileLayoutResolver.FallbackPresetId;
        }
        catch (JsonException)
        {
            return ProfileLayoutResolver.FallbackPresetId;
        }
    }

    public async Task<OperationResult<string>> SetThemeAsync(
        string themeId,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetThemeAsync));
        var normalized = themeId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return OperationResult<string>.Validation(
                OperationErrorCode.ThemeInvalid,
                "A theme identifier is required.");
        }

        var isBuiltIn = ThemeLoader.IsBuiltInThemeId(normalized);
        if (!isBuiltIn)
        {
            var customPath = Path.Combine(InstallPaths.CreateProduction().ThemesPath, $"{normalized}.json");
            if (!File.Exists(customPath))
            {
                return OperationResult<string>.Validation(
                    OperationErrorCode.ThemeInvalid,
                    $"The theme '{normalized}' is neither a built-in theme nor a valid custom theme.");
            }
        }

        var current = await GetThemeAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<string>.Success(normalized);
        }

        var activity = new ActivityEntryPersistence(
            ActivityId: Guid.NewGuid(),
            EventType: ActivityEventType.ThemeChanged,
            ProfileId: null,
            AssetId: null,
            ImportUnitId: null,
            OperationId: null,
            PayloadJson: JsonSerializer.Serialize(new { themeId = normalized }),
            OccurredAtUtc: _timeProvider.GetUtcNow());

        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(ThemeKey, JsonSerializer.Serialize(normalized), activity, cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<string>.Success(normalized);
    }

    public async Task<string> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(ThemeKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return ThemeLoader.FallbackThemeId;
        }

        try
        {
            var themeId = JsonSerializer.Deserialize<string>(stored)?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(themeId))
            {
                return ThemeLoader.FallbackThemeId;
            }

            if (ThemeLoader.IsBuiltInThemeId(themeId))
            {
                return themeId;
            }

            var customPath = Path.Combine(InstallPaths.CreateProduction().ThemesPath, $"{themeId}.json");
            return File.Exists(customPath) ? themeId : ThemeLoader.FallbackThemeId;
        }
        catch (JsonException)
        {
            return ThemeLoader.FallbackThemeId;
        }
    }

    public async Task<OperationResult<DensityMode>> SetDensityAsync(
        DensityMode mode,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetDensityAsync));
        if (!Enum.IsDefined(mode))
        {
            return OperationResult<DensityMode>.Validation(
                OperationErrorCode.DensityInvalid,
                "The density mode is not valid.");
        }

        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(DensityKey, JsonSerializer.Serialize(mode.ToString()), cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<DensityMode>.Success(mode);
    }

    public async Task<OperationResult<DensityMode>> SetDensityAsync(
        string modeText,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetDensityAsync));
        if (Enum.TryParse<DensityMode>(modeText?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return await SetDensityAsync(parsed, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult<DensityMode>.Validation(
            OperationErrorCode.DensityInvalid,
            "The density mode must be Compact, Comfortable, or Spacious.");
    }

    public async Task<DensityMode> GetDensityAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(DensityKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return DensityMode.Comfortable;
        }

        try
        {
            var text = JsonSerializer.Deserialize<string>(stored);
            return Enum.TryParse<DensityMode>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : DensityMode.Comfortable;
        }
        catch (JsonException)
        {
            return DensityMode.Comfortable;
        }
    }

    public async Task<OperationResult<bool>> SetReduceMotionAsync(
        bool reduceMotion,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetReduceMotionAsync));
        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(ReduceMotionKey, JsonSerializer.Serialize(reduceMotion), cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<bool>.Success(reduceMotion);
    }

    public async Task<bool> GetReduceMotionAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(ReduceMotionKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(stored);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<OperationResult<GalleryPresentationPreference>> SetGalleryPresentationPreferencesAsync(
        GalleryPresentationPreference preferences,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetGalleryPresentationPreferencesAsync));
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences.SchemaVersion != GalleryCardCatalog.SupportedSchemaVersion)
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                $"The schemaVersion {preferences.SchemaVersion} is not supported.");
        }

        if (!GalleryCardCatalog.TryGetVariant(preferences.CardVariantId, out _))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                $"The gallery card variant '{preferences.CardVariantId}' is not recognized.");
        }

        if (!Enum.IsDefined(preferences.CardSize))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                "The gallery card size is not valid.");
        }

        if (!Enum.IsDefined(preferences.InformationDensity))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                "The gallery information density is not valid.");
        }

        if (!Enum.IsDefined(preferences.BannerMotion))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                "The gallery banner motion mode is not valid.");
        }

        var json = GalleryCardCatalog.WritePreference(preferences);
        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(GalleryPresentationKey, json, cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<GalleryPresentationPreference>.Success(preferences);
    }

    public async Task<OperationResult<GalleryPresentationPreference>> SetGalleryPresentationPreferencesAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetGalleryPresentationPreferencesAsync));
        if (string.IsNullOrWhiteSpace(json))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                "The gallery presentation preference JSON cannot be empty.");
        }

        var resolution = GalleryCardCatalog.ParsePreference(json, "Settings");
        if (resolution.Diagnostics.Any(d => d.Code is GalleryPreferenceDiagnosticCodes.JsonInvalid or GalleryPreferenceDiagnosticCodes.RootNotObject))
        {
            return OperationResult<GalleryPresentationPreference>.Validation(
                OperationErrorCode.GalleryPresentationInvalid,
                "The gallery presentation preference JSON is invalid.");
        }

        return await SetGalleryPresentationPreferencesAsync(resolution.Preference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GalleryPresentationPreference> GetGalleryPresentationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(GalleryPresentationKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return GalleryCardCatalog.Default;
        }

        var resolution = GalleryCardCatalog.ParsePreference(stored, "Settings");
        return resolution.Preference;
    }

    public async Task<OperationResult<MediaPreferences>> SetMediaPreferencesAsync(
        MediaPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetMediaPreferencesAsync));
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences.SchemaVersion != 1)
        {
            return OperationResult<MediaPreferences>.Validation(
                OperationErrorCode.MediaPreferencesInvalid,
                $"The schemaVersion {preferences.SchemaVersion} is not supported.");
        }

        if (preferences.VideoPreviewDurationSeconds is < 1 or > 60)
        {
            return OperationResult<MediaPreferences>.Validation(
                OperationErrorCode.MediaPreferencesInvalid,
                "Video preview duration must be between 1 and 60 seconds.");
        }

        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(MediaPreferencesKey, JsonSerializer.Serialize(preferences), cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<MediaPreferences>.Success(preferences);
    }

    public async Task<MediaPreferences> GetMediaPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(MediaPreferencesKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new MediaPreferences();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MediaPreferences>(stored);
            if (parsed is null || parsed.SchemaVersion != 1 || parsed.VideoPreviewDurationSeconds is < 1 or > 60)
            {
                return new MediaPreferences();
            }

            return parsed;
        }
        catch (JsonException)
        {
            return new MediaPreferences();
        }
    }

    public async Task<OperationResult<ImportPreferences>> SetImportPreferencesAsync(
        ImportPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(SetImportPreferencesAsync));
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences.SchemaVersion != 1)
        {
            return OperationResult<ImportPreferences>.Validation(
                OperationErrorCode.ImportPreferencesInvalid,
                $"The schemaVersion {preferences.SchemaVersion} is not supported.");
        }

        await new SettingsWrites(_catalog, _timeProvider)
            .SetSettingAsync(ImportPreferencesKey, JsonSerializer.Serialize(preferences), cancellationToken)
            .ConfigureAwait(false);

        return OperationResult<ImportPreferences>.Success(preferences);
    }

    public async Task<ImportPreferences> GetImportPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _catalog.SettingsReads
            .GetSettingValueAsync(ImportPreferencesKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new ImportPreferences();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ImportPreferences>(stored);
            if (parsed is null || parsed.SchemaVersion != 1)
            {
                return new ImportPreferences();
            }

            return parsed;
        }
        catch (JsonException)
        {
            return new ImportPreferences();
        }
    }
}
