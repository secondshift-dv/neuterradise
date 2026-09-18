using System.IO;
using System.Text;
using System.Text.Json;
using Neuterradise.App.Design.ProfileLayouts;

namespace Neuterradise.App.Design.GalleryCards;

public enum GalleryCardVariant
{
    Cinematic,
    CinematicTall,
    Poster,
    PortraitFrame,
    Landscape,
    HeroCard,
    Glass,
    Editorial,
    Compact,
    Minimal,
    Stats,
    Prestige
}

public enum GalleryCardSize
{
    Small,
    Medium,
    Large
}

public enum GalleryInformationDensity
{
    Minimal,
    Standard,
    Detailed
}

public sealed record GalleryCardDefinition(
    GalleryCardVariant Variant,
    string Id,
    string DisplayName,
    double AspectRatio,
    bool UsesBanner,
    bool ShowsCover,
    GalleryInformationDensity BaseDensity);

public sealed record GalleryPresentationPreference(
    int SchemaVersion,
    string CardVariantId,
    GalleryCardSize CardSize,
    GalleryInformationDensity InformationDensity,
    bool ShowTags,
    bool ShowCategory,
    bool ShowRating,
    bool ShowTier,
    bool ShowMediaCount,
    bool ShowRelatedIndicator,
    BannerMotionMode BannerMotion);

public static class GalleryPreferenceDiagnosticCodes
{
    public const string JsonInvalid = "GALLERY_PREFERENCE_JSON_INVALID";
    public const string RootNotObject = "GALLERY_PREFERENCE_ROOT_NOT_OBJECT";
    public const string UnknownField = "GALLERY_PREFERENCE_UNKNOWN_FIELD";
    public const string FieldMissing = "GALLERY_PREFERENCE_FIELD_MISSING";
    public const string FieldTypeInvalid = "GALLERY_PREFERENCE_FIELD_TYPE_INVALID";
    public const string SchemaVersionUnsupported = "GALLERY_PREFERENCE_SCHEMA_VERSION_UNSUPPORTED";
    public const string EnumValueUnknown = "GALLERY_PREFERENCE_ENUM_VALUE_UNKNOWN";
    public const string VariantUnknown = "GALLERY_PREFERENCE_VARIANT_UNKNOWN";
}

public sealed record GalleryPreferenceDiagnostic(string Code, string Source, string Detail);

public sealed record GalleryPreferenceResolution(
    GalleryPresentationPreference Preference,
    IReadOnlyList<GalleryPreferenceDiagnostic> Diagnostics);

public static class GalleryCardCatalog
{

    public const int SupportedSchemaVersion = 1;

    public const string FallbackVariantId = "cinematic";

    private static readonly string[] PreferenceProperties =
    [
        "schemaVersion", "cardVariantId", "cardSize", "informationDensity",
        "showTags", "showCategory", "showRating", "showTier",
        "showMediaCount", "showRelatedIndicator", "bannerMotion",
    ];

    private static readonly GalleryCardDefinition[] BuiltInVariants =
    [
        new(GalleryCardVariant.Cinematic, "cinematic", "Cinematic", 16d / 9d, true, true, GalleryInformationDensity.Standard),
        new(GalleryCardVariant.CinematicTall, "cinematic-tall", "Cinematic Tall", 2d / 3d, true, true, GalleryInformationDensity.Standard),
        new(GalleryCardVariant.Poster, "poster", "Poster", 2d / 3d, false, false, GalleryInformationDensity.Minimal),
        new(GalleryCardVariant.PortraitFrame, "portrait-frame", "Portrait Frame", 3d / 4d, false, true, GalleryInformationDensity.Standard),
        new(GalleryCardVariant.Landscape, "landscape", "Landscape", 16d / 9d, true, false, GalleryInformationDensity.Standard),
        new(GalleryCardVariant.HeroCard, "hero-card", "Hero Card", 21d / 9d, true, true, GalleryInformationDensity.Detailed),
        new(GalleryCardVariant.Glass, "glass", "Glass", 1d, true, true, GalleryInformationDensity.Minimal),
        new(GalleryCardVariant.Editorial, "editorial", "Editorial", 4d / 3d, false, true, GalleryInformationDensity.Detailed),
        new(GalleryCardVariant.Compact, "compact", "Compact", 1d, false, true, GalleryInformationDensity.Minimal),
        new(GalleryCardVariant.Minimal, "minimal", "Minimal", 1d, false, false, GalleryInformationDensity.Minimal),
        new(GalleryCardVariant.Stats, "stats", "Stats", 4d / 3d, false, true, GalleryInformationDensity.Detailed),
        new(GalleryCardVariant.Prestige, "prestige", "Prestige", 3d / 4d, false, true, GalleryInformationDensity.Detailed),
    ];

