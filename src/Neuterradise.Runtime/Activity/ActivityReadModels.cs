namespace Neuterradise.App.Activity;

public sealed record ActivityItemReadModel(
    Guid ActivityId,
    string EventType,
    Guid? ProfileId,
    Guid? AssetId,
    Guid? ImportUnitId,
    Guid? OperationId,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc,
    string? ProfileDisplayName = null,
    string? AssetDisplayName = null,
    string? ImportDisplayName = null);

public sealed record ActivityPage(
    IReadOnlyList<ActivityItemReadModel> Items,
    string? NextPageToken,
    bool HasMore);
