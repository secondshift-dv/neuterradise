using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.SystemServices.Database.Reads;

public static class OperationReceiptReads
{
    public static async Task<OperationReceipt?> ReadAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (operationId == Guid.Empty) throw new ArgumentException("A stable operation identifier is required.", nameof(operationId));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_kind, target_id, request_hash, result_json, committed_at_ms FROM operation_receipts WHERE operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new OperationReceipt(
            operationId,
            reader.GetString(0),
            DbGuid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4));
    }

    public static Task<OperationReceipt?> ReadAsync(
        CatalogDb catalog,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ReadFromCatalogAsync(catalog.ConnectionFactory, operationId, cancellationToken);
    }

    private static async Task<OperationReceipt?> ReadFromCatalogAsync(
        CatalogConnectionFactory factory,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
    }
}
