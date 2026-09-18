namespace Neuterradise.App.SystemServices.Lifecycle;

public enum StartupState
{
    ProcessStart,
    Bootstrapping,
    OpeningStorage,
    MigratingDatabase,
    Recovering,
    Prewarming,
    Ready,
    ShuttingDown,
    Stopped,
    RecoveryRequired,
    StartupFailed
}
