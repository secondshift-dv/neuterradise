using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateStartupRecoveryResult(
    bool CanContinue,
    bool ReplacementInstalled,
    Guid? OperationId,
    string? BackupPath,
    string? SafeError)
{
    public static UpdateStartupRecoveryResult NoAction() => new(true, false, null, null, null);
    public static UpdateStartupRecoveryResult ReplacementReady(Guid operationId, string? backupPath) =>
        new(true, true, operationId, backupPath, null);
    public static UpdateStartupRecoveryResult Blocked(Guid operationId, string? backupPath, string error) =>
        new(false, false, operationId, backupPath, error);
}

public sealed class UpdateStartupRecovery
{
    private readonly AppStatePaths _appState;
    private readonly InstallPaths _install;
    private readonly string? _vaultRoot;
    private readonly UpdateStateStore _stateStore;
    private readonly UpdateRecoveryStore _recoveryStore;

    public UpdateStartupRecovery(
        AppStatePaths appState,
        InstallPaths install,
        string? vaultRoot,
        UpdateStateStore? stateStore = null,
        UpdateRecoveryStore? recoveryStore = null)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _vaultRoot = vaultRoot;
        _install.EnsureDisjointFrom(_appState, _vaultRoot);
        _stateStore = stateStore ?? new UpdateStateStore(_appState);
        _recoveryStore = recoveryStore ?? new UpdateRecoveryStore(_appState);
    }

    public async Task<UpdateStartupRecoveryResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.IsTerminal)
            return UpdateStartupRecoveryResult.NoAction();

        if (state.Cutoff == UpdateStartupCutoff.RecoveryRequired)
            return UpdateStartupRecoveryResult.Blocked(state.OperationId, state.BackupPath, "Update replacement recovery is required before startup can continue.");

        if (state.Cutoff == UpdateStartupCutoff.CatalogWriteStarted)
            return UpdateStartupRecoveryResult.ReplacementReady(state.OperationId, state.BackupPath);

        if (state.Cutoff is not (UpdateStartupCutoff.HandoffPending or UpdateStartupCutoff.None))
            return await BlockAsync(state.OperationId, state.BackupPath, "Persisted update state is not recognized for startup reconciliation.", cancellationToken).ConfigureAwait(false);

        var paths = GetReplacementPaths(state.OperationId);
        UpdateRecoveryPlan? plan;
        try
        {
            plan = await _recoveryStore.LoadAsync(state.OperationId, _install, _vaultRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            plan = null;
        }

        if (plan is null)
        {
            if (Directory.Exists(paths.BackupRoot) || !Directory.Exists(_install.Root))
                return await BlockAsync(state.OperationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Update recovery journal is missing or invalid after possible filesystem mutation.", cancellationToken).ConfigureAwait(false);

            await _stateStore.PublishAbortedNoMutationAsync(state.OperationId, "No replacement filesystem mutation is present.", cancellationToken).ConfigureAwait(false);
            return UpdateStartupRecoveryResult.NoAction();
        }

        if (plan.Step is "AbortedParentStillRunning" or "StagingPrepared" or "RestoredFromBackup")
        {
            if (Directory.Exists(_install.Root) && !Directory.Exists(paths.BackupRoot))
            {
                await _stateStore.PublishAbortedNoMutationAsync(state.OperationId, $"Updater ended at safe step {plan.Step} before a replacement remained installed.", cancellationToken).ConfigureAwait(false);
                return UpdateStartupRecoveryResult.NoAction();
            }

            return await BlockAsync(state.OperationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, $"Update filesystem state contradicts recovery step {plan.Step}.", cancellationToken).ConfigureAwait(false);
        }

        if (plan.Step == "ReplacementCompleted")
            return await VerifyInstalledReplacementAsync(state.OperationId, paths, cancellationToken).ConfigureAwait(false);

        if (plan.Step is "InstallMovedToBackup" or "RestoreFailed" or "ReplacementFailed"
            || plan.Step.StartsWith("Failed:", StringComparison.Ordinal))
        {
            if (!Directory.Exists(_install.Root) && Directory.Exists(paths.BackupRoot))
            {
                try
                {
                    Directory.Move(paths.BackupRoot, _install.Root);
                    await _stateStore.PublishAbortedNoMutationAsync(state.OperationId, "Interrupted replacement was restored from its deterministic backup before catalog access.", cancellationToken).ConfigureAwait(false);
                    return UpdateStartupRecoveryResult.NoAction();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return await BlockAsync(state.OperationId, paths.BackupRoot, "Interrupted replacement backup could not be restored safely.", cancellationToken).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(_install.Root) && Directory.Exists(paths.BackupRoot))
                return await VerifyInstalledReplacementAsync(state.OperationId, paths, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(_install.Root) && !Directory.Exists(paths.BackupRoot) && plan.Step == "ReplacementFailed")
            {
                await _stateStore.PublishAbortedNoMutationAsync(state.OperationId, "Updater failed before InstallRoot was moved.", cancellationToken).ConfigureAwait(false);
                return UpdateStartupRecoveryResult.NoAction();
            }

            return await BlockAsync(state.OperationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Update replacement filesystem state is ambiguous.", cancellationToken).ConfigureAwait(false);
        }

        return await BlockAsync(state.OperationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, $"Unsupported update recovery step '{plan.Step}'.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateStartupRecoveryResult> VerifyInstalledReplacementAsync(
        Guid operationId,
        ReplacementPaths paths,
        CancellationToken cancellationToken)
    {
        UpdateHandoff? handoff;
        try
        {
            handoff = await _recoveryStore.LoadHandoffAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            handoff = null;
        }

        if (handoff is null)
            return await BlockAsync(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Installed replacement has no valid handoff evidence.", cancellationToken).ConfigureAwait(false);

        var expectedPayload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.PayloadRoot));
        var actualPayload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.SourcePayloadPath));
        var actualInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.DestinationInstallRoot));
        if (!RootPathRules.AreSameRoot(expectedPayload, actualPayload) || !RootPathRules.AreSameRoot(_install.Root, actualInstall))
            return await BlockAsync(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Installed replacement handoff violates runtime path authority.", cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_vaultRoot))
        {
            if (string.IsNullOrWhiteSpace(handoff.VaultRoot))
                return await BlockAsync(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Installed replacement handoff is missing VaultRoot protection evidence.", cancellationToken).ConfigureAwait(false);

            var actualVault = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.VaultRoot));
            if (!RootPathRules.AreSameRoot(_vaultRoot, actualVault))
                return await BlockAsync(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, "Installed replacement handoff VaultRoot does not match startup authority.", cancellationToken).ConfigureAwait(false);
        }

        var validation = new UpdatePackageValidator(_install).Validate(handoff.Manifest, _install.Root);
        if (!validation.IsAccepted)
            return await BlockAsync(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null, validation.SafeError ?? "Installed replacement does not match the approved manifest.", cancellationToken).ConfigureAwait(false);

        return UpdateStartupRecoveryResult.ReplacementReady(operationId, Directory.Exists(paths.BackupRoot) ? paths.BackupRoot : null);
    }

    private async Task<UpdateStartupRecoveryResult> BlockAsync(
        Guid operationId,
        string? backupPath,
        string reason,
        CancellationToken cancellationToken)
    {
        await _stateStore.PublishRecoveryRequiredAsync(operationId, backupPath, cancellationToken).ConfigureAwait(false);
        return UpdateStartupRecoveryResult.Blocked(operationId, backupPath, reason);
    }

    private ReplacementPaths GetReplacementPaths(Guid operationId)
    {
        var parent = Path.GetDirectoryName(_install.Root) ?? throw new InvalidOperationException("InstallRoot cannot be a filesystem drive root.");
        var name = Path.GetFileName(_install.Root);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("InstallRoot cannot be a filesystem drive root.");

        var staging = Path.Combine(parent, $"{name}.staged.{operationId:N}");
        var backup = Path.Combine(parent, $"{name}.backup.{operationId:N}");
        var payload = Path.Combine(_appState.UpdateStagingPath, operationId.ToString("D"));
        RootPathRules.EnsureDisjoint("UpdateStagingRoot", staging, "AppStateRoot", _appState.Root);
        RootPathRules.EnsureDisjoint("UpdateBackupRoot", backup, "AppStateRoot", _appState.Root);
        if (!string.IsNullOrWhiteSpace(_vaultRoot))
        {
            RootPathRules.EnsureDisjoint("UpdateStagingRoot", staging, "VaultRoot", _vaultRoot);
            RootPathRules.EnsureDisjoint("UpdateBackupRoot", backup, "VaultRoot", _vaultRoot);
        }

        return new ReplacementPaths(staging, backup, payload);
    }

    private sealed record ReplacementPaths(string StagingRoot, string BackupRoot, string PayloadRoot);
}