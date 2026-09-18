using Neuterradise.App.Media;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record ManagedPathPlan(
    Guid ProfileId,
    ProfileStorageToken ProfileStorageToken,
    string ProfileFolderRelativePath,
    Guid? AssetId,
    AssetStorageToken? AssetStorageToken,
    MediaType? MediaType,
    string? ManagedFileRelativePath,
    string? ManagedFileName)
{

    public string TargetProfileFolderRelativePath => ProfileFolderRelativePath;

    public string? TargetManagedFileRelativePath => ManagedFileRelativePath;

    public bool IsAssetPlan => AssetId is not null;
}
