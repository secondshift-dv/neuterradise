using System.IO;
using System.Threading;

namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// Read-only value contract for the extracted application directory (InstallRoot).
/// InstallRoot holds executables, runtime tools, inference models, built-in themes, assets and notices.
/// It is mutated only by ZIP extraction/replacement/updater, never by data operations, and it is never
/// a Vault authority.
/// </summary>
public sealed class InstallPaths
{
    private static readonly Lazy<InstallPaths> Production = new(
        static () => new InstallPaths(AppContext.BaseDirectory),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public InstallPaths(string root)
    {
        Root = RootPathRules.NormalizeRoot(root, nameof(root));
    }

    /// <summary>
    /// Production InstallRoot is the directory the running executable was deployed into.
    /// This is an application-resource authority only; it never implies a Vault location. The
    /// production authority is created once per process so startup, runtime capability checks,
    /// worker launch and update recovery all observe the same immutable root instance.
    /// </summary>
    public static InstallPaths CreateProduction() => Production.Value;

    public string Root { get; }

    public string ToolsPath => Path.Combine(Root, "tools");

    public string ModelsPath => Path.Combine(Root, "models");

    public string ThemesPath => Path.Combine(Root, "themes");

    public string AssetsPath => Path.Combine(Root, "assets");

    public string NoticesPath => Path.Combine(Root, "LICENSES");

    public string ThirdPartyNoticesFilePath => Path.Combine(Root, "THIRD-PARTY-NOTICES.txt");

    public string ReleaseManifestPath => Path.Combine(Root, "release-manifest.json");

    public string AppExecutablePath => Path.Combine(Root, ProductIdentity.AppExecutableName);

    public string ProfilingWorkerExecutablePath =>
        Path.Combine(Root, ProductIdentity.ProfilingWorkerExecutableName);

    public string UpdaterExecutablePath => Path.Combine(Root, ProductIdentity.UpdaterExecutableName);

    public string GetAreaRoot(InstallPathArea area) => area switch
    {
        InstallPathArea.Root => Root,
        InstallPathArea.Tools => ToolsPath,
        InstallPathArea.Models => ModelsPath,
        InstallPathArea.Themes => ThemesPath,
        InstallPathArea.Assets => AssetsPath,
        InstallPathArea.Notices => NoticesPath,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    /// <summary>
    /// Resolves a deployment-relative resource path inside the requested area. The path must be
    /// relative and traversal-free; resolution never searches PATH, the working directory or any
    /// other implicit location.
    /// </summary>
    public string ResolveContainedPath(InstallPathArea area, string relativePath)
        => RootPathRules.ResolveContainedPath(Root, GetAreaRoot(area), relativePath, nameof(relativePath));

    /// <summary>
    /// Returns the resolved path only when the deployed resource actually exists, so a missing tool or
    /// model becomes a truthful unavailable capability instead of a fabricated success.
    /// </summary>
    public string? FindExistingFile(InstallPathArea area, string relativePath)
    {
        string resolvedPath;
        try
        {
            resolvedPath = ResolveContainedPath(area, relativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    public bool IsWithinInstallRoot(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        return RootPathRules.IsWithinOrEqual(Root, Path.GetFullPath(absolutePath));
    }

    /// <summary>
    /// Two-way disjointness against the other runtime roots. InstallRoot must never contain or live
    /// inside AppStateRoot or VaultRoot.
    /// </summary>
    public void EnsureDisjointFrom(AppStatePaths appState, string? vaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(appState);
        RootPathRules.EnsureRuntimeRootsDisjoint(Root, appState.Root, vaultRoot);
    }
}

public enum InstallPathArea
{
    Root,
    Tools,
    Models,
    Themes,
    Assets,
    Notices,
}
