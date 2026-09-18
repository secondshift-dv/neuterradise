using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateRecoveryPlan(
    Guid OperationId,
    string InstallRoot,
    string StagingRoot,
    string BackupRoot,
    string PayloadRoot,
    string Step,
    DateTimeOffset UpdatedAtUtc)
{
    public void Validate(InstallPaths install, AppStatePaths appState, string? vaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(appState);
        if (OperationId == Guid.Empty)
            throw new FormatException("Update operation id is invalid.");
        if (string.IsNullOrWhiteSpace(Step))
            throw new FormatException("Update recovery step is missing.");

        install.EnsureDisjointFrom(appState, vaultRoot);

        var normalizedInstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallRoot));
        if (!RootPathRules.AreSameRoot(normalizedInstallRoot, install.Root))
            throw new FormatException("Recovery InstallRoot does not match the approved deployment.");

        var expectedPayloadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(appState.UpdateStagingPath, OperationId.ToString("D"))));
        if (string.IsNullOrWhiteSpace(PayloadRoot)
            || !RootPathRules.AreSameRoot(Path.TrimEndingDirectorySeparator(Path.GetFullPath(PayloadRoot)), expectedPayloadRoot))
        {
            throw new FormatException("Recovery payload root does not match the approved AppState operation staging directory.");
        }

        RootPathRules.RejectExistingReparsePoints(appState.Root, expectedPayloadRoot);

        if (string.Equals(Step, "AbortedParentStillRunning", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(StagingRoot) || !string.IsNullOrWhiteSpace(BackupRoot))
                ValidateDeterministicReplacementSiblings(normalizedInstallRoot, appState, vaultRoot);
            return;
        }

        ValidateDeterministicReplacementSiblings(normalizedInstallRoot, appState, vaultRoot);
    }

    private void ValidateDeterministicReplacementSiblings(
        string normalizedInstallRoot,
        AppStatePaths appState,
        string? vaultRoot)
    {
        var parentDirectory = Path.GetDirectoryName(normalizedInstallRoot);
        var installDirectoryName = Path.GetFileName(normalizedInstallRoot);
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(installDirectoryName))
            throw new FormatException("Recovery InstallRoot cannot be a filesystem drive root.");

        var expectedStagingRoot = Path.Combine(parentDirectory, $"{installDirectoryName}.staged.{OperationId:N}");
        var expectedBackupRoot = Path.Combine(parentDirectory, $"{installDirectoryName}.backup.{OperationId:N}");
        if (string.IsNullOrWhiteSpace(StagingRoot) || string.IsNullOrWhiteSpace(BackupRoot))
            throw new FormatException("Recovery replacement sibling paths are incomplete.");

        var normalizedStagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StagingRoot));
        var normalizedBackupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BackupRoot));
        if (!RootPathRules.AreSameRoot(normalizedStagingRoot, expectedStagingRoot))
            throw new FormatException("Recovery staging root does not match the operation's approved InstallRoot sibling.");
        if (!RootPathRules.AreSameRoot(normalizedBackupRoot, expectedBackupRoot))
            throw new FormatException("Recovery backup root does not match the operation's approved InstallRoot sibling.");

        RootPathRules.EnsureDisjoint("StagingRoot", normalizedStagingRoot, "AppStateRoot", appState.Root);
        RootPathRules.EnsureDisjoint("BackupRoot", normalizedBackupRoot, "AppStateRoot", appState.Root);
        if (!string.IsNullOrWhiteSpace(vaultRoot))
        {
            RootPathRules.EnsureDisjoint("StagingRoot", normalizedStagingRoot, "VaultRoot", vaultRoot);
            RootPathRules.EnsureDisjoint("BackupRoot", normalizedBackupRoot, "VaultRoot", vaultRoot);
        }
    }
}

public sealed class UpdateRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AppStatePaths _appState;

    public UpdateRecoveryStore(AppStatePaths appState) =>
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));

    public async Task SaveAsync(UpdateRecoveryPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var path = GetRecoveryPlanPath(plan.OperationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<UpdateRecoveryPlan?> LoadAsync(
        Guid operationId,
        InstallPaths install,
        string? vaultRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Update operation id is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(install);

        var path = GetRecoveryPlanPath(operationId);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var plan = await JsonSerializer
            .DeserializeAsync<UpdateRecoveryPlan>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FormatException("Update recovery plan is empty.");

        if (plan.OperationId != operationId)
            throw new FormatException("Update recovery plan operation id does not match its operation directory.");

        plan.Validate(install, _appState, vaultRoot);
        return plan;
    }

    public async Task<UpdateHandoff?> LoadHandoffAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Update operation id is required.", nameof(operationId));

        var path = Path.Combine(_appState.GetUpdateToolsPath(operationId), "handoff.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var handoff = await JsonSerializer
            .DeserializeAsync<UpdateHandoff>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FormatException("Update handoff is empty.");

        if (handoff.OperationId != operationId)
            throw new FormatException("Update handoff operation id does not match its operation directory.");

        handoff.Manifest.Validate();
        return handoff;
    }

    private string GetRecoveryPlanPath(Guid operationId) =>
        Path.Combine(_appState.GetUpdateToolsPath(operationId), "recovery-plan.json");
}
