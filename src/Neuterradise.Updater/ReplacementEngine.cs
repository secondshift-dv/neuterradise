using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Neuterradise.Updater;

public sealed class ReplacementEngine
{
    private const int MaxWaitParentExitSeconds = 60;

    public static async Task<ReplacementResult> ExecuteAsync(
        string handoffPath,
        int? parentPid,
        bool launchApp,
        CancellationToken cancellationToken = default)
    {
        string normalizedHandoffPath;
        string operationDir;
        try
        {
            normalizedHandoffPath = Path.GetFullPath(handoffPath);
            operationDir = Path.GetDirectoryName(normalizedHandoffPath)
                ?? throw new InvalidOperationException("Handoff path has no operation directory.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Invalid handoff path: {ex.Message}");
        }

        var recoveryPlanPath = Path.Combine(operationDir, "recovery-plan.json");

        HandoffData handoff;
        try
        {
            var json = await File.ReadAllTextAsync(normalizedHandoffPath, cancellationToken);
            handoff = JsonSerializer.Deserialize<HandoffData>(json, JsonOptions)
                ?? throw new InvalidOperationException("Handoff data is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Failed to parse handoff JSON: {ex.Message}");
        }

        if (handoff.OperationId == Guid.Empty)
            return ReplacementResult.Failed("Handoff operation id is invalid.");
        if (string.IsNullOrWhiteSpace(handoff.VaultRoot))
            return ReplacementResult.Failed("Handoff is missing the protected VaultRoot authority.");

        string installRoot;
        string payloadPath;
        string appStateRoot;
        string vaultRoot;
        try
        {
            installRoot = TrimRoot(Path.GetFullPath(handoff.DestinationInstallRoot));
            payloadPath = TrimRoot(Path.GetFullPath(handoff.SourcePayloadPath));
            vaultRoot = TrimRoot(Path.GetFullPath(handoff.VaultRoot));

            if (!string.Equals(Path.GetFileName(operationDir), handoff.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return ReplacementResult.Failed("Handoff operation directory does not match the operation id.");

            var updateToolsRoot = Directory.GetParent(operationDir)?.FullName;
            if (string.IsNullOrWhiteSpace(updateToolsRoot)
                || !string.Equals(Path.GetFileName(TrimRoot(updateToolsRoot)), "update-tools", StringComparison.OrdinalIgnoreCase))
            {
                return ReplacementResult.Failed("Handoff is not located under the approved update-tools authority.");
            }

            appStateRoot = TrimRoot(Directory.GetParent(updateToolsRoot)?.FullName
                ?? throw new InvalidOperationException("Update-tools authority has no AppState root."));

            var expectedPayloadPath = TrimRoot(Path.Combine(
                appStateRoot,
                "updates",
                "staging",
                handoff.OperationId.ToString("D")));
            if (!SamePath(payloadPath, expectedPayloadPath))
                return ReplacementResult.Failed("Source payload is not the approved operation-scoped AppState staging directory.");
            if (Overlaps(installRoot, appStateRoot))
                return ReplacementResult.Failed("InstallRoot and AppStateRoot must be disjoint.");
            if (Overlaps(installRoot, vaultRoot))
                return ReplacementResult.Failed("InstallRoot and VaultRoot must be disjoint.");
            if (Overlaps(appStateRoot, vaultRoot))
                return ReplacementResult.Failed("AppStateRoot and VaultRoot must be disjoint.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReplacementResult.Failed($"Handoff runtime path authority is invalid: {ex.Message}");
        }

        if (!Directory.Exists(installRoot))
            return ReplacementResult.Failed($"Destination InstallRoot does not exist: {installRoot}");
        if (!Directory.Exists(payloadPath))
            return ReplacementResult.Failed($"Expected extracted payload directory does not exist: {payloadPath}");

        var parentDir = Path.GetDirectoryName(installRoot);
        var dirName = Path.GetFileName(installRoot);
        if (string.IsNullOrEmpty(parentDir) || string.IsNullOrWhiteSpace(dirName))
            return ReplacementResult.Failed("InstallRoot cannot be a filesystem drive root.");

        var backupRoot = Path.Combine(parentDir, $"{dirName}.backup.{handoff.OperationId:N}");
        var stagingRoot = Path.Combine(parentDir, $"{dirName}.staged.{handoff.OperationId:N}");
        if (Overlaps(stagingRoot, appStateRoot) || Overlaps(backupRoot, appStateRoot))
            return ReplacementResult.Failed("Replacement sibling paths overlap AppStateRoot.");
        if (Overlaps(stagingRoot, vaultRoot) || Overlaps(backupRoot, vaultRoot))
            return ReplacementResult.Failed("Replacement sibling paths overlap VaultRoot.");

        if (parentPid.HasValue && parentPid.Value > 0)
        {
            var exited = await WaitForProcessExitAsync(
                parentPid.Value,
                TimeSpan.FromSeconds(MaxWaitParentExitSeconds),
                cancellationToken);
            if (!exited)
            {
                await PersistRecoveryStepAsync(
                    recoveryPlanPath,
                    handoff.OperationId,
                    installRoot,
                    stagingRoot,
                    backupRoot,
                    payloadPath,
                    "AbortedParentStillRunning",
                    cancellationToken);
                return ReplacementResult.Failed($"Parent process (PID {parentPid.Value}) did not exit within {MaxWaitParentExitSeconds} seconds. Aborting replacement without mutating files.");
            }
        }

        var installMovedToBackup = false;

        try
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);

            CopyDirectory(payloadPath, stagingRoot);
            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                "StagingPrepared",
                cancellationToken);

            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);

            Directory.Move(installRoot, backupRoot);
            installMovedToBackup = true;
            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                "InstallMovedToBackup",
                CancellationToken.None);

