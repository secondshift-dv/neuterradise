namespace Neuterradise.App.Maintenance;

public enum HealthSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record HealthFinding(
    string Code,
    HealthSeverity Severity,
    Guid? ProfileId,
    Guid? AssetId,
    Guid? JobId,
    string? OperationId,
    string Summary,
    bool RepairAvailable);
