using System.Diagnostics;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Shell;

/// <summary>
/// The single application-level owner of UI scale. The keyboard shortcuts on the main window and
/// Settings → Display both drive this one instance; the shell applies it as a layout transform.
/// Changing scale only rewrites the configuration file — it never touches the catalog, media or
/// import work.
/// </summary>
public sealed class UiScaleService
{
    private readonly AppConfigurationStore? _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task _pendingSave = Task.CompletedTask;
    private double _scale = UiScaleRules.Default;

    public UiScaleService(AppConfigurationStore? store = null)
    {
        _store = store;
    }

    public event EventHandler? ScaleChanged;

    public double Scale => _scale;

    public int Percent => UiScaleRules.ToPercent(_scale);

    public bool CanIncrease => _scale < UiScaleRules.Maximum;

    public bool CanDecrease => _scale > UiScaleRules.Minimum;

    public bool IsDefault => _scale.Equals(UiScaleRules.Default);

    public bool Increase() => SetScale(_scale + UiScaleRules.Step);

    public bool Decrease() => SetScale(_scale - UiScaleRules.Step);

    public bool Reset() => SetScale(UiScaleRules.Default);

    /// <summary>Clamps and snaps to the 5% grid; returns true when the scale actually changed.</summary>
    public bool SetScale(double value)
    {
        var normalized = UiScaleRules.Normalize(value);
        if (normalized.Equals(_scale))
        {
            return false;
        }

        _scale = normalized;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
        _pendingSave = PersistAsync(normalized);
        return true;
    }

    /// <summary>Reads the persisted scale. Called once at startup, before the window is shown.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return;
        }

        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var loaded = UiScaleRules.Normalize(load.Configuration.UiScale);
        if (!loaded.Equals(_scale))
        {
            _scale = loaded;
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Completes once the most recent scale change has been written (or skipped).</summary>
    public Task FlushAsync() => _pendingSave;

    private async Task PersistAsync(double scale)
    {
        if (_store is null)
        {
            return;
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A newer change is queued behind this one and will write the final value.
            if (!_scale.Equals(scale))
            {
                return;
            }

            var save = await _store.UpdateAsync(
                configuration => configuration with { UiScale = scale }).ConfigureAwait(false);
            if (!save.IsSaved)
            {
                Trace.TraceWarning("UI scale could not be saved: {0}", save.SafeErrorDetail);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("UI scale could not be saved: {0}", exception.GetType().Name);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
