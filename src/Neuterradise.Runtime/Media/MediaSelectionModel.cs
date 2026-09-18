namespace Neuterradise.App.Media;

public sealed class MediaSelectionModel
{
    private readonly HashSet<Guid> _selected = [];

    private Guid? _anchor;

    public event EventHandler? Changed;

    public IReadOnlyCollection<Guid> SelectedAssetIds => _selected;

    public Guid? AnchorAssetId => _anchor;

    public int SelectionCount => _selected.Count;

    public bool IsSelected(Guid assetId) => _selected.Contains(assetId);

    public void SelectOnly(Guid assetId)
    {
        RequireNonEmpty(assetId);

        var changed = _selected.Count != 1 || !_selected.Contains(assetId) || _anchor != assetId;

        _selected.Clear();
        _selected.Add(assetId);
        _anchor = assetId;

        Raise(changed);
    }

    public void Toggle(Guid assetId)
    {
        RequireNonEmpty(assetId);

        if (_selected.Add(assetId))
        {
            _anchor = assetId;
        }
        else
        {
            _selected.Remove(assetId);
            if (_anchor == assetId)
            {
                _anchor = null;
            }
        }

        Raise(true);
    }

    public void SelectRange(Guid anchor, Guid target, IReadOnlyList<Guid> currentOrder)
    {
        ArgumentNullException.ThrowIfNull(currentOrder);
        RequireNonEmpty(anchor);
        RequireNonEmpty(target);

        var anchorIndex = IndexOf(currentOrder, anchor);
        var targetIndex = IndexOf(currentOrder, target);

        if (anchorIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);

        _selected.Clear();
        for (var i = start; i <= end; i++)
        {
            _selected.Add(currentOrder[i]);
        }

        _anchor = anchor;
        Raise(true);
    }

    public void SelectLoaded(IReadOnlyCollection<Guid> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        _selected.Clear();
        foreach (var id in loaded)
        {
            if (id != Guid.Empty)
            {
                _selected.Add(id);
            }
        }

        if (_anchor is { } anchor && !_selected.Contains(anchor))
        {
            _anchor = null;
        }

        Raise(true);
    }

    public void ReconcileTo(IReadOnlyCollection<Guid> currentContextAssetIds)
    {
        ArgumentNullException.ThrowIfNull(currentContextAssetIds);

        var keep = currentContextAssetIds as ISet<Guid> ?? new HashSet<Guid>(currentContextAssetIds);
        var removed = _selected.RemoveWhere(id => !keep.Contains(id));

        var anchorDropped = false;
        if (_anchor is { } anchor && !keep.Contains(anchor))
        {
            _anchor = null;
            anchorDropped = true;
        }

        Raise(removed > 0 || anchorDropped);
    }

    public void Clear()
    {
        var changed = _selected.Count > 0 || _anchor is not null;

        _selected.Clear();
        _anchor = null;

        Raise(changed);
    }

    public void RestoreAnchor(Guid? anchorAssetId)
    {
        _anchor = anchorAssetId is { } id && _selected.Contains(id) ? id : null;
    }

    private static int IndexOf(IReadOnlyList<Guid> order, Guid value)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static void RequireNonEmpty(Guid assetId)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An Asset id cannot be empty.", nameof(assetId));
        }
    }

    private void Raise(bool changed)
    {
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
