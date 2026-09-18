using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.SystemServices.Database.Writes;

/// <summary>Durable idempotency gateway for committed logical mutations.</summary>
public sealed class OperationReceiptWrites
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly CatalogWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;

    public OperationReceiptWrites(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _writeCoordinator = catalog.WriteCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationReceipt?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        EnsureId(operationId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await OperationReceiptReads.ReadAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a prior committed result or reserves no mutable state. The mutation and this receipt
    /// must be committed in the caller's transaction with <see cref="CommitAsync"/>.
    /// </summary>
    public async Task<OperationReceipt?> ReadOrValidateAsync(
        Guid operationId,
        string operationKind,
        Guid targetId,
        string requestPayloadJson,
        CancellationToken cancellationToken = default)
    {
        EnsureId(operationId);
        Validate(operationKind, targetId, requestPayloadJson);
        var requestHash = ComputeRequestHash(requestPayloadJson);
        var existing = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !existing.Matches(operationKind, targetId, requestHash))
        {
            throw new OperationReceiptConflictException(
                $"Operation {operationId:D} was already committed with different request facts.");
        }
        return existing;
    }

    public static async Task CommitAsync(
        CatalogTransaction transaction,
        Guid operationId,
        string operationKind,
        Guid targetId,
        string requestPayloadJson,
        string resultJson,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureId(operationId);
        Validate(operationKind, targetId, requestPayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        using var document = System.Text.Json.JsonDocument.Parse(resultJson);
        var hash = ComputeRequestHash(requestPayloadJson);
        await using var command = transaction.CreateCommand(
            """
            INSERT INTO operation_receipts(
                operation_id, operation_kind, target_id, request_hash, result_json, committed_at_ms)
            VALUES ($operationId, $kind, $targetId, $requestHash, $resultJson, $committedAt)
            ON CONFLICT(operation_id) DO UPDATE SET
                operation_kind = operation_receipts.operation_kind
            WHERE operation_receipts.operation_kind = excluded.operation_kind
              AND operation_receipts.target_id = excluded.target_id
              AND operation_receipts.request_hash = excluded.request_hash;
            """);
        command.Parameters.AddWithValue("$operationId", DbGuid.Format(operationId));
        command.Parameters.AddWithValue("$kind", operationKind.Trim());
        command.Parameters.AddWithValue("$targetId", DbGuid.Format(targetId));
        command.Parameters.AddWithValue("$requestHash", hash);
        command.Parameters.AddWithValue("$resultJson", resultJson);
        command.Parameters.AddWithValue("$committedAt", DbTime.Format(committedAtUtc));
        try
        {
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new OperationReceiptConflictException(
                    $"Operation {operationId:D} was already committed with different request facts.");
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new OperationReceiptConflictException(
                $"Operation {operationId:D} was already committed with different request facts.", exception);
        }
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    internal static string ComputeRequestHash(string requestPayloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPayloadJson);
        using var document = System.Text.Json.JsonDocument.Parse(requestPayloadJson);
        var normalized = document.RootElement.GetRawText();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static void Validate(string kind, Guid targetId, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        EnsureId(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var _ = System.Text.Json.JsonDocument.Parse(payload);
    }

    private static void EnsureId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A stable operation identifier is required.", nameof(id));
    }
}

public sealed class OperationReceiptConflictException : InvalidOperationException
{
    public OperationReceiptConflictException(string message) : base(message) { }
    public OperationReceiptConflictException(string message, Exception inner) : base(message, inner) { }
}
