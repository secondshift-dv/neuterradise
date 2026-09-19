using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Trash;

public static class TrashEntryState
{

    public const string Pending = "PENDING";

    public const string Executing = "EXECUTING";

    public const string InTrash = "IN_TRASH";

    public const string RestoreExecuting = "RESTORE_EXECUTING";

    public const string RestoreFinalizing = "RESTORE_FINALIZING";

    public const string Restored = "RESTORED";
}

public static class TrashEntityType
{
    public const string Asset = "ASSET";

    public const string Profile = "PROFILE";
}

public sealed record AssetTrashComponentPlan(
    string ComponentRelativePath,
    string SourceRelativePath,
    string RecoveryRelativePath,
    long ByteLength,
    string Sha256,
    ComponentRole Role);

public sealed record AssetTrashPlan(
    Guid TrashEntryId,
    Guid AssetId,
    Guid OwnerProfileId,
    string CurrentManagedRelativePath,
    string CurrentManagedFileName,
    string AssetStorageToken,
    long ByteLength,
    string Sha256,
    MediaType MediaType,
    string RecoveryRelativePath,
    IReadOnlyList<AffectedAppearanceReference> AffectedAppearanceReferences,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc,
    long ExpectedAssetRowVersion,
    IReadOnlyList<AssetTrashComponentPlan>? PackageComponents = null)
{

    public const int SchemaVersion = 1;

    [JsonInclude]
    public int Version { get; init; } = SchemaVersion;

    public AssetRestoreCheckpoint? RestoreCheckpoint { get; init; }

    public IReadOnlyList<AssetProfileRelationSnapshot> RelationSnapshots { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, TrashPlanJson.Options);

    public static AssetTrashPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<AssetTrashPlan>(json, TrashPlanJson.Options);
            return plan is null || plan.Version != SchemaVersion ? null : plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public bool Equals(AssetTrashPlan? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Version == other.Version
            && TrashEntryId == other.TrashEntryId
            && AssetId == other.AssetId
            && OwnerProfileId == other.OwnerProfileId
            && CurrentManagedRelativePath == other.CurrentManagedRelativePath
            && CurrentManagedFileName == other.CurrentManagedFileName
            && AssetStorageToken == other.AssetStorageToken
            && ByteLength == other.ByteLength
            && Sha256 == other.Sha256
            && MediaType == other.MediaType
            && RecoveryRelativePath == other.RecoveryRelativePath
            && OperationId == other.OperationId
            && PreparedAtUtc == other.PreparedAtUtc
            && ExpectedAssetRowVersion == other.ExpectedAssetRowVersion
            && RestoreCheckpoint == other.RestoreCheckpoint
            && AffectedAppearanceReferences.SequenceEqual(other.AffectedAppearanceReferences)
            && RelationSnapshots.SequenceEqual(other.RelationSnapshots)
            && ((PackageComponents is null && other.PackageComponents is null)
                || (PackageComponents is not null && other.PackageComponents is not null && PackageComponents.SequenceEqual(other.PackageComponents)));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(TrashEntryId);
        hash.Add(AssetId);
        hash.Add(OwnerProfileId);
        hash.Add(AssetStorageToken);
        hash.Add(Sha256);
        hash.Add(RecoveryRelativePath);
        hash.Add(OperationId);
        hash.Add(ExpectedAssetRowVersion);
        hash.Add(RestoreCheckpoint);

        foreach (var reference in AffectedAppearanceReferences)
        {
            hash.Add(reference);
        }

        foreach (var relation in RelationSnapshots)
        {
            hash.Add(relation);
        }

        if (PackageComponents is not null)
        {
            foreach (var comp in PackageComponents)
            {
                hash.Add(comp);
            }
        }

        return hash.ToHashCode();
    }
}

public sealed record AssetRestoreCheckpoint(
    Guid OwnerProfileId,
    VaultPathArea SourceArea,
    string SourceRelativePath,
    string TargetManagedRelativePath,
    string TargetManagedFileName);

public sealed record AffectedAppearanceReference(
    Guid ProfileId,
    bool IsCover,
    bool IsBanner);