    public static IReadOnlyList<GalleryCardDefinition> Variants { get; } = BuiltInVariants;

    public static GalleryPresentationPreference Default { get; } = new(
        SupportedSchemaVersion,
        FallbackVariantId,
        GalleryCardSize.Medium,
        GalleryInformationDensity.Standard,
        ShowTags: true,
        ShowCategory: true,
        ShowRating: true,
        ShowTier: true,
        ShowMediaCount: true,
        ShowRelatedIndicator: true,
        BannerMotionMode.HoverOnly);

    public static bool TryGetVariant(string? id, out GalleryCardDefinition definition)
    {
        if (id is not null)
        {
            foreach (var candidate in BuiltInVariants)
            {
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        definition = BuiltInVariants[0];
        return false;
    }

    public static GalleryCardDefinition ResolveVariant(string? perProfileOverride, GalleryPresentationPreference? preference)
    {
        if (!string.IsNullOrWhiteSpace(perProfileOverride) && TryGetVariant(perProfileOverride, out var overridden))
        {
            return overridden;
        }

        if (!string.IsNullOrWhiteSpace(preference?.CardVariantId) && TryGetVariant(preference.CardVariantId, out var globalDefault))
        {
            return globalDefault;
        }

        return BuiltInVariants[0];
    }

    public static GalleryCardDefinition ResolveVariant(string? perProfileOverride, string? globalDefaultVariantId)
    {
        if (!string.IsNullOrWhiteSpace(perProfileOverride) && TryGetVariant(perProfileOverride, out var overridden))
        {
            return overridden;
        }

        if (!string.IsNullOrWhiteSpace(globalDefaultVariantId) && TryGetVariant(globalDefaultVariantId, out var globalDefault))
        {
            return globalDefault;
        }

        return BuiltInVariants[0];
    }

    public static GalleryCardDefinition ResolveVariant(GalleryPresentationPreference? preference) =>
        ResolveVariant(null, preference);

    public static GalleryPreferenceResolution ParsePreference(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var diagnostics = new List<GalleryPreferenceDiagnostic>();
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new GalleryPreferenceDiagnostic(
                GalleryPreferenceDiagnosticCodes.JsonInvalid, source, ex.Message));

            return new GalleryPreferenceResolution(Default, diagnostics);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new GalleryPreferenceDiagnostic(
                    GalleryPreferenceDiagnosticCodes.RootNotObject,
                    source,
                    $"A Gallery preference must be a JSON object; found {root.ValueKind}."));

                return new GalleryPreferenceResolution(Default, diagnostics);
            }

            ReportUnknownAndMissingFields(root, source, diagnostics);

            var schemaVersion = ReadInt32(root, "schemaVersion", Default.SchemaVersion, source, diagnostics);

            if (schemaVersion != SupportedSchemaVersion)
            {
                diagnostics.Add(new GalleryPreferenceDiagnostic(
                    GalleryPreferenceDiagnosticCodes.SchemaVersionUnsupported,
                    source,
                    $"schemaVersion {schemaVersion} is not supported; this build understands {SupportedSchemaVersion}."));

                schemaVersion = SupportedSchemaVersion;
            }

            var variantId = ReadString(root, "cardVariantId", Default.CardVariantId, source, diagnostics);

            if (!TryGetVariant(variantId, out _))
            {
                diagnostics.Add(new GalleryPreferenceDiagnostic(
                    GalleryPreferenceDiagnosticCodes.VariantUnknown,
                    variantId,
                    $"'{variantId}' is not a shipped Gallery Card variant; '{FallbackVariantId}' is used."));

                variantId = FallbackVariantId;
            }

