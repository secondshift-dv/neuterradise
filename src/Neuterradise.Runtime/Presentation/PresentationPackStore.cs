using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.App.Presentation;

public enum PackOrigin
{
    BuiltIn,
    User,
}

/// <summary>A pack as the registry knows it: manifest, where it lives, and whether it may be used.</summary>
public sealed record InstalledPack(
    PresentationPackManifest Manifest,
    PackOrigin Origin,
    string? RootPath,
    string ContentHash,
    IReadOnlyList<PackDiagnostic> Diagnostics)
{
    public bool IsUsable => Diagnostics.All(d => !d.IsError);

    public string PackId => Manifest.PackId;
}

public sealed record PackInstallResult(bool Succeeded, InstalledPack? Pack, IReadOnlyList<PackDiagnostic> Diagnostics, string? Message = null);

/// <summary>
/// Presentation resolves assets only as (packId, assetId). It never receives or concatenates an
/// arbitrary Vault path; the store owns the mapping and re-validates containment on every lookup.
/// </summary>
public interface IPresentationAssetStore
{
    PackAssetEntry? GetAsset(string packId, string assetId);

    /// <summary>Absolute path of a validated asset, or null when the pack/asset is unknown or unsafe.</summary>
    string? ResolveAssetPath(string packId, string assetId);
}

/// <summary>
/// Storage classes (document 01 §9): built-in packs live with the application payload (manifest embedded,
/// assets under InstallRoot); user packs are durable Vault user data under <c>presentation/packs</c>;
/// compiled plans and derivatives live only in memory / app cache. Nothing here ever deletes media,
/// Profiles or other Vault content.
/// </summary>
public sealed class PresentationPackStore : IPresentationAssetStore
{
    public const string PackArchiveExtension = ".ntpack";
    private const string StagingFolderName = ".staging";
    private const long MaxArchiveUncompressedBytes = PackValidator.MaxPackBytes + (16L * 1024 * 1024);

    private readonly string _userPacksRoot;
    private readonly string? _builtInAssetsRoot;
    private readonly Func<string, string, bool>? _tryMoveDirectory;
    private readonly Lock _sync = new();
    private Dictionary<string, InstalledPack> _packs = new(StringComparer.Ordinal);

    public PresentationPackStore(string userPacksRoot, string? builtInAssetsRoot)
        : this(userPacksRoot, builtInAssetsRoot, null)
    {
    }

