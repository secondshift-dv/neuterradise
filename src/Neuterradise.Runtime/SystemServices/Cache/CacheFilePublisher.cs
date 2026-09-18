using System.IO;

namespace Neuterradise.App.SystemServices.Cache;

public sealed class CacheFilePublisher
{
    private readonly CachePaths _paths;
    private readonly PersistentCacheBudget _budget;

    public CacheFilePublisher(CachePaths paths, PersistentCacheBudget budget)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(budget);

        _paths = paths;
        _budget = budget;
    }

    public CachePaths Paths => _paths;

    public PersistentCacheBudget Budget => _budget;

    public Task<bool> PublishAsync(
        string targetPath,
        ReadOnlyMemory<byte> data,
        Func<string, bool>? validator = null,
        CancellationToken ct = default)
    {
        return PublishStreamAsync(
            targetPath,
            async stream =>
            {
                await stream.WriteAsync(data, ct);
            },
            validator,
            ct);
    }

    public async Task<bool> PublishStreamAsync(
        string targetPath,
        Func<Stream, Task> writeAction,
        Func<string, bool>? validator = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(writeAction);

        if (!_paths.IsContainedCachePath(targetPath))
        {
            throw new ArgumentException($"Target path '{targetPath}' is not contained within the cache root.", nameof(targetPath));
        }

        Directory.CreateDirectory(_paths.TempPath);
        var tempFile = Path.Combine(_paths.TempPath, $"{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await writeAction(stream);
                await stream.FlushAsync(ct);
            }

            var tempInfo = new FileInfo(tempFile);
            if (!tempInfo.Exists || tempInfo.Length == 0)
            {
                TryDelete(tempFile);
                return false;
            }

            if (validator != null && !validator(tempFile))
            {
                TryDelete(tempFile);
                return false;
            }

            if (!_budget.EnsureCapacity(tempInfo.Length, out _))
            {
                TryDelete(tempFile);
                return false;
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Move(tempFile, targetPath, overwrite: true);
            _budget.RecordAccess(targetPath);
            return true;
        }
        catch
        {
            TryDelete(tempFile);
            throw;
        }
    }

    public bool PublishTempFile(
        string sourceTempFile,
        string targetPath,
        Func<string, bool>? validator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTempFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!_paths.IsContainedCachePath(targetPath))
        {
            throw new ArgumentException($"Target path '{targetPath}' is not contained within the cache root.", nameof(targetPath));
        }

        if (!File.Exists(sourceTempFile))
        {
            return false;
        }

        var sourceInfo = new FileInfo(sourceTempFile);
        if (sourceInfo.Length == 0 || (validator != null && !validator(sourceTempFile)))
        {
            TryDelete(sourceTempFile);
            return false;
        }

        if (!_budget.EnsureCapacity(sourceInfo.Length, out _))
        {
            TryDelete(sourceTempFile);
            return false;
        }

        try
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Move(sourceTempFile, targetPath, overwrite: true);
            _budget.RecordAccess(targetPath);
            return true;
        }
        catch
        {
            TryDelete(sourceTempFile);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {

        }
    }
}
