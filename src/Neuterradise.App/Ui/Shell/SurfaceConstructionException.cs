using Neuterradise.App.Shell;

namespace Neuterradise.App.Ui;

/// <summary>
/// A page surface failed to build. Carries the route identity so crash diagnostics name the exact
/// surface (and, through the inner exception, the exact repeater or control) that failed.
/// </summary>
public sealed class SurfaceConstructionException(AppRoute route, Exception inner)
    : InvalidOperationException($"Surface for route '{route.GetType().Name}' ({route}) failed to build: {inner.Message}", inner)
{
    public AppRoute Route { get; } = route;
}
