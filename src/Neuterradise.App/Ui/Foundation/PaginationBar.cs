using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Neuterradise.App.Ui;

/// <summary>A shared, surface-centred pager for Gallery and Profile Media.</summary>
public sealed class PaginationBar : IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<string> _range;
    private readonly Func<int> _page;
    private readonly Func<int> _pages;
    private readonly Action<int> _goToPage;
    private readonly StackPanel _navigation = UI.H(4);
    private readonly TextBlock _rangeText = UI.Text(string.Empty, "metadata");
    private readonly ComboBox _pageSize;
    private readonly Border _balancer = new();
    private bool _syncing;

    public PaginationBar(
        INotifyPropertyChanged source,
        Func<string> range,
        Func<int> page,
        Func<int> pages,
        IReadOnlyList<int> pageSizes,
        Func<int> pageSize,
        Action<int> setPageSize,
        Action<int> goToPage)
    {
        _source = source;
        _range = range;
        _page = page;
        _pages = pages;
        _goToPage = goToPage;
        _pageSize = new ComboBox
        {
            Header = UI.T("Pager.PageSize", "Per page"),
            ItemsSource = pageSizes,
            SelectedItem = pageSize(),
            MinWidth = 96,
        };
        _pageSize.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _pageSize.SelectedItem is int value)
            {
                setPageSize(value);
            }
        };

        var left = UI.H(10, _rangeText.Align(vertical: VerticalAlignment.Center), _pageSize);
        left.SizeChanged += (_, _) => _balancer.Width = left.ActualWidth;
        View = UI.Grid("auto", "*,auto,*",
            left.At(0, 0),
            _navigation.At(0, 1),
            _balancer.Align(HorizontalAlignment.Right).At(0, 2));
        _source.PropertyChanged += OnChanged;
        Refresh();
    }

    public FrameworkElement View { get; }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => UiDispatch.Run(Refresh);

    private void Refresh()
    {
        _syncing = true;
        _rangeText.Text = _range();
        _syncing = false;
        _navigation.Children.Clear();

        var current = Math.Clamp(_page(), 1, Math.Max(1, _pages()));
        var total = Math.Max(1, _pages());
        _navigation.Children.Add(PageButton("«", UI.T("Pager.First", "First page"), 1, current > 1));
        _navigation.Children.Add(PageButton("‹", UI.T("Pager.Previous", "Previous page"), current - 1, current > 1));

        foreach (var value in VisiblePages(current, total))
        {
            if (value == 0)
            {
                _navigation.Children.Add(UI.Text("…", "control").Margin(4, 0, 4, 0).Align(vertical: VerticalAlignment.Center));
                continue;
            }

            if (value == current)
            {
                var input = new TextBox
                {
                    Text = current.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    Width = 52,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
                };
                void Commit()
                {
                    if (int.TryParse(input.Text, out var requested))
                    {
                        _goToPage(Math.Clamp(requested, 1, total));
                    }
                    else
                    {
                        input.Text = current.ToString(System.Globalization.CultureInfo.CurrentCulture);
                    }
                }
                input.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        Commit();
                        e.Handled = true;
                    }
                };
                input.LostFocus += (_, _) => Commit();
                _navigation.Children.Add(input);
            }
            else
            {
                _navigation.Children.Add(PageButton(value.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    UI.F("Pager.GoTo", "Go to page {0}", value), value, true));
            }
        }

        _navigation.Children.Add(PageButton("›", UI.T("Pager.Next", "Next page"), current + 1, current < total));
        _navigation.Children.Add(PageButton("»", UI.T("Pager.Last", "Last page"), total, current < total));
    }

    private Button PageButton(string text, string tooltip, int page, bool enabled)
    {
        var button = UI.Button(text, () => _goToPage(page), ButtonKind.Ghost);
        button.IsEnabled = enabled;
        button.MinWidth = 36;
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static IReadOnlyList<int> VisiblePages(int current, int total)
    {
        if (total <= 7)
        {
            return Enumerable.Range(1, total).ToArray();
        }

        var values = new List<int> { 1 };
        var start = Math.Max(2, current - 1);
        var end = Math.Min(total - 1, current + 1);
        if (start > 2) values.Add(0);
        for (var page = start; page <= end; page++) values.Add(page);
        if (end < total - 1) values.Add(0);
        values.Add(total);
        return values;
    }

    public void Dispose() => _source.PropertyChanged -= OnChanged;
}
