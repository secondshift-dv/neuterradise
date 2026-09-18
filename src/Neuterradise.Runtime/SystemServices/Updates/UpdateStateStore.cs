using System.Text.Json;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Updates;

public enum UpdateStartupCutoff
{
    None,
    HandoffPending,
    CatalogWriteStarted,
    RecoveryRequired,
    Completed,
    AbortedNoMutation
}

public sealed record UpdateState(
    Guid OperationId,
    UpdateStartupCutoff Cutoff,
    string? BackupPath,
    DateTimeOffset UpdatedAtUtc,
    string? AbortReason = null)
{
    public bool MayAutoRollbackBinary => Cutoff < UpdateStartupCutoff.CatalogWriteStarted && Cutoff != UpdateStartupCutoff.AbortedNoMutation;
    public bool IsTerminal => Cutoff is UpdateStartupCutoff.Completed or UpdateStartupCutoff.AbortedNoMutation;
    public bool BlocksStartup => Cutoff == UpdateStartupCutoff.RecoveryRequired;
}

public sealed class UpdateStateStore
{
    private readonly AppStatePaths _paths;

    public UpdateStateStore(AppStatePaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task SaveAsync(UpdateState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.OperationId == Guid.Empty)
            throw new ArgumentException("Update state operation id is required.", nameof(state));

        var path = _paths.UpdateStateFilePath;
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<UpdateState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.UpdateStateFilePath;
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer
            .DeserializeAsync<UpdateState>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (state is not null && state.OperationId == Guid.Empty)
            throw new FormatException("Persisted update state has an invalid operation id.");

        return state;
    }

    public Task PublishHandoffPendingAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        SaveAsync(new UpdateState(operationId, UpdateStartupCutoff.HandoffPending, null, DateTimeOffset.UtcNow), cancellationToken);

    public Task PublishCatalogWriteStartedAsync(Guid operationId, string? backupPath, CancellationToken cancellationToken = default) =>
        SaveAsync(new UpdateState(operationId, UpdateStartupCutoff.CatalogWriteStarted, backupPath, DateTimeOffset.UtcNow), cancellationToken);

    public Task PublishCompletedAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        SaveAsync(new UpdateState(operationId, UpdateStartupCutoff.Completed, null, DateTimeOffset.UtcNow), cancellationToken);

    public Task PublishRecoveryRequiredAsync(Guid operationId, string? backupPath, CancellationToken cancellationToken = default) =>
        SaveAsync(new UpdateState(operationId, UpdateStartupCutoff.RecoveryRequired, backupPath, DateTimeOffset.UtcNow), cancellationToken);

    public Task PublishAbortedNoMutationAsync(Guid operationId, string? reason, CancellationToken cancellationToken = default) =>
        SaveAsync(new UpdateState(operationId, UpdateStartupCutoff.AbortedNoMutation, null, DateTimeOffset.UtcNow, reason), cancellationToken);

    public async Task RetirePendingStateAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current.OperationId != operationId || current.IsTerminal)
            return;

        if (current.Cutoff is UpdateStartupCutoff.HandoffPending or UpdateStartupCutoff.None)
        {
            await PublishAbortedNoMutationAsync(
                operationId,
                "Handoff retired before replacement began.",
                cancellationToken).ConfigureAwait(false);
        }

        // CatalogWriteStarted and RecoveryRequired are deliberately not promoted to Completed here.
        // Those states require evidence from successful startup or explicit recovery respectively.
    }
}
