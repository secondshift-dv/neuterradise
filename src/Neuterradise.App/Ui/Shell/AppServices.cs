using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Import;
using Neuterradise.App.Presentation;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Lifecycle;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Ui;

/// <summary>The composition root handed to every surface. Services, never UI objects, flow down from here.</summary>
public sealed class AppServices
{
    public required CatalogDb Catalog { get; init; }

    public required VaultPaths Paths { get; init; }

    public required ProductionRuntimeRegistry Runtime { get; init; }

    public required PresentationRuntime Presentation { get; init; }

    public required NavigationCoordinator Navigation { get; init; }

    public required RouteViewModelFactory Factory { get; init; }

    public required OverlayHostViewModel Overlay { get; init; }

    public required ImportActivityService ImportActivity { get; init; }

    public required ImportFinalizer Finalizer { get; init; }

    public required ShellStatusViewModel Status { get; init; }

    public required UiScaleService UiScale { get; init; }

    public required AppConfigurationStore Configuration { get; init; }

    public required UnoWindowPlacement Placement { get; init; }

    public required Window Window { get; init; }

    public ProductRoot? Root { get; set; }

    /// <summary>Raised on the UI thread after a presentation Apply or pack change.</summary>
    public event Action<PresentationChangedEventArgs>? PresentationChanged;

    internal void RaisePresentationChanged(PresentationChangedEventArgs args) => PresentationChanged?.Invoke(args);

    public void OpenCustomization(string category, PresentationContext? context = null, string? slot = null, object? subject = null) =>
        Root?.ShowCustomization(category, context ?? PresentationContext.Global, slot, subject);

    public void Toast(string message, string tone = "info") => Root?.ShowToast(message, tone);

    public Task ImportAsync(IEnumerable<string> paths, Guid? destinationProfileId) =>
        Root?.ImportAsync(paths, destinationProfileId) ?? Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool destructive = false) =>
        Root?.Dialogs.ConfirmAsync(title, message, confirm, destructive) ?? Task.FromResult(false);

    public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, params string[] extensions) => Pickers.PickFilesAsync(Window, multiple, extensions);

    public Task<string?> PickFolderAsync() => Pickers.PickFolderAsync(Window);

    public Task<string?> PickSaveFileAsync(string suggestedName, string extension, string label) => Pickers.PickSaveFileAsync(Window, suggestedName, extension, label);

    public void RunUserAction(Task task, string context, string failureMessage) =>
        TaskObserver.Observe(task, context, _ => Toast(failureMessage, "error"));
}

public abstract class Surface : IDisposable
{
    private bool _disposed;

    protected Surface(AppServices services) => Services = services;

    public AppServices Services { get; }

    public abstract FrameworkElement View { get; }

    public virtual ScreenStateViewModel? Model => null;

    public bool IsActive { get; private set; }

    public AppRoute? LastRoute { get; private set; }

    protected Disposables Bag { get; } = new();

    public void Activate(AppRoute route)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsActive = true;
        LastRoute = route;
        OnActivated(route);
    }

    public void Suspend()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = false;
        OnSuspended();
    }

    protected virtual void OnActivated(AppRoute route)
    {
    }

    protected virtual void OnSuspended()
    {
    }

    public virtual void OnPresentationChanged(PresentationChangedEventArgs args)
    {
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsActive = false;
        Bag.Dispose();
        var model = Model;
        model?.RetireRoute();
        if (model is IDisposable disposableModel)
        {
            disposableModel.Dispose();
        }
    }
}

public sealed class ScaleHost : Panel
{
    private readonly ScaleTransform _transform = new();
    private double _scale = 1;

    public ScaleHost()
    {
        RenderTransform = _transform;
    }

    public double UiScale
    {
        get => _scale;
        set
        {
            _scale = UiScaleRules.Normalize(value);
            _transform.ScaleX = _scale;
            _transform.ScaleY = _scale;
            InvalidateMeasure();
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var inner = new Windows.Foundation.Size(availableSize.Width / _scale, availableSize.Height / _scale);
        foreach (var child in Children)
        {
            child.Measure(inner);
        }

        return availableSize;
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var inner = new Windows.Foundation.Rect(0, 0, finalSize.Width / _scale, finalSize.Height / _scale);
        foreach (var child in Children)
        {
            child.Arrange(inner);
        }

        return finalSize;
    }
}

public static class PresentationBinder
{
    public static void ApplyGlobal(PresentationRuntime presentation, BindingSet? bindings = null)
    {
        var context = PresentationContext.Global;
        var theme = presentation.ResolvePlan<ThemePlan>(PresentationSlots.Theme, context, bindings);
        var type = presentation.ResolvePlan<TypographyPlan>(PresentationSlots.Typography, context, bindings);
        var icons = presentation.ResolvePlan<IconPlan>(PresentationSlots.Icons, context, bindings);
        var motion = presentation.ResolvePlan<MotionPlan>(PresentationSlots.Motion, context, bindings);
        ThemeRuntime.Current.Apply(theme, type, icons, motion, ThemeRuntime.Current.Density);
    }
}
