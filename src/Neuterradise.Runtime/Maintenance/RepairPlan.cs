using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.Import;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Maintenance;

public enum RepairKind
{
    RegenerateProfileManifest,
    RetryCommittedSourceDelete
}

public enum RepairPhysicalFact
{
    ManifestMissing,
    ManifestMalformed,
    ManifestStale,
    SourcePresent
}

public sealed record RepairPlan(
    int SchemaVersion,
    Guid RepairPlanId,
    string FindingCode,
    RepairKind Kind,
    Guid? ProfileId,
    Guid? AssetId,
    Guid? ImportItemId,
    long ExpectedRowVersion,
    string ExpectedAuthorityState,
    string SourcePathOrRelative,
    string TargetPathOrRelative,
    string? ExpectedPhysicalSha256,
    long? ExpectedPhysicalByteLength,
    RepairPhysicalFact CurrentPhysicalFact,
    string IntendedTargetAction,
    bool IsMaterialOrDestructive,
    Guid OperationId,
    DateTimeOffset PreparedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, _serializerOptions);
    }

    public static RepairPlan? FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            var plan = JsonSerializer.Deserialize<RepairPlan>(json, _serializerOptions);
            plan?.Validate();
            return plan;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"Unsupported repair-plan schema {SchemaVersion}.");
        }

        if (RepairPlanId == Guid.Empty || OperationId == Guid.Empty)
        {
            throw new ArgumentException("Repair plans require stable non-empty identifiers.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(FindingCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedAuthorityState);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePathOrRelative);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetPathOrRelative);
        ArgumentException.ThrowIfNullOrWhiteSpace(IntendedTargetAction);

        if (ExpectedRowVersion < 0 || ExpectedPhysicalByteLength < 0)
        {
            throw new ArgumentException("Repair-plan versions and byte lengths cannot be negative.");
        }
    }
}

public sealed record PersistedRepairOperation(
    RepairPlan Plan,
    string State,
    long RowVersion,
    string? ErrorCode,
    string? ErrorDetailSafe);

internal sealed record RepairSourceAuthoritySnapshot(
    Guid ImportUnitId,
    string SourcePath,
    string Disposition,
    string? DuplicateDecision,
    string LibraryCommitState,
    Guid? CandidateAssetId,
    Guid? ReusedAssetId,
    string? CandidateState,
    string? CandidateRetirementReason,
    string? CandidateOriginalSourcePath,
    string? CandidateSha256,
    long? CandidateByteLength,
    Guid? ManagedAssetId,
    string? ManagedAssetState,
    string? ManagedSha256,
    long? ManagedByteLength,
    string? CurrentManagedRelativePath,
    string? CurrentManagedFileName)
{
    public static string Capture(PersistedSourceCleanupObligation obligation)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        return JsonSerializer.Serialize(new RepairSourceAuthoritySnapshot(
            obligation.ImportUnitId,
            obligation.SourcePath,
            DbEnum.Format(obligation.Disposition),
            DbEnum.FormatOrNull(obligation.DuplicateDecision),
            DbEnum.Format(obligation.LibraryCommitState),
            obligation.CandidateAssetId,
            obligation.ReusedAssetId,
            obligation.CandidateState is { } candidateState ? DbEnum.Format(candidateState) : null,
            DbEnum.FormatOrNull(obligation.CandidateRetirementReason),
            obligation.CandidateOriginalSourcePath,
            obligation.CandidateSha256,
            obligation.CandidateByteLength,
            obligation.ManagedAssetId,
            obligation.ManagedAssetState is { } managedAssetState ? DbEnum.Format(managedAssetState) : null,
            obligation.ExpectedSha256,
            obligation.ExpectedByteLength,
            obligation.CurrentManagedRelativePath,
            obligation.CurrentManagedFileName));
    }
}