            var preference = new GalleryPresentationPreference(
                schemaVersion,
                variantId,
                ReadEnum(root, "cardSize", Default.CardSize, source, diagnostics),
                ReadEnum(root, "informationDensity", Default.InformationDensity, source, diagnostics),
                ReadBoolean(root, "showTags", Default.ShowTags, source, diagnostics),
                ReadBoolean(root, "showCategory", Default.ShowCategory, source, diagnostics),
                ReadBoolean(root, "showRating", Default.ShowRating, source, diagnostics),
                ReadBoolean(root, "showTier", Default.ShowTier, source, diagnostics),
                ReadBoolean(root, "showMediaCount", Default.ShowMediaCount, source, diagnostics),
                ReadBoolean(root, "showRelatedIndicator", Default.ShowRelatedIndicator, source, diagnostics),
                ReadEnum(root, "bannerMotion", Default.BannerMotion, source, diagnostics));

            return new GalleryPreferenceResolution(preference, diagnostics);
        }
    }

    public static string WritePreference(GalleryPresentationPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);

        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", preference.SchemaVersion);
            writer.WriteString("cardVariantId", preference.CardVariantId);
            writer.WriteString("cardSize", preference.CardSize.ToString());
            writer.WriteString("informationDensity", preference.InformationDensity.ToString());
            writer.WriteBoolean("showTags", preference.ShowTags);
            writer.WriteBoolean("showCategory", preference.ShowCategory);
            writer.WriteBoolean("showRating", preference.ShowRating);
            writer.WriteBoolean("showTier", preference.ShowTier);
            writer.WriteBoolean("showMediaCount", preference.ShowMediaCount);
            writer.WriteBoolean("showRelatedIndicator", preference.ShowRelatedIndicator);
            writer.WriteString("bannerMotion", preference.BannerMotion.ToString());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void ReportUnknownAndMissingFields(
        JsonElement root,
        string source,
        List<GalleryPreferenceDiagnostic> diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!PreferenceProperties.Contains(property.Name, StringComparer.Ordinal))
            {

                diagnostics.Add(new GalleryPreferenceDiagnostic(
                    GalleryPreferenceDiagnosticCodes.UnknownField,
                    source,
                    $"'{property.Name}' is not part of the schema v1 Gallery preference."));
            }
        }

        foreach (var name in PreferenceProperties)
        {
            if (!root.TryGetProperty(name, out _))
            {
                diagnostics.Add(new GalleryPreferenceDiagnostic(
                    GalleryPreferenceDiagnosticCodes.FieldMissing,
                    source,
                    $"'{name}' is missing; the Design System default is used."));
            }
        }
    }

    private static string ReadString(
        JsonElement root,
        string name,
        string fallback,
        string source,
        List<GalleryPreferenceDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? fallback;
        }

        diagnostics.Add(new GalleryPreferenceDiagnostic(
            GalleryPreferenceDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON string; the Design System default is used."));

        return fallback;
    }

    private static bool ReadBoolean(
        JsonElement root,
        string name,
        bool fallback,
        string source,
        List<GalleryPreferenceDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        diagnostics.Add(new GalleryPreferenceDiagnostic(
            GalleryPreferenceDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a JSON boolean; the Design System default is used."));

        return fallback;
    }

    private static int ReadInt32(
        JsonElement root,
        string name,
        int fallback,
        string source,
        List<GalleryPreferenceDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        diagnostics.Add(new GalleryPreferenceDiagnostic(
            GalleryPreferenceDiagnosticCodes.FieldTypeInvalid,
            source,
            $"'{name}' must be a whole JSON number; the Design System default is used."));

        return fallback;
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement root,
        string name,
        TEnum fallback,
        string source,
        List<GalleryPreferenceDiagnostic> diagnostics)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new GalleryPreferenceDiagnostic(
                GalleryPreferenceDiagnosticCodes.FieldTypeInvalid,
                source,
                $"'{name}' must be a JSON string; the Design System default is used."));

            return fallback;
        }

        var text = element.GetString();

        if (Enum.TryParse<TEnum>(text, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        diagnostics.Add(new GalleryPreferenceDiagnostic(
            GalleryPreferenceDiagnosticCodes.EnumValueUnknown,
            source,
            $"'{text}' is not a valid {name} value; the Design System default is used."));

        return fallback;
    }
}