public sealed record AssetProfileRelationSnapshot(
    Guid ProfileId,
    string RelationType,
    string? ProvenanceKey,
    Guid? PublicationImportUnitId,
    long CreatedAtMilliseconds);

public sealed record ProfileRelationSnapshot(
    Guid AssetId,
    string RelationType,
    string? ProvenanceKey,
    Guid? PublicationImportUnitId,
    long CreatedAtMilliseconds);

public sealed record ProfileIdentitySnapshot(
    Guid IdentityId,
    long RowVersion,
    long? RetiredAtMilliseconds);

public sealed record ProfileTrashCheckpoint(
    string RecoveryRelativePath);

public sealed record ProfileRestoreCheckpoint(
    string RecoveryRelativePath,
    string TargetManagedRelativePath);

public sealed record ProfileOwnedAssetSnapshot(
    Guid AssetId,
    long RowVersion);

public enum ProfileOwnedAssetDispositionKind
{

    TrashAsset,

    ChangeOwner,
}

public sealed record ProfileOwnedAssetDisposition(
    Guid AssetId,
    ProfileOwnedAssetDispositionKind Kind,
    Guid? NewOwnerProfileId = null);

public sealed record ProfileTrashPlan(
    Guid TrashEntryId,
    Guid ProfileId,
    long ExpectedProfileRowVersion,
    IReadOnlyList<ProfileOwnedAssetSnapshot> OwnedActiveAssets,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc)
{
    public const int SchemaVersion = 1;

    [JsonInclude]
    public int Version { get; init; } = SchemaVersion;

    public Guid? ExpectedCoverAssetId { get; init; }

    public Guid? ExpectedBannerAssetId { get; init; }

    public IReadOnlyList<ProfileOwnedAssetDisposition> SelectedDispositions { get; init; } = [];

    public IReadOnlyList<ProfileRelationSnapshot> RelationSnapshots { get; init; } = [];

    public ProfileIdentitySnapshot? ActiveIdentitySnapshot { get; init; }

    public ProfileTrashCheckpoint? TrashCheckpoint { get; init; }

    public ProfileRestoreCheckpoint? RestoreCheckpoint { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, TrashPlanJson.Options);

    public static ProfileTrashPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<ProfileTrashPlan>(json, TrashPlanJson.Options);
            return plan is null || plan.Version != SchemaVersion ? null : plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public bool Equals(ProfileTrashPlan? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Version == other.Version
            && TrashEntryId == other.TrashEntryId
            && ProfileId == other.ProfileId
            && ExpectedProfileRowVersion == other.ExpectedProfileRowVersion
            && ExpectedCoverAssetId == other.ExpectedCoverAssetId
            && ExpectedBannerAssetId == other.ExpectedBannerAssetId
            && OperationId == other.OperationId
            && PreparedAtUtc == other.PreparedAtUtc
            && ActiveIdentitySnapshot == other.ActiveIdentitySnapshot
            && TrashCheckpoint == other.TrashCheckpoint
            && RestoreCheckpoint == other.RestoreCheckpoint
            && OwnedActiveAssets.SequenceEqual(other.OwnedActiveAssets)
            && SelectedDispositions.SequenceEqual(other.SelectedDispositions)
            && RelationSnapshots.SequenceEqual(other.RelationSnapshots);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(TrashEntryId);
        hash.Add(ProfileId);
        hash.Add(ExpectedProfileRowVersion);
        hash.Add(ExpectedCoverAssetId);
        hash.Add(ExpectedBannerAssetId);
        hash.Add(OperationId);

        hash.Add(ActiveIdentitySnapshot);
        hash.Add(TrashCheckpoint);
        hash.Add(RestoreCheckpoint);

        foreach (var asset in OwnedActiveAssets)
        {
            hash.Add(asset);
        }

        foreach (var disposition in SelectedDispositions)
        {
            hash.Add(disposition);
        }

        foreach (var relation in RelationSnapshots)
        {
            hash.Add(relation);
        }

        return hash.ToHashCode();
    }
}

internal static class TrashPlanJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