    internal PresentationPackStore(
        string userPacksRoot,
        string? builtInAssetsRoot,
        Func<string, string, bool>? tryMoveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPacksRoot);
        _userPacksRoot = Path.GetFullPath(userPacksRoot);
        _builtInAssetsRoot = builtInAssetsRoot is null ? null : Path.GetFullPath(builtInAssetsRoot);
        _tryMoveDirectory = tryMoveDirectory;
    }

    public string UserPacksRoot => _userPacksRoot;

    public IReadOnlyList<InstalledPack> Packs
    {
        get
        {
            lock (_sync)
            {
                return [.. _packs.Values.OrderBy(p => p.Origin).ThenBy(p => p.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public bool TryGetPack(string packId, out InstalledPack? pack)
    {
        lock (_sync)
        {
            return _packs.TryGetValue(packId, out pack);
        }
    }

    /// <summary>Loads the built-in pack and every user pack. A bad user pack is kept as unusable, never fatal.</summary>
    public IReadOnlyList<InstalledPack> LoadAll()
    {
        var loaded = new Dictionary<string, InstalledPack>(StringComparer.Ordinal);
        var builtIn = LoadBuiltIn();
        loaded[builtIn.PackId] = builtIn;

        if (Directory.Exists(_userPacksRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(_userPacksRoot))
            {
                if (Path.GetFileName(directory).StartsWith('.'))
                {
                    continue;
                }

                var pack = LoadUserPack(directory);
                if (pack is null)
                {
                    continue;
                }

                if (loaded.ContainsKey(pack.PackId))
                {
                    Trace.TraceWarning("Presentation pack {0} is duplicated at {1}; the first copy is used.", pack.PackId, directory);
                    continue;
                }

                loaded[pack.PackId] = pack;
            }
        }

        lock (_sync)
        {
            _packs = loaded;
        }

        return Packs;
    }

    public static string ReadBuiltInManifestJson()
    {
        using var stream = typeof(PresentationPackStore).Assembly.GetManifestResourceStream("Neuterradise.App.Presentation.BuiltIn.neuterradise.core.json")
            ?? throw new InvalidOperationException("The built-in Presentation Pack is not embedded in the runtime.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private InstalledPack LoadBuiltIn()
    {
        var json = ReadBuiltInManifestJson();
        var read = PackManifestReader.Read(json, "built-in");
        if (read.Manifest is null)
        {
            throw new InvalidOperationException("The built-in Presentation Pack manifest is invalid: " + string.Join("; ", read.Diagnostics.Select(d => d.Detail)));
        }

        var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, _builtInAssetsRoot is not null && Directory.Exists(_builtInAssetsRoot) ? _builtInAssetsRoot : null, isBuiltIn: true)).ToList();
        foreach (var diagnostic in diagnostics.Where(d => d.IsError))
        {
            Trace.TraceError("Built-in presentation pack diagnostic {0} on {1}: {2}", diagnostic.Code, diagnostic.Subject, diagnostic.Detail);
        }

        // Built-in assets are optional payload; a missing asset only disables the definitions using it.
        var fatal = diagnostics.Where(d => d.IsError && d.Code is not (PackDiagnosticCodes.AssetMissing or PackDiagnosticCodes.AssetUnreadable)).ToList();
        return new InstalledPack(read.Manifest, PackOrigin.BuiltIn, _builtInAssetsRoot, Hash(json), fatal);
    }

    private static InstalledPack? LoadUserPack(string directory)
    {
        var manifestPath = Path.Combine(directory, PackManifestReader.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            var read = PackManifestReader.Read(json, manifestPath);
            if (read.Manifest is null)
            {
                return null;
            }

            var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, directory, isBuiltIn: false)).ToList();
            if (!string.Equals(Path.GetFileName(directory), read.Manifest.PackId, StringComparison.Ordinal))
            {
                diagnostics.Add(new(PackDiagnosticCodes.IdInvalid, read.Manifest.PackId, "The pack folder name must equal its pack id."));
            }

            return new InstalledPack(read.Manifest, PackOrigin.User, directory, HashDirectory(directory), diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Presentation pack at {0} could not be read: {1}", directory, exception.GetType().Name);
            return null;
        }
    }

    // ----------------------------------------------------------------- install / export / remove

    /// <summary>
    /// Installs a pack from a folder or a <c>.ntpack</c>/<c>.zip</c> archive: stage → validate manifest
    /// and assets → hash → atomic move into the Vault. Nothing is registered unless every check passes.
    /// </summary>
    public async Task<PackInstallResult> InstallAsync(string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Directory.CreateDirectory(_userPacksRoot);
        var staging = Path.Combine(_userPacksRoot, StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            if (Directory.Exists(sourcePath))
            {
                await Task.Run(() => CopyDirectorySafe(sourcePath, staging), cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(sourcePath))
            {
                var extractError = await Task.Run(() => ExtractArchiveSafe(sourcePath, staging), cancellationToken).ConfigureAwait(false);
                if (extractError is not null)
                {
                    return new(false, null, [new(PackDiagnosticCodes.ForbiddenContent, Path.GetFileName(sourcePath), extractError)], extractError);
                }
            }
            else
            {
                return new(false, null, [], "The selected pack could not be found.");
            }

            // Accept archives that wrap the pack in one top-level folder.
            var root = File.Exists(Path.Combine(staging, PackManifestReader.ManifestFileName))
                ? staging
                : Directory.GetDirectories(staging) is [var single] && File.Exists(Path.Combine(single, PackManifestReader.ManifestFileName)) ? single : null;
            if (root is null)
            {
                return new(false, null, [new(PackDiagnosticCodes.FieldMissing, "pack.json", "The pack has no pack.json manifest.")], "This is not a Presentation Pack.");
            }

            var json = await File.ReadAllTextAsync(Path.Combine(root, PackManifestReader.ManifestFileName), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var read = PackManifestReader.Read(json, "pack.json");
            if (read.Manifest is null)
            {
                return new(false, null, read.Diagnostics, "The pack manifest is not valid.");
            }

            var diagnostics = read.Diagnostics.Concat(PackValidator.Validate(read.Manifest, root, isBuiltIn: false)).ToList();
            var compileDiagnostics = PresentationCompiler.ValidateDefinitions(read.Manifest);
            diagnostics.AddRange(compileDiagnostics);
            if (diagnostics.Any(d => d.IsError))
            {
                return new(false, null, diagnostics, "The pack did not pass validation.");
            }

            var packId = read.Manifest.PackId;
            lock (_sync)
            {
                if (_packs.TryGetValue(packId, out var existing) && (existing.Origin == PackOrigin.BuiltIn || !replaceExisting))
                {
                    var conflict = existing.Origin == PackOrigin.BuiltIn
                        ? new PackDiagnostic(PackDiagnosticCodes.IdReserved, packId, "A built-in pack already uses that id.")
                        : new PackDiagnostic(PackDiagnosticCodes.AlreadyInstalled, packId, "A pack with that id is already installed.");
                    return new(false, null, [.. diagnostics, conflict], conflict.Detail);
                }
            }

            var destination = Path.Combine(_userPacksRoot, packId);
            string? backup = null;
            if (Directory.Exists(destination))
            {
                backup = Path.Combine(_userPacksRoot, StagingFolderName, packId + ".previous." + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, backup);
            }

            try
            {
                Directory.Move(root, destination);
            }
            catch
            {
                if (backup is not null && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                }

                throw;
            }

            if (backup is not null)
            {
                TryDeleteStaging(backup);
            }

            var installed = new InstalledPack(read.Manifest, PackOrigin.User, destination, HashDirectory(destination), diagnostics);
            lock (_sync)
            {
                _packs[packId] = installed;
            }

            return new(true, installed, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, null, [], "The pack could not be installed: " + exception.Message);
        }
        finally
        {
            TryDeleteStaging(staging);
        }
    }

    /// <summary>Writes a pack as a portable <c>.ntpack</c> archive. Built-in packs export their manifest only.</summary>
    public async Task ExportAsync(string packId, string destinationFile, CancellationToken cancellationToken = default)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack is null)
        {
            throw new InvalidOperationException("That pack is not installed.");
        }

        var temporary = destinationFile + ".partial";
        await Task.Run(() =>
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                if (pack.Origin == PackOrigin.BuiltIn)
                {
                    var entry = archive.CreateEntry(PackManifestReader.ManifestFileName, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(ReadBuiltInManifestJson());
                }
                else
                {
                    foreach (var file in Directory.EnumerateFiles(pack.RootPath!, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(pack.RootPath!, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    }
                }
            }

            File.Move(temporary, destinationFile, overwrite: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<PackRemovalStage?> StageRemovalAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack is null || pack.Origin != PackOrigin.User || pack.RootPath is null)
        {
            return Task.FromResult<PackRemovalStage?>(null);
        }

        return Task.Run(() =>
        {
            var root = Path.GetFullPath(pack.RootPath);
            if (!root.StartsWith(_userPacksRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var removed = Path.Combine(_userPacksRoot, StagingFolderName, packId + ".removed." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
            try
            {
                var moved = _tryMoveDirectory?.Invoke(root, removed) ?? MoveDirectory(root, removed);
                return moved ? new PackRemovalStage(packId, root, removed) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Presentation pack could not be staged for removal: {0}", exception.GetType().Name);
                return null;
            }
        }, cancellationToken);
    }

    internal bool RestoreRemoval(PackRemovalStage stage)
    {
        try
        {
            if (Directory.Exists(stage.OriginalPath))
            {
                return true;
            }

            Directory.Move(stage.StagedPath, stage.OriginalPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Presentation pack removal rollback failed for {0}: {1}", stage.PackId, exception.Message);
            return false;
        }
    }

    internal void CompleteRemoval(PackRemovalStage stage)
    {
        lock (_sync)
        {
            _packs.Remove(stage.PackId);
        }

        // The atomic rename made the pack unavailable. A locked staging folder is harmless and can
        // be retried by later maintenance; it must not turn a coherent logical removal into data loss.
        TryDeleteStaging(stage.StagedPath);
    }

    private static bool MoveDirectory(string source, string destination)
    {
        Directory.Move(source, destination);
        return true;
    }

    // ----------------------------------------------------------------- asset store

    public PackAssetEntry? GetAsset(string packId, string assetId)
    {
        lock (_sync)
        {
            return _packs.TryGetValue(packId, out var pack)
                ? pack.Manifest.Assets.FirstOrDefault(asset => asset.Id == assetId)
                : null;
        }
    }

    public string? ResolveAssetPath(string packId, string assetId)
    {
        InstalledPack? pack;
        lock (_sync)
        {
            _packs.TryGetValue(packId, out pack);
        }

        if (pack?.RootPath is null)
        {
            return null;
        }

        var asset = pack.Manifest.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset is null || !PackValidator.IsSafeRelativePath(asset.Path))
        {
            return null;
        }

        var root = Path.GetFullPath(pack.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    // ----------------------------------------------------------------- helpers

    private static void CopyDirectorySafe(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Packs cannot contain links.");
            }

            if (++count > PackValidator.MaxFiles || (bytes += info.Length) > MaxArchiveUncompressedBytes)
            {
                throw new InvalidDataException("The pack is too large.");
            }

            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string? ExtractArchiveSafe(string archivePath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > PackValidator.MaxFiles)
        {
            return "The archive contains too many files.";
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaxArchiveUncompressedBytes)
            {
                return "The archive expands beyond the allowed pack size.";
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "The archive contains a path that escapes the pack.";
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (PackValidator.ForbiddenExtensions.Contains(Path.GetExtension(entry.FullName)))
            {
                return $"'{entry.FullName}' is executable or script content and is not allowed in a pack.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }

        return null;
    }

    private static void TryDeleteStaging(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Presentation pack staging folder could not be removed: {0}", exception.GetType().Name);
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashDirectory(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/')));
            using var stream = File.OpenRead(file);
            sha.AppendData(SHA256.HashData(stream));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

internal sealed record PackRemovalStage(string PackId, string OriginalPath, string StagedPath);
