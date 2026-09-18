using Neuterradise.App.Faces;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Preparation;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Profiles;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.SystemServices.Database;

public static class DbEnum
{
    public static string Format(DestinationKind value) => value switch
    {
        DestinationKind.NewNormal => "NEW_NORMAL",
        DestinationKind.ExistingNormal => "EXISTING_NORMAL",
        DestinationKind.SystemUnknown => "SYSTEM_UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DestinationKind ParseDestinationKind(string value) => value switch
    {
        "NEW_NORMAL" => DestinationKind.NewNormal,
        "EXISTING_NORMAL" => DestinationKind.ExistingNormal,
        "SYSTEM_UNKNOWN" => DestinationKind.SystemUnknown,
        _ => throw new FormatException($"'{value}' is not a valid DestinationKind database value."),
    };

    public static string Format(ItemDisposition value) => value switch
    {
        ItemDisposition.Included => "INCLUDED",
        ItemDisposition.Skipped => "SKIPPED",
        ItemDisposition.Reused => "REUSED",
        ItemDisposition.Invalid => "INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ItemDisposition ParseItemDisposition(string value) => value switch
    {
        "INCLUDED" => ItemDisposition.Included,
        "SKIPPED" => ItemDisposition.Skipped,
        "REUSED" => ItemDisposition.Reused,
        "INVALID" => ItemDisposition.Invalid,
        _ => throw new FormatException($"'{value}' is not a valid ItemDisposition database value."),
    };

    public static string Format(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => "INCLUDE",
        DuplicateDecision.Reuse => "REUSE",
        DuplicateDecision.Skip => "SKIP",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>NULL means the exact-duplicate decision is still open.</summary>
    public static string? FormatOrNull(DuplicateDecision? value) =>
        value is { } decision ? Format(decision) : null;

    public static DuplicateDecision ParseDuplicateDecision(string value) => value switch
    {
        "INCLUDE" => DuplicateDecision.Include,
        "REUSE" => DuplicateDecision.Reuse,
        "SKIP" => DuplicateDecision.Skip,
        _ => throw new FormatException($"'{value}' is not a valid DuplicateDecision database value."),
    };

    public static DuplicateDecision? ParseDuplicateDecisionOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseDuplicateDecision(value);

    /// <summary>The disposition an item takes once the duplicate decision is applied.</summary>
    public static ItemDisposition DispositionFor(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => ItemDisposition.Included,
        DuplicateDecision.Reuse => ItemDisposition.Reused,
        DuplicateDecision.Skip => ItemDisposition.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>
    /// The retirement reason the candidate carries when a duplicate decision retires it. INCLUDE keeps
    /// the candidate alive, so it has no retirement reason.
    /// </summary>
    public static AssetRetirementReason? RetirementReasonFor(DuplicateDecision value) => value switch
    {
        DuplicateDecision.Include => null,
        DuplicateDecision.Reuse => AssetRetirementReason.DedupReused,
        DuplicateDecision.Skip => AssetRetirementReason.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Format(ImportSessionState value) => value switch
    {
        ImportSessionState.Open => "OPEN",
        ImportSessionState.Completed => "COMPLETED",
        ImportSessionState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportSessionState ParseImportSessionState(string value) => value switch
    {
        "OPEN" => ImportSessionState.Open,
        "COMPLETED" => ImportSessionState.Completed,
        "CANCELLED" => ImportSessionState.Cancelled,
        _ => throw new FormatException($"'{value}' is not a valid ImportSessionState database value."),
    };

    public static string Format(ImportUnitState value) => value switch
    {
        ImportUnitState.Intake => "INTAKE",
        ImportUnitState.Preparing => "PREPARING",
        ImportUnitState.ReadyForVerification => "READY_FOR_VERIFICATION",
        ImportUnitState.Committing => "COMMITTING",
        ImportUnitState.Committed => "COMMITTED",
        ImportUnitState.Completed => "COMPLETED",
        ImportUnitState.FailedRetryable => "FAILED_RETRYABLE",
        ImportUnitState.FailedTerminal => "FAILED_TERMINAL",
        ImportUnitState.Cancelled => "CANCELLED",
        ImportUnitState.CommittedWithCleanupAttention => "COMMITTED_WITH_CLEANUP_ATTENTION",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportUnitState ParseImportUnitState(string value) => value switch
    {
        "INTAKE" => ImportUnitState.Intake,
        "PREPARING" => ImportUnitState.Preparing,
        "READY_FOR_VERIFICATION" => ImportUnitState.ReadyForVerification,
        "COMMITTING" => ImportUnitState.Committing,
        "COMMITTED" => ImportUnitState.Committed,
        "COMPLETED" => ImportUnitState.Completed,
        "FAILED_RETRYABLE" => ImportUnitState.FailedRetryable,
        "FAILED_TERMINAL" => ImportUnitState.FailedTerminal,
        "CANCELLED" => ImportUnitState.Cancelled,
        "COMMITTED_WITH_CLEANUP_ATTENTION" => ImportUnitState.CommittedWithCleanupAttention,
        _ => throw new FormatException($"'{value}' is not a valid ImportUnitState database value."),
    };

    public static string Format(ImportPreparationStatus value) => value switch
    {
        ImportPreparationStatus.Pending => "PENDING",
        ImportPreparationStatus.Running => "RUNNING",
        ImportPreparationStatus.Ready => "READY",
        ImportPreparationStatus.FailedRetryable => "FAILED_RETRYABLE",
        ImportPreparationStatus.FailedTerminal => "FAILED_TERMINAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportPreparationStatus ParseImportPreparationStatus(string value) => value switch
    {
        "PENDING" => ImportPreparationStatus.Pending,
        "RUNNING" => ImportPreparationStatus.Running,
        "READY" => ImportPreparationStatus.Ready,
        "FAILED_RETRYABLE" => ImportPreparationStatus.FailedRetryable,
        "FAILED_TERMINAL" => ImportPreparationStatus.FailedTerminal,
        _ => throw new FormatException($"'{value}' is not a valid ImportPreparationStatus database value."),
    };

    public static string Format(ImportCleanupPolicy value) => value switch
    {
        ImportCleanupPolicy.Copy => "COPY",
        ImportCleanupPolicy.Move => "MOVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportCleanupPolicy ParseImportCleanupPolicy(string value) => value switch
    {
        "COPY" => ImportCleanupPolicy.Copy,
        "MOVE" => ImportCleanupPolicy.Move,
        _ => throw new FormatException($"'{value}' is not a valid ImportCleanupPolicy database value."),
    };

    public static string Format(SourceCleanupState value) => value switch
    {
        SourceCleanupState.SourcePresent => "SOURCE_PRESENT",
        SourceCleanupState.DestinationVerified => "DESTINATION_VERIFIED",
        SourceCleanupState.LibraryCommitted => "LIBRARY_COMMITTED",
        SourceCleanupState.SourceDeletePending => "SOURCE_DELETE_PENDING",
        SourceCleanupState.SourceConsumed => "SOURCE_CONSUMED",
        SourceCleanupState.SourceDeleteFailed => "SOURCE_DELETE_FAILED",
        SourceCleanupState.SourcePreserved => "SOURCE_PRESERVED",
        SourceCleanupState.SourceChanged => "SOURCE_CHANGED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static SourceCleanupState ParseSourceCleanupState(string value) => value switch
    {
        "SOURCE_PRESENT" => SourceCleanupState.SourcePresent,
        "DESTINATION_VERIFIED" => SourceCleanupState.DestinationVerified,
        "LIBRARY_COMMITTED" => SourceCleanupState.LibraryCommitted,
        "SOURCE_DELETE_PENDING" => SourceCleanupState.SourceDeletePending,
        "SOURCE_CONSUMED" => SourceCleanupState.SourceConsumed,
        "SOURCE_DELETE_FAILED" => SourceCleanupState.SourceDeleteFailed,
        "SOURCE_PRESERVED" => SourceCleanupState.SourcePreserved,
        "SOURCE_CHANGED" => SourceCleanupState.SourceChanged,
        _ => throw new FormatException($"'{value}' is not a valid SourceCleanupState database value."),
    };

    public static string Format(ImportCommitCheckpoint value) => value switch
    {
        ImportCommitCheckpoint.NotCommitted => "NOT_COMMITTED",
        ImportCommitCheckpoint.DecisionValidated => "DECISION_VALIDATED",
        ImportCommitCheckpoint.DestinationPrepared => "DESTINATION_PREPARED",
        ImportCommitCheckpoint.PlacementPlanned => "PLACEMENT_PLANNED",
        ImportCommitCheckpoint.DestinationBytesVerified => "DESTINATION_BYTES_VERIFIED",
        ImportCommitCheckpoint.DomainAuthorityCommitted => "DOMAIN_AUTHORITY_COMMITTED",
        ImportCommitCheckpoint.SourceCleanupPending => "SOURCE_CLEANUP_PENDING",
        ImportCommitCheckpoint.SourceCleanupComplete => "SOURCE_CLEANUP_COMPLETE",
        ImportCommitCheckpoint.Terminal => "TERMINAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ImportCommitCheckpoint ParseImportCommitCheckpoint(string value) => value switch
    {
        "NOT_COMMITTED" => ImportCommitCheckpoint.NotCommitted,
        "DECISION_VALIDATED" => ImportCommitCheckpoint.DecisionValidated,
        "DESTINATION_PREPARED" => ImportCommitCheckpoint.DestinationPrepared,
        "PLACEMENT_PLANNED" => ImportCommitCheckpoint.PlacementPlanned,
        "DESTINATION_BYTES_VERIFIED" => ImportCommitCheckpoint.DestinationBytesVerified,
        "DOMAIN_AUTHORITY_COMMITTED" => ImportCommitCheckpoint.DomainAuthorityCommitted,
        "SOURCE_CLEANUP_PENDING" => ImportCommitCheckpoint.SourceCleanupPending,
        "SOURCE_CLEANUP_COMPLETE" => ImportCommitCheckpoint.SourceCleanupComplete,
        "TERMINAL" => ImportCommitCheckpoint.Terminal,
        _ => throw new FormatException($"'{value}' is not a valid ImportCommitCheckpoint database value."),
    };

    /// <summary>An absent library commit state means the unit has not committed anything yet.</summary>
    public static ImportCommitCheckpoint ParseImportCommitCheckpointOrDefault(string? value) =>
        string.IsNullOrEmpty(value)
            ? ImportCommitCheckpoint.NotCommitted
            : ParseImportCommitCheckpoint(value);

    public static string Format(AssetDependencyStatus value) => value switch
    {
        AssetDependencyStatus.SelfContained => "SELF_CONTAINED",
        AssetDependencyStatus.Complete => "COMPLETE",
        AssetDependencyStatus.DependenciesMissing => "DEPENDENCIES_MISSING",
        AssetDependencyStatus.DependenciesUnknown => "DEPENDENCIES_UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static AssetDependencyStatus ParseAssetDependencyStatus(string value) => value switch
    {
        "SELF_CONTAINED" => AssetDependencyStatus.SelfContained,
        "COMPLETE" => AssetDependencyStatus.Complete,
        "DEPENDENCIES_MISSING" => AssetDependencyStatus.DependenciesMissing,
        "DEPENDENCIES_UNKNOWN" => AssetDependencyStatus.DependenciesUnknown,
        _ => throw new FormatException($"'{value}' is not a valid AssetDependencyStatus database value."),
    };

    public static string Format(ComponentRole value) => value switch
    {
        ComponentRole.Primary => "PRIMARY",
        ComponentRole.Dependency => "DEPENDENCY",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ComponentRole ParseComponentRole(string value) => value switch
    {
        "PRIMARY" => ComponentRole.Primary,
        "DEPENDENCY" => ComponentRole.Dependency,
        _ => throw new FormatException($"'{value}' is not a valid ComponentRole database value."),
    };

    public static string Format(AssetState value) => value switch
    {
        AssetState.Candidate => "CANDIDATE",
        AssetState.Active => "ACTIVE",
        AssetState.Trashed => "TRASHED",
        AssetState.Retired => "RETIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static AssetState ParseAssetState(string value) => value switch
    {
        "CANDIDATE" => AssetState.Candidate,
        "ACTIVE" => AssetState.Active,
        "TRASHED" => AssetState.Trashed,
        "RETIRED" => AssetState.Retired,
        _ => throw new FormatException($"'{value}' is not a valid AssetState database value."),
    };

    public static string Format(AssetRetirementReason value) => value switch
    {
        AssetRetirementReason.Skipped => "SKIPPED",
        AssetRetirementReason.Cancelled => "CANCELLED",
        AssetRetirementReason.Invalid => "INVALID",
        AssetRetirementReason.DedupReused => "DEDUP_REUSED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Only RETIRED assets carry a retirement reason; every other state stores NULL.</summary>
    public static string? FormatOrNull(AssetRetirementReason? value) =>
        value is { } reason ? Format(reason) : null;

    public static AssetRetirementReason? ParseAssetRetirementReasonOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseAssetRetirementReason(value);

    public static AssetRetirementReason ParseAssetRetirementReason(string value) => value switch
    {
        "SKIPPED" => AssetRetirementReason.Skipped,
        "CANCELLED" => AssetRetirementReason.Cancelled,
        "INVALID" => AssetRetirementReason.Invalid,
        "DEDUP_REUSED" => AssetRetirementReason.DedupReused,
        _ => throw new FormatException($"'{value}' is not a valid AssetRetirementReason database value."),
    };

    public static string Format(MediaType value) => value switch
    {
        MediaType.Image => "IMAGE",
        MediaType.Video => "VIDEO",
        MediaType.Model => "MODEL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static MediaType ParseMediaType(string value) => value switch
    {
        "IMAGE" => MediaType.Image,
        "VIDEO" => MediaType.Video,
        "MODEL" => MediaType.Model,
        _ => throw new FormatException($"'{value}' is not a valid MediaType database value."),
    };

    public static string Format(ProfileKind value) => value switch
    {
        ProfileKind.Normal => "NORMAL",
        ProfileKind.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ProfileKind ParseProfileKind(string value) => value switch
    {
        "NORMAL" => ProfileKind.Normal,
        "UNKNOWN" => ProfileKind.Unknown,
        _ => throw new FormatException($"'{value}' is not a valid ProfileKind database value."),
    };

    public static string Format(ProfileAssetRelation value) => value switch
    {
        ProfileAssetRelation.Owner => "OWNER",
        ProfileAssetRelation.Appears => "APPEARS",
        ProfileAssetRelation.Manual => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ProfileAssetRelation ParseProfileAssetRelation(string value) => value switch
    {
        "OWNER" => ProfileAssetRelation.Owner,
        "APPEARS" => ProfileAssetRelation.Appears,
        "MANUAL" => ProfileAssetRelation.Manual,
        _ => throw new FormatException($"'{value}' is not a valid ProfileAssetRelation database value."),
    };

    public static string Format(ManagedPathState value) => value switch
    {
        ManagedPathState.None => "NONE",
        ManagedPathState.Pending => "PENDING",
        ManagedPathState.NeedsAttention => "NEEDS_ATTENTION",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ManagedPathState ParseManagedPathState(string value) => value switch
    {
        "NONE" => ManagedPathState.None,
        "PENDING" => ManagedPathState.Pending,
        "NEEDS_ATTENTION" => ManagedPathState.NeedsAttention,
        _ => throw new FormatException($"'{value}' is not a valid ManagedPathState database value."),
    };

    public static string Format(JobLane value) => value switch
    {
        JobLane.Fast => "FAST",
        JobLane.Io => "IO",
        JobLane.Cpu => "CPU",
        JobLane.Media => "MEDIA",
        JobLane.Face => "FACE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static JobLane ParseJobLane(string value) => value switch
    {
        "FAST" => JobLane.Fast,
        "IO" => JobLane.Io,
        "CPU" => JobLane.Cpu,
        "MEDIA" => JobLane.Media,
        "FACE" => JobLane.Face,
        _ => throw new FormatException($"'{value}' is not a valid JobLane database value."),
    };

    public static string Format(JobState value) => value switch
    {
        JobState.Pending => "PENDING",
        JobState.Runnable => "RUNNABLE",
        JobState.Running => "RUNNING",
        JobState.Paused => "PAUSED",
        JobState.Succeeded => "SUCCEEDED",
        JobState.FailedRetryable => "FAILED_RETRYABLE",
        JobState.FailedTerminal => "FAILED_TERMINAL",
        JobState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static JobState ParseJobState(string value) => value switch
    {
        "PENDING" => JobState.Pending,
        "RUNNABLE" => JobState.Runnable,
        "RUNNING" => JobState.Running,
        "PAUSED" => JobState.Paused,
        "SUCCEEDED" => JobState.Succeeded,
        "FAILED_RETRYABLE" => JobState.FailedRetryable,
        "FAILED_TERMINAL" => JobState.FailedTerminal,
        "CANCELLED" => JobState.Cancelled,
        _ => throw new FormatException($"'{value}' is not a valid JobState database value."),
    };

    public static string Format(FaceDecisionState value) => value switch
    {
        FaceDecisionState.Unknown => "UNKNOWN",
        FaceDecisionState.Suggested => "SUGGESTED",
        FaceDecisionState.Confirmed => "CONFIRMED",
        FaceDecisionState.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static FaceDecisionState ParseFaceDecisionState(string value) => value switch
    {
        "UNKNOWN" => FaceDecisionState.Unknown,
        "SUGGESTED" => FaceDecisionState.Suggested,
        "CONFIRMED" => FaceDecisionState.Confirmed,
        "REJECTED" => FaceDecisionState.Rejected,
        _ => throw new FormatException($"'{value}' is not a valid FaceDecisionState database value."),
    };

    public static string Format(RelatedProfileEvidence value) => value switch
    {
        RelatedProfileEvidence.SharedAsset => "SHARED_ASSET",
        RelatedProfileEvidence.ConfirmedFace => "CONFIRMED_FACE",
        RelatedProfileEvidence.Manual => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static RelatedProfileEvidence ParseRelatedProfileEvidence(string value) => value switch
    {
        "SHARED_ASSET" => RelatedProfileEvidence.SharedAsset,
        "CONFIRMED_FACE" => RelatedProfileEvidence.ConfirmedFace,
        "MANUAL" => RelatedProfileEvidence.Manual,
        _ => throw new FormatException(
            $"'{value}' is not a valid RelatedProfileEvidence database value."),
    };

    public static string Format(AssetCapability value) => value switch
    {
        AssetCapability.CanonicalMedia => "CANONICAL_MEDIA",
        AssetCapability.Metadata => "METADATA",
        AssetCapability.Thumbnail => "THUMBNAIL",
        AssetCapability.PresentationStill => "PRESENTATION_STILL",
        AssetCapability.VideoPreview => "VIDEO_PREVIEW",
        AssetCapability.FaceDetection => "FACE_DETECTION",
        AssetCapability.FaceEmbedding => "FACE_EMBEDDING",
        AssetCapability.SearchProjection => "SEARCH_PROJECTION",
        AssetCapability.SimilarityRelated => "SIMILARITY_RELATED",
        AssetCapability.PresentationInput => "PRESENTATION_INPUT",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static AssetCapability ParseAssetCapability(string value) => value switch
    {
        "CANONICAL_MEDIA" => AssetCapability.CanonicalMedia,
        "METADATA" => AssetCapability.Metadata,
        "THUMBNAIL" => AssetCapability.Thumbnail,
        "PRESENTATION_STILL" => AssetCapability.PresentationStill,
        "VIDEO_PREVIEW" => AssetCapability.VideoPreview,
        "FACE_DETECTION" => AssetCapability.FaceDetection,
        "FACE_EMBEDDING" => AssetCapability.FaceEmbedding,
        "SEARCH_PROJECTION" => AssetCapability.SearchProjection,
        "SIMILARITY_RELATED" => AssetCapability.SimilarityRelated,
        "PRESENTATION_INPUT" => AssetCapability.PresentationInput,
        _ => throw new FormatException($"'{value}' is not a valid AssetCapability database value."),
    };

    public static string Format(AssetCapabilityState value) => value switch
    {
        AssetCapabilityState.Queued => "QUEUED",
        AssetCapabilityState.Processing => "PROCESSING",
        AssetCapabilityState.Ready => "READY",
        AssetCapabilityState.NotApplicable => "NOT_APPLICABLE",
        AssetCapabilityState.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static AssetCapabilityState ParseAssetCapabilityState(string value) => value switch
    {
        "QUEUED" => AssetCapabilityState.Queued,
        "PROCESSING" => AssetCapabilityState.Processing,
        "READY" => AssetCapabilityState.Ready,
        "NOT_APPLICABLE" => AssetCapabilityState.NotApplicable,
        "FAILED" => AssetCapabilityState.Failed,
        _ => throw new FormatException($"'{value}' is not a valid AssetCapabilityState database value."),
    };
}

public enum ManagedPathState
{
    None,
    Pending,
    NeedsAttention,
}

public sealed class CatalogInvariantException : InvalidOperationException
{
    public CatalogInvariantException(string message)
        : base(message)
    {
    }

    public CatalogInvariantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CatalogConcurrencyConflictException : InvalidOperationException
{
    public CatalogConcurrencyConflictException(string message)
        : base(message)
    {
    }
}
