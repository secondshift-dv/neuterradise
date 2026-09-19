using System.Globalization;
using System.IO;
using System.Text;
using Neuterradise.App.Media;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Canonical, side-effect-free planner for the R13 Profile/Hero/Media tree. A caller resolves
/// collisions before persisting a plan and before any filesystem write; retries consume that
/// persisted plan instead of calling the allocator again.
/// </summary>
public sealed class ManagedPathPlanner
{
    public const int MaximumAbsolutePathCodeUnits = 240;
    public const string PathTooLongCode = "MANAGED_PATH_TOO_LONG";
    public const string PathConflictCode = "PATH_CONFLICT";

    private static readonly int[] AllowedSuffixLengths = [8, 12, 16, 20, 24, 28, 32];
    private readonly ManagedNamePolicy _namePolicy;
    private readonly string? _vaultRoot;

    public ManagedPathPlanner(ManagedNamePolicy? namePolicy = null)
        : this(null, namePolicy)
    {
    }

    public ManagedPathPlanner(string? vaultRoot, ManagedNamePolicy? namePolicy = null)
    {
        _namePolicy = namePolicy ?? new ManagedNamePolicy();
        _vaultRoot = string.IsNullOrWhiteSpace(vaultRoot) ? null : Path.GetFullPath(vaultRoot);
    }

