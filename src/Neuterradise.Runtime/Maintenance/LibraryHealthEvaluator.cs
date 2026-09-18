using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Maintenance;

public sealed class LibraryHealthEvaluator
{
    private readonly CatalogDb _catalog;
    private readonly VaultPaths _paths;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly TimeProvider _timeProvider;

    public LibraryHealthEvaluator(
        CatalogDb catalog,
        ManagedPathPlanner? pathPlanner = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _paths = catalog.Paths;
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_paths.Root);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<HealthFinding>> ScanAsync(
        HealthScanOptions? options = null,
        IProgress<HealthScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunScanAsync(options, progress, cancellationToken).ConfigureAwait(false);
        return result.Findings;
    }

    public async Task<HealthScanResult> RunScanAsync(
        HealthScanOptions? options = null,
        IProgress<HealthScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new HealthScanOptions();
        var startTime = _timeProvider.GetTimestamp();
        var findings = new List<HealthFinding>();
        var itemsScanned = 0;

        if (opts.Mode == HealthScanMode.StartupCritical)
        {
            progress?.Report(new HealthScanProgress("Startup critical authority checks", 0, null, 0));
            var dbFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
                .ConfigureAwait(false);

            itemsScanned += dbFindings.Count;
            findings.AddRange(dbFindings);
            progress?.Report(new HealthScanProgress("Startup checks completed", itemsScanned, itemsScanned, findings.Count));

            var elapsedStartup = _timeProvider.GetElapsedTime(startTime);
            return new HealthScanResult(
                findings,
                opts.Mode,
                itemsScanned,
                elapsedStartup,
                _timeProvider.GetUtcNow());
        }

        progress?.Report(new HealthScanProgress("Evaluating database integrity invariants", itemsScanned, null, findings.Count));
        var rawDbFindings = await _catalog.HealthReads.GetDatabaseIntegrityFindingsAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var finding in rawDbFindings)
        {
            if (opts.FocusedProfileId.HasValue && finding.ProfileId != opts.FocusedProfileId)
            {
                continue;
            }

            if (opts.FocusedAssetId.HasValue && finding.AssetId != opts.FocusedAssetId)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(opts.FocusedOperationId) && finding.OperationId != opts.FocusedOperationId)
            {
                continue;
            }

