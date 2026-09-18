using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class StorageTokenAllocator
{
    private const int _profileInitialHexLength = 6;
    private const int _assetInitialHexLength = 8;
    private const int _maximumHexLength = 64;

    public ProfileStorageToken GetProfileCandidate(Guid profileId, int collisionAttempt)
    {
        var digest = CreateDigest(profileId);
        var length = GetCandidateLength(_profileInitialHexLength, collisionAttempt);
        return new ProfileStorageToken($"P-{digest[..length]}");
    }

    public AssetStorageToken GetAssetCandidate(Guid assetId, int collisionAttempt)
    {
        var digest = CreateDigest(assetId);
        var length = GetCandidateLength(_assetInitialHexLength, collisionAttempt);
        return new AssetStorageToken($"A-{digest[..length]}");
    }

    public ProfileStorageToken AllocateProfile(
        Guid profileId,
        Func<ProfileStorageToken, bool> isAssigned)
    {
        ArgumentNullException.ThrowIfNull(isAssigned);
        for (var attempt = 0; ; attempt++)
        {
            var candidate = GetProfileCandidate(profileId, attempt);
            if (!isAssigned(candidate))
            {
                return candidate;
            }
        }
    }

    public AssetStorageToken AllocateAsset(
        Guid assetId,
        Func<AssetStorageToken, bool> isAssigned)
    {
        ArgumentNullException.ThrowIfNull(isAssigned);
        for (var attempt = 0; ; attempt++)
        {
            var candidate = GetAssetCandidate(assetId, attempt);
            if (!isAssigned(candidate))
            {
                return candidate;
            }
        }
    }

    public Func<int, string> CreateProfileCandidateProvider(Guid profileId) =>
        attempt => GetProfileCandidate(profileId, attempt).Value;

    public Func<int, string> CreateAssetCandidateProvider(Guid assetId) =>
        attempt => GetAssetCandidate(assetId, attempt).Value;

    private static string CreateDigest(Guid entityId)
    {
        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("A storage token requires a non-empty stable identifier.", nameof(entityId));
        }

        var canonicalId = entityId.ToString("D").ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId)));
    }

    private static int GetCandidateLength(int initialLength, int collisionAttempt)
    {
        var maximumAttempt = (_maximumHexLength - initialLength) / 2;
        if (collisionAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collisionAttempt));
        }

        if (collisionAttempt > maximumAttempt)
        {
            throw new InvalidOperationException(
                "Every deterministic storage-token candidate for the stable identifier is already assigned.");
        }

        return initialLength + (collisionAttempt * 2);
    }
}
