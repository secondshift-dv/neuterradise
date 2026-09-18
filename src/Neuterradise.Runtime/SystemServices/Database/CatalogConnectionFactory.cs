using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Database;

public sealed class CatalogConnectionFactory
{
    private readonly string _connectionString;

    public CatalogConnectionFactory(VaultPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        DatabasePath = paths.CatalogDbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqlitePragmas.ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
