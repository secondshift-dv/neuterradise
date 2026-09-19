using Neuterradise.App.Import.Preparation;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed record AssetCapabilityRow(
    Guid AssetId,
    AssetCapability Capability,
    AssetCapabilityState State,
    Guid? JobId,
    string? SourceFingerprint);

public sealed record UnitCapabilitySummary(
    Guid UnitId,
    int TotalAssets,
    int AssetsAllCapabilitiesTerminal,
    bool AllRequiredTerminal);

public sealed class CapabilityReads
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public CapabilityReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public async Task<IReadOnlyList<AssetCapabilityRow>> GetAssetCapabilitiesAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT capability, state, job_id, source_fingerprint
            FROM asset_capability_readiness
            WHERE asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var rows = new List<AssetCapabilityRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AssetCapabilityRow(
                assetId,
                DbEnum.ParseAssetCapability(reader.GetString(0)),
                DbEnum.ParseAssetCapabilityState(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DbGuid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Returns readiness over the complete eligible asset denominator, but only the canonical
    /// Required capability set gates readiness.
    /// </summary>
    public async Task<UnitCapabilitySummary> GetUnitCapabilitySummaryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT CASE
                    WHEN ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL THEN ii.reused_asset_id
                    ELSE ii.candidate_asset_id
                END AS asset_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND ((ii.disposition='REUSED' AND ii.reused_asset_id IS NOT NULL)
                    OR (ii.disposition='INCLUDED' AND ii.candidate_asset_id IS NOT NULL))
            )
            SELECT ea.asset_id, a.media_type, cr.capability, cr.state
            FROM eligible_assets ea
            JOIN assets a ON a.asset_id = ea.asset_id
            LEFT JOIN asset_capability_readiness cr ON cr.asset_id = ea.asset_id
            ORDER BY ea.asset_id, cr.capability;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var assets = new Dictionary<Guid, CapabilityAssetSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            if (!assets.TryGetValue(assetId, out var snapshot))
            {
                snapshot = new CapabilityAssetSnapshot(
                    DbEnum.ParseMediaType(reader.GetString(1)),
                    new Dictionary<AssetCapability, AssetCapabilityState>());
                assets.Add(assetId, snapshot);
            }

            if (!reader.IsDBNull(2) && !reader.IsDBNull(3))
            {
                snapshot.States[DbEnum.ParseAssetCapability(reader.GetString(2))] =
                    DbEnum.ParseAssetCapabilityState(reader.GetString(3));
            }
        }

        var terminalAssets = 0;
        foreach (var snapshot in assets.Values)
        {
            var required = CapabilityApplicability.GetRequired(snapshot.MediaType);
            if (required.All(capability =>
                    snapshot.States.TryGetValue(capability, out var state)
                    && state is AssetCapabilityState.Ready or AssetCapabilityState.NotApplicable))
            {
                terminalAssets++;
            }
        }

        return new UnitCapabilitySummary(
            unitId,
            assets.Count,
            terminalAssets,
            assets.Count > 0 && terminalAssets == assets.Count);
    }

    /// <summary>
    /// Returns assets whose Required capability set contains a durable FAILED row. Optional
    /// profiling/search/similarity failure may degrade features but cannot fail the import.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetUnitFailedAssetIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT CASE
                    WHEN ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL THEN ii.reused_asset_id
                    ELSE ii.candidate_asset_id
                END AS asset_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED','REUSED')
                  AND ((ii.disposition='REUSED' AND ii.reused_asset_id IS NOT NULL)
                    OR (ii.disposition='INCLUDED' AND ii.candidate_asset_id IS NOT NULL))
            )
            SELECT DISTINCT cr.asset_id, a.media_type, cr.capability
            FROM eligible_assets ea
            JOIN assets a ON a.asset_id = ea.asset_id
            JOIN asset_capability_readiness cr ON cr.asset_id = ea.asset_id
            WHERE cr.state = 'FAILED';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var mediaType = DbEnum.ParseMediaType(reader.GetString(1));
            var capability = DbEnum.ParseAssetCapability(reader.GetString(2));
            if (CapabilityApplicability.GetRequired(mediaType).Contains(capability))
            {
                ids.Add(assetId);
            }
        }

        return ids.Order().ToArray();
    }

    private sealed record CapabilityAssetSnapshot(
        Neuterradise.App.Media.MediaType MediaType,
        Dictionary<AssetCapability, AssetCapabilityState> States);

}