            findings.Add(finding);
        }
        itemsScanned += rawDbFindings.Count;

        progress?.Report(new HealthScanProgress("Scanning profiles and manifests", itemsScanned, null, findings.Count));
        var profiles = await _catalog.HealthReads.GetHealthProfileItemsAsync(opts.FocusedProfileId, cancellationToken)
            .ConfigureAwait(false);

        var knownProfileIds = new HashSet<Guid>();
        var knownProfileFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownProfileTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenProfileTokens = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsScanned++;

            knownProfileIds.Add(profile.ProfileId);
            if (!string.IsNullOrWhiteSpace(profile.StorageToken))
            {
                knownProfileTokens.Add(profile.StorageToken);
                if (seenProfileTokens.TryGetValue(profile.StorageToken, out var existingProfileId) && existingProfileId != profile.ProfileId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StorageTokenConflict,
                        HealthSeverity.Critical,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Storage token '{profile.StorageToken}' is claimed by multiple profiles ({existingProfileId} and {profile.ProfileId}).",
                        RepairAvailable: false));
                }
                else
                {
                    seenProfileTokens[profile.StorageToken] = profile.ProfileId;
                }
            }

            if (profile.IsTrashed)
            {

                var trashProfileDir = Path.Combine(_paths.TrashProfilesPath, profile.ProfileId.ToString("D").ToLowerInvariant());
                var recoveryManifest = Path.Combine(trashProfileDir, ProfileManifestWriter.ManifestFileName);

                if (!Directory.Exists(trashProfileDir) || !File.Exists(recoveryManifest))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TrashStateMismatch,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile {profile.ProfileId} is marked TRASHED in catalog but recovery manifest is missing from trash root.",
                        RepairAvailable: true));
                }

                if (!string.IsNullOrWhiteSpace(profile.StorageToken))
                {
                    var planned = _pathPlanner.PlanProfile(profile.ProfileId, profile.DisplayName, new ProfileStorageToken(profile.StorageToken));
                    var activeDir = Path.Combine(_paths.ProfilesPath, Path.GetFileName(planned.ProfileFolderRelativePath));
                    if (Directory.Exists(activeDir))
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.TrashStateMismatch,
                            HealthSeverity.Error,
                            profile.ProfileId,
                            null,
                            null,
                            null,
                            $"Profile {profile.ProfileId} is marked TRASHED in catalog but active folder '{Path.GetFileName(activeDir)}' still exists in profiles directory.",
                            RepairAvailable: true));
                    }
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.StorageToken))
            {

                continue;
            }

            var plan = _pathPlanner.PlanProfile(profile.ProfileId, profile.DisplayName, new ProfileStorageToken(profile.StorageToken));
            var expectedFolderName = Path.GetFileName(plan.ProfileFolderRelativePath);
            knownProfileFolderNames.Add(expectedFolderName);
            var expectedFolderPath = Path.Combine(_paths.ProfilesPath, expectedFolderName);

            string actualFolderPath;
            if (!Directory.Exists(expectedFolderPath))
            {

                if (TryFindFolderByToken(_paths.ProfilesPath, profile.StorageToken, out var foundDir))
                {
                    actualFolderPath = foundDir;
                    knownProfileFolderNames.Add(Path.GetFileName(foundDir));
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileFolderNameMismatch,
                        HealthSeverity.Warning,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile folder name '{Path.GetFileName(foundDir)}' does not match canonical name '{expectedFolderName}'.",
                        RepairAvailable: true));
                }
                else
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileFolderMissing,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Canonical profile folder '{expectedFolderName}' does not exist on disk.",
                        RepairAvailable: true));
                    continue;
                }
            }
            else
            {
                actualFolderPath = expectedFolderPath;
            }

            var inspection = ProfileManifestWriter.InspectManifest(actualFolderPath);
            if (inspection.Status == ManifestStatus.Missing)
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.ProfileManifestMissing,
                    HealthSeverity.Warning,
                    profile.ProfileId,
                    null,
                    null,
                    null,
                    $"Profile manifest '{ProfileManifestWriter.ManifestFileName}' is missing from folder '{Path.GetFileName(actualFolderPath)}'.",
                    RepairAvailable: true));
            }
            else if (inspection.Status == ManifestStatus.Malformed)
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.ProfileManifestMalformed,
                    HealthSeverity.Warning,
                    profile.ProfileId,
                    null,
                    null,
                    null,
                    $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' is malformed: {inspection.ErrorDetail}",
                    RepairAvailable: true));
            }
            else if (inspection.Status == ManifestStatus.Valid && inspection.Manifest is not null)
            {
                var manifest = inspection.Manifest;
                if (manifest.ProfileId != profile.ProfileId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileManifestIdMismatch,
                        HealthSeverity.Error,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' carries ProfileId {manifest.ProfileId} instead of authoritative {profile.ProfileId}.",
                        RepairAvailable: false));
                }
                else if (!string.Equals(manifest.DisplayName, profile.DisplayName, StringComparison.Ordinal)
                         || manifest.Kind != profile.Kind
                         || !string.Equals(manifest.FolderName, expectedFolderName, StringComparison.Ordinal))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ProfileManifestStale,
                        HealthSeverity.Info,
                        profile.ProfileId,
                        null,
                        null,
                        null,
                        $"Profile manifest in '{Path.GetFileName(actualFolderPath)}' is stale compared to database authority.",
                        RepairAvailable: true));
                }
            }
        }

        AddDuplicateFolderTokenFindings(findings);

        progress?.Report(new HealthScanProgress("Scanning managed assets and integrity", itemsScanned, null, findings.Count));
        var assets = await _catalog.HealthReads.GetHealthAssetItemsAsync(opts.FocusedAssetId, opts.FocusedProfileId, cancellationToken)
            .ConfigureAwait(false);

        var knownAssetIds = new HashSet<Guid>();
        var knownAssetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAssetTokens = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsScanned++;

            knownAssetIds.Add(asset.AssetId);

            if (!string.IsNullOrWhiteSpace(asset.StorageToken))
            {
                if (seenAssetTokens.TryGetValue(asset.StorageToken, out var existingAssetId) && existingAssetId != asset.AssetId)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StorageTokenConflict,
                        HealthSeverity.Critical,
                        asset.OwnerProfileId,
                        asset.AssetId,
                        null,
                        null,
                        $"Storage token '{asset.StorageToken}' is claimed by multiple assets ({existingAssetId} and {asset.AssetId}).",
                        RepairAvailable: false));
                }
                else
                {
                    seenAssetTokens[asset.StorageToken] = asset.AssetId;
                }
            }

            if (asset.IsTrashed || asset.State == AssetState.Trashed)
            {
                var recoveryDir = Path.Combine(_paths.TrashAssetsPath, asset.AssetId.ToString("D").ToLowerInvariant());
                if (!Directory.Exists(recoveryDir) || !Directory.EnumerateFiles(recoveryDir).Any())
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.TrashStateMismatch,
                        HealthSeverity.Error,
                        asset.OwnerProfileId,
                        asset.AssetId,
                        null,
                        null,
                        $"Asset {asset.AssetId} is marked TRASHED in catalog but recovery bytes are missing from trash root.",
                        RepairAvailable: true));
                }

                if (!string.IsNullOrWhiteSpace(asset.CurrentManagedRelativePath)
                    && !string.IsNullOrWhiteSpace(asset.CurrentManagedFileName))
                {
                    try
                    {
                        var managedFile = _paths.ResolveVaultRelativePath(
                            $"{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}");
                        if (File.Exists(managedFile))
                        {
                            findings.Add(new HealthFinding(
                                HealthFindingCode.TrashStateMismatch,
                                HealthSeverity.Error,
                                asset.OwnerProfileId,
                                asset.AssetId,
                                null,
                                null,
                                $"Asset {asset.AssetId} is marked TRASHED in catalog but file still exists at managed path '{asset.CurrentManagedRelativePath}/{asset.CurrentManagedFileName}'.",
                                RepairAvailable: true));
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                continue;
            }

            if (asset.State != AssetState.Active)
            {
                continue;
            }

            if (!asset.OwnerProfileId.HasValue || string.IsNullOrWhiteSpace(asset.OwnerStorageToken))
            {

                continue;
            }

            if (string.IsNullOrWhiteSpace(asset.StorageToken))
            {

                continue;
            }

            var assetPlan = _pathPlanner.PlanAsset(
                asset.OwnerProfileId.Value,
                asset.OwnerDisplayName ?? "Profile",
                new ProfileStorageToken(asset.OwnerStorageToken),
                asset.AssetId,
                new AssetStorageToken(asset.StorageToken),
                asset.MediaType,
                asset.Extension);

            var expectedFileName = assetPlan.ManagedFileName!;
            knownAssetFileNames.Add(expectedFileName);
            var expectedRelative = NormalizeRelative(assetPlan.ManagedFileRelativePath!);
            string expectedFullPath;
            try
            {
                expectedFullPath = _paths.ResolveVaultRelativePath(
                    assetPlan.ManagedFileRelativePath!);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                expectedFullPath = Path.Combine(_paths.Root, expectedRelative);
            }

            string actualFilePath;
            if (!File.Exists(expectedFullPath))
            {

                var profileFolder = Path.Combine(_paths.ProfilesPath, Path.GetFileName(assetPlan.ProfileFolderRelativePath));
                var expectedDirectory = Path.GetDirectoryName(expectedFullPath) ?? profileFolder;
                if (TryFindAssetFileByToken(profileFolder, expectedDirectory, asset.StorageToken, out var foundPath))
                {
                    actualFilePath = foundPath;
                    knownAssetFileNames.Add(Path.GetFileName(foundPath));
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ManagedFileNameMismatch,
                        HealthSeverity.Warning,
                        asset.OwnerProfileId,
                        asset.AssetId,
                        null,
                        null,
                        $"Managed file name '{Path.GetFileName(foundPath)}' does not match canonical name '{expectedFileName}'.",
                        RepairAvailable: true));
                }
                else
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ManagedPathMismatch,
                        HealthSeverity.Error,
                        asset.OwnerProfileId,
                        asset.AssetId,
                        null,
                        null,
                        $"Managed file is missing at expected path '{expectedRelative}'.",
                        RepairAvailable: true));
                    continue;
                }
            }
            else
            {
                actualFilePath = expectedFullPath;
            }

            var actualName = Path.GetFileName(actualFilePath);
            var extractedToken = ExtractAssetStorageTokenFromFileName(actualName);
            if (extractedToken is not null && !string.Equals(extractedToken, asset.StorageToken, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding(
                    HealthFindingCode.StorageTokenPathMismatch,
                    HealthSeverity.Error,
                    asset.OwnerProfileId,
                    asset.AssetId,
                    null,
                    null,
                    $"Managed file '{actualName}' carries storage token '{extractedToken}' which does not match assigned token '{asset.StorageToken}'.",
                    RepairAvailable: true));
            }

            if (opts.Mode == HealthScanMode.Deep && !string.IsNullOrWhiteSpace(asset.Sha256))
            {
                await using var stream = File.OpenRead(actualFilePath);
                var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                var actualSha256 = Convert.ToHexStringLower(hashBytes);

                if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.ContentMismatch,
                        HealthSeverity.Critical,
                        asset.OwnerProfileId,
                        asset.AssetId,
                        null,
                        null,
                        $"Content fingerprint mismatch for asset {asset.AssetId}. Catalog SHA-256: {asset.Sha256}, actual file SHA-256: {actualSha256}.",
                        RepairAvailable: false));
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.ProfilesPath))
        {
            progress?.Report(new HealthScanProgress("Auditing filesystem for unexpected artifacts", itemsScanned, null, findings.Count));
            foreach (var profileDir in Directory.EnumerateDirectories(_paths.ProfilesPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(profileDir);
                var folderToken = ExtractProfileStorageTokenFromFolder(dirName);

                bool isKnown = knownProfileFolderNames.Contains(dirName)
                    || (folderToken is not null && knownProfileTokens.Contains(folderToken));

                if (!isKnown)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.UnexpectedManagedFile,
                        HealthSeverity.Warning,
                        null,
                        null,
                        null,
                        null,
                        $"Unexpected directory in profiles root: '{dirName}'.",
                        RepairAvailable: false));
                    continue;
                }

                var mediaRoot = Path.Combine(profileDir, "media");
                if (Directory.Exists(mediaRoot))
                {
                    foreach (var mediaTypeDir in Directory.EnumerateDirectories(mediaRoot))
                    {
                        foreach (var file in Directory.EnumerateFiles(mediaTypeDir))
                        {
                            var fileName = Path.GetFileName(file);
                            if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!knownAssetFileNames.Contains(fileName))
                            {
                                var fileToken = ExtractAssetStorageTokenFromFileName(fileName);
                                if (fileToken is null || !seenAssetTokens.ContainsKey(fileToken))
                                {
                                    findings.Add(new HealthFinding(
                                        HealthFindingCode.UnexpectedManagedFile,
                                        HealthSeverity.Warning,
                                        null,
                                        null,
                                        null,
                                        null,
                                        $"Unexpected file in profile media: '{Path.GetRelativePath(_paths.ProfilesPath, file)}'.",
                                        RepairAvailable: false));
                                }
                            }
                        }
                    }
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.TrashAssetsPath))
        {
            foreach (var trashAssetDir in Directory.EnumerateDirectories(_paths.TrashAssetsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(trashAssetDir);
                if (Guid.TryParse(dirName, out var trashAssetId))
                {
                    var match = assets.FirstOrDefault(a => a.AssetId == trashAssetId);
                    if (match is not null && match.State == AssetState.Active && !match.IsTrashed)
                    {
                        findings.Add(new HealthFinding(
                            HealthFindingCode.TrashStateMismatch,
                            HealthSeverity.Error,
                            match.OwnerProfileId,
                            trashAssetId,
                            null,
                            null,
                            $"Asset {trashAssetId} is ACTIVE in catalog but recovery directory exists in trash assets root.",
                            RepairAvailable: true));
                    }
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.StagingImportsPath))
        {
            progress?.Report(new HealthScanProgress("Auditing staging directory", itemsScanned, null, findings.Count));
            foreach (var stagingDir in Directory.EnumerateDirectories(_paths.StagingImportsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(stagingDir);
                if (!await IsActiveStagingDirectoryAsync(dirName, cancellationToken).ConfigureAwait(false))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.StagingOrphanAmbiguous,
                        HealthSeverity.Warning,
                        null,
                        null,
                        null,
                        null,
                        $"Staging directory '{dirName}' has no active import session.",
                        RepairAvailable: false));
                }
            }
        }

        if (opts.Mode != HealthScanMode.Focused && Directory.Exists(_paths.CachePath))
        {
            progress?.Report(new HealthScanProgress("Auditing cache directories", itemsScanned, null, findings.Count));
            foreach (var file in Directory.EnumerateFiles(_paths.CachePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.Length == 0)
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.CacheCorrupt,
                        HealthSeverity.Warning,
                        null,
                        null,
                        null,
                        null,
                        $"Cache file '{Path.GetRelativePath(_paths.CachePath, file)}' is 0 bytes (corrupted).",
                        RepairAvailable: true));
                    continue;
                }

                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(fileNameWithoutExt, out var parsedAssetId) && !knownAssetIds.Contains(parsedAssetId))
                {
                    findings.Add(new HealthFinding(
                        HealthFindingCode.CacheOrphan,
                        HealthSeverity.Info,
                        null,
                        parsedAssetId,
                        null,
                        null,
                        $"Cache artifact '{Path.GetFileName(file)}' does not correspond to any known asset in the catalog.",
                        RepairAvailable: true));
                }
            }
        }

        var elapsed = _timeProvider.GetElapsedTime(startTime);
        progress?.Report(new HealthScanProgress("Scan completed", itemsScanned, itemsScanned, findings.Count));

        return new HealthScanResult(
            findings,
            opts.Mode,
            itemsScanned,
            elapsed,
            _timeProvider.GetUtcNow());
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

    private static string? ExtractProfileStorageTokenFromFolder(string folderName)
    {
        var open = folderName.LastIndexOf('[');
        var close = folderName.LastIndexOf(']');
        if (open >= 0 && close > open + 1)
        {
            return folderName.Substring(open + 1, close - open - 1).Trim();
        }
        return null;
    }

    private static string? ExtractProfileStorageTokenFromFolderName(string folderName)
    {
        var open = folderName.LastIndexOf('[');
        var close = folderName.LastIndexOf(']');
        if (open < 0 || close <= open + 1)
        {
            return null;
        }

        return folderName[(open + 1)..close].Trim();
    }

    private static string? ExtractAssetStorageTokenFromFileName(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var dashIndex = nameWithoutExt.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex >= 0 && dashIndex + 3 < nameWithoutExt.Length)
        {
            return nameWithoutExt.Substring(dashIndex + 3).Trim();
        }
        return null;
    }

    private void AddDuplicateFolderTokenFindings(List<HealthFinding> findings)
    {
        if (!Directory.Exists(_paths.ProfilesPath))
        {
            return;
        }

        var byToken = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(_paths.ProfilesPath))
        {
            var name = Path.GetFileName(directory);
            if (ExtractProfileStorageTokenFromFolderName(name) is not { } token)
            {
                continue;
            }

            if (!byToken.TryGetValue(token, out var claimants))
            {
                claimants = [];
                byToken[token] = claimants;
            }

            claimants.Add(name);
        }

        foreach (var (token, claimants) in byToken.Where(entry => entry.Value.Count > 1)
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            findings.Add(new HealthFinding(
                HealthFindingCode.StorageTokenConflict,
                HealthSeverity.Critical,
                null,
                null,
                null,
                null,
                $"Storage token '{token}' is claimed by {claimants.Count} managed folders: "
                    + string.Join(", ", claimants.OrderBy(name => name, StringComparer.Ordinal)) + ".",
                RepairAvailable: false));
        }
    }

    private static bool TryFindFolderByToken(string profilesPath, string token, out string foundPath)
    {
        foundPath = string.Empty;
        if (!Directory.Exists(profilesPath))
        {
            return false;
        }

        var marker = $"[{token}]";
        foreach (var dir in Directory.EnumerateDirectories(profilesPath))
        {
            if (dir.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
                || dir.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                foundPath = dir;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindAssetFileByToken(
        string profileFolder,
        string expectedDirectory,
        string token,
        out string foundPath)
    {
        foundPath = string.Empty;

        if (TryMatchTokenInDirectory(expectedDirectory, token, SearchOption.TopDirectoryOnly, out foundPath))
        {
            return true;
        }

        return TryMatchTokenInDirectory(profileFolder, token, SearchOption.AllDirectories, out foundPath);
    }

    private static bool TryMatchTokenInDirectory(
        string directory,
        string token,
        SearchOption searchOption,
        out string foundPath)
    {
        foundPath = string.Empty;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", searchOption))
        {
            if (Path.GetFileName(file).Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                foundPath = file;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsActiveStagingDirectoryAsync(string dirName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM import_sessions
                WHERE state IN ('PENDING', 'SCANNING', 'STAGED', 'IMPORTING')
                  AND (import_id = $dirName OR staging_relative_path LIKE '%' || $dirName || '%');
                """;
            command.Parameters.AddWithValue("$dirName", dirName);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
