using Neuterradise.App.Media;
using Neuterradise.App.Localization;

namespace Neuterradise.App.Import;

/// <summary>
/// Canonical persisted disposition of one import item (Section 44.2.6):
/// <c>INCLUDED | SKIPPED | REUSED | INVALID</c>. A pending exact-duplicate choice is not a
/// disposition; it is the absence of <see cref="DuplicateDecision"/> on an item whose bytes match an
/// authoritative asset, and it is reported as a readiness blocker.
/// </summary>
public enum ItemDisposition
{
    Included,
    Skipped,
    Reused,
    Invalid,
}

/// <summary>Canonical persisted lifecycle of an import session: <c>OPEN | COMPLETED | CANCELLED</c>.</summary>
public enum ImportSessionState
{
    Open,
    Completed,
    Cancelled,
}

/// <summary>
/// Canonical persisted lifecycle of an import unit (Section 44.2.6):
/// <c>INTAKE → PREPARING → READY_FOR_VERIFICATION → COMMITTING → COMMITTED → COMPLETED</c>, with
/// pre-commit branches to <c>FAILED_RETRYABLE</c>, <c>FAILED_TERMINAL</c> or <c>CANCELLED</c>, and
/// <c>COMMITTED_WITH_CLEANUP_ATTENTION</c> when source cleanup failed after a durable commit.
/// Pause is not a lifecycle state; it is the separate persisted <c>is_paused</c> flag.
/// </summary>
public enum ImportUnitState
{
    Intake,
    Preparing,
    ReadyForVerification,
    Committing,
    Committed,
    Completed,
    FailedRetryable,
    FailedTerminal,
    Cancelled,
    CommittedWithCleanupAttention,
}

/// <summary>
/// Canonical readiness of one import item before verification (Section 44.2.6):
/// <c>PENDING | RUNNING | READY | FAILED_RETRYABLE | FAILED_TERMINAL</c>.
/// </summary>
public enum ImportPreparationStatus
{
    Pending,
    Running,
    Ready,
    FailedRetryable,
    FailedTerminal,
}

/// <summary>Canonical persisted source policy of one import item: <c>COPY | MOVE</c>.</summary>
public enum ImportCleanupPolicy
{
    Copy,
    Move,
}

/// <summary>
/// Canonical persisted source-cleanup state of one import item (Section 44.2.6). <c>SourceConsumed</c>
/// means an authorized source really was deleted; a Copy import ends at <c>SourcePreserved</c> and is
/// never called consumed.
/// </summary>
public enum SourceCleanupState
{
    SourcePresent,
    DestinationVerified,
    LibraryCommitted,
    SourceDeletePending,
    SourceConsumed,
    SourceDeleteFailed,
    SourcePreserved,
    SourceChanged,
}

/// <summary>Derived per-unit summary of source cleanup; not a persisted column.</summary>
public enum ImportUnitCleanupSummary
{
    None,
    Pending,
    Complete,
    NeedsAttention,
}

