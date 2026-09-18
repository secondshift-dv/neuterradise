namespace Neuterradise.App.SystemServices.Recovery;

public enum RecoveryOutcome
{
    NoAction,
    Completed,
    Resumed,
    Requeued,
    NeedsAttention,
    Fatal,
}

public sealed record RecoveryResult(IReadOnlyList<RecoveryFinding> Findings)
{
    public static RecoveryResult NoAction { get; } = new(Array.Empty<RecoveryFinding>());

    public bool HasFatal => Findings.Any(finding => finding.Outcome == RecoveryOutcome.Fatal);
}

public sealed class RecoveryFailedException : Exception
{
    public RecoveryFailedException(RecoveryResult result)
        : base("Startup recovery encountered a fatal persisted operation failure.")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public RecoveryResult Result { get; }
}
