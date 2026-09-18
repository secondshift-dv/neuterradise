using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Shell;

/// <summary>
/// User-facing window-size presets. A preset resizes the window once; it never locks the size.
/// <see cref="Custom"/> is not selectable — it describes any size the user dragged to by hand.
/// </summary>
public enum WindowSizePreset
{
    FitToScreen,
    Size1280x720,
    Size1128x634,
    Size1024x768,
    Size800x600,
    Custom,
}

/// <summary>
/// Pure window geometry for Section 25. Everything is in device-independent units and works on
/// monitor <em>work areas</em>, so the rules (presets, Custom detection, clamping, monitor choice)
/// can be verified without a live window or a real desktop.
/// </summary>
public static class WindowGeometry
{
    /// <summary>Smallest supported window. 800 × 600 is a real, reachable size.</summary>
    public const double MinimumWidth = 800;

    public const double MinimumHeight = 600;

    /// <summary>Largest size Fit to Screen asks for; anything bigger is wasted on a single-window app.</summary>
    public const double PreferredWidth = 1400;

    public const double PreferredHeight = 900;

    /// <summary>Fraction of the work area Fit to Screen occupies when the preferred size does not fit.</summary>
    public const double WorkAreaFraction = 0.92;

    /// <summary>How close a size must be to a preset to count as that preset.</summary>
    public const double MatchTolerance = 2;

    /// <summary>How much of the window must remain on a monitor for a saved position to be usable.</summary>
    public const double RequiredVisibleWidth = 220;

    public const double RequiredVisibleHeight = 80;

    public static IReadOnlyList<WindowSizePreset> SelectablePresets { get; } =
    [
        WindowSizePreset.FitToScreen,
        WindowSizePreset.Size1280x720,
        WindowSizePreset.Size1128x634,
        WindowSizePreset.Size1024x768,
        WindowSizePreset.Size800x600,
    ];

    /// <summary>The nominal size of a fixed preset, or null for Fit to Screen and Custom.</summary>
    public static Size? FixedSize(WindowSizePreset preset) => preset switch
    {
        WindowSizePreset.Size1280x720 => new Size(1280, 720),
        WindowSizePreset.Size1128x634 => new Size(1128, 634),
        WindowSizePreset.Size1024x768 => new Size(1024, 768),
        WindowSizePreset.Size800x600 => new Size(800, 600),
        _ => null,
    };

    /// <summary>Minimum size the window may take on the given work area.</summary>
    public static Size MinimumFor(Rect workArea) =>
        new(Math.Min(MinimumWidth, workArea.Width), Math.Min(MinimumHeight, workArea.Height));

    /// <summary>
    /// Fit to Screen: the preferred size, shrunk to a fraction of the work area on small monitors,
    /// never below the supported minimum and never beyond the work area itself.
    /// </summary>
    public static Size FitToScreenSize(Rect workArea)
    {
        var minimum = MinimumFor(workArea);
        var width = Math.Min(PreferredWidth, workArea.Width * WorkAreaFraction);
        var height = Math.Min(PreferredHeight, workArea.Height * WorkAreaFraction);
        width = Math.Max(minimum.Width, width);
        height = Math.Max(minimum.Height, height);
        return new Size(Math.Floor(width), Math.Floor(height));
    }

    /// <summary>The size a preset asks for on the given work area, already clamped to it.</summary>
    public static Size RequestedSize(WindowSizePreset preset, Rect workArea)
    {
        if (preset == WindowSizePreset.FitToScreen)
        {
            return FitToScreenSize(workArea);
        }

        var nominal = FixedSize(preset)
            ?? throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom is not a requestable size.");
        var minimum = MinimumFor(workArea);
        return new Size(
            Math.Clamp(nominal.Width, minimum.Width, Math.Max(minimum.Width, workArea.Width)),
            Math.Clamp(nominal.Height, minimum.Height, Math.Max(minimum.Height, workArea.Height)));
    }

    /// <summary>
    /// Names the preset a window size corresponds to on the given work area, or <see cref="WindowSizePreset.Custom"/>
    /// when it matches none — which is what any manual drag-resize produces.
    /// </summary>
    public static WindowSizePreset Match(Size size, Rect workArea)
    {
        foreach (var preset in SelectablePresets)
        {
            if (preset == WindowSizePreset.FitToScreen)
            {
                continue;
            }

            if (IsClose(size, RequestedSize(preset, workArea)))
            {
                return preset;
            }
        }

        return IsClose(size, FitToScreenSize(workArea)) ? WindowSizePreset.FitToScreen : WindowSizePreset.Custom;
    }