public sealed record ImportSessionSummary(
    Guid SessionId,
    ImportSessionState State,
    int UnitCount,
    int TotalItemCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ImportUnitSummary(
    Guid UnitId,
    Guid SessionId,
    Guid? ParentUnitId,
    string SourceKind,
    string SourceDisplayName,
    string? SourcePathOrReference,
    ImportUnitState State,
    int TotalItemCount,
    int IncludedCount,
    int SkippedCount,
    int ExactDuplicateCount,
    int NeedsAttentionCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? DestinationProfileId = null,
    Verification.ImportCommitCheckpoint LibraryCommitState = Verification.ImportCommitCheckpoint.NotCommitted,
    int VerificationStep = 1,
    int CleanupFailedCount = 0,
    bool IsPaused = false,
    int CleanupConsumedCount = 0,
    int CleanupPreservedCount = 0,
    int CleanupPendingCount = 0);

/// <summary>
/// Import-critical aggregate for one unit (see <c>ImportReads.ListUnitProgressAsync</c>). Admitted items
/// are the ones that will end up in the library (new or already present); prepared means every deterministic
/// capability for the effective asset is terminal (READY or NOT_APPLICABLE); placed means the library copy exists.
/// </summary>
public sealed record ImportUnitProgress(
    Guid UnitId,
    string? DraftJson,
    string? DestinationDisplayName,
    int TotalItemCount,
    int AdmittedItemCount,
    int PreparedItemCount,
    int PlacedItemCount,
    int UnusableItemCount,
    int AlreadyInLibraryCount);

public sealed record ImportItemSummary(
    Guid ItemId,
    Guid UnitId,
    Guid? CandidateAssetId,
    Guid? ReusedAssetId,
    string SourcePath,
    string SourceFileName,
    long? SourceByteLength,
    DateTimeOffset? SourceLastWriteUtc,
    ItemDisposition Disposition,
    Verification.DuplicateDecision? DuplicateDecision,
    SourceCleanupState SourceCleanupState,
    string? SourceCleanupError,
    MediaType? MediaType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ImportCleanupPolicy CleanupPolicy = ImportCleanupPolicy.Copy);

public sealed record ImportHistoryEntry(
    Guid SessionId,
    Guid UnitId,
    string SourceDisplayName,
    string SourceKind,
    ImportUnitState UnitState,
    int ItemCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int IncludedCount = 0,
    int SkippedCount = 0,
    int ReusedCount = 0,
    int NeedsAttentionCount = 0,
    Guid? DestinationProfileId = null,
    string? DestinationProfileLabel = null,
    Verification.ImportCommitCheckpoint LibraryCommitState = Verification.ImportCommitCheckpoint.NotCommitted,
    ImportUnitCleanupSummary SourceCleanupStatus = ImportUnitCleanupSummary.None,
    int SourceCleanupFailedCount = 0,
    int SourceCleanupPendingCount = 0,
    int FailedJobCount = 0,
    bool OptionalProfilingRunning = false)
{

    public string PresentationState => ImportPresentation.DescribeUnitState(
        UnitState,
        LibraryCommitState,
        NeedsAttentionCount,
        SourceCleanupFailedCount);

    public bool CanRetryUnit => UnitState.IsRetryable() && !LibraryCommitState.HasReachedDomainCommit();

    public bool CanRetrySourceCleanup => SourceCleanupFailedCount > 0;

    public bool CanOpenProfile =>
        LibraryCommitState.HasReachedDomainCommit()
        && DestinationProfileId is { } id
        && id != Guid.Empty;

    public string? OptionalProfilingNotice =>
        OptionalProfilingRunning && LibraryCommitState.HasReachedDomainCommit()
            ? ImportPresentation.OptionalProfilingNotice
            : null;
}

public sealed record ImportCancellationOutcome(
    Guid UnitId,
    ImportCancellationResult Result,
    int CancelledJobCount = 0,
    int RetiredCandidateCount = 0,
    int RunningJobCount = 0,
    IReadOnlyList<Guid> AssetsForTrashDisposition = null,
    bool DestinationProfileCreated = false)
{
    public bool WasCancelled => Result is ImportCancellationResult.Cancelled
        or ImportCancellationResult.AlreadyCancelled
        or ImportCancellationResult.RollbackPending;

    /// <summary>True when rollback still needs to be completed (Trash, Profile disposition, DB delta cleanup).</summary>
    public bool NeedsRollback => (Result is ImportCancellationResult.Cancelled or ImportCancellationResult.RollbackPending)
        && (AssetsNeedingTrash.Count > 0 || DestinationProfileCreated);

    /// <summary>Exclusively-imported ACTIVE assets that need recoverable Trash/Quarantine disposition.</summary>
    public IReadOnlyList<Guid> AssetsNeedingTrash => AssetsForTrashDisposition ?? [];

    public string UserMessage => Result switch
    {
        ImportCancellationResult.Cancelled =>
            "This import was cancelled. No source files were touched.",
        ImportCancellationResult.AlreadyCancelled =>
            "This import was already cancelled. No source files were touched.",
        ImportCancellationResult.RefusedAlreadyCommitted =>
            "This import already added media to your library, so it cannot be cancelled.",
        ImportCancellationResult.UnitNotFound =>
            "That import no longer exists.",
        _ => "This import could not be cancelled.",
    };
}

public enum ImportCancellationResult
{

    Cancelled,

    AlreadyCancelled,

    /// <summary>Cancel was persisted but external rollback (Trash, Profile disposition) has not yet settled.</summary>
    RollbackPending,

    RefusedAlreadyCommitted,

    UnitNotFound
}

/// <summary>
/// Lifecycle predicates over the canonical import enums. This is the only place that decides what
/// counts as committed or terminal, so surfaces and writers cannot disagree.
/// </summary>
public static class ImportLifecycle
{
    /// <summary>Checkpoints at or after which library state is durably committed.</summary>
    public static IReadOnlyList<Verification.ImportCommitCheckpoint> CommittedCheckpoints { get; } =
    [
        Verification.ImportCommitCheckpoint.DomainAuthorityCommitted,
        Verification.ImportCommitCheckpoint.SourceCleanupPending,
        Verification.ImportCommitCheckpoint.SourceCleanupComplete,
        Verification.ImportCommitCheckpoint.Terminal,
    ];

    /// <summary>True once the domain authority transaction committed; cancellation can no longer undo it.</summary>
    public static bool HasReachedDomainCommit(this Verification.ImportCommitCheckpoint checkpoint) =>
        checkpoint is Verification.ImportCommitCheckpoint.DomainAuthorityCommitted
            or Verification.ImportCommitCheckpoint.SourceCleanupPending
            or Verification.ImportCommitCheckpoint.SourceCleanupComplete
            or Verification.ImportCommitCheckpoint.Terminal;

    /// <summary>True when the unit itself already claims committed library state.</summary>
    public static bool IsUnitCommitted(this ImportUnitState state) =>
        state is ImportUnitState.Committed
            or ImportUnitState.Completed
            or ImportUnitState.CommittedWithCleanupAttention;

    /// <summary>
    /// True when either the unit lifecycle or the library commit checkpoint indicates durable
    /// commit. Both may advance independently during crash recovery; checking either is the
    /// correct guard for "no further commit work is needed".
    /// </summary>
    public static bool IsEffectivelyCommitted(this ImportUnitState unitState, Verification.ImportCommitCheckpoint libraryCommitState) =>
        unitState.IsUnitCommitted() || libraryCommitState.HasReachedDomainCommit();

    /// <summary>True when no further automatic work will move this unit forward.</summary>
    public static bool IsTerminal(this ImportUnitState state) =>
        state is ImportUnitState.Completed
            or ImportUnitState.FailedTerminal
            or ImportUnitState.Cancelled;

    /// <summary>True when the same unit may be retried without user redecision.</summary>
    public static bool IsRetryable(this ImportUnitState state) =>
        state is ImportUnitState.FailedRetryable;

    /// <summary>True when the source bytes of the item are still present on disk.</summary>
    public static bool SourceStillPresent(this SourceCleanupState state) =>
        state is not (SourceCleanupState.SourceConsumed);
}

public static class ImportPresentation
{
    public static string Discovering => SurfaceText.Get("Import.Discovering", "Discovering");
    public static string Preparation => SurfaceText.Get("Import.Preparation", "Preparing");
    public static string ReadyForVerification => SurfaceText.Get("Import.ReadyForVerification", "Ready for Verification");
    public static string Verifying => SurfaceText.Get("Import.Verifying", "Verifying");
    public static string Committing => SurfaceText.Get("Import.Committing", "Committing");
    public static string Imported => SurfaceText.Get("Import.Imported", "Imported");
    public static string ImportedWithCleanupAttention => SurfaceText.Get("Import.ImportedWithCleanupAttention", "Imported · Source cleanup needs attention");
    public static string Paused => SurfaceText.Get("Import.Paused", "Paused");
    public static string NeedsAttention => SurfaceText.Get("Import.NeedsAttention", "Needs Attention");
    public static string Failed => SurfaceText.Get("Import.Failed", "Failed");
    public static string Cancelled => SurfaceText.Get("Import.Cancelled", "Cancelled");

    public static string OptionalProfilingNotice => SurfaceText.Get(
        "Import.OptionalProfilingNotice",
        "Media is available now. Optional face profiling is still running in the background.");

    public static IReadOnlyList<string> Vocabulary { get; } =
    [
        Discovering,
        Preparation,
        ReadyForVerification,
        Verifying,
        Committing,
        Imported,
        ImportedWithCleanupAttention,
        Paused,
        NeedsAttention,
        Failed,
        Cancelled,
    ];

    /// <summary>
    /// One presentation mapping for the canonical unit lifecycle. Pause is reported from the separate
    /// persisted pause flag, never from the lifecycle value, so resume does not have to guess the
    /// previous state.
    /// </summary>
    public static string DescribeUnitState(
        ImportUnitState unitState,
        Verification.ImportCommitCheckpoint libraryCommitState,
        int needsAttentionCount = 0,
        int sourceCleanupFailedCount = 0,
        bool isPaused = false,
        int verificationStep = 1)
    {
        if (unitState.IsEffectivelyCommitted(libraryCommitState))
        {
            return sourceCleanupFailedCount > 0
                || unitState == ImportUnitState.CommittedWithCleanupAttention
                ? ImportedWithCleanupAttention
                : Imported;
        }

        if (isPaused)
        {
            return Paused;
        }

        return unitState switch
        {
            ImportUnitState.Cancelled => Cancelled,
            ImportUnitState.FailedTerminal => Failed,
            ImportUnitState.FailedRetryable => NeedsAttention,
            ImportUnitState.Committing => Committing,
            ImportUnitState.ReadyForVerification when needsAttentionCount > 0 => NeedsAttention,
            ImportUnitState.ReadyForVerification when verificationStep > 1 => Verifying,
            ImportUnitState.ReadyForVerification => ReadyForVerification,
            ImportUnitState.Intake => Discovering,
            ImportUnitState.Preparing => Preparation,
            _ when needsAttentionCount > 0 => NeedsAttention,
            _ => Preparation,
        };
    }

    public static string DescribeUnitState(ImportUnitSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return DescribeUnitState(
            summary.State,
            summary.LibraryCommitState,
            summary.NeedsAttentionCount,
            summary.CleanupFailedCount,
            summary.IsPaused,
            summary.VerificationStep);
    }
}
