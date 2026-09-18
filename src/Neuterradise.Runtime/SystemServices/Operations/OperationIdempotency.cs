using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Operations;

/// <summary>
/// Canonical request payload for the idempotency hook (Section 44.2.2). Values are appended with a
/// stable name, sorted ordinal by name before hashing, so the same intent always produces the same
/// SHA-256 digest regardless of call order. Only the digest is ever persisted: the builder never
/// keeps a readable copy of user text such as Notes in the receipt row.
/// </summary>
public sealed class OperationRequestPayload
{
    /// <summary>Marker for an absent value; it cannot collide with user text.</summary>
    private const string NullMarker = "\u0000null";

    private readonly SortedDictionary<string, string> _values =
        new(StringComparer.Ordinal);

    public OperationRequestPayload Add(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _values[name] = value is null ? NullMarker : value.Normalize(NormalizationForm.FormC);
        return this;
    }

    public OperationRequestPayload Add(string name, Guid? value) =>
        Add(name, DomainId.FormatOrNull(value));

    public OperationRequestPayload Add(string name, long? value) =>
        Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public OperationRequestPayload Add(string name, int? value) =>
        Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public OperationRequestPayload Add(string name, bool? value) =>
        Add(name, value is null ? null : value.Value ? "true" : "false");

    /// <summary>Adds an ordered sequence; order is preserved because it is part of the intent.</summary>
    public OperationRequestPayload AddSequence(string name, IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(name, string.Join(",", values.Select(DomainId.Format)));
    }

    /// <summary>Adds a set whose order is not part of the intent; entries are sorted canonically.</summary>
    public OperationRequestPayload AddSet(string name, IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = values.Select(DomainId.Format).ToList();
        canonical.Sort(StringComparer.Ordinal);
        return Add(name, string.Join(",", canonical));
    }

    /// <summary>The canonical text that is hashed. Exposed so a failure can be reproduced in place.</summary>
    public string ToCanonicalText()
    {
        var builder = new StringBuilder();
        foreach (var pair in _values)
        {
            builder.Append(pair.Key.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(pair.Key)
                .Append('=')
                .Append(pair.Value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(pair.Value)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Lowercase hex SHA-256 of the canonical text.</summary>
    public string ComputeHash()
    {
        var bytes = Encoding.UTF8.GetBytes(ToCanonicalText());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>
/// A durable record that one logical mutation already committed (Section 44.2.2).
/// </summary>
public sealed record OperationReceipt(
    Guid OperationId,
    string OperationKind,
    Guid TargetId,
    string RequestHash,
    string? ResultJson,
    long CommittedAtMilliseconds)
{
    public bool Matches(string operationKind, Guid targetId, string requestHash) =>
        string.Equals(OperationKind, operationKind.Trim(), StringComparison.Ordinal)
        && TargetId == targetId
        && string.Equals(RequestHash, requestHash, StringComparison.Ordinal);
}

/// <summary>What a retry of an already-known OperationId is allowed to do.</summary>
public enum OperationReplayDecision
{
    /// <summary>No receipt exists; the command must execute normally.</summary>
    Execute,

    /// <summary>The same intent already committed; return the committed result, do not commit again.</summary>
    ReturnCommitted,

    /// <summary>The same OperationId was reused with a different payload or target; this is a conflict.</summary>
    Conflict,
}

/// <summary>
/// The decision rules that make durable commands restart-safe (Section 7.1 idempotency boundary).
/// </summary>
public static class OperationReplay
{
    /// <summary>
    /// Compares an existing receipt with the current attempt. Same operation, kind, target and
    /// payload hash means the work already committed; anything else with the same OperationId is a
    /// conflict, never a silent second commit.
    /// </summary>
    public static OperationReplayDecision Decide(
        OperationReceipt? existing,
        OperationContext context,
        Guid targetId,
        string requestHash)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        if (existing is null)
        {
            return OperationReplayDecision.Execute;
        }

        if (!DomainId.AreSame(existing.OperationId, context.OperationId))
        {
            return OperationReplayDecision.Conflict;
        }

        var sameIntent = string.Equals(existing.OperationKind, context.Kind, StringComparison.Ordinal)
            && DomainId.AreSame(existing.TargetId, targetId)
            && string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal);

        return sameIntent
            ? OperationReplayDecision.ReturnCommitted
            : OperationReplayDecision.Conflict;
    }

    /// <summary>The canonical conflict result for a replayed OperationId with a different payload.</summary>
    public static OperationResult PayloadMismatch(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OperationResult.Conflict(
            OperationErrorCode.OperationPayloadMismatch,
            "This action was already recorded with different details. Reload and try again.");
    }

    /// <inheritdoc cref="PayloadMismatch(OperationContext)"/>
    public static OperationResult<T> PayloadMismatch<T>(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OperationResult<T>.Conflict(
            OperationErrorCode.OperationPayloadMismatch,
            "This action was already recorded with different details. Reload and try again.");
    }

    /// <summary>
    /// Builds the receipt for a mutation that is about to commit, stamped from the canonical clock.
    /// </summary>
    public static OperationReceipt CreateReceipt(
        OperationContext context,
        Guid targetId,
        string requestHash,
        string? resultJson,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        if (targetId == Guid.Empty)
        {
            throw new ArgumentException(
                "A receipt must name the aggregate the mutation targeted.",
                nameof(targetId));
        }

        return new OperationReceipt(
            DomainId.Require(context.OperationId, DomainIdKind.Operation, nameof(context)),
            context.Kind,
            targetId,
            requestHash,
            resultJson,
            DomainTime.ToUnixMilliseconds(clock.UtcNow));
    }
}
