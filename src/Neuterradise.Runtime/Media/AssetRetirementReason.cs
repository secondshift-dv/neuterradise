namespace Neuterradise.App.Media;

/// <summary>
/// The one canonical reason a candidate asset retired (Section 9.2):
/// <c>SKIPPED | CANCELLED | INVALID | DEDUP_REUSED</c>. It is persisted only together with
/// <see cref="AssetState.Retired"/>.
/// </summary>
public enum AssetRetirementReason
{
    /// <summary>The user chose to skip the item.</summary>
    Skipped,

    /// <summary>The import scope was cancelled before the candidate could commit.</summary>
    Cancelled,

    /// <summary>The candidate could not be admitted as valid media.</summary>
    Invalid,

    /// <summary>An exact duplicate decision reused an existing active asset instead.</summary>
    DedupReused,
}
