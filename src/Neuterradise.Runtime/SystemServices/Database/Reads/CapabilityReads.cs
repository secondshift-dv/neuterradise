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
    /// Returns a per-unit summary over the complete eligible asset denominator. Duplicate-reuse
    /// items resolve through reused_asset_id; ordinary included items resolve through candidate_asset_id.
    /// An eligible asset is terminal only when every deterministic capability row for its media type
    /// exists and no capability remains QUEUED, PROCESSING, or FAILED.
    /// </summary>
    public async Task<UnitCapabilitySummary> GetUnitCapabilitySummaryAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT
                    CASE
                        WHEN ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL
                            THEN ii.reused_asset_id
                        ELSE ii.candidate_asset_id
                    END AS asset_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED')
                  AND (
                      (ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL)
                      OR (ii.disposition = 'INCLUDED' AND ii.candidate_asset_id IS NOT NULL)
                  )
            )
            SELECT ea.asset_id,
                   a.media_type,
                   COUNT(cr.capability) AS capability_count,
                   SUM(CASE
                       WHEN cr.state IN ('QUEUED', 'PROCESSING', 'FAILED') THEN 1
                       ELSE 0
                   END) AS blocking_count
            FROM eligible_assets ea
            JOIN assets a ON a.asset_id = ea.asset_id
            LEFT JOIN asset_capability_readiness cr ON cr.asset_id = ea.asset_id
            GROUP BY ea.asset_id, a.media_type
            ORDER BY ea.asset_id;
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var totalAssets = 0;
        var assetsAllTerminal = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalAssets++;
            var mediaType = DbEnum.ParseMediaType(reader.GetString(1));
            var capabilityCount = reader.GetInt32(2);
            var blockingCount = reader.GetInt32(3);
            var expectedCapabilityCount = CapabilityApplicability.GetAll(mediaType).Count;

            // Missing readiness rows are blocking evidence, not omitted denominator members.
            if (capabilityCount >= expectedCapabilityCount && blockingCount == 0)
            {
                assetsAllTerminal++;
            }
        }

        return new UnitCapabilitySummary(
            unitId,
            totalAssets,
            assetsAllTerminal,
            totalAssets > 0 && assetsAllTerminal >= totalAssets);
    }

    /// <summary>
    /// Returns effective asset IDs that belong to the given import unit and have at least one
    /// FAILED capability. Duplicate-reuse items resolve through reused_asset_id so their failure
    /// projection cannot disappear behind the retired candidate identity.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetUnitFailedAssetIdsAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH eligible_assets AS (
                SELECT DISTINCT
                    CASE
                        WHEN ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL
                            THEN ii.reused_asset_id
                        ELSE ii.candidate_asset_id
                    END AS asset_id
                FROM import_items ii
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED')
                  AND (
                      (ii.disposition = 'REUSED' AND ii.reused_asset_id IS NOT NULL)
                      OR (ii.disposition = 'INCLUDED' AND ii.candidate_asset_id IS NOT NULL)
                  )
            )
            SELECT DISTINCT cr.asset_id
            FROM eligible_assets ea
            JOIN asset_capability_readiness cr ON cr.asset_id = ea.asset_id
            WHERE cr.state = 'FAILED';
            """;
        command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(DbGuid.Parse(reader.GetString(0)));
        }

        return ids;
    }
}
