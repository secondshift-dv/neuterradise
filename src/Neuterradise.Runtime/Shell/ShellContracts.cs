using Neuterradise.App.Design.Themes;

namespace Neuterradise.App.Shell;

/// <summary>
/// Applies Theme / Density / Reduced Motion to the live presentation. Implemented by the renderer
/// (Uno) so view models change appearance without holding renderer types.
/// </summary>
public interface IPresentationPreferenceApplier
{
    Task<PresentationPreferenceApplyResult> ApplyThemeAsync(
        string themeId,
        CancellationToken cancellationToken = default);

    void ApplyDensity(DensityMode density);

    void ApplyReduceMotion(bool userReduceMotion);
}

public sealed record PresentationPreferenceApplyResult(
    bool Succeeded,
    ThemeSelection Selection,
    string? Message = null);

/// <summary>
/// The single owner of main-window geometry (Section 25). Settings → Display talks to it; the
/// renderer host implements it against its real window.
/// </summary>
public interface IWindowPlacement
{
    event EventHandler? PlacementChanged;

    bool IsAttached { get; }

    bool IsMaximized { get; }

    Size? RestoreSize { get; }

    WindowSizePreset CurrentPreset { get; }

    void ApplyPreset(WindowSizePreset preset);
}

public enum UiScaleCommand
{
    Increase,
    Decrease,
    Reset,
}

/// <summary>
/// UI-scale keyboard gestures: Ctrl+Plus / Ctrl+NumpadPlus, Ctrl+Minus / Ctrl+NumpadMinus, Ctrl+0 /
/// Ctrl+Numpad0. The main window registers these once; nothing else does.
/// </summary>
public static class UiScaleCommands
{
    public static IReadOnlyList<(UiScaleCommand Command, Key Key, ModifierKeys Modifiers)> Gestures { get; } =
    [
        (UiScaleCommand.Increase, Key.OemPlus, ModifierKeys.Control),
        // Shift+= types "+" on many layouts; accept it so "Ctrl and plus" works as the user means it.
        (UiScaleCommand.Increase, Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift),
        (UiScaleCommand.Increase, Key.Add, ModifierKeys.Control),
        (UiScaleCommand.Decrease, Key.OemMinus, ModifierKeys.Control),
        (UiScaleCommand.Decrease, Key.Subtract, ModifierKeys.Control),
        (UiScaleCommand.Reset, Key.D0, ModifierKeys.Control),
        (UiScaleCommand.Reset, Key.NumPad0, ModifierKeys.Control),
    ];

    /// <summary>Runs a scale command against the one authority. Returns true when scale changed.</summary>
    public static bool Execute(UiScaleCommand command, UiScaleService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return command switch
        {
            UiScaleCommand.Increase => service.Increase(),
            UiScaleCommand.Decrease => service.Decrease(),
            _ => service.Reset(),
        };
    }

    public static bool CanExecute(UiScaleCommand command, UiScaleService service) => command switch
    {
        UiScaleCommand.Increase => service.CanIncrease,
        UiScaleCommand.Decrease => service.CanDecrease,
        _ => true,
    };

    public static UiScaleCommand? Match(Key key, ModifierKeys modifiers)
    {
        foreach (var gesture in Gestures)
        {
            if (gesture.Key == key && gesture.Modifiers == modifiers)
            {
                return gesture.Command;
            }
        }

        return null;
    }
}
