namespace Neuterradise.App.RelatedProfiles;

public sealed record RelatedProfileSummaryReadModel(
    Guid ProfileIdLow,
    Guid ProfileIdHigh,
    Guid RelatedProfileId,
    string RelatedDisplayName,
    int SharedAssetCount,
    int ConfirmedFaceCount,
    bool ManualRelation,
    DateTimeOffset? LastEvidenceAtUtc,
    int RankScore,
    DateTimeOffset UpdatedAtUtc);

public sealed record RelatedProfileEvidenceEntry(
    string EvidenceKey,
    Guid ProfileIdLow,
    Guid ProfileIdHigh,
    RelatedProfileEvidence EvidenceType,
    Guid? AssetId,
    Guid? FaceId,
    DateTimeOffset CreatedAtUtc);
