using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Diagnostics;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class CriticalIntegrityGate
{
    private readonly CatalogDb _catalog;
    private readonly StructuredDiagnostics? _diagnostics;

    public CriticalIntegrityGate(CatalogDb catalog, StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnostics = diagnostics;
    }

    public async Task<CriticalIntegrityResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var violations = new List<CriticalIntegrityViolation>();
        await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await CheckSchemaMigrationsAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckActiveAssetsOwnerAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNonActiveAssetsOwnerAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNormalProfilesIdentityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckUnknownProfilesIdentityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckNonterminalStagingCoherenceAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckManagedPathAuthorityAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        await CheckActiveAppearanceReferencesAsync(connection, violations, cancellationToken).ConfigureAwait(false);
        var result = violations.Count == 0 ? CriticalIntegrityResult.Success() : CriticalIntegrityResult.Failure(violations);
        if (!result.Passed)
            foreach (var violation in result.Violations)
                _diagnostics?.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, DiagnosticSeverity.Error, "lifecycle.integrity", violation.Code, StructuredDiagnostics.SanitizeDetail(violation.Message)));
        return result;
    }

    private static async Task CheckSchemaMigrationsAsync(SqliteConnection c, List<CriticalIntegrityViolation> v, CancellationToken t)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        try { if (Convert.ToInt64(await cmd.ExecuteScalarAsync(t).ConfigureAwait(false)) <= 0) v.Add(new("SCHEMA_MIGRATIONS_EMPTY", "The schema_migrations ledger is empty.")); }
        catch (SqliteException) { v.Add(new("SCHEMA_MIGRATIONS_TABLE_MISSING", "The schema_migrations table could not be queried.")); }
    }

    private static async Task CheckActiveAssetsOwnerAsync(SqliteConnection c, List<CriticalIntegrityViolation> v, CancellationToken t)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = """
            SELECT a.asset_id, COUNT(pa.profile_id) FROM assets a LEFT JOIN profile_assets pa ON a.asset_id=pa.asset_id AND pa.relation_type='OWNER'
            WHERE a.state='ACTIVE' AND a.trashed_at_ms IS NULL GROUP BY a.asset_id HAVING COUNT(pa.profile_id) != 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while (await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new(r.GetInt64(1)==0 ? "ACTIVE_ASSET_NO_OWNER" : "ACTIVE_ASSET_MULTIPLE_OWNERS", "Active asset owner cardinality is invalid.", "Asset", r.GetString(0)));
    }

    private static async Task CheckNonActiveAssetsOwnerAsync(SqliteConnection c, List<CriticalIntegrityViolation> v, CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT a.asset_id FROM assets a JOIN profile_assets pa ON a.asset_id=pa.asset_id AND pa.relation_type='OWNER' WHERE a.state IN ('CANDIDATE','RETIRED');";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("NON_ACTIVE_ASSET_HAS_OWNER","A non-active asset has an OWNER relation.","Asset",r.GetString(0)));
    }

    private static async Task CheckNormalProfilesIdentityAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT p.profile_id,COUNT(i.identity_id) FROM profiles p LEFT JOIN identities i ON p.profile_id=i.profile_id AND i.is_active=1 WHERE p.kind='NORMAL' AND p.trashed_at_ms IS NULL GROUP BY p.profile_id HAVING COUNT(i.identity_id)!=1;";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new(r.GetInt64(1)==0?"NORMAL_PROFILE_NO_IDENTITY":"NORMAL_PROFILE_MULTIPLE_IDENTITIES","Active NORMAL identity cardinality is invalid.","Profile",r.GetString(0)));
    }

    private static async Task CheckUnknownProfilesIdentityAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT p.profile_id FROM profiles p JOIN identities i ON p.profile_id=i.profile_id AND i.is_active=1 WHERE p.kind='UNKNOWN' AND p.trashed_at_ms IS NULL;";
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("UNKNOWN_PROFILE_HAS_IDENTITY","An active UNKNOWN profile has an active identity.","Profile",r.GetString(0)));
    }

    private static async Task CheckNonterminalStagingCoherenceAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="""
            SELECT ii.import_item_id FROM import_items ii LEFT JOIN import_units iu ON ii.import_unit_id=iu.import_unit_id WHERE iu.import_unit_id IS NULL
            UNION ALL SELECT ii.import_item_id FROM import_items ii LEFT JOIN assets a ON ii.candidate_asset_id=a.asset_id WHERE ii.candidate_asset_id IS NOT NULL AND a.asset_id IS NULL;
            """;
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("STAGING_ORPHANED_REFERENCE","An import item has an orphaned durable reference.","ImportItem",r.GetString(0)));
    }

    private static async Task CheckManagedPathAuthorityAsync(
        SqliteConnection c,
        List<CriticalIntegrityViolation> v,
        CancellationToken t)
    {
        await using (var profiles = c.CreateCommand())
        {
            profiles.CommandText =
                """
                SELECT profile_id, path_state
                FROM profiles
                WHERE path_state IN ('PENDING','NEEDS_ATTENTION');
                """;
            await using var reader = await profiles.ExecuteReaderAsync(t).ConfigureAwait(false);
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                v.Add(new CriticalIntegrityViolation(
                    "UNRESOLVED_PROFILE_PATH_AUTHORITY",
                    $"Profile managed-path authority remains {reader.GetString(1)} after recovery.",
                    "Profile",
                    reader.GetString(0)));
            }
        }

        await using (var assets = c.CreateCommand())
        {
            assets.CommandText =
                """
                SELECT asset_id, path_state
                FROM assets
                WHERE path_state IN ('PENDING','NEEDS_ATTENTION');
                """;
            await using var reader = await assets.ExecuteReaderAsync(t).ConfigureAwait(false);
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                v.Add(new CriticalIntegrityViolation(
                    "UNRESOLVED_ASSET_PATH_AUTHORITY",
                    $"Asset managed-path authority remains {reader.GetString(1)} after recovery.",
                    "Asset",
                    reader.GetString(0)));
            }
        }
    }

    private static async Task CheckActiveAppearanceReferencesAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await CheckActiveCoverReferencesAsync(c, v, t).ConfigureAwait(false);

        await using var cmd=c.CreateCommand(); cmd.CommandText="""
            SELECT p.profile_id FROM profiles p LEFT JOIN assets a ON p.banner_asset_id=a.asset_id WHERE p.trashed_at_ms IS NULL AND p.banner_asset_id IS NOT NULL AND (a.asset_id IS NULL OR a.state!='ACTIVE' OR a.trashed_at_ms IS NOT NULL OR a.media_type NOT IN ('IMAGE','VIDEO'))
            UNION ALL SELECT p.profile_id FROM profiles p WHERE p.kind='UNKNOWN' AND p.trashed_at_ms IS NULL AND (p.cover_asset_id IS NOT NULL OR p.banner_asset_id IS NOT NULL);
            """;
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while(await r.ReadAsync(t).ConfigureAwait(false))
            v.Add(new("INVALID_ACTIVE_APPEARANCE_REFERENCE","An active Profile has an invalid appearance reference.","Profile",r.GetString(0)));
    }

    private static async Task CheckActiveCoverReferencesAsync(
        SqliteConnection c,
        List<CriticalIntegrityViolation> v,
        CancellationToken t)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.profile_id,
                a.asset_id,
                a.state,
                a.trashed_at_ms,
                a.media_type,
                pa.overrides_json
            FROM profiles p
            LEFT JOIN assets a ON p.cover_asset_id = a.asset_id
            LEFT JOIN profile_appearance pa ON p.profile_id = pa.profile_id
            WHERE p.trashed_at_ms IS NULL
              AND p.cover_asset_id IS NOT NULL;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
        while (await reader.ReadAsync(t).ConfigureAwait(false))
        {
            var profileId = reader.GetString(0);
            var validAsset = !reader.IsDBNull(1)
                && string.Equals(reader.GetString(2), "ACTIVE", StringComparison.Ordinal)
                && reader.IsDBNull(3)
                && !reader.IsDBNull(4);
            if (!validAsset)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            MediaType mediaType;
            try
            {
                mediaType = DbEnum.ParseMediaType(reader.GetString(4));
            }
            catch (FormatException)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            ProfileAppearanceOverrides overrides;
            try
            {
                overrides = reader.IsDBNull(5)
                    ? ProfileAppearanceOverrides.Default
                    : ProfileAppearanceOverrides.Parse(reader.GetString(5));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or System.Text.Json.JsonException)
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            CoverVisualSourceKind sourceKind;
            if (string.IsNullOrWhiteSpace(overrides.CoverSourceKind))
            {
                // Legacy image Covers are unambiguous. A video Cover is not: its exact frame
                // timestamp is durable authority and therefore may never be inferred.
                if (mediaType != MediaType.Image)
                {
                    AddInvalidAppearanceViolation(v, profileId);
                    continue;
                }

                sourceKind = CoverVisualSourceKind.Image;
            }
            else if (!Enum.TryParse(overrides.CoverSourceKind, ignoreCase: false, out sourceKind))
            {
                AddInvalidAppearanceViolation(v, profileId);
                continue;
            }

            if (!ProfileAppearanceRules.IsCoverVisualSourceValid(
                    mediaType,
                    sourceKind,
                    overrides.CoverVideoTimestampMilliseconds))
            {
                AddInvalidAppearanceViolation(v, profileId);
            }
        }
    }

    private static void AddInvalidAppearanceViolation(List<CriticalIntegrityViolation> violations, string profileId) =>
        violations.Add(new(
            "INVALID_ACTIVE_APPEARANCE_REFERENCE",
            "An active Profile has an invalid appearance reference.",
            "Profile",
            profileId));
}

public sealed record CriticalIntegrityViolation(string Code,string Message,string? TargetEntity=null,string? TargetId=null);
public sealed record CriticalIntegrityResult(bool Passed,IReadOnlyList<CriticalIntegrityViolation> Violations)
{
    public static CriticalIntegrityResult Success()=>new(true,[]);
    public static CriticalIntegrityResult Failure(IReadOnlyList<CriticalIntegrityViolation> v)=>new(false,v);
}
public sealed class CriticalIntegrityException : Exception
{
    public CriticalIntegrityResult Result { get; }
    public CriticalIntegrityException(CriticalIntegrityResult result):base("Critical integrity gate failed.") { Result=result??throw new ArgumentNullException(nameof(result)); }
}
