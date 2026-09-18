using System.IO.Compression;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed class UpdatePackageStager
{
    private const int MaxEntries = 100_000;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    private readonly AppStatePaths _appState;
    private readonly UpdatePackageValidator _validator;

    public UpdatePackageStager(AppStatePaths appState, UpdatePackageValidator validator)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<UpdatePackageValidationResult> ExtractAndValidateAsync(
        string archivePath,
        Guid operationId,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An update operation id is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(manifest);

        var operationName = operationId.ToString("D");
        var staging = _appState.ResolveContainedPath(AppStatePathArea.UpdateStaging, operationName);
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            return UpdatePackageValidationResult.Reject(
                "Update operation staging already exists; retry with a new operation id.");
        }

        var writtenFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);

            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count > MaxEntries)
            {
                return RejectAndCleanup(
                    "Update archive contains too many entries.",
                    writtenFiles);
            }

            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalized)
                    || normalized.EndsWith('/')
                    || Path.IsPathRooted(normalized)
                    || normalized.Contains("../", StringComparison.Ordinal)
                    || normalized.Contains(":", StringComparison.Ordinal)
                    || normalized.Contains('\0'))
                {
                    return RejectAndCleanup(
                        "Update archive contains an unsafe entry.",
                        writtenFiles);
                }

                if (!names.Add(normalized))
                {
                    return RejectAndCleanup(
                        "Update archive contains duplicate entries.",
                        writtenFiles);
                }

                if (entry.Length < 0
                    || entry.Length > MaxEntryBytes
                    || (entry.CompressedLength > 0
                        && entry.Length / (double)entry.CompressedLength > 100))
                {
                    return RejectAndCleanup(
                        "Update archive entry exceeds safety bounds.",
                        writtenFiles);
                }

                total = checked(total + entry.Length);
                if (total > MaxTotalBytes)
                {
                    return RejectAndCleanup(
                        "Update archive exceeds the total extraction bound.",
                        writtenFiles);
                }

                var destination = RootPathRules.ResolveContainedPath(
                    staging,
                    staging,
                    normalized,
                    nameof(operationId));

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // Re-resolve immediately before file mutation so a reparse point introduced
                // after parent creation cannot redirect extraction outside this operation root.
                destination = RootPathRules.ResolveContainedPath(
                    staging,
                    staging,
                    normalized,
                    nameof(operationId));

                await using var input = entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                writtenFiles.Add(destination);
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            }

            var validation = _validator.Validate(manifest, staging);
            if (!validation.IsAccepted)
            {
                CleanupWrittenFiles(writtenFiles);
            }

            return validation;
        }
        catch (OperationCanceledException)
        {
            CleanupWrittenFiles(writtenFiles);
            throw;
        }
        catch (ArgumentException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject(
                $"Update archive staging rejected an unsafe path: {ex.Message}");
        }
        catch (InvalidDataException)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject("Update archive is not a valid ZIP payload.");
        }
        catch (IOException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject($"Update archive staging failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CleanupWrittenFiles(writtenFiles);
            return UpdatePackageValidationResult.Reject($"Update archive staging access was denied: {ex.Message}");
        }
    }

    private static UpdatePackageValidationResult RejectAndCleanup(
        string error,
        IEnumerable<string> writtenFiles)
    {
        CleanupWrittenFiles(writtenFiles);
        return UpdatePackageValidationResult.Reject(error);
    }

    private static void CleanupWrittenFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup is deliberately limited to files created by this invocation.
            }
        }
    }
}