    private static bool IsClose(Size a, Size b) =>
        Math.Abs(a.Width - b.Width) <= MatchTolerance && Math.Abs(a.Height - b.Height) <= MatchTolerance;

    /// <summary>
    /// True when a rectangle still lands on some connected monitor with enough of the title bar
    /// reachable. The primary monitor is not special: a window on a secondary monitor that is still
    /// connected is on screen.
    /// </summary>
    public static bool IsOnScreen(Rect bounds, IReadOnlyList<Rect> monitorWorkAreas)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        foreach (var area in monitorWorkAreas)
        {
            var overlap = Rect.Intersect(bounds, area);
            if (!overlap.IsEmpty
                && overlap.Width >= Math.Min(RequiredVisibleWidth, bounds.Width)
                && overlap.Height >= Math.Min(RequiredVisibleHeight, bounds.Height))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The work area of the monitor the rectangle mostly lies on; the fallback (normally the primary
    /// monitor) when it touches none of them, for example because its monitor was unplugged.
    /// </summary>
    public static Rect SelectWorkArea(Rect bounds, IReadOnlyList<Rect> monitorWorkAreas, Rect fallback)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        var best = fallback;
        var bestArea = 0.0;
        foreach (var area in monitorWorkAreas)
        {
            var overlap = Rect.Intersect(bounds, area);
            if (overlap.IsEmpty)
            {
                continue;
            }

            var overlapArea = overlap.Width * overlap.Height;
            if (overlapArea > bestArea)
            {
                best = area;
                bestArea = overlapArea;
            }
        }

        return best;
    }

    /// <summary>Shrinks the rectangle to fit the work area and moves it fully inside.</summary>
    public static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var minimum = MinimumFor(workArea);
        var width = Math.Clamp(bounds.Width, minimum.Width, Math.Max(minimum.Width, workArea.Width));
        var height = Math.Clamp(bounds.Height, minimum.Height, Math.Max(minimum.Height, workArea.Height));
        var left = Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    public static Rect CenterIn(Size size, Rect workArea) =>
        ClampToWorkArea(
            new Rect(
                Math.Floor(workArea.Left + ((workArea.Width - size.Width) / 2)),
                Math.Floor(workArea.Top + ((workArea.Height - size.Height) / 2)),
                size.Width,
                size.Height),
            workArea);

    /// <summary>
    /// Resizes a window to a requested size while keeping it where it is: the centre is preserved
    /// and the result is clamped to the monitor the window is on.
    /// </summary>
    public static Rect ResizeInPlace(Rect current, Size requested, Rect workArea)
    {
        var center = new Point(current.Left + (current.Width / 2), current.Top + (current.Height / 2));
        return ClampToWorkArea(
            new Rect(
                Math.Floor(center.X - (requested.Width / 2)),
                Math.Floor(center.Y - (requested.Height / 2)),
                requested.Width,
                requested.Height),
            workArea);
    }

    /// <summary>
    /// Decides the restore rectangle and maximized state the window opens with. A saved placement on
    /// any still-connected monitor is honoured (clamped to that monitor); otherwise the window is
    /// centred on the fallback monitor, keeping the saved size when there was one.
    /// </summary>
    public static StartupPlacement ResolveStartup(
        WindowPlacementConfiguration? saved,
        IReadOnlyList<Rect> monitorWorkAreas,
        Rect fallbackWorkArea)
    {
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        if (saved is { IsUsable: true })
        {
            var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            if (IsOnScreen(bounds, monitorWorkAreas))
            {
                var workArea = SelectWorkArea(bounds, monitorWorkAreas, fallbackWorkArea);
                return new StartupPlacement(ClampToWorkArea(bounds, workArea), workArea, saved.IsMaximized, RestoredSaved: true);
            }

            var size = ClampToWorkArea(new Rect(0, 0, saved.Width, saved.Height), fallbackWorkArea).Size;
            return new StartupPlacement(CenterIn(size, fallbackWorkArea), fallbackWorkArea, saved.IsMaximized, RestoredSaved: false);
        }

        return new StartupPlacement(
            CenterIn(FitToScreenSize(fallbackWorkArea), fallbackWorkArea),
            fallbackWorkArea,
            IsMaximized: false,
            RestoredSaved: false);
    }
}

public readonly record struct StartupPlacement(Rect Bounds, Rect WorkArea, bool IsMaximized, bool RestoredSaved);
