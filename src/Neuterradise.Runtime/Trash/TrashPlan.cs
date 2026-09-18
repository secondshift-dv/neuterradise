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
            && OwnedActiveAssets.SequenceEqual(other.OwnedActiveAssets);
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

        foreach (var asset in OwnedActiveAssets)
        {
            hash.Add(asset);
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
