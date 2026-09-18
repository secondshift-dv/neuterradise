using Neuterradise.App.Import.Preparation;

namespace Neuterradise.App.SystemServices.Database.Writes;

public sealed class CapabilityWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public CapabilityWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Upsert one capability row. Idempotent: if the row already exists with the same or later
    /// state, the write is a no-op.
    /// </summary>
    public async Task UpsertCapabilityAsync(
        Guid assetId,
        AssetCapability capability,
        AssetCapabilityState state,
        Guid? jobId = null,
        string? sourceFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO asset_capability_readiness (asset_id, capability, state, job_id, source_fingerprint, updated_at_ms)
            VALUES ($assetId, $capability, $state, $jobId, $fingerprint, $now)
            ON CONFLICT (asset_id, capability) DO UPDATE SET
                state = CASE
                    WHEN excluded.state IN ('READY', 'NOT_APPLICABLE') THEN excluded.state
                    WHEN excluded.state = 'FAILED' AND asset_capability_readiness.state NOT IN ('READY', 'NOT_APPLICABLE') THEN excluded.state
                    WHEN excluded.state = 'PROCESSING' AND asset_capability_readiness.state = 'QUEUED' THEN excluded.state
                    ELSE asset_capability_readiness.state
                END,
                job_id = COALESCE(excluded.job_id, asset_capability_readiness.job_id),
                source_fingerprint = COALESCE(excluded.source_fingerprint, asset_capability_readiness.source_fingerprint),
                updated_at_ms = excluded.updated_at_ms
            WHERE asset_capability_readiness.state NOT IN ('READY', 'NOT_APPLICABLE')
               OR excluded.state IN ('READY', 'NOT_APPLICABLE');
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        command.Parameters.AddWithValue("$capability", DbEnum.Format(capability));
        command.Parameters.AddWithValue("$state", DbEnum.Format(state));
        command.Parameters.AddWithValue("$jobId", jobId.HasValue ? DbGuid.Format(jobId.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Format(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seed all applicable capabilities for an asset in QUEUED state.
    /// Idempotent: existing rows with terminal states are not disturbed.
    /// </summary>
    public async Task SeedCapabilitiesAsync(
        Guid assetId,
        IReadOnlyList<AssetCapability> requiredCapabilities,
        CancellationToken cancellationToken = default)
    {
        foreach (var cap in requiredCapabilities)
        {
            await UpsertCapabilityAsync(assetId, cap, AssetCapabilityState.Queued, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transition a capability to NOT_APPLICABLE. Used for FaceEmbedding when no faces are detected.
    /// </summary>
    public async Task MarkNotApplicableAsync(
        Guid assetId,
        AssetCapability capability,
        CancellationToken cancellationToken = default)
    {
        await UpsertCapabilityAsync(assetId, capability, AssetCapabilityState.NotApplicable, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delete all capability rows for assets in a unit. Used for restart/resume cleanup only.
    /// </summary>
    public async Task DeleteUnitCapabilitiesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM asset_capability_readiness
            WHERE asset_id IN (
                SELECT ii.candidate_asset_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.candidate_asset_id IS NOT NULL
            );
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
