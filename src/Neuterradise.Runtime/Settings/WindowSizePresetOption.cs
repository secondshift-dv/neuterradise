using System.Globalization;
using Neuterradise.App.Localization;
using Neuterradise.App.Shell;

namespace Neuterradise.App.Settings;

/// <summary>One selectable window-size preset in Settings &gt; Display.</summary>
public sealed class WindowSizePresetOption : ObservableObject
{
    private bool _isCurrent;

    public WindowSizePresetOption(WindowSizePreset preset)
    {
        Preset = preset;
    }

    public WindowSizePreset Preset { get; }

    public string Label => LabelFor(Preset);

    public string Detail => Preset == WindowSizePreset.FitToScreen
        ? SurfaceText.Get("Settings.WindowSize.Recommended", "Recommended — sized to this monitor")
        : WindowGeometry.FixedSize(Preset) is { } size
            ? string.Create(CultureInfo.InvariantCulture, $"{size.Width:0} × {size.Height:0}")
            : string.Empty;

    /// <summary>True when the window's current restore size is this preset.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public static string LabelFor(WindowSizePreset preset) => preset switch
    {
        WindowSizePreset.FitToScreen => SurfaceText.Get("Settings.WindowSize.FitToScreen", "Fit to Screen"),
        WindowSizePreset.Size1280x720 => "1280 × 720",
        WindowSizePreset.Size1128x634 => "1128 × 634",
        WindowSizePreset.Size1024x768 => "1024 × 768",
        WindowSizePreset.Size800x600 => "800 × 600",
        _ => SurfaceText.Get("Settings.WindowSize.Custom", "Custom"),
    };
}
