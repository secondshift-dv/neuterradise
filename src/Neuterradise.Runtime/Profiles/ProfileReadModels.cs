using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Profiles;

public sealed record ProfileHeaderReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CategoryId,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    string? Overview,
    string? Notes,
    Guid? IdentityId,
    Guid? CoverAssetId,
    Guid? BannerAssetId,
    long? UnknownSequence,
    long ActiveOwnedAssetCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion);

public sealed record ProfileFolderReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CurrentManagedRelativePath,
    string? TargetManagedRelativePath,
    ManagedPathState PathState,
    string? ReconciliationOperationId,
    long RowVersion);

public sealed record ProfileDetailReadModel(
    Guid ProfileId,
    ProfileKind Kind,
    string DisplayName,
    string? StorageToken,
    string? CategoryId,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool IsFavorite,
    string? Overview,
    string? Notes,
    Guid? IdentityId,
    int IdentitySampleCount,
    Guid? CoverAssetId,
    Guid? BannerAssetId,
    long? UnknownSequence,
    long ActiveOwnedAssetCount,
    string? LayoutPresetId,
    string? AppearanceOverridesJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion,
    IReadOnlyList<ProfileTagAssignment>? TagAssignments = null,
    MediaType? BannerMediaType = null)
{
    public IReadOnlyList<ProfileTagAssignment> AssignedTags => TagAssignments ?? [];
}

public sealed record ProfileTagAssignment(string TagId, string DisplayName);

public sealed record ProfileMediaPage(
    IReadOnlyList<ProfileMediaItemReadModel> Items,
    string? NextPageToken,
    bool HasMore,
    int TotalCount = 0);

public sealed record ProfileMediaItemReadModel(
    Guid AssetId,
    MediaType MediaType,
    ProfileAssetRelation RelationType,
    string? ManagedRelativePath,
    string? ManagedFileName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AddedToLibraryAtUtc,
    int? PixelWidth = null,
    int? PixelHeight = null,
    int? DurationMs = null,
    ManagedPathState PathState = ManagedPathState.None,
    string? ContentFingerprint = null,
    bool IsFavorite = false);

public enum ProfileMediaFilter
{
    All,
    Owned,
    AppearsIn,
    Manual
}

public sealed record ProfileAssetRelationEntry(
    Guid ProfileId,
    Guid AssetId,
    ProfileAssetRelation RelationType,
    DateTimeOffset CreatedAtUtc,
    string? ProvenanceKey = null);

public sealed record UnknownProfileSummary(
    Guid ProfileId,
    long UnknownSequence,
    string DerivedLabel,
    string? StorageToken,
    long ActiveOwnedAssetCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TrashedAtUtc,
    long RowVersion);