            try
            {
                Directory.Move(stagingRoot, installRoot);
                await PersistRecoveryStepAsync(
                    recoveryPlanPath,
                    handoff.OperationId,
                    installRoot,
                    stagingRoot,
                    backupRoot,
                    payloadPath,
                    "ReplacementCompleted",
                    CancellationToken.None);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                try
                {
                    if (Directory.Exists(backupRoot) && !Directory.Exists(installRoot))
                    {
                        Directory.Move(backupRoot, installRoot);
                        await PersistRecoveryStepAsync(
                            recoveryPlanPath,
                            handoff.OperationId,
                            installRoot,
                            stagingRoot,
                            backupRoot,
                            payloadPath,
                            "RestoredFromBackup",
                            CancellationToken.None);
                    }
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    await PersistRecoveryStepAsync(
                        recoveryPlanPath,
                        handoff.OperationId,
                        installRoot,
                        stagingRoot,
                        backupRoot,
                        payloadPath,
                        "RestoreFailed",
                        CancellationToken.None);
                    return ReplacementResult.Failed($"Replacement failed and restoration also failed: {moveEx.Message} | Restore error: {restoreEx.Message}");
                }

                return ReplacementResult.Failed($"Replacement failed and was restored from backup: {moveEx.Message}");
            }

            if (launchApp)
            {
                var appExe = Path.Combine(installRoot, "NeuTerradise.exe");
                if (File.Exists(appExe))
                {
                    var startInfo = new ProcessStartInfo(appExe)
                    {
                        WorkingDirectory = installRoot,
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                }
            }

            return ReplacementResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await PersistRecoveryStepAsync(
                recoveryPlanPath,
                handoff.OperationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadPath,
                "ReplacementFailed",
                installMovedToBackup ? CancellationToken.None : cancellationToken);
            return ReplacementResult.Failed(ex.Message);
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return true;

            var start = Stopwatch.GetTimestamp();
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(start) > timeout)
                    return false;

                await Task.Delay(250, cancellationToken);
                process.Refresh();
            }

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if ((File.GetAttributes(sourceDir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Update payload root cannot be a reparse point.");

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update payload cannot contain reparse-point files.");

            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update payload cannot contain reparse-point directories.");

            var destSub = Path.Combine(targetDir, Path.GetFileName(sub));
            CopyDirectory(sub, destSub);
        }
    }

    private static async Task PersistRecoveryStepAsync(
        string planPath,
        Guid operationId,
        string installRoot,
        string stagingRoot,
        string backupRoot,
        string payloadRoot,
        string step,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new
            {
                operationId,
                installRoot,
                stagingRoot,
                backupRoot,
                payloadRoot,
                step,
                updatedAtUtc = DateTimeOffset.UtcNow
            };
            var temp = planPath + ".tmp";
            var json = JsonSerializer.Serialize(record, JsonOptions);
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, planPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The deterministic sibling paths and update-state operation id remain recoverable evidence
            // even when the best-effort diagnostic journal cannot be persisted.
        }
    }

    private static string TrimRoot(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool SamePath(string left, string right) =>
        string.Equals(TrimRoot(left), TrimRoot(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinOrEqual(string ancestor, string candidate)
    {
        var normalizedAncestor = TrimRoot(ancestor);
        var normalizedCandidate = TrimRoot(candidate);
        if (SamePath(normalizedAncestor, normalizedCandidate))
            return true;

        return normalizedCandidate.StartsWith(
            normalizedAncestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string left, string right) =>
        IsWithinOrEqual(left, right) || IsWithinOrEqual(right, left);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record ReplacementResult(bool Success, string? Error)
{
    public static ReplacementResult Succeeded() => new(true, null);
    public static ReplacementResult Failed(string error) => new(false, error);
}

internal sealed record HandoffData(
    Guid OperationId,
    string SourcePayloadPath,
    string DestinationInstallRoot,
    string? VaultRoot);