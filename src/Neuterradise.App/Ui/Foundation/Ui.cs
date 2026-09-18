using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Localization;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Resources;

namespace Neuterradise.App.Ui;

/// <summary>Bridges the runtime's <see cref="UiDispatch"/> to the Uno UI thread.</summary>
public sealed class DispatcherQueueSyncContext(DispatcherQueue queue) : EnqueueSynchronizationContext(
    () => queue.HasThreadAccess,
    callback => queue.TryEnqueue(() => callback()))
{
    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>A bag of subscriptions released together when a surface is retired.</summary>
public sealed class Disposables : IDisposable
{
    private readonly List<IDisposable> _items = [];

    public T Add<T>(T item) where T : IDisposable
    {
        _items.Add(item);
        return item;
    }

    public void Add(Action release) => _items.Add(new ActionDisposable(release));

    public void Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }

        _items.Clear();
    }

    private sealed class ActionDisposable(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Type-checked property observation (compile-time lambdas instead of reflection bindings). The apply
/// callback runs immediately and then on the UI thread whenever one of the named properties changes.
/// </summary>
public static class Observe
{
    public static IDisposable Props(INotifyPropertyChanged? source, Action apply, params string[] properties)
    {
        apply();
        if (source is null)
        {
            return new Disposables();
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (properties.Length == 0 || string.IsNullOrEmpty(args.PropertyName) || properties.Contains(args.PropertyName))
            {
                UiDispatch.Run(apply);
            }
        };
        source.PropertyChanged += handler;
        var bag = new Disposables();
        bag.Add(() => source.PropertyChanged -= handler);
        return bag;
    }

    public static IDisposable Collection(INotifyCollectionChanged? source, Action apply)
    {
        apply();
        if (source is null)
        {
            return new Disposables();
        }

        NotifyCollectionChangedEventHandler handler = (_, _) => UiDispatch.Run(apply);
        source.CollectionChanged += handler;
        var bag = new Disposables();
        bag.Add(() => source.CollectionChanged -= handler);
        return bag;
    }
}

public enum ButtonKind
{
    Primary,
    Secondary,
    Ghost,
    Destructive,
}

/// <summary>Material hierarchy (R2 §9, §35).</summary>
public enum Material
{
    Grounded,
    Raised,
    Frost,
    Deep,
    Glass,
}

/// <summary>Code-first element builders. Keeps every surface on the same token vocabulary.</summary>
public static class UI
{
    public static string T(string key, string fallback) => SurfaceText.Get(key, fallback);

    public static string F(string key, string fallback, params object[] args) => SurfaceText.Format(key, fallback, args);

    public static TextBlock Text(string? text = null, string role = "body", string? color = null, int maxLines = 0)
    {
        var block = new TextBlock { Text = text ?? string.Empty };
        ThemeRuntime.Current.ApplyType(block, role, color);
        if (maxLines > 0)
        {
            block.MaxLines = maxLines;
            block.TextWrapping = maxLines > 1 ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        return block;
    }

    public static StackPanel V(double spacing, params UIElement?[] children) => Stack(Orientation.Vertical, spacing, children);

    public static StackPanel H(double spacing, params UIElement?[] children) => Stack(Orientation.Horizontal, spacing, children);

    public static StackPanel Stack(Orientation orientation, double spacing, params UIElement?[] children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children)
        {
            if (child is not null)
            {
                panel.Children.Add(child);
            }
        }

        return panel;
    }

    /// <summary>Grid from track strings: "auto,*,2*,48".</summary>
    public static Grid Grid(string rows = "*", string columns = "*", params UIElement?[] children)
    {
        var grid = new Grid();
        foreach (var track in rows.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = Track(track.Trim()) });
        }

        foreach (var track in columns.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Track(track.Trim()) });
        }

        foreach (var child in children)
        {
            if (child is not null)
            {
                grid.Children.Add(child);
            }
        }

        return grid;
    }

    public static GridLength Track(string track)
    {
        if (track == "auto")
        {
            return GridLength.Auto;
        }

        if (track.EndsWith('*'))
        {
            var factor = track.Length == 1 ? 1 : double.Parse(track[..^1], System.Globalization.CultureInfo.InvariantCulture);
            return new GridLength(factor, GridUnitType.Star);
        }

        return new GridLength(double.Parse(track, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static T At<T>(this T element, int row, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : FrameworkElement
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(element, rowSpan);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static T Margin<T>(this T element, double uniform) where T : FrameworkElement
    {
        element.Margin = new Thickness(uniform);
        return element;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static T Align<T>(this T element, HorizontalAlignment horizontal = HorizontalAlignment.Stretch, VerticalAlignment vertical = VerticalAlignment.Stretch) where T : FrameworkElement
    {
        element.HorizontalAlignment = horizontal;
        element.VerticalAlignment = vertical;
        return element;
    }

    public static T Size<T>(this T element, double? width = null, double? height = null) where T : FrameworkElement
    {
        if (width is { } w)
        {
            element.Width = w;
        }

        if (height is { } h)
        {
            element.Height = h;
        }

        return element;
    }

    public static T Tip<T>(this T element, string? text) where T : FrameworkElement
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            ToolTipService.SetToolTip(element, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        }

        return element;
    }

    public static T Visible<T>(this T element, bool visible) where T : UIElement
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        return element;
    }

    public static Button Button(string label, Action? onClick = null, ButtonKind kind = ButtonKind.Secondary, string? icon = null, ICommand? command = null, object? parameter = null)
    {
        var theme = ThemeRuntime.Current;
        var content = icon is null
            ? (UIElement)Text(label, "control", kind is ButtonKind.Primary ? "textOnAccent" : null)
            : H(8, new IconView(icon, 16, kind is ButtonKind.Primary ? "textOnAccent" : "textPrimary"), Text(label, "control", kind is ButtonKind.Primary ? "textOnAccent" : null));
        var button = new Button
        {
            Content = content,
            MinHeight = theme.Tokens.Number("densityControlHeight", 32),
            Padding = new Thickness(theme.Tokens.Number("densityControlPaddingHorizontal", 12), 4, theme.Tokens.Number("densityControlPaddingHorizontal", 12), 4),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Command = command,
            CommandParameter = parameter,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };

        switch (kind)
        {
            case ButtonKind.Primary:
                button.Background = theme.Brush("accent");
                button.BorderBrush = theme.Brush("accent");
                break;
            case ButtonKind.Ghost:
                button.Background = theme.Brush("transparent");
                button.BorderBrush = theme.Brush("transparent");
                break;
            case ButtonKind.Destructive:
                button.Background = theme.Brush("surface3");
                button.BorderBrush = theme.Brush("danger");
                button.Foreground = theme.Brush("danger");
                if (content is TextBlock label1)
                {
                    label1.Foreground = theme.Brush("danger");
                }

                break;
            default:
                button.Background = theme.Brush("surface3");
                button.BorderBrush = theme.Brush("borderDefault");
                break;
        }

        button.BorderThickness = new Thickness(theme.Tokens.Number("borderWeight", 1));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    public static Button IconButton(string iconKey, string tooltip, Action? onClick = null, double size = 32, ICommand? command = null, object? parameter = null)
    {
        var theme = ThemeRuntime.Current;
        var button = new Button
        {
            Content = new IconView(iconKey, Math.Round(size * 0.5), "textPrimary"),
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Background = theme.Brush("transparent"),
            BorderBrush = theme.Brush("transparent"),
            Command = command,
            CommandParameter = parameter,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };
        button.Tip(tooltip);
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    /// <summary>A surface with a material from the R2 hierarchy. Glass has body, edge and highlight.</summary>
    public static Border Surface(UIElement? child, Material material = Material.Grounded, double? radius = null, double padding = 0)
    {
        var theme = ThemeRuntime.Current;
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(radius ?? theme.Tokens.Number("radiusCard", 12)),
            BorderThickness = new Thickness(theme.Tokens.Number("borderWeight", 1)),
        };

        switch (material)
        {
            case Material.Raised:
                border.Background = theme.Brush("surface2");
                border.BorderBrush = theme.Brush("borderSubtle");
                break;
            case Material.Frost:
                border.Background = theme.Brush("glassFill");
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            case Material.Deep:
                border.Background = theme.Brush("glassDeepFill");
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            case Material.Glass:
                border.Background = theme.GlassBrush();
                border.BorderBrush = theme.Brush("glassBorder");
                break;
            default:
                border.Background = theme.Brush("surface1");
                border.BorderBrush = theme.Brush("borderSubtle");
                break;
        }

        return border;
    }

    public static ScrollViewer Scroll(UIElement content, bool horizontal = false)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        };
        scroll.ViewChanged += (_, _) => ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        return scroll;
    }

    public static Border Divider()
    {
        return new Border { Height = 1, Background = ThemeRuntime.Current.Brush("borderSubtle"), Margin = new Thickness(0, 4, 0, 4) };
    }

    public static Border Chip(string text, bool selected = false, Action? onClick = null)
    {
        var theme = ThemeRuntime.Current;
        var chip = new Border
        {
            Child = Text(text, "control", selected ? "textOnAccent" : "textPrimary"),
            Padding = new Thickness(10, 4, 10, 4),
            MinHeight = theme.Tokens.Number("interactiveChipHeight", 30),
            CornerRadius = new CornerRadius(theme.Tokens.Number("radiusControl", 8)),
            Background = selected ? theme.Brush("accent") : theme.Brush("surface3"),
            BorderBrush = selected ? theme.Brush("accent") : theme.Brush("borderSubtle"),
            BorderThickness = new Thickness(1),
        };
        if (onClick is not null)
        {
            chip.Tapped += (_, _) => onClick();
            chip.PointerEntered += (_, _) => chip.BorderBrush = theme.Brush("borderInteractive");
            chip.PointerExited += (_, _) => chip.BorderBrush = selected ? theme.Brush("accent") : theme.Brush("borderSubtle");
        }

        return chip;
    }

    public static Border Badge(string text, string tone = "neutral")
    {
        var theme = ThemeRuntime.Current;
        var (background, foreground) = tone switch
        {
            "accent" => ("accent", "textOnAccent"),
            "warning" => ("warning", "textInverse"),
            "danger" => ("danger", "textOnAccent"),
            "success" => ("success", "textInverse"),
            "glass" => ("glassStrip", "onMediaPrimary"),
            _ => ("surface3", "textSecondary"),
        };
        return new Border
        {
            Child = Text(text, "badge", foreground),
            Padding = new Thickness(6, 1, 6, 1),
            MinHeight = theme.Tokens.Number("badgeHeight", 18),
            CornerRadius = new CornerRadius(4),
            Background = theme.Brush(background),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBox Input(string placeholder, string? text = null, Action<string>? changed = null)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            Text = text ?? string.Empty,
            MinHeight = ThemeRuntime.Current.Tokens.Number("densityControlHeight", 32),
            CornerRadius = new CornerRadius(ThemeRuntime.Current.Tokens.Number("radiusControl", 8)),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, placeholder);
        if (changed is not null)
        {
            box.TextChanged += (_, _) => changed(box.Text);
        }

        return box;
    }

    /// <summary>Section header used by quiet utility surfaces.</summary>
    public static StackPanel Section(string title, string? description = null, params UIElement?[] content)
    {
        var header = V(2, Text(title, "section-title"), description is null ? null : Text(description, "body-muted"));
        var panel = V(ThemeRuntime.Current.Tokens.Number("space12", 12), header);
        foreach (var item in content)
        {
            if (item is not null)
            {
                panel.Children.Add(item);
            }
        }

        return panel;
    }

    /// <summary>Page padding follows R2 §5: 16 at 800–959, 24 at 960–1199, 32 at 1200+.</summary>
    public static double PagePadding(double width) => width < 960 ? 16 : width < 1200 ? 24 : 32;

    public static SolidColorBrush Solid(ArgbColor color) => new(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));

    public static Windows.UI.Color ToColor(this ArgbColor color) => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
}
