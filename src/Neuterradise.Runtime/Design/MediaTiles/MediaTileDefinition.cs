using System.Collections.Generic;
using System.Linq;

namespace Neuterradise.App.Design.MediaTiles;

public enum MediaTileVariant
{
    Immersive,
    Metadata,
    Clean,
    Compact
}

public sealed record MediaTileDefinition(
    MediaTileVariant Variant,
    string DisplayName);

public sealed record MediaTilePresentationModel(
    Guid AssetId,
    string MediaType,
    string? FileName = null,
    string? DurationText = null,
    string? DimensionsText = null,
    string? ByteLengthText = null,
    bool IsSelected = false,
    string? StatusText = null,
    bool HasWarning = false);

public static class MediaTileCatalog
{
    public static readonly IReadOnlyList<MediaTileDefinition> Variants =
    [
        new(MediaTileVariant.Immersive, "Immersive"),
        new(MediaTileVariant.Metadata, "Metadata"),
        new(MediaTileVariant.Clean, "Clean"),
        new(MediaTileVariant.Compact, "Compact"),
    ];

    public static readonly MediaTileDefinition Default = Variants[0];

    public static MediaTileDefinition Resolve(MediaTileVariant variant) =>
        Variants.FirstOrDefault(v => v.Variant == variant) ?? Default;

    public static MediaTileDefinition Resolve(string? name) =>
        Variants.FirstOrDefault(v => string.Equals(v.Variant.ToString(), name, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
