using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database;
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

    private static async Task CheckActiveAppearanceReferencesAsync(SqliteConnection c,List<CriticalIntegrityViolation> v,CancellationToken t)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText="""
            SELECT p.profile_id FROM profiles p LEFT JOIN assets a ON p.cover_asset_id=a.asset_id WHERE p.trashed_at_ms IS NULL AND p.cover_asset_id IS NOT NULL AND (a.asset_id IS NULL OR a.state!='ACTIVE' OR a.trashed_at_ms IS NOT NULL OR a.media_type!='IMAGE')
            UNION ALL SELECT p.profile_id FROM profiles p LEFT JOIN assets a ON p.banner_asset_id=a.asset_id WHERE p.trashed_at_ms IS NULL AND p.banner_asset_id IS NOT NULL AND (a.asset_id IS NULL OR a.state!='ACTIVE' OR a.trashed_at_ms IS NOT NULL OR a.media_type NOT IN ('IMAGE','VIDEO'))
            UNION ALL SELECT p.profile_id FROM profiles p WHERE p.kind='UNKNOWN' AND p.trashed_at_ms IS NULL AND (p.cover_asset_id IS NOT NULL OR p.banner_asset_id IS NOT NULL);
            """;
        await using var r=await cmd.ExecuteReaderAsync(t).ConfigureAwait(false); while(await r.ReadAsync(t).ConfigureAwait(false)) v.Add(new("INVALID_ACTIVE_APPEARANCE_REFERENCE","An active Profile has an invalid appearance reference.","Profile",r.GetString(0)));
    }
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