    public ManagedPathPlan PlanProfile(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        int idSuffixLength = 8)
    {
        EnsureNonEmpty(profileId, nameof(profileId));
        Validate(profileStorageToken);
        ValidateSuffixLength(idSuffixLength);

        var suffix = $"__{IdSuffix(profileId, idSuffixLength)}";
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel),
            suffix,
            "profiles");
        var profileFolder = CombineRelative("profiles", safeName + suffix);
        EnsureWithinBudget(profileFolder);

        return new ManagedPathPlan(
            profileId,
            profileStorageToken,
            profileFolder,
            AssetId: null,
            AssetStorageToken: null,
            MediaType: null,
            ManagedFileRelativePath: null,
            ManagedFileName: null);
    }

    public ManagedPathPlan PlanAsset(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        AssetStorageToken assetStorageToken,
        MediaType mediaType,
        string extension,
        int profileIdSuffixLength = 8,
        int assetIdSuffixLength = 8)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        Validate(assetStorageToken);
        ValidateSuffixLength(assetIdSuffixLength);

        var profilePlan = PlanProfile(profileId, displayLabel, profileStorageToken, profileIdSuffixLength);
        var normalizedExtension = _namePolicy.NormalizeExtension(extension);
        var mediaFolder = GetMediaFolder(mediaType);
        var role = GetMediaRole(mediaType);
        var suffix = $"__{role}__{IdSuffix(assetId, assetIdSuffixLength)}.{normalizedExtension}";
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel),
            suffix,
            profilePlan.ProfileFolderRelativePath,
            "Media",
            mediaFolder);
        var managedFileName = safeName + suffix;
        var managedFilePath = CombineRelative(
            profilePlan.ProfileFolderRelativePath,
            "Media",
            mediaFolder,
            managedFileName);
        EnsureWithinBudget(managedFilePath);

        return profilePlan with
        {
            AssetId = assetId,
            AssetStorageToken = assetStorageToken,
            MediaType = mediaType,
            ManagedFileRelativePath = managedFilePath,
            ManagedFileName = managedFileName,
        };
    }

    /// <summary>Plans the directory form required for a model package with dependencies.</summary>
    public ManagedModelPackagePlan PlanModelPackage(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        AssetStorageToken assetStorageToken,
        string primaryRelativeComponentPath,
        int profileIdSuffixLength = 8,
        int assetIdSuffixLength = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryRelativeComponentPath);
        EnsureSafeComponentPath(primaryRelativeComponentPath);
        var profile = PlanProfile(profileId, displayLabel, profileStorageToken, profileIdSuffixLength);
        Validate(assetStorageToken);
        ValidateSuffixLength(assetIdSuffixLength);

        var suffix = $"__model__{IdSuffix(assetId, assetIdSuffixLength)}";
        var normalizedPrimary = NormalizeComponentPath(primaryRelativeComponentPath);
        var safeName = FitHumanSegment(
            _namePolicy.ToSafeProfileName(displayLabel), suffix + "/" + normalizedPrimary,
            profile.ProfileFolderRelativePath, "Media", "Models");
        var packageDirectory = CombineRelative(profile.ProfileFolderRelativePath, "Media", "Models", safeName + suffix);
        var primary = CombineRelative(packageDirectory, normalizedPrimary);
        EnsureWithinBudget(primary);
        return new ManagedModelPackagePlan(assetId, packageDirectory, primary, Path.GetFileName(primaryRelativeComponentPath));
    }

    /// <summary>Plans a derived Hero materialization keyed by authoritative AssetId and appearance bytes.</summary>
    public ManagedHeroPathPlan PlanHero(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        ReadOnlySpan<byte> appearanceHash,
        HeroMaterializationKind kind,
        int profileIdSuffixLength = 8,
        int assetIdSuffixLength = 8,
        int appearanceHashLength = 8)
    {
        EnsureNonEmpty(assetId, nameof(assetId));
        ValidateSuffixLength(assetIdSuffixLength);
        ValidateSuffixLength(appearanceHashLength);
        if (appearanceHash.Length < appearanceHashLength / 2)
        {
            throw new ArgumentException("Appearance hash does not contain enough bytes for the requested collision suffix.", nameof(appearanceHash));
        }

        var profile = PlanProfile(profileId, displayLabel, profileStorageToken, profileIdSuffixLength);
        var (role, extension) = kind switch
        {
            HeroMaterializationKind.Cover => ("cover", "webp"),
            HeroMaterializationKind.BannerStill => ("banner-still", "webp"),
            HeroMaterializationKind.BannerLoop => ("banner-loop", "mp4"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        var appearance = Convert.ToHexStringLower(appearanceHash)[..appearanceHashLength];
        var fileName = $"{role}__{IdSuffix(assetId, assetIdSuffixLength)}__{appearance}.{extension}";
        var relative = CombineRelative(profile.ProfileFolderRelativePath, "Hero", fileName);
        EnsureWithinBudget(relative);
        return new ManagedHeroPathPlan(assetId, kind, appearance, relative, fileName);
    }

    public ManagedPathPlan AllocateProfilePlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Func<ManagedPathPlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            var candidate = PlanProfile(profileId, displayLabel, profileStorageToken, profileLength);
            if (!conflicts(candidate))
            {
                return candidate;
            }
        }

        throw new ManagedPathPlanningException(
            PathConflictCode,
            "All deterministic Profile ID suffixes conflict with another entity or unknown bytes.");
    }

    public ManagedModelPackagePlan AllocateModelPackagePlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        AssetStorageToken assetStorageToken,
        string primaryRelativeComponentPath,
        Func<ManagedModelPackagePlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            foreach (var assetLength in AllowedSuffixLengths)
            {
                var candidate = PlanModelPackage(
                    profileId,
                    displayLabel,
                    profileStorageToken,
                    assetId,
                    assetStorageToken,
                    primaryRelativeComponentPath,
                    profileLength,
                    assetLength);
                if (!conflicts(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new ManagedPathPlanningException(
            PathConflictCode,
            "All deterministic model-package ID suffixes conflict with another entity or unknown bytes.");
    }

    public ManagedHeroPathPlan AllocateHeroPlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        ReadOnlySpan<byte> appearanceHash,
        HeroMaterializationKind kind,
        Func<ManagedHeroPathPlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            foreach (var assetLength in AllowedSuffixLengths)
            {
                foreach (var appearanceLength in AllowedSuffixLengths)
                {
                    if (appearanceHash.Length < appearanceLength / 2)
                    {
                        continue;
                    }

                    var candidate = PlanHero(
                        profileId,
                        displayLabel,
                        profileStorageToken,
                        assetId,
                        appearanceHash,
                        kind,
                        profileLength,
                        assetLength,
                        appearanceLength);
                    if (!conflicts(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new ManagedPathPlanningException(
            PathConflictCode,
            "All deterministic Hero identity suffixes conflict with another entity or unknown bytes.");
    }

    /// <summary>
    /// Selects the first nonconflicting deterministic ID suffix. The predicate must return true
    /// only when the candidate is owned by another entity or unknown bytes.
    /// </summary>
    public ManagedPathPlan AllocateAssetPlan(
        Guid profileId,
        string displayLabel,
        ProfileStorageToken profileStorageToken,
        Guid assetId,
        AssetStorageToken assetStorageToken,
        MediaType mediaType,
        string extension,
        Func<ManagedPathPlan, bool> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var profileLength in AllowedSuffixLengths)
        {
            foreach (var assetLength in AllowedSuffixLengths)
            {
                var candidate = PlanAsset(profileId, displayLabel, profileStorageToken, assetId,
                    assetStorageToken, mediaType, extension, profileLength, assetLength);
                if (!conflicts(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new ManagedPathPlanningException(PathConflictCode,
            "All deterministic ID suffixes conflict with another entity or unknown bytes.");
    }

    private string FitHumanSegment(string value, string fixedSuffix, params string[] parentSegments)
    {
        if (_vaultRoot is null)
        {
            return value;
        }

        var prefix = Path.Combine([_vaultRoot, .. parentSegments]);
        var available = MaximumAbsolutePathCodeUnits - prefix.Length - 1 - fixedSuffix.Length;
        if (available < 1)
        {
            throw TooLong();
        }

        var starts = StringInfo.ParseCombiningCharacters(value);
        while (value.Length > available && starts.Length > 0)
        {
            value = value[..starts[^1]];
            starts = StringInfo.ParseCombiningCharacters(value);
        }

        value = value.TrimEnd('.', ' ');
        if (value.Length == 0)
        {
            throw TooLong();
        }

        return value;
    }

    private void EnsureWithinBudget(string relativePath)
    {
        if (_vaultRoot is not null
            && Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)).Length > MaximumAbsolutePathCodeUnits)
        {
            throw TooLong();
        }
    }

    private static ManagedPathPlanningException TooLong() => new(
        PathTooLongCode,
        $"The Vault root is too long for a managed path limited to {MaximumAbsolutePathCodeUnits} UTF-16 code units.");

    private static string GetMediaFolder(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => "Images",
        MediaType.Video => "Videos",
        MediaType.Model => "Models",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null),
    };

    private static string GetMediaRole(MediaType mediaType) => mediaType switch
    {
        MediaType.Image => "image",
        MediaType.Video => "video",
        MediaType.Model => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null),
    };

    private static string IdSuffix(Guid id, int length) => id.ToString("N")[..length].ToLowerInvariant();

    private static void ValidateSuffixLength(int length)
    {
        if (!AllowedSuffixLengths.Contains(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Suffix length must be 8, 12, 16, 20, 24, 28, or 32.");
        }
    }

    private static void EnsureSafeComponentPath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A model component path must be relative.", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("A model component path cannot contain traversal.", nameof(path));
        }
    }

    private static string NormalizeComponentPath(string path) =>
        string.Join('/', path.Normalize(NormalizationForm.FormC).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string CombineRelative(params string[] segments) => string.Join('/', segments);
    private static void Validate(ProfileStorageToken token) => _ = new ProfileStorageToken(token.Value);
    private static void Validate(AssetStorageToken token) => _ = new AssetStorageToken(token.Value);

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A managed path requires a non-empty stable identifier.", parameterName);
        }
    }
}

public enum HeroMaterializationKind
{
    Cover,
    BannerStill,
    BannerLoop,
}

public sealed record ManagedHeroPathPlan(
    Guid AssetId,
    HeroMaterializationKind Kind,
    string AppearanceHashSuffix,
    string RelativePath,
    string FileName);

public sealed record ManagedModelPackagePlan(
    Guid AssetId,
    string PackageDirectoryRelativePath,
    string PrimaryManagedRelativePath,
    string PrimaryFileName);

public sealed class ManagedPathPlanningException : InvalidOperationException
{
    public ManagedPathPlanningException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
