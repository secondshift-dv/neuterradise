using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Import.Preparation;

public enum ExactDuplicateMatchKind
{
    None,
    ActiveDuplicate,
    CandidateDuplicate,
    TrashedDuplicate
}

public sealed record ExactDuplicateMatch(
    Guid ExistingAssetId,
    AssetState AssetState,
    long ByteLength,
    string Sha256,
    string? CurrentManagedFileName,
    string? CurrentManagedRelativePath,
    Guid? OwnerProfileId,
    string? OwnerProfileDisplayName,
    Guid? CandidateImportUnitId = null,
    string? CandidateSourcePath = null)
{
    public bool IsLibraryReuseEligible => AssetState == AssetState.Active;
    public bool IsCandidateDuplicate => AssetState == AssetState.Candidate;
    public bool IsTrashedDuplicate => AssetState == AssetState.Trashed;
}

public sealed record ExactDuplicateResult(
    bool IsExactDuplicate,
    ExactDuplicateMatchKind MatchKind,
    ExactDuplicateMatch? Match,
    IReadOnlyList<ExactDuplicateMatch> AllMatches)
{
    public bool IsLibraryReuseEligible => MatchKind == ExactDuplicateMatchKind.ActiveDuplicate;
    public bool IsCandidateDuplicate => MatchKind == ExactDuplicateMatchKind.CandidateDuplicate;
    public bool IsTrashedDuplicate => MatchKind == ExactDuplicateMatchKind.TrashedDuplicate;

    public static ExactDuplicateResult None { get; } =
        new(false, ExactDuplicateMatchKind.None, null, Array.Empty<ExactDuplicateMatch>());
}

public sealed class ExactDuplicateDetector
{
    private readonly CatalogConnectionFactory _connectionFactory;

    public ExactDuplicateDetector(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
    }

    public async Task<ExactDuplicateResult> DetectDuplicateAsync(
        string sha256,
        long byteLength,
        Guid? excludeAssetId = null,
        string? bundleSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha256) || byteLength < 0)
        {
            return ExactDuplicateResult.None;
        }

        var normalizedSha = sha256.Trim().ToLowerInvariant();
        if (normalizedSha.Length != 64)
        {
            return ExactDuplicateResult.None;
        }

        var normalizedBundleSha = string.IsNullOrWhiteSpace(bundleSha256)
            ? null
            : bundleSha256.Trim().ToLowerInvariant();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.asset_id, a.state, a.byte_length, a.sha256,
                   a.current_managed_file_name, a.current_managed_relative_path,
                   pa.profile_id, p.display_name,
                   ii.import_unit_id, ii.source_path
            FROM assets a
            LEFT JOIN profile_assets pa ON pa.asset_id = a.asset_id AND pa.relation_type = $ownerRelation
            LEFT JOIN profiles p ON p.profile_id = pa.profile_id
            LEFT JOIN import_items ii ON ii.candidate_asset_id = a.asset_id
            WHERE (
                    ($bundleSha IS NOT NULL AND a.bundle_sha256 = $bundleSha)
                    OR
                    ($bundleSha IS NULL AND a.sha256 = $sha256 AND a.byte_length = $byteLength)
                  )
              AND a.state IN ($activeState, $candidateState, $trashedState)
              AND ($excludeAssetId IS NULL OR a.asset_id <> $excludeAssetId)
            ORDER BY 
              (CASE 
                 WHEN a.state = $activeState THEN 0 
                 WHEN a.state = $candidateState THEN 1 
                 ELSE 2 
               END),
               a.added_to_library_at_ms ASC,
               a.created_at_ms ASC;
            """;
        command.Parameters.AddWithValue("$bundleSha", (object?)normalizedBundleSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha256", normalizedSha);
        command.Parameters.AddWithValue("$byteLength", byteLength);
        command.Parameters.AddWithValue("$ownerRelation", DbEnum.Format(ProfileAssetRelation.Owner));
        command.Parameters.AddWithValue("$activeState", DbEnum.Format(AssetState.Active));
        command.Parameters.AddWithValue("$candidateState", DbEnum.Format(AssetState.Candidate));
        command.Parameters.AddWithValue("$trashedState", DbEnum.Format(AssetState.Trashed));
        command.Parameters.AddWithValue(
            "$excludeAssetId",
            excludeAssetId.HasValue ? DbGuid.Format(excludeAssetId.Value) : DBNull.Value);

        var matches = new List<ExactDuplicateMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var assetId = DbGuid.Parse(reader.GetString(0));
            var state = DbEnum.ParseAssetState(reader.GetString(1));
            var len = reader.GetInt64(2);
            var hash = reader.GetString(3);
            var fileName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var relPath = reader.IsDBNull(5) ? null : reader.GetString(5);
            var ownerProfileId = reader.IsDBNull(6) ? (Guid?)null : DbGuid.Parse(reader.GetString(6));
            var ownerName = reader.IsDBNull(7) ? null : reader.GetString(7);
            var candidateUnitId = reader.IsDBNull(8) ? (Guid?)null : DbGuid.Parse(reader.GetString(8));
            var candidateSourcePath = reader.IsDBNull(9) ? null : reader.GetString(9);

            matches.Add(new ExactDuplicateMatch(
                assetId,
                state,
                len,
                hash,
                fileName,
                relPath,
                ownerProfileId,
                ownerName,
                candidateUnitId,
                candidateSourcePath));
        }

        if (matches.Count == 0)
        {
            return ExactDuplicateResult.None;
        }

        var activeMatch = matches.FirstOrDefault(m => m.AssetState == AssetState.Active);
        if (activeMatch is not null)
        {
            return new ExactDuplicateResult(
                IsExactDuplicate: true,
                MatchKind: ExactDuplicateMatchKind.ActiveDuplicate,
                Match: activeMatch,
                AllMatches: matches);
        }

        var candidateMatch = matches.FirstOrDefault(m => m.AssetState == AssetState.Candidate);
        if (candidateMatch is not null)
        {
            return new ExactDuplicateResult(
                IsExactDuplicate: true,
                MatchKind: ExactDuplicateMatchKind.CandidateDuplicate,
                Match: candidateMatch,
                AllMatches: matches);
        }

        return new ExactDuplicateResult(
            IsExactDuplicate: true,
            MatchKind: ExactDuplicateMatchKind.TrashedDuplicate,
            Match: matches[0],
            AllMatches: matches);
    }
}
