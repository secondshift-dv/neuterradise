using System.IO;
using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import;

/// <summary>
/// Reads authoritative ownership evidence for one durable import unit and delegates the actual
/// decision to <see cref="AutomaticMediaAssignmentPolicy"/>. Exact-byte reuse/duplicate ownership is
/// authoritative; face suggestions, names and folder text are deliberately not promoted to ownership.
/// </summary>
public sealed class AutomaticImportAssignmentService
{
    private readonly CatalogDb _catalog;
    private readonly AutomaticMediaAssignmentPolicy _policy;

    public AutomaticImportAssignmentService(
        CatalogDb catalog,
        AutomaticMediaAssignmentPolicy? policy = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? new AutomaticMediaAssignmentPolicy();
    }

    public async Task<ImportAssignmentAssessment> AssessAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("Import unit id cannot be empty.", nameof(unitId));
        }

        await using var connection = await _catalog.ConnectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var items = new Dictionary<Guid, ImportAssignmentEvidenceItem>();
        await using (var itemsCommand = connection.CreateCommand())
        {
            itemsCommand.CommandText = """
                SELECT import_item_id, source_path
                FROM import_items
                WHERE import_unit_id = $unitId
                  AND disposition IN ('INCLUDED', 'REUSED')
                ORDER BY created_at_ms, import_item_id;
                """;
            itemsCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

            await using var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                var sourcePath = reader.GetString(1);
                items[itemId] = new ImportAssignmentEvidenceItem(
                    itemId,
                    SafeDirectoryName(sourcePath),
                    DurableOwnerProfileId: null);
            }
        }

        if (items.Count == 0)
        {
            return _policy.EvaluateBatch([]);
        }

        var ownersByItem = items.Keys.ToDictionary(id => id, _ => new HashSet<Guid>());
        await using (var evidenceCommand = connection.CreateCommand())
        {
            evidenceCommand.CommandText = """
                SELECT DISTINCT ii.import_item_id, pa.profile_id
                FROM import_items ii
                JOIN profile_assets pa
                  ON pa.asset_id = ii.reused_asset_id
                 AND pa.relation_type = 'OWNER'
                JOIN profiles p
                  ON p.profile_id = pa.profile_id
                 AND p.trashed_at_ms IS NULL
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED')

                UNION

                SELECT DISTINCT ii.import_item_id, pa.profile_id
                FROM import_items ii
                JOIN assets candidate
                  ON candidate.asset_id = ii.candidate_asset_id
                JOIN assets existing
                  ON existing.state = 'ACTIVE'
                 AND existing.asset_id <> candidate.asset_id
                 AND (
                     (candidate.media_type = 'MODEL'
                      AND candidate.bundle_sha256 IS NOT NULL
                      AND existing.bundle_sha256 = candidate.bundle_sha256)
                     OR
                     (candidate.media_type <> 'MODEL'
                      AND existing.sha256 IS NOT NULL
                      AND candidate.sha256 IS NOT NULL
                      AND existing.sha256 = candidate.sha256
                      AND existing.byte_length = candidate.byte_length)
                 )
                JOIN profile_assets pa
                  ON pa.asset_id = existing.asset_id
                 AND pa.relation_type = 'OWNER'
                JOIN profiles p
                  ON p.profile_id = pa.profile_id
                 AND p.trashed_at_ms IS NULL
                WHERE ii.import_unit_id = $unitId
                  AND ii.disposition IN ('INCLUDED', 'REUSED');
                """;
            evidenceCommand.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));

            await using var reader = await evidenceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = DbGuid.Parse(reader.GetString(0));
                if (ownersByItem.TryGetValue(itemId, out var owners))
                {
                    owners.Add(DbGuid.Parse(reader.GetString(1)));
                }
            }
        }

        var evidence = new List<ImportAssignmentEvidenceItem>(items.Count);
        foreach (var item in items.Values)
        {
            var owners = ownersByItem[item.ItemId];
            evidence.Add(item with
            {
                DurableOwnerProfileId = owners.Count == 1 ? owners.Single() : null,
            });
        }

        var assessment = _policy.EvaluateBatch(evidence);
        await PersistClustersAsync(
            unitId,
            assessment.HasAmbiguity ? assessment.Clusters : [],
            cancellationToken).ConfigureAwait(false);

        return assessment;
    }

    private async Task PersistClustersAsync(
        Guid unitId,
        IReadOnlyList<ImportAssignmentCluster> clusters,
        CancellationToken cancellationToken)
    {
        var now = DbTime.Format(DateTimeOffset.UtcNow);
        await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);

        var stalePendingClusterIds = new HashSet<Guid>();
        await using (var pending = transaction.CreateCommand(
            """
            SELECT cluster_id
            FROM import_assignment_clusters
            WHERE import_unit_id = $unitId
              AND state = 'PENDING';
            """))
        {
            pending.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stalePendingClusterIds.Add(DbGuid.Parse(reader.GetString(0)));
            }
        }

        foreach (var cluster in clusters)
        {
            var clusterId = StableClusterId(unitId, cluster.Key);
            await using var command = transaction.CreateCommand(
                """
                INSERT INTO import_assignment_clusters(
                    cluster_id, import_unit_id, cluster_key, source_directory,
                    candidate_profile_id, evidence_kind, state, created_at_ms, updated_at_ms)
                VALUES ($clusterId, $unitId, $clusterKey, $sourceDirectory,
                        $candidateProfileId, $evidenceKind, 'PENDING', $now, $now)
                ON CONFLICT(import_unit_id, cluster_key) DO UPDATE SET
                    source_directory = excluded.source_directory,
                    candidate_profile_id = excluded.candidate_profile_id,
                    evidence_kind = excluded.evidence_kind,
                    updated_at_ms = excluded.updated_at_ms,
                    row_version = import_assignment_clusters.row_version + 1
                WHERE import_assignment_clusters.state = 'PENDING';
                """);
            command.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
            command.Parameters.AddWithValue("$unitId", DbGuid.Format(unitId));
            command.Parameters.AddWithValue("$clusterKey", cluster.Key);
            command.Parameters.AddWithValue("$sourceDirectory", (object?)cluster.SourceDirectory ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$candidateProfileId",
                cluster.CandidateProfileId is { } candidate ? DbGuid.Format(candidate) : DBNull.Value);
            command.Parameters.AddWithValue("$evidenceKind", Format(cluster.Evidence));
            command.Parameters.AddWithValue("$now", now);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                // A previously decided cluster with the same durable key must never be rewritten or
                // have its membership expanded by a later background reassessment.
                continue;
            }

            stalePendingClusterIds.Remove(clusterId);

            await using (var clearMembers = transaction.CreateCommand(
                "DELETE FROM import_assignment_cluster_items WHERE cluster_id = $clusterId;"))
            {
                clearMembers.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
                await clearMembers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var itemId in cluster.ItemIds.Distinct())
            {
                await using var member = transaction.CreateCommand(
                    """
                    INSERT INTO import_assignment_cluster_items(cluster_id, import_item_id)
                    VALUES ($clusterId, $itemId);
                    """);
                member.Parameters.AddWithValue("$clusterId", DbGuid.Format(clusterId));
                member.Parameters.AddWithValue("$itemId", DbGuid.Format(itemId));
                await member.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var staleClusterId in stalePendingClusterIds)
        {
            await using var remove = transaction.CreateCommand(
                """
                DELETE FROM import_assignment_clusters
                WHERE cluster_id = $clusterId
                  AND state = 'PENDING';
                """);
            remove.Parameters.AddWithValue("$clusterId", DbGuid.Format(staleClusterId));
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid StableClusterId(Guid unitId, string clusterKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{unitId:D}|{clusterKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Format(ImportAssignmentClusterEvidence evidence) => evidence switch
    {
        ImportAssignmentClusterEvidence.HeuristicCandidate => "HEURISTIC_CANDIDATE",
        ImportAssignmentClusterEvidence.Conflicting => "CONFLICTING",
        _ => "INSUFFICIENT",
    };

    private static string? SafeDirectoryName(string sourcePath)
    {
        try
        {
            return Path.GetDirectoryName(sourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
