using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

public sealed class SchemaMigrator
{
    private const string MigrationResourceMarker = ".SystemServices.Database.Migrations.";

    private static readonly Regex MigrationResourceName = new(
        @"(?:^|\.)(?<version>\d{4})_(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$",
        RegexOptions.CultureInvariant);

    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public SchemaMigrator(
        CatalogConnectionFactory connectionFactory,
        CatalogWriteCoordinator writeCoordinator,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<MigrationDescriptor> DiscoverMigrations(Assembly? assembly = null)
    {
        assembly ??= typeof(SchemaMigrator).Assembly;

        var migrations = new List<MigrationDescriptor>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(MigrationResourceMarker, StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var match = MigrationResourceName.Match(resourceName);
            if (!match.Success)
            {
                throw new MigrationIntegrityException(
                    $"Embedded migration resource '{resourceName}' does not use NNNN_name.sql naming.");
            }

            if (!int.TryParse(match.Groups["version"].Value, out var version))
            {
                throw new MigrationIntegrityException(
                    $"Embedded migration resource '{resourceName}' has an invalid numeric version.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new MigrationIntegrityException(
                    $"Embedded migration resource '{resourceName}' could not be opened.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            migrations.Add(MigrationDescriptor.FromUtf8Bytes(
                version,
                match.Groups["name"].Value,
                buffer.ToArray(),
                resourceName));
        }

        return ValidateAndOrder(migrations, requireAtLeastOne: true);
    }

    public Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken = default) =>
        MigrateAsync(DiscoverMigrations(), databaseOpened: null, cancellationToken);

    internal Task<MigrationResult> MigrateAsync(
        Action databaseOpened,
        CancellationToken cancellationToken = default) =>
        MigrateAsync(
            DiscoverMigrations(),
            databaseOpened ?? throw new ArgumentNullException(nameof(databaseOpened)),
            cancellationToken);

    public async Task<MigrationResult> MigrateAsync(
        IEnumerable<MigrationDescriptor> migrations,
        CancellationToken cancellationToken = default)
        => await MigrateAsync(migrations, databaseOpened: null, cancellationToken).ConfigureAwait(false);

    private async Task<MigrationResult> MigrateAsync(
        IEnumerable<MigrationDescriptor> migrations,
        Action? databaseOpened,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var orderedMigrations = ValidateAndOrder(migrations, requireAtLeastOne: true);

        await using var lease = await _writeCoordinator.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await SqlitePragmas.InitializeAndValidateDatabaseAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        databaseOpened?.Invoke();
        await EnsureMigrationLedgerAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = await LoadAppliedMigrationsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        VerifyAppliedMigrations(applied, orderedMigrations);

        var newlyApplied = new List<int>();
        foreach (var migration in orderedMigrations)
        {
            if (applied.ContainsKey(migration.Version))
            {
                continue;
            }

            await ApplyMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            newlyApplied.Add(migration.Version);
        }

        return new MigrationResult(
            newlyApplied.Count,
            orderedMigrations[^1].Version,
            newlyApplied.AsReadOnly());
    }

    private static IReadOnlyList<MigrationDescriptor> ValidateAndOrder(
        IEnumerable<MigrationDescriptor> migrations,
        bool requireAtLeastOne)
    {
        var ordered = migrations.OrderBy(migration => migration.Version).ToArray();
        if (requireAtLeastOne && ordered.Length == 0)
        {
            throw new MigrationIntegrityException("No embedded database migrations were discovered.");
        }

        for (var index = 0; index < ordered.Length; index++)
        {
            var expectedVersion = index + 1;
            if (ordered[index].Version != expectedVersion)
            {
                throw new MigrationIntegrityException(
                    $"Migration versions must be contiguous from 0001; expected {expectedVersion:D4} but found {ordered[index].Version:D4}.");
            }

            if (index > 0 && ordered[index - 1].Version == ordered[index].Version)
            {
                throw new MigrationIntegrityException(
                    $"Migration version {ordered[index].Version:D4} is declared more than once.");
            }
        }

        return ordered;
    }

    private static async Task EnsureMigrationLedgerAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version       INTEGER PRIMARY KEY,
                name          TEXT NOT NULL,
                checksum      TEXT NOT NULL,
                applied_at_ms INTEGER NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, AppliedMigration>> LoadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, AppliedMigration>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version, name, checksum FROM schema_migrations ORDER BY version;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var version = reader.GetInt32(0);
            applied.Add(version, new AppliedMigration(reader.GetString(1), reader.GetString(2)));
        }

        return applied;
    }

    private static void VerifyAppliedMigrations(
        IReadOnlyDictionary<int, AppliedMigration> applied,
        IReadOnlyList<MigrationDescriptor> available)
    {
        var byVersion = available.ToDictionary(migration => migration.Version);
        var expectedAppliedVersion = 1;

        foreach (var (version, record) in applied.OrderBy(pair => pair.Key))
        {
            if (version != expectedAppliedVersion)
            {
                throw new MigrationIntegrityException(
                    $"Applied migration history is not a contiguous prefix; expected {expectedAppliedVersion:D4} but found {version:D4}.");
            }

            if (!byVersion.TryGetValue(version, out var migration))
            {
                throw new MigrationIntegrityException(
                    $"Database records migration {version:D4}, but the embedded resource is missing.");
            }

            if (!string.Equals(record.Name, migration.Name, StringComparison.Ordinal)
                || !string.Equals(record.Checksum, migration.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationIntegrityException(
                    $"Migration {version:D4}_{migration.Name}.sql does not match the immutable applied record.");
            }

            expectedAppliedVersion++;
        }
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        MigrationDescriptor migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            if (!string.IsNullOrWhiteSpace(migration.Sql))
            {
                await using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = migration.Sql;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var ledgerCommand = connection.CreateCommand();
            ledgerCommand.Transaction = transaction;
            ledgerCommand.CommandText =
                """
                INSERT INTO schema_migrations(version, name, checksum, applied_at_ms)
                VALUES ($version, $name, $checksum, $appliedAtMs);
                """;
            ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
            ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
            ledgerCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
            ledgerCommand.Parameters.AddWithValue(
                "$appliedAtMs",
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

            await ledgerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {

            }

            throw new MigrationApplyException(migration.Version, migration.Name, ex);
        }
    }

    private sealed record AppliedMigration(string Name, string Checksum);
}

public sealed record MigrationResult(
    int AppliedCount,
    int CurrentVersion,
    IReadOnlyList<int> AppliedVersions);

public sealed class MigrationIntegrityException : InvalidOperationException
{
    public MigrationIntegrityException(string message)
        : base(message)
    {
    }
}

public sealed class MigrationApplyException : InvalidOperationException
{
    public MigrationApplyException(int version, string name, Exception innerException)
        : base($"Migration {version:D4}_{name}.sql failed and was rolled back.", innerException)
    {
        Version = version;
        MigrationName = name;
    }

    public int Version { get; }

    public string MigrationName { get; }
}
